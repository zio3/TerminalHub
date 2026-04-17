namespace TerminalHub.Models;

/// <summary>
/// アプリケーション全体の設定（ファイルベースで永続化）
/// </summary>
public class AppSettings
{
    public NotificationSettings Notifications { get; set; } = new();
    public WebhookSettings Webhook { get; set; } = new();
    public ClaudeHookSettings ClaudeHook { get; set; } = new();
    public SpecialSettings Special { get; set; } = new();
    public SessionDisplaySettings Sessions { get; set; } = new();
    public DevToolsSettings DevTools { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
    public GeminiSettings Gemini { get; set; } = new();
    public CustomCommandSettings Commands { get; set; } = new();
    public RemoteLaunchSettings RemoteLaunch { get; set; } = new();
}

/// <summary>
/// ブラウザ通知設定
/// </summary>
public class NotificationSettings
{
    public bool EnableBrowserNotifications { get; set; } = true;
    public int ProcessingTimeThresholdSeconds { get; set; } = 5;
}

/// <summary>
/// Webhook通知設定
/// </summary>
public class WebhookSettings
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public Dictionary<string, string>? Headers { get; set; }
}

/// <summary>
/// Claude Code Hook設定
/// 個別イベントのトグルは実装していない (Stop / UserPromptSubmit / Notification は常に一括で有効化/削除する)。
/// </summary>
public class ClaudeHookSettings
{
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// 特殊設定
/// </summary>
public class SpecialSettings
{
    public string ClaudeModeSwitchKey { get; set; } = "none";
    public bool VoiceInputEnabled { get; set; } = false;
    /// <summary>
    /// テキスト事前整形（実験的）を有効にするかどうか。
    /// 有効時、テキスト入力パネルに「事前整形」ボタンが表示され、Claude Code CLI の
    /// -p モードで入力テキストを整形してから置換する。
    /// </summary>
    public bool TextRefineEnabled { get; set; } = false;
}

/// <summary>
/// セッション表示設定
/// </summary>
public class SessionDisplaySettings
{
    public string SortMode { get; set; } = "lastAccessed";
    public bool ShowTerminalType { get; set; }
    public bool ShowGitInfo { get; set; }
    public bool HideInputPanel { get; set; }
}

/// <summary>
/// 開発ツール設定
/// </summary>
public class DevToolsSettings
{
    public bool Enabled { get; set; }
}

/// <summary>
/// 一般設定
/// </summary>
public class GeneralSettings
{
    public string DefaultFolderPath { get; set; } = "";
    public List<string> FavoriteFolders { get; set; } = new();
    public double SessionListScale { get; set; } = 1.0;
    public int TerminalFontSize { get; set; } = 14;
    public int TerminalHeightPercent { get; set; } = 70;
    public int SidebarWidthPercent { get; set; } = 25;
    public string Theme { get; set; } = "dark";
}

/// <summary>
/// Gemini設定
/// </summary>
public class GeminiSettings
{
    public List<string> Models { get; set; } = new();
}

/// <summary>
/// カスタムコマンド設定
/// </summary>
public class CustomCommandSettings
{
    public Dictionary<string, List<CustomCommand>> CommandsByTerminalType { get; set; } = new();
}

/// <summary>
/// カスタムコマンドの種別
/// </summary>
public enum CustomCommandType
{
    /// <summary>テキスト送信（末尾に Enter を自動付与）</summary>
    Text = 0,
    /// <summary>プリセットキーのエスケープシーケンス送信（Enter は付与しない）</summary>
    KeySequence = 1
}

/// <summary>
/// カスタムコマンド
/// </summary>
public class CustomCommand
{
    public string? Title { get; set; }
    public string CommandText { get; set; } = "";
    public CustomCommandType Type { get; set; } = CustomCommandType.Text;
    /// <summary>KeySequence 時に参照するプリセットキー名（KeySequencePresets.All のキー）</summary>
    public string? KeyName { get; set; }
}

/// <summary>
/// リモート起動設定
/// </summary>
public class RemoteLaunchSettings
{
    public bool Enabled { get; set; }
    public string TopicGuid { get; set; } = "";
    public string? PasswordHash { get; set; }
}
