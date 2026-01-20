using Microsoft.Extensions.Logging;
using TerminalHub.Models;

namespace TerminalHub.Services
{
    /// <summary>
    /// SQLite を使用したストレージサービス実装
    /// </summary>
    public class SqliteStorageService : IStorageService
    {
        private readonly ISessionRepository _repository;
        private readonly ILogger<SqliteStorageService> _logger;

        public SqliteStorageService(ISessionRepository repository, ILogger<SqliteStorageService> logger)
        {
            _repository = repository;
            _logger = logger;
        }

        public StorageType CurrentStorageType => StorageType.SQLite;

        public async Task SaveSessionsAsync(IEnumerable<SessionInfo> sessions)
        {
            foreach (var session in sessions)
            {
                await _repository.SaveSessionAsync(session);
            }
        }

        public async Task<List<SessionInfo>> LoadSessionsAsync()
        {
            return await _repository.GetAllSessionsAsync();
        }

        public async Task SaveSessionAsync(SessionInfo session)
        {
            await _repository.SaveSessionAsync(session);
        }

        public async Task UpdateLastAccessedAtAsync(Guid sessionId, DateTime time)
        {
            await _repository.UpdateLastAccessedAtAsync(sessionId, time);
        }

        public async Task UpdateMemoAsync(Guid sessionId, string memo)
        {
            await _repository.UpdateMemoAsync(sessionId, memo);
        }

        public async Task UpdateArchivedStateAsync(Guid sessionId, bool isArchived, DateTime? archivedAt)
        {
            await _repository.UpdateArchivedStateAsync(sessionId, isArchived, archivedAt);
        }

        public async Task SaveActiveSessionIdAsync(Guid? sessionId)
        {
            await _repository.SetActiveSessionIdAsync(sessionId);
        }

        public async Task<Guid?> LoadActiveSessionIdAsync()
        {
            return await _repository.GetActiveSessionIdAsync();
        }

        public async Task SaveSessionExpandedStatesAsync(Dictionary<Guid, bool> expandedStates)
        {
            await _repository.SetExpandedStatesAsync(expandedStates);
        }

        public async Task<Dictionary<Guid, bool>> LoadSessionExpandedStatesAsync()
        {
            return await _repository.GetExpandedStatesAsync();
        }

        public Task<T?> GetAsync<T>(string key)
        {
            // SQLiteでは汎用キー/値保存は未実装
            // 必要に応じてAppStateテーブルを使って実装可能
            _logger.LogWarning("SqliteStorageService.GetAsync<{Type}>({Key}) は未実装", typeof(T).Name, key);
            return Task.FromResult<T?>(default);
        }

        public Task SetAsync<T>(string key, T value)
        {
            // SQLiteでは汎用キー/値保存は未実装
            _logger.LogWarning("SqliteStorageService.SetAsync<{Type}>({Key}) は未実装", typeof(T).Name, key);
            return Task.CompletedTask;
        }

        public Task ClearAsync()
        {
            // SQLiteのデータ削除は慎重に行う必要があるため、
            // ここでは実装しない（手動でDBファイルを削除する）
            _logger.LogWarning("SqliteStorageService.ClearAsync() は実装されていません");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// ストレージサービスファクトリ実装
    /// </summary>
    public class StorageServiceFactory : IStorageServiceFactory
    {
        private readonly ISessionRepository _repository;
        private readonly ILocalStorageService _localStorageService;
        private readonly SqliteStorageService _sqliteStorageService;
        private readonly LocalStorageServiceAdapter _localStorageAdapter;
        private readonly ILogger<StorageServiceFactory> _logger;

        public StorageServiceFactory(
            ISessionRepository repository,
            ILocalStorageService localStorageService,
            ILogger<StorageServiceFactory> logger,
            ILogger<SqliteStorageService> sqliteLogger,
            ILogger<LocalStorageServiceAdapter> adapterLogger)
        {
            _repository = repository;
            _localStorageService = localStorageService;
            _logger = logger;
            _sqliteStorageService = new SqliteStorageService(repository, sqliteLogger);
            _localStorageAdapter = new LocalStorageServiceAdapter(localStorageService, adapterLogger);
        }

        public bool SqliteDatabaseExists => _repository.DatabaseExists();

        public IStorageService GetSqliteStorageService() => _sqliteStorageService;

        public IStorageService GetLocalStorageService() => _localStorageAdapter;

        public IStorageService GetCurrentStorageService()
        {
            // 同期版：ファイル存在チェックのみ（後方互換性のため残す）
            if (SqliteDatabaseExists)
            {
                return _sqliteStorageService;
            }
            return _localStorageAdapter;
        }

        public async Task<IStorageService> GetCurrentStorageServiceAsync()
        {
            // LocalStorageのセッション数を確認
            int localStorageSessionCount = 0;
            try
            {
                var localSessions = await _localStorageAdapter.LoadSessionsAsync();
                localStorageSessionCount = localSessions.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LocalStorageのセッション確認に失敗");
            }

            // セッション数ベースの判定
            if (SqliteDatabaseExists)
            {
                // 既存DBのスキーママイグレーションを確認・実行
                await _repository.InitializeAsync();

                var sqliteSessionCount = await _repository.GetSessionCountAsync();
                if (sqliteSessionCount > 0)
                {
                    // SQLiteにセッションがある → SQLite使用
                    _logger.LogInformation("SQLiteを使用（セッション数: {Count}）", sqliteSessionCount);
                    return _sqliteStorageService;
                }

                if (localStorageSessionCount > 0)
                {
                    // SQLiteは空、LocalStorageにセッションがある → LocalStorage使用（未移行ユーザー）
                    _logger.LogInformation("LocalStorageを使用（LocalStorageにセッション {Count} 件あり）", localStorageSessionCount);
                    return _localStorageAdapter;
                }

                // SQLiteは空、LocalStorageも空 → SQLite使用（新規ユーザー）
                _logger.LogInformation("SQLiteを使用（新規ユーザー）");
                return _sqliteStorageService;
            }

            // SQLite DBが存在しない
            if (localStorageSessionCount > 0)
            {
                // LocalStorageにセッションがある → LocalStorage使用（既存ユーザー）
                _logger.LogInformation("LocalStorageを使用（SQLite DB未作成、LocalStorageにセッション {Count} 件）", localStorageSessionCount);
                return _localStorageAdapter;
            }

            // SQLite DBも存在せず、LocalStorageも空 → SQLiteを初期化して使用（新規ユーザー）
            _logger.LogInformation("SQLiteを初期化（新規ユーザー）");
            await _repository.InitializeAsync();
            return _sqliteStorageService;
        }

        public async Task<bool> MigrateToSqliteAsync(
            List<SessionInfo> sessions,
            Guid? activeSessionId,
            Dictionary<Guid, bool> expandedStates)
        {
            try
            {
                // SQLite DBを初期化
                await _repository.InitializeAsync();

                // データを移行
                await _repository.MigrateFromLocalStorageAsync(sessions, activeSessionId, expandedStates);

                _logger.LogInformation("LocalStorageからSQLiteへの移行が完了しました");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LocalStorageからSQLiteへの移行に失敗しました");
                return false;
            }
        }

        public async Task<int> GetSqliteSessionCountAsync()
        {
            if (!SqliteDatabaseExists)
                return 0;

            return await _repository.GetSessionCountAsync();
        }
    }
}
