namespace TerminalHub.Models;

/// <summary>
/// Webhook 送信時の入力データ。
/// ワイヤー上の JSON は AppSettingsService がこれに timestamp / elapsedMinutes を加えて組み立てる。
/// （送信元は「本来の hook イベントをそのまま流す」方針。開始/終了の解釈は受信側で行う）
/// </summary>
public class WebhookPayload
{
    /// <summary>発火した hook イベント名そのまま（UserPromptSubmit / Stop / SubagentStart / SubagentStop / PreCompact / PostCompact / Notification / PreToolUse）。</summary>
    public required string EventType { get; init; }

    /// <summary>セッションの生 GUID（プレフィックス無し）。</summary>
    public required Guid SessionId { get; init; }

    /// <summary>セッション表示名（サブエージェントは "{セッション名} / {agent_type}"）。</summary>
    public string SessionName { get; init; } = "";

    /// <summary>ターミナル種別（ClaudeCode 等）。</summary>
    public string TerminalType { get; init; } = "";

    /// <summary>処理秒。Stop のみ値が入り、それ以外は null。</summary>
    public int? ElapsedSeconds { get; init; }

    /// <summary>セッションの作業ディレクトリ。</summary>
    public string FolderPath { get; init; } = "";

    /// <summary>送信元 CLI 名（"ClaudeCode" 等）。未指定なら null。</summary>
    public string? Tool { get; init; }

    /// <summary>サブエージェント由来のときの agent_id。キーの振り分けは受信側に委ねる。</summary>
    public string? AgentId { get; init; }

    /// <summary>Notification 本文（許可待ち/idle の判別用）。</summary>
    public string? Message { get; init; }

    /// <summary>PreToolUse の対象ツール名（AskUserQuestion 等）。</summary>
    public string? ToolName { get; init; }
}
