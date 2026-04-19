using TerminalHub.Models;

namespace TerminalHub.Services
{
    /// <summary>
    /// メモ編集履歴 (スナップショット) の永続化リポジトリ
    /// </summary>
    public interface ISessionMemoSnapshotRepository
    {
        /// <summary>スナップショットを 1 件挿入</summary>
        Task InsertAsync(SessionMemoSnapshot snapshot);

        /// <summary>指定メモの最新スナップショット (変更検知用)</summary>
        Task<SessionMemoSnapshot?> GetLatestAsync(Guid memoId);

        /// <summary>指定メモのスナップショット一覧を SavedAt 降順で取得</summary>
        Task<List<SessionMemoSnapshot>> GetByMemoAsync(Guid memoId);

        /// <summary>
        /// Trigger="auto" のスナップショットが <paramref name="maxAutoKeep"/> 件を超えたら
        /// 古い順に物理削除する。pre-delete は削除対象外 (復元の最終防衛線を保護)。
        /// </summary>
        Task TrimAutoSnapshotsAsync(Guid memoId, int maxAutoKeep);
    }
}
