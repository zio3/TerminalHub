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

            // SavedAt 同時刻のタイ時に順序がぶれないよう SnapshotId を第 2 キーに使う
            await using var reader = await connection.ExecuteReaderAsync(@"
                SELECT SnapshotId, MemoId, Title, Body, SavedAt, Trigger
                FROM SessionMemoSnapshots
                WHERE MemoId = @memoId
                ORDER BY SavedAt DESC, SnapshotId DESC
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
                ORDER BY SavedAt DESC, SnapshotId DESC",
                ("@memoId", memoId.ToString()));

            while (await reader.ReadAsync())
            {
                result.Add(Read(reader));
            }
            return result;
        }

        public async Task<Dictionary<Guid, int>> GetCountsBySessionAsync(Guid sessionId)
        {
            var result = new Dictionary<Guid, int>();
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            // セッション配下のメモ (論理削除済みも含む) に紐づく snapshot 件数を一括取得。
            // アクティブメモだけでなく削除済みも対象にしておくことで、将来「削除済みメモの履歴」
            // UI を足すときにも再利用できる。履歴 0 件のメモは結果に含まれない。
            await using var reader = await connection.ExecuteReaderAsync(@"
                SELECT sn.MemoId, COUNT(*) AS Cnt
                FROM SessionMemoSnapshots sn
                INNER JOIN SessionMemos m ON m.MemoId = sn.MemoId
                WHERE m.SessionId = @sessionId
                GROUP BY sn.MemoId",
                ("@sessionId", sessionId.ToString()));

            while (await reader.ReadAsync())
            {
                result[Guid.Parse(reader.GetString(0))] = reader.GetInt32(1);
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
            // SavedAt 同時刻のタイ時に tie-breaker が無いと削除対象が非決定的になるので SnapshotId を第 2 キーに。
            await connection.ExecuteNonQueryAsync(@"
                DELETE FROM SessionMemoSnapshots
                WHERE SnapshotId IN (
                    SELECT SnapshotId FROM SessionMemoSnapshots
                    WHERE MemoId = @memoId AND Trigger = @trigger
                    ORDER BY SavedAt DESC, SnapshotId DESC
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
