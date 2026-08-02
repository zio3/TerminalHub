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
        private const int CurrentSchemaVersion = 13;

        private readonly SemaphoreSlim _initLock = new(1, 1);
        private bool _initialized = false;

        public SessionDbContext(string dbPath, ILogger<SessionDbContext> logger)
        {
            _connectionString = $"Data Source={dbPath}";
            _logger = logger;
        }

        public string ConnectionString => _connectionString;

        /// <summary>
        /// データベースを初期化し、必要に応じてスキーマをマイグレーション。
        /// 並行呼び出し安全で、最初の呼び出しのみ実マイグレーションを実行する。
        /// </summary>
        public async Task InitializeAsync()
        {
            // ロック前の fast-path（既に初期化済みなら即リターン）
            if (_initialized) return;

            await _initLock.WaitAsync();
            try
            {
                // double-checked locking
                if (_initialized) return;

                var dbPath = _connectionString.Replace("Data Source=", "");
                var dbExists = File.Exists(dbPath);
                var dbSize = dbExists ? new FileInfo(dbPath).Length : 0;
                _logger.LogInformation("[DB][診断] DB初期化開始: Path={Path} / Exists={Exists} / Size={Size} bytes",
                    dbPath, dbExists, dbSize);

                var dbDir = Path.GetDirectoryName(dbPath);
                if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
                {
                    Directory.CreateDirectory(dbDir);
                    _logger.LogInformation("[DB][診断] データベースディレクトリを作成: {Path}", dbDir);
                }

                await MigrateSchemaAsync();
                await DumpSchemaDiagnosticsAsync();

                _initialized = true;
            }
            finally
            {
                _initLock.Release();
            }
        }

        /// <summary>
        /// マイグレーション後のスキーマ状態を診断ログに出力する
        /// </summary>
        private async Task DumpSchemaDiagnosticsAsync()
        {
            try
            {
                await using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                // 全テーブル一覧
                var tables = new List<string>();
                await using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        tables.Add(reader.GetString(0));
                    }
                }
                _logger.LogInformation("[DB][診断] テーブル一覧: [{Tables}]", string.Join(", ", tables));

                // Sessions テーブルのカラム
                if (tables.Contains("Sessions"))
                {
                    var columns = new List<string>();
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = "PRAGMA table_info(Sessions)";
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        columns.Add(reader.GetString(1)); // name column
                    }
                    _logger.LogInformation("[DB][診断] Sessions カラム: [{Columns}]", string.Join(", ", columns));
                }

                // SchemaVersion レコード
                if (tables.Contains("SchemaVersion"))
                {
                    var versions = new List<long>();
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = "SELECT Version FROM SchemaVersion ORDER BY Version";
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        versions.Add(reader.GetInt64(0));
                    }
                    _logger.LogInformation("[DB][診断] 適用済みスキーマバージョン: [{Versions}]",
                        string.Join(", ", versions));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DB][診断] スキーマ診断中にエラー");
            }
        }

        // ============================================================
        // スキーママイグレーションのルール（必読）
        // ============================================================
        // 1. CreateV1SchemaAsync は v1 リリース時点のスキーマの凍結版。
        //    絶対に編集しない。新しい列・テーブルは ALTER/CREATE の
        //    マイグレーションブロックとしてのみ追加する。
        //
        // 2. 新規DBでも既存DBでも、同じマイグレーションを 0 → 最新まで
        //    順次適用する。分岐させない（"新規DB shortcut" は作らない）。
        //    → 初期スキーマとマイグレーションの乖離による duplicate
        //      column 等のバグを構造的に防ぐため。
        //
        // 3. 新しいマイグレーションを追加する際は：
        //    a. 下の `if (currentVersion < N)` ブロックを追加
        //    b. テーブル追加なら専用のプライベートメソッドを作る
        //       （CreateInputHistoryTableAsync のパターン）
        //    c. カラム追加なら ALTER TABLE をその場で書く
        // ============================================================

        /// <summary>
        /// スキーママイグレーションを実行
        /// </summary>
        private async Task MigrateSchemaAsync()
        {
            var currentVersion = await GetSchemaVersionAsync();
            _logger.LogInformation("[DB][マイグレーション] 開始: 現在のスキーマバージョン = {Version}", currentVersion);

            if (currentVersion < 1)
            {
                // v1: 初期スキーマ作成
                _logger.LogInformation("[DB][マイグレーション] v1 適用開始: CreateV1SchemaAsync");
                await CreateV1SchemaAsync();
                await SetSchemaVersionAsync(1);
                _logger.LogInformation("[DB][マイグレーション] v1 適用完了");
            }

            if (currentVersion < 2)
            {
                // v2: 入力履歴テーブルを追加
                _logger.LogInformation("[DB][マイグレーション] v2 適用開始: 入力履歴テーブル追加");
                await CreateInputHistoryTableAsync();
                await SetSchemaVersionAsync(2);
                _logger.LogInformation("[DB][マイグレーション] v2 適用完了");
            }

            if (currentVersion < 3)
            {
                // v3: ピン留め・優先度カラムを追加
                // ⚠️ 過去に IsPinned/PinPriority が先回りして入っているのに
                //    SchemaVersion=2 のまま残された DB が観測されたため、
                //    カラムの存在を個別チェックして不足分だけ追加する冪等実装にしている。
                _logger.LogInformation("[DB][マイグレーション] v3 適用開始: IsPinned, PinPriority カラム追加");
                await using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                if (!await ColumnExistsAsync(connection, "Sessions", "IsPinned"))
                {
                    await connection.ExecuteNonQueryAsync("ALTER TABLE Sessions ADD COLUMN IsPinned INTEGER DEFAULT 0");
                    _logger.LogInformation("[DB][マイグレーション] v3: IsPinned カラムを追加");
                }
                else
                {
                    _logger.LogInformation("[DB][マイグレーション] v3: IsPinned カラムは既存のためスキップ");
                }

                if (!await ColumnExistsAsync(connection, "Sessions", "PinPriority"))
                {
                    await connection.ExecuteNonQueryAsync("ALTER TABLE Sessions ADD COLUMN PinPriority INTEGER");
                    _logger.LogInformation("[DB][マイグレーション] v3: PinPriority カラムを追加");
                }
                else
                {
                    _logger.LogInformation("[DB][マイグレーション] v3: PinPriority カラムは既存のためスキップ");
                }

                await SetSchemaVersionAsync(3);
                _logger.LogInformation("[DB][マイグレーション] v3 適用完了");
            }

            if (currentVersion < 4)
            {
                // v4: セッション紐づきメモテーブルを追加
                _logger.LogInformation("[DB][マイグレーション] v4 適用開始: SessionMemos テーブル追加");
                await CreateSessionMemosTableAsync();
                await SetSchemaVersionAsync(4);
                _logger.LogInformation("[DB][マイグレーション] v4 適用完了");
            }

            // v5 は廃止された「ファイル参照」機能 (SessionFiles テーブル) で一度消費された番号。
            // 開発環境の DB には SchemaVersion=5 と空のままの SessionFiles テーブルが残っている
            // ケースがあるため、メモの論理削除は v6 として新設する。新規 DB でも害はない
            // (単に v5 が no-op で通過するだけ)。

            if (currentVersion < 6)
            {
                // v6: SessionMemos に論理削除カラム (IsDeleted, DeletedAt) を追加
                _logger.LogInformation("[DB][マイグレーション] v6 適用開始: SessionMemos に論理削除カラムを追加");
                await AddMemoSoftDeleteColumnsAsync();
                await SetSchemaVersionAsync(6);
                _logger.LogInformation("[DB][マイグレーション] v6 適用完了");
            }

            if (currentVersion < 7)
            {
                // v7: メモ編集履歴 (スナップショット) テーブルを追加
                _logger.LogInformation("[DB][マイグレーション] v7 適用開始: SessionMemoSnapshots テーブル追加");
                await CreateSessionMemoSnapshotsTableAsync();
                await SetSchemaVersionAsync(7);
                _logger.LogInformation("[DB][マイグレーション] v7 適用完了");
            }

            if (currentVersion < 8)
            {
                // v8: セッション専用カスタムコマンドを保持する SessionCommands カラム (JSON) を追加
                _logger.LogInformation("[DB][マイグレーション] v8 適用開始: Sessions に SessionCommands カラムを追加");
                await using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                if (!await ColumnExistsAsync(connection, "Sessions", "SessionCommands"))
                {
                    await connection.ExecuteNonQueryAsync("ALTER TABLE Sessions ADD COLUMN SessionCommands TEXT");
                    _logger.LogInformation("[DB][マイグレーション] v8: SessionCommands カラムを追加");
                }
                else
                {
                    _logger.LogInformation("[DB][マイグレーション] v8: SessionCommands カラムは既存のためスキップ");
                }

                await SetSchemaVersionAsync(8);
                _logger.LogInformation("[DB][マイグレーション] v8 適用完了");
            }

            if (currentVersion < 9)
            {
                // v9: 自己紹介カード（セッションの「何ができるか」自己申告・A2A Agent Card のローカル版）を保持する Card カラムを追加
                _logger.LogInformation("[DB][マイグレーション] v9 適用開始: Sessions に Card カラムを追加");
                await using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                if (!await ColumnExistsAsync(connection, "Sessions", "Card"))
                {
                    await connection.ExecuteNonQueryAsync("ALTER TABLE Sessions ADD COLUMN Card TEXT");
                    _logger.LogInformation("[DB][マイグレーション] v9: Card カラムを追加");
                }
                else
                {
                    _logger.LogInformation("[DB][マイグレーション] v9: Card カラムは既存のためスキップ");
                }

                await SetSchemaVersionAsync(9);
                _logger.LogInformation("[DB][マイグレーション] v9 適用完了");
            }

            if (currentVersion < 10)
            {
                // v10: ContextSummary（依頼の状況札）テーブルを追加
                _logger.LogInformation("[DB][マイグレーション] v10 適用開始: Contexts テーブル追加");
                await CreateContextsTableAsync();
                await SetSchemaVersionAsync(10);
                _logger.LogInformation("[DB][マイグレーション] v10 適用完了");
            }

            if (currentVersion < 11)
            {
                // v11: Contexts に依頼元（RequesterSessionId / RequesterName）を追加。
                // 終端 status の書き込みを依頼元セッションへ通知するために使う。
                _logger.LogInformation("[DB][マイグレーション] v11 適用開始: Contexts に依頼元カラムを追加");
                await using (var connection = new SqliteConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    foreach (var column in new[] { "RequesterSessionId", "RequesterName" })
                    {
                        if (!await ColumnExistsAsync(connection, "Contexts", column))
                        {
                            await connection.ExecuteNonQueryAsync($"ALTER TABLE Contexts ADD COLUMN {column} TEXT");
                            _logger.LogInformation("[DB][マイグレーション] v11: {Column} カラムを追加", column);
                        }
                        else
                        {
                            _logger.LogInformation("[DB][マイグレーション] v11: {Column} カラムは既存のためスキップ", column);
                        }
                    }
                }
                await SetSchemaVersionAsync(11);
                _logger.LogInformation("[DB][マイグレーション] v11 適用完了");
            }

            if (currentVersion < 12)
            {
                // v12: 配送記録（Deliveries）テーブルを追加。
                // エンベロープの #ID から「本当に TerminalHub を通ったか・どこからどこへか」を検証する台帳。
                _logger.LogInformation("[DB][マイグレーション] v12 適用開始: Deliveries テーブル追加");
                await CreateDeliveriesTableAsync();
                await SetSchemaVersionAsync(12);
                _logger.LogInformation("[DB][マイグレーション] v12 適用完了");
            }

            if (currentVersion < 13)
            {
                // v13: Deliveries に Committed（確定フラグ）を追加。上限掃除は確定済みの行だけを
                // 数える（並行送信の未確定行が掃除の判断に混ざるのを防ぐ）。
                // 既定 1 = 既存行（旧コードが作った行）は確定済み扱い。新規行はコード側で 0 を入れる。
                _logger.LogInformation("[DB][マイグレーション] v13 適用開始: Deliveries に Committed カラムを追加");
                await using (var connection = new SqliteConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    if (!await ColumnExistsAsync(connection, "Deliveries", "Committed"))
                    {
                        await connection.ExecuteNonQueryAsync(
                            "ALTER TABLE Deliveries ADD COLUMN Committed INTEGER NOT NULL DEFAULT 1");
                        _logger.LogInformation("[DB][マイグレーション] v13: Committed カラムを追加");
                    }
                    else
                    {
                        _logger.LogInformation("[DB][マイグレーション] v13: Committed カラムは既存のためスキップ");
                    }
                }
                await SetSchemaVersionAsync(13);
                _logger.LogInformation("[DB][マイグレーション] v13 適用完了");
            }

            _logger.LogInformation("[DB][マイグレーション] 完了");
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

        /// <summary>
        /// v1 リリース時点のスキーマを作成する（凍結版）。
        /// </summary>
        /// <remarks>
        /// ⚠️ このメソッドは絶対に編集しないこと。
        /// 新しい列・テーブルは必ず MigrateSchemaAsync の
        /// `if (currentVersion &lt; N)` ブロックとして追加する。
        /// このメソッドを編集すると、既存DBと新規DBで
        /// スキーマ状態が乖離し、duplicate column 等のバグが発生する。
        /// </remarks>
        private async Task CreateV1SchemaAsync()
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

        private async Task CreateSessionMemosTableAsync()
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            // 既存の SessionOptions / CheckedScripts と同様、ON DELETE CASCADE を宣言しつつ
            // PRAGMA foreign_keys の状態に依存しないよう DeleteSessionAsync 側で明示削除する方針。
            var sql = @"
                CREATE TABLE IF NOT EXISTS SessionMemos (
                    MemoId TEXT PRIMARY KEY,
                    SessionId TEXT NOT NULL,
                    Title TEXT NOT NULL DEFAULT '',
                    Body TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY(SessionId) REFERENCES Sessions(SessionId) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_session_memos_session ON SessionMemos(SessionId, SortOrder, CreatedAt);
            ";

            await connection.ExecuteNonQueryAsync(sql);
        }

        private async Task AddMemoSoftDeleteColumnsAsync()
        {
            // v5 マイグレーション: メモのうっかり消失対策で論理削除に移行するため、
            // IsDeleted (フラグ) と DeletedAt (論理削除日時) を追加する。
            // 既存レコードは IsDeleted=0 で何も変わらない。
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await connection.ExecuteNonQueryAsync(
                "ALTER TABLE SessionMemos ADD COLUMN IsDeleted INTEGER NOT NULL DEFAULT 0");
            await connection.ExecuteNonQueryAsync(
                "ALTER TABLE SessionMemos ADD COLUMN DeletedAt TEXT");
            await connection.ExecuteNonQueryAsync(
                "CREATE INDEX IF NOT EXISTS idx_session_memos_deleted ON SessionMemos(SessionId, IsDeleted, DeletedAt)");
        }

        private async Task CreateSessionMemoSnapshotsTableAsync()
        {
            // v7 マイグレーション: メモの編集履歴 (スナップショット) テーブルを追加。
            // 10 分毎の自動スナップショット (Trigger="auto") と、論理削除直前の
            // 保険スナップショット (Trigger="pre-delete") を保存する。
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                CREATE TABLE IF NOT EXISTS SessionMemoSnapshots (
                    SnapshotId TEXT PRIMARY KEY,
                    MemoId TEXT NOT NULL,
                    Title TEXT NOT NULL DEFAULT '',
                    Body TEXT NOT NULL DEFAULT '',
                    SavedAt TEXT NOT NULL,
                    Trigger TEXT NOT NULL,
                    FOREIGN KEY(MemoId) REFERENCES SessionMemos(MemoId) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_memo_snapshots_memo ON SessionMemoSnapshots(MemoId, SavedAt DESC);
            ";

            await connection.ExecuteNonQueryAsync(sql);
        }

        private async Task CreateContextsTableAsync()
        {
            // v10 マイグレーション: ContextSummary（依頼の状況札）テーブル。
            // send_to_session の contextId="new" でサーバーが発行し、get_context/update_context で
            // 読み書きする。ContextId は推測不能な値で capability を兼ねる（知っている=読み書きできる）。
            // セッションには紐づかない（外部クライアントも依頼主になれる）ため FK なし。
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                CREATE TABLE IF NOT EXISTS Contexts (
                    ContextId TEXT PRIMARY KEY,
                    Status TEXT NOT NULL,
                    Summary TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    UpdatedBySessionId TEXT,
                    UpdatedByName TEXT,
                    RequesterSessionId TEXT,
                    RequesterName TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_contexts_updated ON Contexts(UpdatedAt);
            ";

            await connection.ExecuteNonQueryAsync(sql);
        }

        /// <summary>
        /// v12: 配送記録テーブル。send_to_session の各配送に発行される deliveryId
        /// （エンベロープの #ID）から From/To を引く台帳。
        /// 行の内容は作成後変わらないが、Committed（Pending→確定、v13）の更新と
        /// Rejected 時の削除がある。
        /// From は接続キー検証済みの送信元で、NULL = 無記名（外部クライアントの可能性を含む）。
        /// </summary>
        private async Task CreateDeliveriesTableAsync()
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                CREATE TABLE IF NOT EXISTS Deliveries (
                    DeliveryId TEXT PRIMARY KEY,
                    FromSessionId TEXT,
                    FromName TEXT,
                    ToSessionId TEXT NOT NULL,
                    ToName TEXT NOT NULL,
                    ContextId TEXT,
                    SentAt TEXT NOT NULL,
                    Committed INTEGER NOT NULL DEFAULT 1
                );

                CREATE INDEX IF NOT EXISTS idx_deliveries_sentat ON Deliveries(SentAt);
            ";

            await connection.ExecuteNonQueryAsync(sql);
        }

        /// <summary>
        /// 指定したテーブルに指定カラムが存在するか確認する。
        /// PRAGMA table_info の結果を走査して名前一致を見る。
        /// </summary>
        private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string table, string column)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table})";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                // PRAGMA table_info: cid(0), name(1), type(2), notnull(3), dflt_value(4), pk(5)
                if (reader.GetString(1) == column) return true;
            }
            return false;
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
