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

    /// <summary>
    /// TerminalHub.exe のパスを取得する
    /// </summary>
    string GetExecutablePath();
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

    public string GetExecutablePath()
    {
        // 現在の実行ファイルのパスを取得
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            // .dll の場合は .exe に変換
            if (exePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                exePath = Path.ChangeExtension(exePath, ".exe");
            }
        }
        return exePath;
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

            var exePath = GetExecutablePath();

            // 有効なイベントのみ hook を追加
            var enabledEvents = new List<string>();
            if (eventSettings.Stop) enabledEvents.Add("Stop");
            if (eventSettings.UserPromptSubmit) enabledEvents.Add("UserPromptSubmit");
            if (eventSettings.Notification) enabledEvents.Add("Notification");

            foreach (var eventName in enabledEvents)
            {
                var hookCommand = BuildHookCommand(exePath, eventName, sessionId, port);
                AddOrUpdateHook(hooks, eventName, hookCommand);
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

    private string BuildHookCommand(string exePath, string eventName, Guid sessionId, int port)
    {
        // Windows パスのエスケープ（JSON 内でバックスラッシュをエスケープ）
        var escapedPath = exePath.Replace("\\", "/");
        return $"\"{escapedPath}\" --notify --event {eventName} --session {sessionId} --port {port}";
    }

    private void AddOrUpdateHook(JsonObject hooks, string eventName, string command)
    {
        // イベントの hook 配列を取得または作成
        if (hooks[eventName] is not JsonArray hookArray)
        {
            hookArray = new JsonArray();
            hooks[eventName] = hookArray;
        }

        // 既存の TerminalHub hook エントリを探す
        int terminalHubEntryIndex = -1;
        for (int i = 0; i < hookArray.Count; i++)
        {
            if (hookArray[i] is JsonObject entryObj && entryObj["hooks"] is JsonArray entryHooks)
            {
                // hooks 配列内に TerminalHub のコマンドがあるか確認
                for (int j = 0; j < entryHooks.Count; j++)
                {
                    if (entryHooks[j] is JsonObject hookObj)
                    {
                        var cmd = hookObj["command"]?.GetValue<string>() ?? "";
                        if (cmd.Contains("--notify") && cmd.Contains("--session"))
                        {
                            terminalHubEntryIndex = i;
                            break;
                        }
                    }
                }
            }
            if (terminalHubEntryIndex >= 0) break;
        }

        // 既存の TerminalHub エントリを削除
        if (terminalHubEntryIndex >= 0)
        {
            hookArray.RemoveAt(terminalHubEntryIndex);
        }

        // 新しいフォーマットで hook を追加
        // 形式: {"hooks": [{"type": "command", "command": "..."}]}
        // matcher フィールドは省略可能（マッチャーを使用しないイベントの場合）
        var newHookEntry = new JsonObject
        {
            ["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command
                }
            }
        };
        hookArray.Add(newHookEntry);

        _logger.LogDebug("Hook を追加: {EventName} -> {Command}", eventName, command);
    }
}
