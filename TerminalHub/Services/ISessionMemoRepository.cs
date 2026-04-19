using TerminalHub.Models;

namespace TerminalHub.Services
{
    /// <summary>
    /// セッション紐づきメモ (BottomPanel の Memo タブ用) の永続化リポジトリ
    /// </summary>
    public interface ISessionMemoRepository
    {
        /// <summary>指定セッションの有効なメモ一覧を SortOrder, CreatedAt 昇順で取得 (論理削除は除外)</summary>
        Task<List<SessionMemo>> GetBySessionAsync(Guid sessionId);

        /// <summary>指定セッションの論理削除済みメモ一覧を DeletedAt 降順で取得</summary>
        Task<List<SessionMemo>> GetDeletedBySessionAsync(Guid sessionId);

        /// <summary>指定セッションにメモが1件でも存在するか (active/deleted 両方を対象)</summary>
        Task<bool> AnyExistsAsync(Guid sessionId);

        /// <summary>新規メモを挿入</summary>
        Task InsertAsync(SessionMemo memo);

        /// <summary>タイトルのみ更新 (UpdatedAt も更新)</summary>
        Task UpdateTitleAsync(Guid memoId, string title);

        /// <summary>本文のみ更新 (UpdatedAt も更新)</summary>
        Task UpdateBodyAsync(Guid memoId, string body);

        /// <summary>メモを論理削除する (IsDeleted=1, DeletedAt=now)。復元可能。</summary>
        Task DeleteAsync(Guid memoId);

        /// <summary>論理削除されたメモを復元する (IsDeleted=0, DeletedAt=null)</summary>
        Task RestoreAsync(Guid memoId);

        /// <summary>完全削除 (DB 行削除)。復元不可。</summary>
        Task HardDeleteAsync(Guid memoId);
    }
}
