namespace TerminalHub.Models
{
    /// <summary>
    /// メモ編集の履歴スナップショット (v7 以降)。
    /// 10 分毎の自動保存 (Trigger="auto") と、論理削除直前の保険 (Trigger="pre-delete") を記録する。
    /// </summary>
    public class SessionMemoSnapshot
    {
        public Guid SnapshotId { get; set; } = Guid.NewGuid();
        public Guid MemoId { get; set; }
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public DateTime SavedAt { get; set; } = DateTime.Now;

        /// <summary>スナップショット生成のトリガー ("auto" | "pre-delete")。</summary>
        public string Trigger { get; set; } = TriggerAuto;

        public const string TriggerAuto = "auto";
        public const string TriggerPreDelete = "pre-delete";
    }
}
