using System.Text.Json.Serialization;

namespace TerminalHub.Models;

/// <summary>
/// Claude Code が HTTP hook で送信するネイティブ JSON ペイロード
/// 参考: https://code.claude.com/docs/en/hooks
/// cwd / prompt / message / stop_hook_active などを使う必要が出たら都度フィールドを追加する。
/// </summary>
public class ClaudeHookPayload
{
    /// <summary>イベント種類（Stop, UserPromptSubmit, Notification, SubagentStart, SubagentStop 等）</summary>
    [JsonPropertyName("hook_event_name")]
    public string? HookEventName { get; set; }

    /// <summary>
    /// サブエージェント固有 ID。公式ドキュメント記載の共通フィールドで、
    /// 「--agent 実行時 or サブエージェント内で hook が発火した時のみ」値が入る。
    /// SubagentStart と SubagentStop で同一値になるため、起動↔終了の突き合わせに使える。
    /// </summary>
    [JsonPropertyName("agent_id")]
    public string? AgentId { get; set; }

    /// <summary>サブエージェント種別（Explore など）。agent_id と同条件で入る。</summary>
    [JsonPropertyName("agent_type")]
    public string? AgentType { get; set; }

    /// <summary>
    /// Notification 等で通知メッセージ本文が入る（公式ドキュメント未記載のため実証で確認中）。
    /// 例: 許可ダイアログ時の "Claude needs your permission to use Bash" など。
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>PreToolUse 等で実行対象のツール名が入る（matcher で AskUserQuestion に絞って登録）。</summary>
    [JsonPropertyName("tool_name")]
    public string? ToolName { get; set; }
}
