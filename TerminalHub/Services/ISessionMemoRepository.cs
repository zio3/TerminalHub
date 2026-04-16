using TerminalHub.Models;

namespace TerminalHub.Services
{
    /// <summary>
    /// セッション紐づきメモ (BottomPanel の Memo タブ用) の永続化リポジトリ
    /// </summary>
    public interface ISessionMemoRepository
    {
        /// <summary>指定セッションのメモ一覧を SortOrder, CreatedAt 昇順で取得</summary>
        Task<List<SessionMemo>> GetBySessionAsync(Guid sessionId);

        /// <summary>新規メモを挿入</summary>
        Task InsertAsync(SessionMemo memo);

        /// <summary>タイトルのみ更新 (UpdatedAt も更新)</summary>
        Task UpdateTitleAsync(Guid memoId, string title);

        /// <summary>本文のみ更新 (UpdatedAt も更新)</summary>
        Task UpdateBodyAsync(Guid memoId, string body);

        /// <summary>個別メモを削除</summary>
        Task DeleteAsync(Guid memoId);
    }
}
