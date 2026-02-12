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
/// </summary>
public class ClaudeHookSettings
{
    public bool Enabled { get; set; } = true;
    public ClaudeHookEvents Events { get; set; } = new();
}

/// <summary>
/// Claude Code Hookイベント設定
/// </summary>
public class ClaudeHookEvents
{
    public bool Stop { get; set; } = true;
    public bool UserPromptSubmit { get; set; } = true;
    public bool Notification { get; set; } = true;
}

/// <summary>
/// 特殊設定
/// </summary>
public class SpecialSettings
{
    public string ClaudeModeSwitchKey { get; set; } = "altM";
}

/// <summary>
/// セッション表示設定
/// </summary>
public class SessionDisplaySettings
{
    public string SortMode { get; set; } = "createdAt";
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
    public double SessionListScale { get; set; } = 1.0;
    public int TerminalFontSize { get; set; } = 14;
    public int TerminalHeightPercent { get; set; } = 70;
}

/// <summary>
/// Gemini設定
/// </summary>
public class GeminiSettings
{
    public List<string> Models { get; set; } = new();
}
