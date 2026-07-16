using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace TerminalHub.Services;

/// <summary>
/// Claude Code の hook 設定を管理するサービス。
///
/// hook はセッション起動オプション <c>--settings &lt;JSONパス&gt;</c> で渡す（MCP の --mcp-config と同じ流儀）。
/// ユーザーの設定ファイル（.claude/settings.local.json）へは書き込まない。JSON は TerminalHub 自身の
/// データ領域（<see cref="AppDataPaths.GetClaudeHookConfigFilePath"/>）に置き、パスだけをコマンドラインに乗せる。
///
/// hook の送信先 URL にはセッション GUID が入る（/api/hook/claude/{sessionId}）ため、
/// MCP（ポート毎に1ファイル）と違い<b>セッション毎に1ファイル</b>になる。
/// 毎起動時に実行中のポートで生成し直し、セッション完全削除時にファイルも消す。
///
/// 【旧方式の残骸と二重発火】旧バージョン（〜v1.0.71）は &lt;folder&gt;/.claude/settings.local.json へ
/// 書き込む方式だった。hook は MCP と違って「同名上書き」ではなく<b>加算</b>（--settings と
/// settings.local.json の両方が発火する。実測確認済み）なので、残骸を放置すると同じイベントが
/// 二重に飛ぶ。このため MCP の「残骸は放置」方針は使えず、セッション起動前に
/// <see cref="RemoveLegacyHooksAsync"/> で TerminalHub 由来のエントリだけを掃除する。
/// </summary>
public interface IClaudeHookService
{
    /// <summary>
    /// Claude Code に <c>--settings</c> で渡す hook 設定 JSON を用意し、そのフルパスを返す。
    /// (Stop / UserPromptSubmit / Notification / SubagentStart / SubagentStop /
    ///  PreCompact / PostCompact / PreToolUse(matcher=AskUserQuestion) の各イベントを一括登録。
    ///  実際の登録内容は HookRegistrations を参照)
    /// 失敗したら null（呼び出し側はオプション無しで起動する＝hook 通知が来ないだけ）。
    /// </summary>
    /// <param name="baseUrl">TerminalHub サーバーのベース URL（例: http://localhost:5081）。
    /// hook の送信先 URL に使われる</param>
    string? EnsureHookConfigFile(Guid sessionId, string baseUrl);

    /// <summary>
    /// セッション完全削除時に、そのセッション用の hook 設定 JSON を消す（ベストエフォート）。
    /// </summary>
    void DeleteHookConfigFile(Guid sessionId);

    /// <summary>
    /// 旧方式（〜v1.0.71）が &lt;folder&gt;/.claude/settings.local.json に書き残した
    /// TerminalHub 由来の hook エントリを削除する。放置すると --settings 側と二重発火するため、
    /// セッション起動前に毎回呼ぶ（残骸が無ければ何もしない）。ユーザーが手書きした hook は消さない。
    /// </summary>
    Task RemoveLegacyHooksAsync(string folderPath);
}

/// <summary>
/// ClaudeHookService の実装
/// </summary>
public class ClaudeHookService : IClaudeHookService
{
    private readonly ILogger<ClaudeHookService> _logger;
    private const string SettingsFileName = ".claude/settings.local.json";

    // TerminalHub が登録する Claude Code hook (event, matcher) の定義。matcher=null は全マッチ。
    // - SubagentStart / SubagentStop: サブエージェントの起動・終了を agent_id で追跡。
    // - PreCompact / PostCompact: compact 中の「作業中／作業可能」状態。
    // - PreToolUse は matcher で AskUserQuestion のみに絞る（質問が出た＝回答待ちを検知するため）。
    //   matcher 無し（全ツール）にすると Read/Bash/Edit 等あらゆるツール呼び出しで hook HTTP が飛び、
    //   ツール実行のたびに待ちが入って重くなるため、AskUserQuestion だけに限定する。
    //   「回答待ち解除」は不要（ユーザーが回答＝そのセッションに切替済み＝ベルは自然に消える）ため
    //   PostToolUse は登録しない。
    private static readonly (string Event, string? Matcher)[] HookRegistrations =
    {
        ("Stop", null),
        ("UserPromptSubmit", null),
        ("Notification", null),
        ("SubagentStart", null),
        ("SubagentStop", null),
        ("PreCompact", null),
        ("PostCompact", null),
        ("PreToolUse", "AskUserQuestion"),
    };

