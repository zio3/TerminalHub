using Microsoft.Extensions.Logging;
using TerminalHub.Models;

namespace TerminalHub.Services
{
    /// <summary>
    /// 既存のLocalStorageServiceをIStorageServiceインターフェースに適合させるアダプター
    /// </summary>
    public class LocalStorageServiceAdapter : IStorageService
    {
        private readonly ILocalStorageService _localStorageService;
        private readonly ILogger<LocalStorageServiceAdapter> _logger;

        // キャッシュ（LocalStorage操作を減らすため）
        private List<SessionInfo>? _cachedSessions;

        public LocalStorageServiceAdapter(ILocalStorageService localStorageService, ILogger<LocalStorageServiceAdapter> logger)
        {
            _localStorageService = localStorageService;
            _logger = logger;
        }

        public StorageType CurrentStorageType => StorageType.LocalStorage;

        public async Task SaveSessionsAsync(IEnumerable<SessionInfo> sessions)
        {
            var sessionList = sessions.ToList();
            await _localStorageService.SaveSessionsAsync(sessionList);
            _cachedSessions = sessionList;
        }

        public async Task<List<SessionInfo>> LoadSessionsAsync()
        {
            _cachedSessions = await _localStorageService.LoadSessionsAsync();
            return _cachedSessions;
        }

        public async Task SaveSessionAsync(SessionInfo session)
        {
            // LocalStorageでは単一セッション保存はできないので、全体を更新
            // キャッシュがない場合は読み込み
            if (_cachedSessions == null)
            {
                _cachedSessions = await _localStorageService.LoadSessionsAsync();
            }

            // 既存のセッションを更新または追加
            var existingIndex = _cachedSessions.FindIndex(s => s.SessionId == session.SessionId);
            if (existingIndex >= 0)
            {
                _cachedSessions[existingIndex] = session;
            }
            else
            {
                _cachedSessions.Add(session);
            }

            await _localStorageService.SaveSessionsAsync(_cachedSessions);
        }

        public async Task UpdateLastAccessedAtAsync(Guid sessionId, DateTime time)
        {
            // LocalStorageでは部分更新ができないので、全体を更新
            if (_cachedSessions == null)
            {
                _cachedSessions = await _localStorageService.LoadSessionsAsync();
            }

            var session = _cachedSessions.FirstOrDefault(s => s.SessionId == sessionId);
            if (session != null)
            {
                session.LastAccessedAt = time;
                await _localStorageService.SaveSessionsAsync(_cachedSessions);
            }
        }

        public async Task UpdateMemoAsync(Guid sessionId, string memo)
        {
            // LocalStorageでは部分更新ができないので、全体を更新
            if (_cachedSessions == null)
            {
                _cachedSessions = await _localStorageService.LoadSessionsAsync();
            }

            var session = _cachedSessions.FirstOrDefault(s => s.SessionId == sessionId);
            if (session != null)
            {
                session.Memo = memo;
                await _localStorageService.SaveSessionsAsync(_cachedSessions);
            }
        }

        public async Task UpdateArchivedStateAsync(Guid sessionId, bool isArchived, DateTime? archivedAt)
        {
            // LocalStorageでは部分更新ができないので、全体を更新
            if (_cachedSessions == null)
            {
                _cachedSessions = await _localStorageService.LoadSessionsAsync();
            }

            var session = _cachedSessions.FirstOrDefault(s => s.SessionId == sessionId);
            if (session != null)
            {
                session.IsArchived = isArchived;
                session.ArchivedAt = archivedAt;
                await _localStorageService.SaveSessionsAsync(_cachedSessions);
            }
        }

        public async Task SaveActiveSessionIdAsync(Guid? sessionId)
        {
            await _localStorageService.SaveActiveSessionIdAsync(sessionId);
        }

        public async Task<Guid?> LoadActiveSessionIdAsync()
        {
            return await _localStorageService.LoadActiveSessionIdAsync();
        }

        public async Task SaveSessionExpandedStatesAsync(Dictionary<Guid, bool> expandedStates)
        {
            await _localStorageService.SaveSessionExpandedStatesAsync(expandedStates);
        }

        public async Task<Dictionary<Guid, bool>> LoadSessionExpandedStatesAsync()
        {
            return await _localStorageService.LoadSessionExpandedStatesAsync();
        }

        public async Task<T?> GetAsync<T>(string key)
        {
            return await _localStorageService.GetAsync<T>(key);
        }

        public async Task SetAsync<T>(string key, T value)
        {
            await _localStorageService.SetAsync(key, value);
        }

        public async Task ClearAsync()
        {
            await _localStorageService.ClearAsync();
            _cachedSessions = null;
        }
    }
}
