using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace TerminalHub.Services
{
    /// <summary>
    /// ContextSummary（依頼の状況札）1件分。
    /// UpdatedBy* は最終書き込み者。proof 検証済みのセッションが書いた場合のみ入り、
    /// proof 無しの書き込み（外部クライアント等）では null（=無記名と区別できる）。
    /// </summary>
    public record ContextRecord(
        string ContextId,
        string Status,
        string Summary,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        string? UpdatedBySessionId,
        string? UpdatedByName);

    /// <summary>
    /// ContextSummary の永続化リポジトリ。
    /// contextId は send_to_session が発行する推測不能な値で capability を兼ねる
    /// （知っている=読み書きできる。proof と同じ哲学で認証を別途作らない）。
    /// </summary>
    public interface IContextRepository
    {
        Task CreateAsync(string contextId);
        Task<ContextRecord?> GetAsync(string contextId);
        /// <summary>
        /// 要約を全体上書きし、status 指定があれば併せて更新する。対象が無ければ false。
        /// updatedBy* は proof 検証済みの書き込み元（無記名なら null を渡す＝前回の記名は消える）。
        /// </summary>
        Task<bool> UpdateAsync(string contextId, string summary, string? status,
            string? updatedBySessionId, string? updatedByName);

        /// <summary>
        /// 札を削除する（send_to_session が発行直後に送信失敗した際の後片付け用）。
        /// 呼び出し元の例外処理を単純に保つため、失敗しても例外を投げずログに残す。
        /// </summary>
        Task DeleteAsync(string contextId);
    }

    public class ContextRepository : IContextRepository
    {
        private readonly SessionDbContext _dbContext;
        private readonly ILogger<ContextRepository> _logger;

        // TTL 掃除: 終端状態(completed/failed/canceled)になってから14日で削除。
        // さらに総数上限を超えた分は終端状態の古いものから削除する（進行中は消さない）。
        // サーバー再起動でコンテキストが蒸発すると依頼側が永久ポーリングになるため永続化は必須で、
        // その代償として掃除を作成時に必ず走らせる。
        private const int TtlDays = 14;
        private const int MaxCount = 500;
        private static readonly string[] TerminalStatuses = { "completed", "failed", "canceled" };

        public ContextRepository(SessionDbContext dbContext, ILogger<ContextRepository> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        public async Task CreateAsync(string contextId)
        {
            await _dbContext.InitializeAsync();
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            var now = DateTime.UtcNow.ToString("o");
            await connection.ExecuteNonQueryAsync(@"
                INSERT INTO Contexts (ContextId, Status, Summary, CreatedAt, UpdatedAt)
                VALUES (@contextId, 'submitted', '', @now, @now)",
                ("@contextId", contextId),
                ("@now", now));

            await PruneAsync(connection);
        }

        public async Task<ContextRecord?> GetAsync(string contextId)
        {
            await _dbContext.InitializeAsync();
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            await using var reader = await connection.ExecuteReaderAsync(@"
                SELECT ContextId, Status, Summary, CreatedAt, UpdatedAt, UpdatedBySessionId, UpdatedByName
                FROM Contexts WHERE ContextId = @contextId",
                ("@contextId", contextId));

            if (!await reader.ReadAsync())
                return null;

            // RoundtripKind: 保存は "o" 形式の UTC(Z終端)。既定の Parse は Z をローカル時刻へ
            // 変換してしまい、get_context が返す updatedAt がオフセット付き表記に化けるため、
            // Kind=Utc のまま往復させる。
            return new ContextRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6));
        }

        public async Task<bool> UpdateAsync(string contextId, string summary, string? status,
            string? updatedBySessionId, string? updatedByName)
        {
            await _dbContext.InitializeAsync();
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            var now = DateTime.UtcNow.ToString("o");
            int affected;
            if (string.IsNullOrEmpty(status))
            {
                affected = await connection.ExecuteNonQueryAsync(@"
                    UPDATE Contexts SET Summary = @summary, UpdatedAt = @now,
                        UpdatedBySessionId = @updatedBySessionId, UpdatedByName = @updatedByName
                    WHERE ContextId = @contextId",
                    ("@contextId", contextId),
                    ("@summary", summary),
                    ("@now", now),
                    ("@updatedBySessionId", updatedBySessionId),
                    ("@updatedByName", updatedByName));
            }
            else
            {
                affected = await connection.ExecuteNonQueryAsync(@"
                    UPDATE Contexts SET Summary = @summary, Status = @status, UpdatedAt = @now,
                        UpdatedBySessionId = @updatedBySessionId, UpdatedByName = @updatedByName
                    WHERE ContextId = @contextId",
                    ("@contextId", contextId),
                    ("@summary", summary),
                    ("@status", status),
                    ("@now", now),
                    ("@updatedBySessionId", updatedBySessionId),
                    ("@updatedByName", updatedByName));
            }
            return affected > 0;
        }

        public async Task DeleteAsync(string contextId)
        {
            try
            {
                await _dbContext.InitializeAsync();
                await using var connection = _dbContext.CreateConnection();
                await connection.OpenAsync();

                await connection.ExecuteNonQueryAsync(
                    "DELETE FROM Contexts WHERE ContextId = @contextId",
                    ("@contextId", contextId));
            }
            catch (Exception ex)
            {
                // 後片付けの失敗で送信エラー本体を握り潰さないため例外は投げない。
                // ただし不可視化しない（レビュー指摘 #173 の教訓）。残った札は submitted のまま
                // TTL 対象外になるが、実害は「使われない行が1件残る」だけ。
                _logger.LogWarning(ex, "[Context] 札の後片付けに失敗: {ContextId}", contextId);
            }
        }

        private async Task PruneAsync(SqliteConnection connection)
        {
            try
            {
                var terminalList = $"'{string.Join("','", TerminalStatuses)}'";
                var cutoff = DateTime.UtcNow.AddDays(-TtlDays).ToString("o");

                // 終端状態かつ TTL 超過を削除
                await connection.ExecuteNonQueryAsync(
                    $"DELETE FROM Contexts WHERE Status IN ({terminalList}) AND UpdatedAt < @cutoff",
                    ("@cutoff", cutoff));

                // 総数上限超過分は終端状態の古いものから削除（進行中は消さない）
                await connection.ExecuteNonQueryAsync($@"
                    DELETE FROM Contexts WHERE ContextId IN (
                        SELECT ContextId FROM Contexts
                        WHERE Status IN ({terminalList})
                        ORDER BY UpdatedAt ASC
                        LIMIT max(0, (SELECT COUNT(*) FROM Contexts) - @maxCount)
                    )",
                    ("@maxCount", MaxCount));
            }
            catch (Exception ex)
            {
                // 掃除の失敗で本流（作成）を壊さない。ただし不可視化しない（レビュー指摘 #173 の教訓）
                _logger.LogWarning(ex, "[Context] TTL掃除に失敗");
            }
        }
    }
}