    // 旧方式残骸のクリーンアップ対象イベント名（重複排除）
    private static readonly string[] HookEventNames =
        HookRegistrations.Select(r => r.Event).Distinct().ToArray();

    public ClaudeHookService(ILogger<ClaudeHookService> logger)
    {
        _logger = logger;
    }

    public string? EnsureHookConfigFile(Guid sessionId, string baseUrl)
    {
        try
        {
            var path = AppDataPaths.GetClaudeHookConfigFilePath(sessionId);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var hooks = new JsonObject();
            foreach (var (eventName, matcher) in HookRegistrations)
            {
                // 入れ子形式（Claude Code の新仕様に準拠）:
                // {"matcher": "AskUserQuestion", "hooks": [{"type": "http", "url": "...", "timeout": 5}]}
                // matcher は省略可能（省略時はすべてにマッチ）。
                var entry = new JsonObject();
                if (!string.IsNullOrEmpty(matcher))
                {
                    entry["matcher"] = matcher;
                }
                entry["hooks"] = new JsonArray { BuildHookEntry(sessionId, baseUrl) };
                hooks[eventName] = new JsonArray { entry };
            }
            var root = new JsonObject { ["hooks"] = hooks };
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            // 既に同じ内容なら触らない（起動のたびに書き換えて claude の読み取りと競合させない）。
            if (File.Exists(path) && File.ReadAllText(path) == json)
                return path;

            // 一時ファイルへ書いてから差し替える。直接上書きすると、claude が読んでいる最中に
            // 中身が空/途中の状態を晒し得る。
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);

            _logger.LogInformation("Hook 設定ファイルを用意(--settings): {Path} baseUrl={BaseUrl}", path, baseUrl);
            return path;
        }
        catch (Exception ex)
        {
            // 失敗してもセッションは起動させる（hook 通知が来ないだけ）。
            _logger.LogWarning(ex, "Hook 設定ファイルの用意に失敗: SessionId={SessionId} baseUrl={BaseUrl}", sessionId, baseUrl);
            return null;
        }
    }

    public void DeleteHookConfigFile(Guid sessionId)
    {
        try
        {
            var path = AppDataPaths.GetClaudeHookConfigFilePath(sessionId);
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogInformation("Hook 設定ファイルを削除: {Path}", path);
            }
        }
        catch (Exception ex)
        {
            // 消せなくても実害はない（次に同じ GUID で使われることはなく、ただ残るだけ）。
            _logger.LogWarning(ex, "Hook 設定ファイルの削除に失敗: SessionId={SessionId}", sessionId);
        }
    }

    public async Task RemoveLegacyHooksAsync(string folderPath)
    {
        try
        {
            var settingsPath = Path.Combine(folderPath, SettingsFileName);
            if (!File.Exists(settingsPath))
            {
                return;
            }

            var existingJson = await File.ReadAllTextAsync(settingsPath);
            var settings = JsonNode.Parse(existingJson)?.AsObject();
            if (settings == null || settings["hooks"] is not JsonObject hooks)
            {
                return;
            }

            bool modified = false;
            foreach (var eventName in HookEventNames)
            {
                if (hooks[eventName] is not JsonArray hookArray) continue;

                if (RemoveTerminalHubHooksFromArray(hookArray) > 0)
                {
                    modified = true;
                }

                // 空になった配列はキーごと除去（modified もこの時点で true にして書き戻し対象にする）
                if (hookArray.Count == 0)
                {
                    hooks.Remove(eventName);
                    modified = true;
                }
            }

            if (!modified)
            {
                return;
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(settingsPath, settings.ToJsonString(options));
            _logger.LogInformation("旧方式の TerminalHub 由来 hook を削除（二重発火防止）: {Path}", settingsPath);
        }
        catch (Exception ex)
        {
            // 掃除に失敗してもセッションは起動させる（最悪、旧残骸と二重発火するだけ）。
            _logger.LogWarning(ex, "旧方式 hook の削除に失敗: {FolderPath}", folderPath);
        }
    }

    /// <summary>
    /// Claude Code の type:"http" hook エントリを生成する
    /// baseUrl は呼び出し元が IServerAddressesFeature から取得した実際のバインド URL（HTTP 優先）
    /// </summary>
    private static JsonObject BuildHookEntry(Guid sessionId, string baseUrl)
    {
        // 末尾スラッシュがある場合は除去してから結合
        var trimmedBase = baseUrl.TrimEnd('/');
        return new JsonObject
        {
            ["type"] = "http",
            ["url"] = $"{trimmedBase}/api/hook/claude/{sessionId}",
            // 5 秒固定。長くすると Claude Code 側が hook の完了を待つ分、
            // Stop / UserPromptSubmit 等の発火に遅延が乗ってしまう
            // (= ユーザー体感の処理完了通知が遅れる)。短めを維持する。
            ["timeout"] = 5
        };
    }

    /// <summary>
    /// TerminalHub 由来の hook エントリかを判定する
    /// - 旧形式: type:"command" で --notify --session を含む
    /// - 新形式: type:"http" で URL に /api/hook/claude/ を含む（TerminalHub 専用パスに限定）
    /// </summary>
    private static bool IsTerminalHubHook(JsonObject hookObj)
    {
        var type = hookObj["type"]?.GetValue<string>();

        // 新形式: TerminalHub 専用の /api/hook/claude/ パスのみを対象とする。
        // 汎用の /api/hook を含む URL（他ツール向けに手書きされた hook 等）は誤削除しない。
        if (type == "http")
        {
            var url = hookObj["url"]?.GetValue<string>() ?? "";
            if (url.Contains("/api/hook/claude/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 旧形式: type:"command" で --notify --session を含む
        var command = hookObj["command"]?.GetValue<string>() ?? "";
        if (command.Contains("--notify") && command.Contains("--session"))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// hook 配列から TerminalHub 由来のエントリを全て取り除く。戻り値は削除件数。
    /// 直接形式 {"type": "..."} と入れ子形式 {"hooks": [...]} の両方をチェックする。
    /// </summary>
    private static int RemoveTerminalHubHooksFromArray(JsonArray hookArray)
    {
        var indicesToRemove = new List<int>();
        for (int i = 0; i < hookArray.Count; i++)
        {
            if (hookArray[i] is not JsonObject entryObj) continue;

            // 形式1: 直接形式 {"type": "command"|"http", ...}
            if (IsTerminalHubHook(entryObj))
            {
                indicesToRemove.Add(i);
                continue;
            }

            // 形式2: 入れ子形式 {"hooks": [{"type": "command"|"http", ...}]}
            if (entryObj["hooks"] is JsonArray entryHooks)
            {
                for (int j = 0; j < entryHooks.Count; j++)
                {
                    if (entryHooks[j] is JsonObject hookObj && IsTerminalHubHook(hookObj))
                    {
                        indicesToRemove.Add(i);
                        break;
                    }
                }
            }
        }

        // インデックスを降順でソートして削除（後ろから削除しないとインデックスがずれる）
        foreach (var index in indicesToRemove.OrderByDescending(i => i))
        {
            hookArray.RemoveAt(index);
        }
        return indicesToRemove.Count;
    }
}
