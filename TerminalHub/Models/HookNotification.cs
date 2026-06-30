namespace TerminalHub.Models;

/// <summary>
/// Claude Code hook からの通知イベント種類
/// </summary>
public enum HookEventType
{
    /// <summary>処理完了（メインエージェント）</summary>
    Stop,
    /// <summary>ユーザーがプロンプトを送信</summary>
    UserPromptSubmit,
    /// <summary>通知発生</summary>
    Notification,
    /// <summary>サブエージェント起動</summary>
    SubagentStart,
    /// <summary>サブエージェント終了</summary>
    SubagentStop,
    /// <summary>コンテキスト compact 実行直前（作業中入り）</summary>
    PreCompact,
    /// <summary>コンテキスト compact 完了後（作業可能に復帰）</summary>
    PostCompact,
    /// <summary>ツール実行直前（AskUserQuestion に絞って登録＝ユーザーへの質問＝回答待ち）</summary>
    PreToolUse,
    /// <summary>許可要求（Codex の PermissionRequest＝ツール実行の承認待ち。Claude の Notification(permission) 相当）</summary>
    PermissionRequest
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

    /// <summary>
    /// サブエージェント固有 ID（Claude Code の agent_id）。
    /// SubagentStart / SubagentStop など、サブエージェント内で発火した hook のみ値が入る。
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>サブエージェント種別（Claude Code の agent_type、例: Explore）</summary>
    public string? AgentType { get; set; }

    /// <summary>通知メッセージ本文（Notification 等の message。許可待ち内容の判別用）</summary>
    public string? Message { get; set; }

    /// <summary>ツール名（PreToolUse 等の tool_name。AskUserQuestion 判別用）</summary>
    public string? ToolName { get; set; }

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
            "subagentstart" => HookEventType.SubagentStart,
            "subagentstop" => HookEventType.SubagentStop,
            "precompact" => HookEventType.PreCompact,
            "postcompact" => HookEventType.PostCompact,
            "pretooluse" => HookEventType.PreToolUse,
            "permissionrequest" => HookEventType.PermissionRequest,
            _ => null
        };
    }
}
