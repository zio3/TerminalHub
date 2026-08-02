namespace TerminalHub.Services
{
    /// <summary>
    /// ContextSummary の完了通知の要否判定（純ロジック・テスト用に分離）。
    ///
    /// 通知の規則（2026-08-02 確定）:
    /// - update_context 経路: **終端 status の書き込みが成功したら毎回**通知する
    ///   （遷移だけでなく completed → completed の再書き込み＝「再完了」の続報も含む。
    ///   遷移縛りだと、完了後に人間が受け手セッションで追加調整をさせて再完了を書いたとき
    ///   依頼元に伝わらない実害があった）。
    /// - ただし**依頼元自身の書き込みは通知しない**。通知文面が「必要なことは update_context に
    ///   書いて終えてください」と促すため、遷移ガードを外した状態で自己書き込みにも通知すると
    ///   「自分へ再通知 → また書く → また通知」の自己励振ループが成立してしまう（レビュー指摘）。
    /// - working 等の非終端は通知しない（「着手した」を聞くためだけに依頼元をフルターン起こさない）。
    /// - システム経路（配送失敗の failed 化）はこのクラスを使わず従来どおり遷移時のみ
    ///   （機械的な失敗記録の重複で依頼元を起こす価値がない）。
    /// </summary>
    public static class ContextNotifyPolicy
    {
        /// <summary>依頼が閉じた状態（A2A TaskState の終端）。</summary>
        public static readonly string[] TerminalStatuses = { "completed", "failed", "canceled" };

        public static bool IsTerminal(string? status) =>
            status != null && TerminalStatuses.Contains(status, StringComparer.Ordinal);

        /// <summary>
        /// 依頼元へ通知してよいか。writerSessionId が依頼元と同一なら false（自己再通知ループ防止）。
        /// writerSessionId=null（無記名・システム記名）は抑止しない: 依頼元セッション自身の書き込みは
        /// 接続キーで必ず記名されるため、無記名がループの起点になることはない。
        /// </summary>
        public static bool ShouldNotifyRequester(string? requesterSessionId, Guid? writerSessionId)
        {
            if (!writerSessionId.HasValue)
                return true;
            return !string.Equals(
                requesterSessionId, writerSessionId.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
