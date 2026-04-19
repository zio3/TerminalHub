using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using TerminalHub.Models;

namespace TerminalHub.Services
{
    public class SessionMemoSnapshotRepository : ISessionMemoSnapshotRepository
    {
        private readonly SessionDbContext _dbContext;
        private readonly ILogger<SessionMemoSnapshotRepository> _logger;

        public SessionMemoSnapshotRepository(SessionDbContext dbContext, ILogger<SessionMemoSnapshotRepository> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        public async Task InsertAsync(SessionMemoSnapshot snapshot)
        {
            try
            {
                await using var connection = _dbContext.CreateConnection();
                await connection.OpenAsync();

                await connection.ExecuteNonQueryAsync(@"
                    INSERT INTO SessionMemoSnapshots (SnapshotId, MemoId, Title, Body, SavedAt, Trigger)
                    VALUES (@snapshotId, @memoId, @title, @body, @savedAt, @trigger)",
                    ("@snapshotId", snapshot.SnapshotId.ToString()),
                    ("@memoId", snapshot.MemoId.ToString()),
                    ("@title", snapshot.Title),
                    ("@body", snapshot.Body),
                    ("@savedAt", snapshot.SavedAt.ToString("o")),
                    ("@trigger", snapshot.Trigger));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MemoSnapshot] InsertAsync 失敗: SnapshotId={SnapshotId}, MemoId={MemoId}, Trigger={Trigger}",
                    snapshot.SnapshotId, snapshot.MemoId, snapshot.Trigger);
                throw;
            }
        }

        public async Task<SessionMemoSnapshot?> GetLatestAsync(Guid memoId)
        {
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            await using var reader = await connection.ExecuteReaderAsync(@"
                SELECT SnapshotId, MemoId, Title, Body, SavedAt, Trigger
                FROM SessionMemoSnapshots
                WHERE MemoId = @memoId
                ORDER BY SavedAt DESC
                LIMIT 1",
                ("@memoId", memoId.ToString()));

            if (await reader.ReadAsync())
            {
                return Read(reader);
            }
            return null;
        }

        public async Task<List<SessionMemoSnapshot>> GetByMemoAsync(Guid memoId)
        {
            var result = new List<SessionMemoSnapshot>();
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            await using var reader = await connection.ExecuteReaderAsync(@"
                SELECT SnapshotId, MemoId, Title, Body, SavedAt, Trigger
                FROM SessionMemoSnapshots
                WHERE MemoId = @memoId
                ORDER BY SavedAt DESC",
                ("@memoId", memoId.ToString()));

            while (await reader.ReadAsync())
            {
                result.Add(Read(reader));
            }
            return result;
        }

        public async Task TrimAutoSnapshotsAsync(Guid memoId, int maxAutoKeep)
        {
            if (maxAutoKeep <= 0) return;

            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            // auto トリガーだけを対象に、古い順に超過分を削除する。
            // pre-delete スナップショットは対象外 (復元の最終防衛線として保護)。
            await connection.ExecuteNonQueryAsync(@"
                DELETE FROM SessionMemoSnapshots
                WHERE SnapshotId IN (
                    SELECT SnapshotId FROM SessionMemoSnapshots
                    WHERE MemoId = @memoId AND Trigger = @trigger
                    ORDER BY SavedAt DESC
                    LIMIT -1 OFFSET @keep
                )",
                ("@memoId", memoId.ToString()),
                ("@trigger", SessionMemoSnapshot.TriggerAuto),
                ("@keep", maxAutoKeep));
        }

        private static SessionMemoSnapshot Read(SqliteDataReader reader)
        {
            return new SessionMemoSnapshot
            {
                SnapshotId = Guid.Parse(reader.GetString(0)),
                MemoId = Guid.Parse(reader.GetString(1)),
                Title = reader.GetString(2),
                Body = reader.GetString(3),
                SavedAt = DateTime.Parse(reader.GetString(4)),
                Trigger = reader.GetString(5)
            };
        }
    }
}
