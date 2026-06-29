using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace TerminalHub.Services;

/// <summary>
/// Claude Code の hook 設定を管理するサービス
/// </summary>
public interface IClaudeHookService
{
    /// <summary>
    /// セッション用の hook 設定をセットアップする
    /// (Stop / UserPromptSubmit / Notification / SubagentStart / SubagentStop /
    ///  PreCompact / PostCompact / PreToolUse(matcher=AskUserQuestion) の各イベントを一括登録。
    ///  実際の登録内容は HookRegistrations を参照)
    /// </summary>
    /// <param name="baseUrl">TerminalHub サーバーのベース URL（例: http://localhost:5081）。
    /// hook の送信先 URL に使われる</param>
    Task SetupHooksAsync(Guid sessionId, string folderPath, string baseUrl);

    /// <summary>
    /// 既存の TerminalHub 由来の hook エントリを .claude/settings.local.json から削除する
    /// (Hook 設定を無効化したときのクリーンアップ用途)
    /// </summary>
    Task RemoveHooksAsync(string folderPath);
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

    // クリーンアップ対象のイベント名（重複排除）
    private static readonly string[] HookEventNames =
        HookRegistrations.Select(r => r.Event).Distinct().ToArray();

    public ClaudeHookService(ILogger<ClaudeHookService> logger)
    {
        _logger = logger;
    }

    public async Task SetupHooksAsync(Guid sessionId, string folderPath, string baseUrl)
    {
        try
        {
            var settingsPath = Path.Combine(folderPath, SettingsFileName);
            var settingsDir = Path.GetDirectoryName(settingsPath);

            // .claude ディレクトリがなければ作成
            if (!string.IsNullOrEmpty(settingsDir) && !Directory.Exists(settingsDir))
            {
                Directory.CreateDirectory(settingsDir);
                _logger.LogInformation(".claude ディレクトリを作成: {Path}", settingsDir);
            }

            // 既存の設定を読み込む
            JsonObject settings;
            if (File.Exists(settingsPath))
            {
                var existingJson = await File.ReadAllTextAsync(settingsPath);
                settings = JsonNode.Parse(existingJson)?.AsObject() ?? new JsonObject();
                _logger.LogInformation("既存の設定ファイルを読み込み: {Path}", settingsPath);
            }
            else
            {
                settings = new JsonObject();
            }

            // hooks オブジェクトを取得または作成
            if (settings["hooks"] is not JsonObject hooks)
            {
                hooks = new JsonObject();
                settings["hooks"] = hooks;
            }

            foreach (var (eventName, matcher) in HookRegistrations)
            {
                var hookEntry = BuildHookEntry(sessionId, baseUrl);
                AddOrUpdateHook(hooks, eventName, hookEntry, matcher);
            }

            // 設定を保存
            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };
            var json = settings.ToJsonString(options);
            await File.WriteAllTextAsync(settingsPath, json);

            _logger.LogInformation("Hook 設定を保存: {Path}", settingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hook 設定のセットアップに失敗: {FolderPath}", folderPath);
            throw;
        }
    }

    public async Task RemoveHooksAsync(string folderPath)
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
            _logger.LogInformation("TerminalHub 由来の hook を削除: {Path}", settingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hook 削除に失敗: {FolderPath}", folderPath);
            throw;
        }
    }

    /// <summary>
    /// Claude Code の type:"http" hook エントリを生成する
    /// baseUrl は呼び出し元が IServerAddressesFeature から取得した実際のバインド URL（HTTP 優先）
    /// </summary>
    private JsonObject BuildHookEntry(Guid sessionId, string baseUrl)
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

    private void AddOrUpdateHook(JsonObject hooks, string eventName, JsonObject newHook, string? matcher = null)
    {
        // イベントの hook 配列を取得または作成
        if (hooks[eventName] is not JsonArray hookArray)
        {
            hookArray = new JsonArray();
            hooks[eventName] = hookArray;
        }

        RemoveTerminalHubHooksFromArray(hookArray);

        // 入れ子形式で hook を追加（Claude Code の新仕様に準拠）
        // 形式: {"matcher": "AskUserQuestion", "hooks": [{"type": "http", "url": "...", "timeout": 5}]}
        // matcher は省略可能（省略時はすべてにマッチ）。指定時は対象ツール名等で絞り込む。
        var newHookEntry = new JsonObject();
        if (!string.IsNullOrEmpty(matcher))
        {
            newHookEntry["matcher"] = matcher;
        }
        newHookEntry["hooks"] = new JsonArray { newHook };
        hookArray.Add(newHookEntry);

        _logger.LogDebug("Hook を追加: {EventName}{Matcher} -> {Url}",
            eventName,
            string.IsNullOrEmpty(matcher) ? "" : $"(matcher={matcher})",
            newHook["url"]?.GetValue<string>() ?? "");
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
