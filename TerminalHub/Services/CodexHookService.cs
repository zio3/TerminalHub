using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace TerminalHub.Services;

/// <summary>
/// Codex CLI の lifecycle hook 設定を管理するサービス。
/// Codex は <c>type:"http"</c> をサポートせず <c>type:"command"</c> のみのため、
/// 各 hook で TerminalHub.exe を「ブリッジ」として起動し、stdin の JSON を
/// TerminalHub の HTTP API (/api/hook/codex/{sessionId}) へ転送させる。
/// 設定先は <c>&lt;folder&gt;/.codex/hooks.json</c>。
/// </summary>
public interface ICodexHookService
{
    /// <summary>セッション用の Codex hook 設定をセットアップする</summary>
    Task SetupHooksAsync(Guid sessionId, string folderPath, string baseUrl);

    /// <summary>TerminalHub 由来の Codex hook エントリを .codex/hooks.json から削除する</summary>
    Task RemoveHooksAsync(string folderPath);
}

public class CodexHookService : ICodexHookService
{
    private readonly ILogger<CodexHookService> _logger;
    private const string HooksFileName = ".codex/hooks.json";

    // ブリッジ起動コマンドに含める識別子。TerminalHub 由来エントリの判定にも使う。
    private const string BridgeMarker = "--source codex";

    // Codex に登録する hook (event, matcher)。matcher=null は全マッチ。command 型はプロセス起動コストが
    // あるため低頻度イベントに絞る。PreToolUse は matcher=request_user_input（Codex のユーザー選択ツール、
    // Claude の AskUserQuestion 相当）だけに絞り、全ツールでは発火させない。
    private static readonly (string Event, string? Matcher)[] HookRegistrations =
    {
        ("Stop", null),
        ("UserPromptSubmit", null),
        ("SubagentStart", null),
        ("SubagentStop", null),
        ("PreCompact", null),
        ("PostCompact", null),
        // 許可待ち（ツール実行の承認待ち）を検知して「確認待ち」ベル/Webhook に載せる。
        ("PermissionRequest", null),
        // ユーザーへの選択肢提示（request_user_input）＝「選択待ち」。matcher で絞る。
        ("PreToolUse", "^request_user_input$"),
    };

    // クリーンアップ対象のイベント名（重複排除）
    private static readonly string[] HookEventNames =
        HookRegistrations.Select(r => r.Event).Distinct().ToArray();

    public CodexHookService(ILogger<CodexHookService> logger)
    {
        _logger = logger;
    }

