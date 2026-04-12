using System.Text.Json.Serialization;

namespace TerminalHub.Models;

/// <summary>
/// Claude Code が HTTP hook で送信するネイティブ JSON ペイロード
/// 参考: https://code.claude.com/docs/en/hooks
/// 現時点ではイベント種別の判定にしか使わないため hook_event_name のみ受ける。
/// cwd / prompt / message / stop_hook_active などを使う必要が出たら都度フィールドを追加する。
/// </summary>
public class ClaudeHookPayload
{
    /// <summary>イベント種類（Stop, UserPromptSubmit, Notification 等）</summary>
    [JsonPropertyName("hook_event_name")]
    public string? HookEventName { get; set; }
}
