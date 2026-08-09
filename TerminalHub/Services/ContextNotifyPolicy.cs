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
    /// - ただし**依頼元自身の書き込みは通知しない**。遷移ガードを外した状態で自己書き込みにも
    ///   通知すると「自分へ再通知 → また書く → また通知」の自己励振ループが成立してしまう
    ///   （レビュー指摘）。かつては通知文面自体が「必要なことは update_context に書いて終えて
    ///   ください」とループを誘発していた。文面は報告のみに改めた（2026-08-08）が、依頼元も
    ///   contextId を知るキー付きセッションである以上いつでも札へ書けるので、防波堤は維持する。
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
        /// writerSessionId=null は抑止しない: update_context 経由の書き込みは接続キーで必ず記名される
        /// （2026-08-08 以降、無記名接続からの書き込みはツール層で拒否）ため、null が来るのは
        /// システム経路（配送失敗の failed 化）だけ＝依頼元自身がループの起点にはならない。
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
