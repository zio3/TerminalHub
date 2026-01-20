using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TerminalHub.Models;

namespace TerminalHub.Services
{
    /// <summary>
    /// SQLite を使用したセッションリポジトリ実装
    /// </summary>
    public class SessionRepository : ISessionRepository
    {
        private readonly SessionDbContext _dbContext;
        private readonly ILogger<SessionRepository> _logger;

        public SessionRepository(SessionDbContext dbContext, ILogger<SessionRepository> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        public bool DatabaseExists() => _dbContext.DatabaseExists();

        public async Task InitializeAsync()
        {
            await _dbContext.InitializeAsync();
        }

        public async Task<List<SessionInfo>> GetAllSessionsAsync()
        {
            var sessions = new List<SessionInfo>();

            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            // セッション取得
            await using var reader = await connection.ExecuteReaderAsync(@"
                SELECT SessionId, DisplayName, FolderPath, FolderName, CreatedAt, LastAccessedAt,
                       IsActive, TerminalType, Memo, IsArchived, ArchivedAt, ParentSessionId
                FROM Sessions
                ORDER BY LastAccessedAt DESC");

            while (await reader.ReadAsync())
            {
                var session = new SessionInfo
                {
                    SessionId = Guid.Parse(reader.GetString(0)),
                    DisplayName = reader.IsDBNull(1) ? null : reader.GetString(1),
                    FolderPath = reader.GetString(2),
                    FolderName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    CreatedAt = DateTime.Parse(reader.GetString(4)),
                    LastAccessedAt = DateTime.Parse(reader.GetString(5)),
                    IsActive = reader.GetInt64(6) == 1,
                    TerminalType = (TerminalType)reader.GetInt64(7),
                    Memo = reader.IsDBNull(8) ? "" : reader.GetString(8),
                    IsArchived = reader.GetInt64(9) == 1,
                    ArchivedAt = reader.IsDBNull(10) ? null : DateTime.Parse(reader.GetString(10)),
                    ParentSessionId = reader.IsDBNull(11) ? null : Guid.Parse(reader.GetString(11))
                };
                sessions.Add(session);
            }

            // オプションを取得
            foreach (var session in sessions)
            {
                session.Options = await GetSessionOptionsAsync(connection, session.SessionId);
                session.CheckedScripts = await GetCheckedScriptsAsync(connection, session.SessionId);
            }

            return sessions;
        }

        public async Task<SessionInfo?> GetSessionAsync(Guid sessionId)
        {
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            await using var reader = await connection.ExecuteReaderAsync(@"
                SELECT SessionId, DisplayName, FolderPath, FolderName, CreatedAt, LastAccessedAt,
                       IsActive, TerminalType, Memo, IsArchived, ArchivedAt, ParentSessionId
                FROM Sessions
                WHERE SessionId = @sessionId",
                ("@sessionId", sessionId.ToString()));

            if (!await reader.ReadAsync())
            {
                return null;
            }

            var session = new SessionInfo
            {
                SessionId = Guid.Parse(reader.GetString(0)),
                DisplayName = reader.IsDBNull(1) ? null : reader.GetString(1),
                FolderPath = reader.GetString(2),
                FolderName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                CreatedAt = DateTime.Parse(reader.GetString(4)),
                LastAccessedAt = DateTime.Parse(reader.GetString(5)),
                IsActive = reader.GetInt64(6) == 1,
                TerminalType = (TerminalType)reader.GetInt64(7),
                Memo = reader.IsDBNull(8) ? "" : reader.GetString(8),
                IsArchived = reader.GetInt64(9) == 1,
                ArchivedAt = reader.IsDBNull(10) ? null : DateTime.Parse(reader.GetString(10)),
                ParentSessionId = reader.IsDBNull(11) ? null : Guid.Parse(reader.GetString(11))
            };

            await reader.CloseAsync();

            session.Options = await GetSessionOptionsAsync(connection, sessionId);
            session.CheckedScripts = await GetCheckedScriptsAsync(connection, sessionId);

            return session;
        }

        public async Task SaveSessionAsync(SessionInfo session)
        {
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            await using var transaction = connection.BeginTransaction();

            try
            {
                // UPSERT (INSERT OR REPLACE)
                await connection.ExecuteNonQueryAsync(@"
                    INSERT OR REPLACE INTO Sessions
                    (SessionId, DisplayName, FolderPath, FolderName, CreatedAt, LastAccessedAt,
                     IsActive, TerminalType, Memo, IsArchived, ArchivedAt, ParentSessionId)
                    VALUES (@sessionId, @displayName, @folderPath, @folderName, @createdAt, @lastAccessedAt,
                            @isActive, @terminalType, @memo, @isArchived, @archivedAt, @parentSessionId)",
                    ("@sessionId", session.SessionId.ToString()),
                    ("@displayName", session.DisplayName),
                    ("@folderPath", session.FolderPath),
                    ("@folderName", session.FolderName),
                    ("@createdAt", session.CreatedAt.ToString("o")),
                    ("@lastAccessedAt", session.LastAccessedAt.ToString("o")),
                    ("@isActive", session.IsActive ? 1 : 0),
                    ("@terminalType", (int)session.TerminalType),
                    ("@memo", session.Memo),
                    ("@isArchived", session.IsArchived ? 1 : 0),
                    ("@archivedAt", session.ArchivedAt?.ToString("o")),
                    ("@parentSessionId", session.ParentSessionId?.ToString()));

                // オプションを保存
                await SaveSessionOptionsAsync(connection, session.SessionId, session.Options);

                // チェック済みスクリプトを保存
                await SaveCheckedScriptsAsync(connection, session.SessionId, session.CheckedScripts);

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        public async Task UpdateLastAccessedAtAsync(Guid sessionId, DateTime time)
        {
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            await connection.ExecuteNonQueryAsync(@"
                UPDATE Sessions SET LastAccessedAt = @time WHERE SessionId = @sessionId",
                ("@sessionId", sessionId.ToString()),
                ("@time", time.ToString("o")));

            _logger.LogDebug("LastAccessedAt更新: SessionId={SessionId}", sessionId);
        }

        public async Task UpdateMemoAsync(Guid sessionId, string memo)
        {
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            await connection.ExecuteNonQueryAsync(@"
                UPDATE Sessions SET Memo = @memo WHERE SessionId = @sessionId",
                ("@sessionId", sessionId.ToString()),
                ("@memo", memo));
        }

        public async Task UpdateArchivedStateAsync(Guid sessionId, bool isArchived, DateTime? archivedAt)
        {
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            await connection.ExecuteNonQueryAsync(@"
                UPDATE Sessions SET IsArchived = @isArchived, ArchivedAt = @archivedAt
                WHERE SessionId = @sessionId",
                ("@sessionId", sessionId.ToString()),
                ("@isArchived", isArchived ? 1 : 0),
                ("@archivedAt", archivedAt?.ToString("o")));
        }

        public async Task DeleteSessionAsync(Guid sessionId)
        {
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            // 外部キー制約でオプションとスクリプトも自動削除
            await connection.ExecuteNonQueryAsync(@"
                DELETE FROM SessionOptions WHERE SessionId = @sessionId;
                DELETE FROM CheckedScripts WHERE SessionId = @sessionId;
                DELETE FROM Sessions WHERE SessionId = @sessionId;",
                ("@sessionId", sessionId.ToString()));
        }

        public async Task<int> GetSessionCountAsync()
        {
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            var count = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Sessions");
            return (int)count;
        }

        public async Task<Guid?> GetActiveSessionIdAsync()
        {
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            var value = await connection.ExecuteScalarAsync<string?>(
                "SELECT Value FROM AppState WHERE Key = 'ActiveSessionId'");

            if (string.IsNullOrEmpty(value))
                return null;

            return Guid.TryParse(value, out var guid) ? guid : null;
        }

        public async Task SetActiveSessionIdAsync(Guid? sessionId)
        {
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            await connection.ExecuteNonQueryAsync(@"
                INSERT OR REPLACE INTO AppState (Key, Value) VALUES ('ActiveSessionId', @value)",
                ("@value", sessionId?.ToString()));
        }

        public async Task<Dictionary<Guid, bool>> GetExpandedStatesAsync()
        {
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            var value = await connection.ExecuteScalarAsync<string?>(
                "SELECT Value FROM AppState WHERE Key = 'ExpandedStates'");

            if (string.IsNullOrEmpty(value))
                return new Dictionary<Guid, bool>();

            try
            {
                var stringDict = JsonSerializer.Deserialize<Dictionary<string, bool>>(value);
                if (stringDict == null)
                    return new Dictionary<Guid, bool>();

                return stringDict
                    .Where(kvp => Guid.TryParse(kvp.Key, out _))
                    .ToDictionary(
                        kvp => Guid.Parse(kvp.Key),
                        kvp => kvp.Value);
            }
            catch
            {
                return new Dictionary<Guid, bool>();
            }
        }

        public async Task SetExpandedStatesAsync(Dictionary<Guid, bool> states)
        {
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            var stringDict = states.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => kvp.Value);

            var json = JsonSerializer.Serialize(stringDict);

            await connection.ExecuteNonQueryAsync(@"
                INSERT OR REPLACE INTO AppState (Key, Value) VALUES ('ExpandedStates', @value)",
                ("@value", json));
        }

        public async Task MigrateFromLocalStorageAsync(
            List<SessionInfo> sessions,
            Guid? activeSessionId,
            Dictionary<Guid, bool> expandedStates)
        {
            _logger.LogInformation("LocalStorageからSQLiteへの移行開始: {Count}件のセッション", sessions.Count);

            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            // 移行中は外部キー制約を無効にする（親子関係の順序問題を回避）
            await connection.ExecuteNonQueryAsync("PRAGMA foreign_keys = OFF");

            await using var transaction = connection.BeginTransaction();

            try
            {
                // 既存データをクリア（再移行対応）
                await connection.ExecuteNonQueryAsync("DELETE FROM CheckedScripts");
                await connection.ExecuteNonQueryAsync("DELETE FROM SessionOptions");
                await connection.ExecuteNonQueryAsync("DELETE FROM Sessions");

                // 全セッションを保存
                foreach (var session in sessions)
                {
                    await connection.ExecuteNonQueryAsync(@"
                        INSERT OR REPLACE INTO Sessions
                        (SessionId, DisplayName, FolderPath, FolderName, CreatedAt, LastAccessedAt,
                         IsActive, TerminalType, Memo, IsArchived, ArchivedAt, ParentSessionId)
                        VALUES (@sessionId, @displayName, @folderPath, @folderName, @createdAt, @lastAccessedAt,
                                @isActive, @terminalType, @memo, @isArchived, @archivedAt, @parentSessionId)",
                        ("@sessionId", session.SessionId.ToString()),
                        ("@displayName", session.DisplayName),
                        ("@folderPath", session.FolderPath),
                        ("@folderName", session.FolderName),
                        ("@createdAt", session.CreatedAt.ToString("o")),
                        ("@lastAccessedAt", session.LastAccessedAt.ToString("o")),
                        ("@isActive", session.IsActive ? 1 : 0),
                        ("@terminalType", (int)session.TerminalType),
                        ("@memo", session.Memo),
                        ("@isArchived", session.IsArchived ? 1 : 0),
                        ("@archivedAt", session.ArchivedAt?.ToString("o")),
                        ("@parentSessionId", session.ParentSessionId?.ToString()));

                    // オプションを保存
                    await SaveSessionOptionsAsync(connection, session.SessionId, session.Options);

                    // チェック済みスクリプトを保存
                    await SaveCheckedScriptsAsync(connection, session.SessionId, session.CheckedScripts);
                }

                // アクティブセッションIDを保存
                if (activeSessionId.HasValue)
                {
                    await connection.ExecuteNonQueryAsync(@"
                        INSERT OR REPLACE INTO AppState (Key, Value) VALUES ('ActiveSessionId', @value)",
                        ("@value", activeSessionId.Value.ToString()));
                }

                // 展開状態を保存
                if (expandedStates.Count > 0)
                {
                    var stringDict = expandedStates.ToDictionary(
                        kvp => kvp.Key.ToString(),
                        kvp => kvp.Value);
                    var json = JsonSerializer.Serialize(stringDict);

                    await connection.ExecuteNonQueryAsync(@"
                        INSERT OR REPLACE INTO AppState (Key, Value) VALUES ('ExpandedStates', @value)",
                        ("@value", json));
                }

                transaction.Commit();
                _logger.LogInformation("LocalStorageからSQLiteへの移行完了");
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                _logger.LogError(ex, "LocalStorageからSQLiteへの移行失敗");
                throw;
            }
            finally
            {
                // 外部キー制約を再度有効にする
                await connection.ExecuteNonQueryAsync("PRAGMA foreign_keys = ON");
            }
        }

        public async Task<List<string>> GetInputHistoryAsync(int limit = 100)
        {
            var history = new List<string>();

            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            await using var reader = await connection.ExecuteReaderAsync(@"
                SELECT Text FROM InputHistory ORDER BY Id DESC LIMIT @limit",
                ("@limit", limit));

            while (await reader.ReadAsync())
            {
                history.Add(reader.GetString(0));
            }

            // 古い順に並べ替え（リストの先頭が最も古い）
            history.Reverse();
            return history;
        }

        public async Task AddInputHistoryAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            // 直前の履歴と同じ場合は追加しない
            var lastText = await connection.ExecuteScalarAsync<string?>(
                "SELECT Text FROM InputHistory ORDER BY Id DESC LIMIT 1");

            if (lastText == text)
                return;

            // 新しい履歴を追加
            await connection.ExecuteNonQueryAsync(@"
                INSERT INTO InputHistory (Text, CreatedAt) VALUES (@text, @createdAt)",
                ("@text", text),
                ("@createdAt", DateTime.UtcNow.ToString("o")));

            // 100件を超えた古い履歴を削除
            await connection.ExecuteNonQueryAsync(@"
                DELETE FROM InputHistory WHERE Id NOT IN (
                    SELECT Id FROM InputHistory ORDER BY Id DESC LIMIT 100
                )");
        }

        public async Task ClearInputHistoryAsync()
        {
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            await connection.ExecuteNonQueryAsync("DELETE FROM InputHistory");
        }

        private async Task<Dictionary<string, string>> GetSessionOptionsAsync(SqliteConnection connection, Guid sessionId)
        {
            var options = new Dictionary<string, string>();

            await using var reader = await connection.ExecuteReaderAsync(@"
                SELECT OptionKey, OptionValue FROM SessionOptions WHERE SessionId = @sessionId",
                ("@sessionId", sessionId.ToString()));

            while (await reader.ReadAsync())
            {
                var key = reader.GetString(0);
                var value = reader.IsDBNull(1) ? "" : reader.GetString(1);
                options[key] = value;
            }

            return options;
        }

        private async Task<HashSet<string>> GetCheckedScriptsAsync(SqliteConnection connection, Guid sessionId)
        {
            var scripts = new HashSet<string>();

            await using var reader = await connection.ExecuteReaderAsync(@"
                SELECT ScriptName FROM CheckedScripts WHERE SessionId = @sessionId",
                ("@sessionId", sessionId.ToString()));

            while (await reader.ReadAsync())
            {
                scripts.Add(reader.GetString(0));
            }

            return scripts;
        }

        private async Task SaveSessionOptionsAsync(SqliteConnection connection, Guid sessionId, Dictionary<string, string> options)
        {
            // 既存のオプションを削除
            await connection.ExecuteNonQueryAsync(@"
                DELETE FROM SessionOptions WHERE SessionId = @sessionId",
                ("@sessionId", sessionId.ToString()));

            // 新しいオプションを挿入
            foreach (var (key, value) in options)
            {
                await connection.ExecuteNonQueryAsync(@"
                    INSERT INTO SessionOptions (SessionId, OptionKey, OptionValue)
                    VALUES (@sessionId, @key, @value)",
                    ("@sessionId", sessionId.ToString()),
                    ("@key", key),
                    ("@value", value));
            }
        }

        private async Task SaveCheckedScriptsAsync(SqliteConnection connection, Guid sessionId, HashSet<string> scripts)
        {
            // 既存のスクリプトを削除
            await connection.ExecuteNonQueryAsync(@"
                DELETE FROM CheckedScripts WHERE SessionId = @sessionId",
                ("@sessionId", sessionId.ToString()));

            // 新しいスクリプトを挿入
            foreach (var script in scripts)
            {
                await connection.ExecuteNonQueryAsync(@"
                    INSERT INTO CheckedScripts (SessionId, ScriptName) VALUES (@sessionId, @scriptName)",
                    ("@sessionId", sessionId.ToString()),
                    ("@scriptName", script));
            }
        }
    }
}
