namespace TerminalHub.Models
{
    /// <summary>
    /// セッションに紐づくメモ (BottomPanel の Memo タブ用)。
    /// 注釈用の <see cref="SessionInfo.Memo"/> とは別物で、長文・複数件を想定。
    /// </summary>
    public class SessionMemo
    {
        public Guid MemoId { get; set; } = Guid.NewGuid();
        public Guid SessionId { get; set; }
        public string Title { get; set; } = "メモ";
        public string Body { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public int SortOrder { get; set; } = 0;

        /// <summary>論理削除フラグ (v5)</summary>
        public bool IsDeleted { get; set; }

        /// <summary>論理削除日時 (v5, IsDeleted=false のときは null)</summary>
        public DateTime? DeletedAt { get; set; }
    }
}
