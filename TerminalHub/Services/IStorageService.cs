using TerminalHub.Models;

namespace TerminalHub.Services
{
    /// <summary>
    /// ストレージタイプを示す列挙型
    /// </summary>
    public enum StorageType
    {
        LocalStorage,
        SQLite
    }

    /// <summary>
    /// セッションストレージの統一インターフェース
    /// LocalStorageとSQLiteの両方に対応
    /// </summary>
    public interface IStorageService
    {
        /// <summary>
        /// 現在使用中のストレージタイプ
        /// </summary>
        StorageType CurrentStorageType { get; }

        /// <summary>
        /// セッション一覧を保存
        /// </summary>
        Task SaveSessionsAsync(IEnumerable<SessionInfo> sessions);

        /// <summary>
        /// セッション一覧を読み込み
        /// </summary>
        Task<List<SessionInfo>> LoadSessionsAsync();

        /// <summary>
        /// 単一セッションを保存（部分更新対応）
        /// </summary>
        Task SaveSessionAsync(SessionInfo session);

        /// <summary>
        /// LastAccessedAtのみ更新（軽量更新）
        /// </summary>
        Task UpdateLastAccessedAtAsync(Guid sessionId, DateTime time);

        /// <summary>
        /// Memoのみ更新（軽量更新）
        /// </summary>
        Task UpdateMemoAsync(Guid sessionId, string memo);

        /// <summary>
        /// アーカイブ状態を更新（軽量更新）
        /// </summary>
        Task UpdateArchivedStateAsync(Guid sessionId, bool isArchived, DateTime? archivedAt);

        /// <summary>
        /// アクティブセッションIDを保存
        /// </summary>
        Task SaveActiveSessionIdAsync(Guid? sessionId);

        /// <summary>
        /// アクティブセッションIDを読み込み
        /// </summary>
        Task<Guid?> LoadActiveSessionIdAsync();

        /// <summary>
        /// セッションの展開状態を保存
        /// </summary>
        Task SaveSessionExpandedStatesAsync(Dictionary<Guid, bool> expandedStates);

        /// <summary>
        /// セッションの展開状態を読み込み
        /// </summary>
        Task<Dictionary<Guid, bool>> LoadSessionExpandedStatesAsync();

        /// <summary>
        /// 汎用的なキー/値保存
        /// </summary>
        Task<T?> GetAsync<T>(string key);

        /// <summary>
        /// 汎用的なキー/値読み込み
        /// </summary>
        Task SetAsync<T>(string key, T value);

        /// <summary>
        /// すべてのデータをクリア
        /// </summary>
        Task ClearAsync();
    }

    /// <summary>
    /// ストレージサービスファクトリ
    /// 現在のストレージ状態を判定し、適切なサービスを提供
    /// </summary>
    public interface IStorageServiceFactory
    {
        /// <summary>
        /// SQLiteデータベースが存在するか
        /// </summary>
        bool SqliteDatabaseExists { get; }

        /// <summary>
        /// SQLite用のストレージサービスを取得
        /// </summary>
        IStorageService GetSqliteStorageService();

        /// <summary>
        /// LocalStorage用のストレージサービスを取得
        /// </summary>
        IStorageService GetLocalStorageService();

        /// <summary>
        /// 現在推奨されるストレージサービスを取得（同期版、ファイル存在チェックのみ）
        /// </summary>
        IStorageService GetCurrentStorageService();

        /// <summary>
        /// 現在推奨されるストレージサービスを取得（非同期版、セッション数ベース）
        /// SQLiteにセッションがあればSQLite、なければLocalStorage
        /// </summary>
        Task<IStorageService> GetCurrentStorageServiceAsync();

        /// <summary>
        /// LocalStorageからSQLiteへ移行
        /// </summary>
        Task<bool> MigrateToSqliteAsync(List<SessionInfo> sessions, Guid? activeSessionId, Dictionary<Guid, bool> expandedStates);

        /// <summary>
        /// セッション数を取得（SQLiteの場合はDBから、そうでなければ0）
        /// </summary>
        Task<int> GetSqliteSessionCountAsync();
    }
}
