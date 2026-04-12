using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace TerminalHub.Services;

/// <summary>
/// Hook イベント設定
/// </summary>
public class HookEventSettings
{
    public bool Stop { get; set; } = true;
    public bool UserPromptSubmit { get; set; } = true;
    public bool Notification { get; set; } = true;
}

/// <summary>
/// Claude Code の hook 設定を管理するサービス
/// </summary>
public interface IClaudeHookService
{
    /// <summary>
    /// セッション用の hook 設定をセットアップする
    /// </summary>
    Task SetupHooksAsync(Guid sessionId, string folderPath, int port = 5081, HookEventSettings? eventSettings = null);
}

/// <summary>
/// ClaudeHookService の実装
/// </summary>
public class ClaudeHookService : IClaudeHookService
{
    private readonly ILogger<ClaudeHookService> _logger;
    private const string SettingsFileName = ".claude/settings.local.json";

    public ClaudeHookService(ILogger<ClaudeHookService> logger)
    {
        _logger = logger;
    }

    public async Task SetupHooksAsync(Guid sessionId, string folderPath, int port = 5081, HookEventSettings? eventSettings = null)
    {
        // デフォルト設定を使用
        eventSettings ??= new HookEventSettings();

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

            // 有効なイベントのみ hook を追加
            var enabledEvents = new List<string>();
            if (eventSettings.Stop) enabledEvents.Add("Stop");
            if (eventSettings.UserPromptSubmit) enabledEvents.Add("UserPromptSubmit");
            if (eventSettings.Notification) enabledEvents.Add("Notification");

            foreach (var eventName in enabledEvents)
            {
                var hookEntry = BuildHookEntry(sessionId, port);
                AddOrUpdateHook(hooks, eventName, hookEntry);
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

    /// <summary>
    /// Claude Code の type:"http" hook エントリを生成する
    /// 注: TerminalHub は現状 HTTP バインド前提 (Program.cs の UseHttpsRedirection はコメントアウト済み)。
    /// HTTPS バインドに変更する場合はこの URL スキームも合わせて変更する必要がある。
    /// </summary>
    private JsonObject BuildHookEntry(Guid sessionId, int port)
    {
        return new JsonObject
        {
            ["type"] = "http",
            ["url"] = $"http://localhost:{port}/api/hook/claude/{sessionId}",
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

    private void AddOrUpdateHook(JsonObject hooks, string eventName, JsonObject newHook)
    {
        // イベントの hook 配列を取得または作成
        if (hooks[eventName] is not JsonArray hookArray)
        {
            hookArray = new JsonArray();
            hooks[eventName] = hookArray;
        }

        // 既存の TerminalHub hook エントリをすべて探して削除
        // 両方の形式をチェック：直接形式と入れ子形式
        var indicesToRemove = new List<int>();
        for (int i = 0; i < hookArray.Count; i++)
        {
            if (hookArray[i] is JsonObject entryObj)
            {
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
        }

        // インデックスを降順でソートして削除（後ろから削除しないとインデックスがずれる）
        foreach (var index in indicesToRemove.OrderByDescending(i => i))
        {
            hookArray.RemoveAt(index);
        }

        // 入れ子形式で hook を追加（Claude Code の新仕様に準拠）
        // 形式: {"hooks": [{"type": "http", "url": "...", "timeout": 5}]}
        // matcher は省略可能（すべてのイベントにマッチ）
        var newHookEntry = new JsonObject
        {
            ["hooks"] = new JsonArray { newHook }
        };
        hookArray.Add(newHookEntry);

        _logger.LogDebug("Hook を追加: {EventName} -> {Url}", eventName, newHook["url"]?.GetValue<string>() ?? "");
    }
}
