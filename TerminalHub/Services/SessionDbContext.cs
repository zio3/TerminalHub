using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace TerminalHub.Services
{
    /// <summary>
    /// SQLite データベースの接続とスキーマ管理を行うクラス
    /// </summary>
    public class SessionDbContext
    {
        private readonly string _connectionString;
        private readonly ILogger<SessionDbContext> _logger;
        private const int CurrentSchemaVersion = 2;

        public SessionDbContext(string dbPath, ILogger<SessionDbContext> logger)
        {
            _connectionString = $"Data Source={dbPath}";
            _logger = logger;
        }

        public string ConnectionString => _connectionString;

        /// <summary>
        /// データベースを初期化し、必要に応じてスキーマをマイグレーション
        /// </summary>
        public async Task InitializeAsync()
        {
            // データベースファイルのディレクトリを作成
            var dbPath = _connectionString.Replace("Data Source=", "");
            var dbDir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
            {
                Directory.CreateDirectory(dbDir);
                _logger.LogInformation("データベースディレクトリを作成: {Path}", dbDir);
            }

            await MigrateSchemaAsync();
        }

        /// <summary>
        /// スキーママイグレーションを実行
        /// </summary>
        private async Task MigrateSchemaAsync()
        {
            var currentVersion = await GetSchemaVersionAsync();
            _logger.LogInformation("現在のスキーマバージョン: {Version}", currentVersion);

            if (currentVersion < 1)
            {
                // v1: 初期スキーマ作成
                await CreateInitialSchemaAsync();
                await SetSchemaVersionAsync(1);
                _logger.LogInformation("スキーマ v1 を作成");
            }

            if (currentVersion < 2)
            {
                // v2: 入力履歴テーブルを追加
                await CreateInputHistoryTableAsync();
                await SetSchemaVersionAsync(2);
                _logger.LogInformation("スキーマ v2 を作成（入力履歴テーブル追加）");
            }
        }

        private async Task<int> GetSchemaVersionAsync()
        {
            try
            {
                await using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                // SchemaVersionテーブルが存在するか確認
                var tableExists = await connection.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='SchemaVersion'");

                if (tableExists == 0)
                {
                    return 0;
                }

                var version = await connection.ExecuteScalarAsync<long>(
                    "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersion");

                return (int)version;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "スキーマバージョン取得でエラー（新規DB）");
                return 0;
            }
        }

        private async Task SetSchemaVersionAsync(int version)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await connection.ExecuteNonQueryAsync(
                "INSERT OR REPLACE INTO SchemaVersion (Version, AppliedAt) VALUES (@version, @appliedAt)",
                ("@version", version),
                ("@appliedAt", DateTime.UtcNow.ToString("o")));
        }

        private async Task CreateInitialSchemaAsync()
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                -- スキーマバージョン管理
                CREATE TABLE IF NOT EXISTS SchemaVersion (
                    Version INTEGER PRIMARY KEY,
                    AppliedAt TEXT NOT NULL
                );

                -- メインテーブル
                CREATE TABLE IF NOT EXISTS Sessions (
                    SessionId TEXT PRIMARY KEY,
                    DisplayName TEXT,
                    FolderPath TEXT NOT NULL,
                    FolderName TEXT,
                    CreatedAt TEXT NOT NULL,
                    LastAccessedAt TEXT NOT NULL,
                    IsActive INTEGER DEFAULT 0,
                    TerminalType INTEGER NOT NULL,
                    Memo TEXT,
                    IsArchived INTEGER DEFAULT 0,
                    ArchivedAt TEXT,
                    ParentSessionId TEXT,
                    FOREIGN KEY(ParentSessionId) REFERENCES Sessions(SessionId)
                );

                -- オプション（1:N）
                CREATE TABLE IF NOT EXISTS SessionOptions (
                    SessionId TEXT NOT NULL,
                    OptionKey TEXT NOT NULL,
                    OptionValue TEXT,
                    PRIMARY KEY(SessionId, OptionKey),
                    FOREIGN KEY(SessionId) REFERENCES Sessions(SessionId) ON DELETE CASCADE
                );

                -- チェック済みスクリプト（1:N）
                CREATE TABLE IF NOT EXISTS CheckedScripts (
                    SessionId TEXT NOT NULL,
                    ScriptName TEXT NOT NULL,
                    PRIMARY KEY(SessionId, ScriptName),
                    FOREIGN KEY(SessionId) REFERENCES Sessions(SessionId) ON DELETE CASCADE
                );

                -- アプリ状態（単一レコード管理）
                CREATE TABLE IF NOT EXISTS AppState (
                    Key TEXT PRIMARY KEY,
                    Value TEXT
                );

                -- インデックス
                CREATE INDEX IF NOT EXISTS idx_sessions_lastaccessed ON Sessions(LastAccessedAt DESC);
                CREATE INDEX IF NOT EXISTS idx_sessions_archived ON Sessions(IsArchived);
                CREATE INDEX IF NOT EXISTS idx_sessions_parent ON Sessions(ParentSessionId);
            ";

            await connection.ExecuteNonQueryAsync(sql);
        }

        private async Task CreateInputHistoryTableAsync()
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                CREATE TABLE IF NOT EXISTS InputHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Text TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_inputhistory_createdat ON InputHistory(CreatedAt DESC);
            ";

            await connection.ExecuteNonQueryAsync(sql);
        }

        /// <summary>
        /// データベースが存在するかチェック
        /// </summary>
        public bool DatabaseExists()
        {
            var dbPath = _connectionString.Replace("Data Source=", "");
            return File.Exists(dbPath);
        }

        /// <summary>
        /// 新規接続を作成
        /// </summary>
        public SqliteConnection CreateConnection()
        {
            return new SqliteConnection(_connectionString);
        }
    }

    /// <summary>
    /// SqliteConnection の拡張メソッド
    /// </summary>
    public static class SqliteConnectionExtensions
    {
        public static async Task<T?> ExecuteScalarAsync<T>(this SqliteConnection connection, string sql, params (string name, object? value)[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
            var result = await command.ExecuteScalarAsync();
            if (result == null || result == DBNull.Value)
                return default;
            return (T)Convert.ChangeType(result, typeof(T));
        }

        public static async Task<int> ExecuteNonQueryAsync(this SqliteConnection connection, string sql, params (string name, object? value)[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
            return await command.ExecuteNonQueryAsync();
        }

        public static async Task<SqliteDataReader> ExecuteReaderAsync(this SqliteConnection connection, string sql, params (string name, object? value)[] parameters)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
            return await command.ExecuteReaderAsync();
        }
    }
}
