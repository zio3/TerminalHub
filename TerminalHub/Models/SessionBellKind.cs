namespace TerminalHub.Models;

/// <summary>
/// セッションリストに表示する通知ベルの種類。
/// 色は共通（赤）で、状態の違いはアイコンの形で区別する。
/// </summary>
public enum SessionBellKind
{
    /// <summary>停止: ターンが終わって次の入力待ちで止まっている（bi-bell-fill）</summary>
    Stopped,

    /// <summary>確認: 許可待ち（Notification / permission_prompt）。「実行していい?」（bi-question-octagon）</summary>
    Confirm,

    /// <summary>選択: 質問（PreToolUse / AskUserQuestion）。3択など選んでほしい（bi-list-ol）</summary>
    Select
}
