using System.Text.Json.Serialization;

namespace TerminalHub.Models;

/// <summary>
/// Claude Code が HTTP hook で送信するネイティブ JSON ペイロード
/// 参考: https://code.claude.com/docs/en/hooks
/// </summary>
public class ClaudeHookPayload
{
    /// <summary>イベント種類（Stop, UserPromptSubmit, Notification 等）</summary>
    [JsonPropertyName("hook_event_name")]
    public string? HookEventName { get; set; }

    /// <summary>Claude Code 側のセッションID（TerminalHub のセッションIDではない）</summary>
    [JsonPropertyName("session_id")]
    public string? ClaudeSessionId { get; set; }

    /// <summary>作業ディレクトリ</summary>
    [JsonPropertyName("cwd")]
    public string? Cwd { get; set; }

    /// <summary>トランスクリプトファイルパス</summary>
    [JsonPropertyName("transcript_path")]
    public string? TranscriptPath { get; set; }

    /// <summary>UserPromptSubmit 時に送られるユーザー入力</summary>
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    /// <summary>Notification 時に送られる通知メッセージ</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>Stop 時に送られる、stop hook 実行中フラグ</summary>
    [JsonPropertyName("stop_hook_active")]
    public bool? StopHookActive { get; set; }
}
