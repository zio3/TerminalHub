using TerminalHub.Models;

namespace TerminalHub.Services
{
    /// <summary>
    /// セッションデータの永続化を担当するリポジトリインターフェース
    /// </summary>
    public interface ISessionRepository
    {
        // セッション操作
        Task<List<SessionInfo>> GetAllSessionsAsync();
        Task<SessionInfo?> GetSessionAsync(Guid sessionId);
        Task SaveSessionAsync(SessionInfo session);
        Task UpdateLastAccessedAtAsync(Guid sessionId, DateTime time);
        Task UpdateMemoAsync(Guid sessionId, string memo);
        Task UpdateArchivedStateAsync(Guid sessionId, bool isArchived, DateTime? archivedAt);
        Task DeleteSessionAsync(Guid sessionId);
        Task<int> GetSessionCountAsync();

        // アプリ状態
        Task<Guid?> GetActiveSessionIdAsync();
        Task SetActiveSessionIdAsync(Guid? sessionId);
        Task<Dictionary<Guid, bool>> GetExpandedStatesAsync();
        Task SetExpandedStatesAsync(Dictionary<Guid, bool> states);

        // マイグレーション
        Task MigrateFromLocalStorageAsync(List<SessionInfo> sessions, Guid? activeSessionId, Dictionary<Guid, bool> expandedStates);

        // 入力履歴
        Task<List<string>> GetInputHistoryAsync(int limit = 100);
        Task AddInputHistoryAsync(string text);
        Task ClearInputHistoryAsync();

        // データベース状態
        bool DatabaseExists();
        Task InitializeAsync();
    }
}