    public async Task SetupHooksAsync(Guid sessionId, string folderPath, string baseUrl)
    {
        try
        {
            var hooksPath = Path.Combine(folderPath, HooksFileName);
            var hooksDir = Path.GetDirectoryName(hooksPath);
            if (!string.IsNullOrEmpty(hooksDir) && !Directory.Exists(hooksDir))
            {
                Directory.CreateDirectory(hooksDir);
                _logger.LogInformation(".codex ディレクトリを作成: {Path}", hooksDir);
            }

            // 既存の hooks.json を読み込む（無ければ空）
            JsonObject root;
            if (File.Exists(hooksPath))
            {
                var existing = await File.ReadAllTextAsync(hooksPath);
                root = JsonNode.Parse(existing)?.AsObject() ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            if (root["hooks"] is not JsonObject hooks)
            {
                hooks = new JsonObject();
                root["hooks"] = hooks;
            }

            var command = BuildBridgeCommand(sessionId, baseUrl);
            foreach (var (eventName, matcher) in HookRegistrations)
            {
                AddOrUpdateHook(hooks, eventName, command, matcher);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(hooksPath, root.ToJsonString(options));
            _logger.LogInformation("Codex Hook 設定を保存: {Path}", hooksPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Codex Hook 設定のセットアップに失敗: {FolderPath}", folderPath);
            throw;
        }
    }

    public async Task RemoveHooksAsync(string folderPath)
    {
        try
        {
            var hooksPath = Path.Combine(folderPath, HooksFileName);
            if (!File.Exists(hooksPath))
            {
                return;
            }

            var root = JsonNode.Parse(await File.ReadAllTextAsync(hooksPath))?.AsObject();
            if (root == null || root["hooks"] is not JsonObject hooks)
            {
                return;
            }

            bool modified = false;
            foreach (var eventName in HookEventNames)
            {
                if (hooks[eventName] is not JsonArray hookArray) continue;
                if (RemoveTerminalHubHooksFromArray(hookArray) > 0) modified = true;
                if (hookArray.Count == 0) { hooks.Remove(eventName); modified = true; }
            }

            if (!modified) return;

            var options = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(hooksPath, root.ToJsonString(options));
            _logger.LogInformation("TerminalHub 由来の Codex hook を削除: {Path}", hooksPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Codex Hook 削除に失敗: {FolderPath}", folderPath);
            throw;
        }
    }

    /// <summary>
    /// ブリッジ起動コマンドを生成する。実行中の TerminalHub.exe を --notify --source codex モードで起動し、
    /// stdin の Codex JSON を /api/hook/codex/{sessionId} へ転送させる。
    /// </summary>
    private static string BuildBridgeCommand(Guid sessionId, string baseUrl)
    {
        // 実行中サーバーの exe パス（= TerminalHub.exe）。ブリッジも同じ exe を別プロセスで起動する。
        var exePath = Environment.ProcessPath ?? "TerminalHub.exe";
        var port = GetPort(baseUrl);
        // Codex は hook の command を Windows 既定シェル（cmd.exe /C の場合あり）で実行し、commandWindows は
        // 読まない。cmd.exe でも PowerShell でも安定して動くよう、command 自体に powershell.exe を明示する。
        // PowerShell 内ではクォート付き exe パスの実行に呼び出し演算子 & が必要。
        // exe パスは PowerShell の単一引用符でくくる（シングルクォートは '' でエスケープ）。
        var escapedExePath = exePath.Replace("'", "''");
        return $"powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"& '{escapedExePath}' --notify {BridgeMarker} --session {sessionId} --port {port}\"";
    }

    private static int GetPort(string baseUrl)
    {
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Port > 0 ? uri.Port : 5081;
    }

    private void AddOrUpdateHook(JsonObject hooks, string eventName, string command, string? matcher = null)
    {
        if (hooks[eventName] is not JsonArray hookArray)
        {
            hookArray = new JsonArray();
            hooks[eventName] = hookArray;
        }

        RemoveTerminalHubHooksFromArray(hookArray);

        // Codex の入れ子形式: { "matcher": "...", "hooks": [ { "type":"command", "command":"...", "timeout":5 } ] }
        // matcher 省略時は全マッチ。指定時は対象ツール名等の正規表現で絞る。
        var inner = new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
            ["commandWindows"] = command, // TerminalHub は Windows 専用。Windows override も同値で明示
            // 5 秒固定。Codex は hook 完了まで待つため短く。ブリッジは POST 投げて即終了する。
            ["timeout"] = 5
        };
        var entry = new JsonObject();
        if (!string.IsNullOrEmpty(matcher))
        {
            entry["matcher"] = matcher;
        }
        entry["hooks"] = new JsonArray { inner };
        hookArray.Add(entry);
    }

    /// <summary>hook 配列から TerminalHub 由来（command に --source codex を含む）エントリを除去。戻り値は削除件数。</summary>
    private static int RemoveTerminalHubHooksFromArray(JsonArray hookArray)
    {
        var indices = new List<int>();
        for (int i = 0; i < hookArray.Count; i++)
        {
            if (hookArray[i] is not JsonObject entry) continue;
            if (entry["hooks"] is not JsonArray inner) continue;
            for (int j = 0; j < inner.Count; j++)
            {
                if (inner[j] is JsonObject h && IsTerminalHubHook(h))
                {
                    indices.Add(i);
                    break;
                }
            }
        }
        foreach (var idx in indices.OrderByDescending(x => x))
        {
            hookArray.RemoveAt(idx);
        }
        return indices.Count;
    }

    private static bool IsTerminalHubHook(JsonObject hookObj)
    {
        var cmd = hookObj["command"]?.GetValue<string>() ?? "";
        var cmdWin = hookObj["commandWindows"]?.GetValue<string>() ?? "";
        return cmd.Contains(BridgeMarker) || cmdWin.Contains(BridgeMarker);
    }
}
