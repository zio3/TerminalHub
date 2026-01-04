namespace TerminalHub.Models;

/// <summary>
/// Claude Code hook からの通知イベント種類
/// </summary>
public enum HookEventType
{
    /// <summary>処理完了</summary>
    Stop,
    /// <summary>ユーザーがプロンプトを送信</summary>
    UserPromptSubmit,
    /// <summary>通知発生</summary>
    Notification
}

/// <summary>
/// Claude Code hook からの通知リクエスト
/// </summary>
public class HookNotification
{
    /// <summary>イベント種類</summary>
    public string Event { get; set; } = "";

    /// <summary>セッションID</summary>
    public Guid SessionId { get; set; }

    /// <summary>タイムスタンプ</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>イベント種類を列挙型として取得</summary>
    public HookEventType? GetEventType()
    {
        return Event?.ToLowerInvariant() switch
        {
            "stop" => HookEventType.Stop,
            "userpromptsubmit" => HookEventType.UserPromptSubmit,
            "notification" => HookEventType.Notification,
            _ => null
        };
    }
}
