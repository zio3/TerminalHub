using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using TerminalHub.Models;

namespace TerminalHub.Services
{
    public class SessionMemoRepository : ISessionMemoRepository
    {
        private readonly SessionDbContext _dbContext;
        private readonly ILogger<SessionMemoRepository> _logger;

        public SessionMemoRepository(SessionDbContext dbContext, ILogger<SessionMemoRepository> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        public async Task<List<SessionMemo>> GetBySessionAsync(Guid sessionId)
        {
            var result = new List<SessionMemo>();
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            await using var reader = await connection.ExecuteReaderAsync(@"
                SELECT MemoId, SessionId, Title, Body, CreatedAt, UpdatedAt, SortOrder
                FROM SessionMemos
                WHERE SessionId = @sessionId AND IsDeleted = 0
                ORDER BY SortOrder ASC, CreatedAt ASC",
                ("@sessionId", sessionId.ToString()));

            while (await reader.ReadAsync())
            {
                result.Add(new SessionMemo
                {
                    MemoId = Guid.Parse(reader.GetString(0)),
                    SessionId = Guid.Parse(reader.GetString(1)),
                    Title = reader.GetString(2),
                    Body = reader.GetString(3),
                    CreatedAt = DateTime.Parse(reader.GetString(4)),
                    UpdatedAt = DateTime.Parse(reader.GetString(5)),
                    SortOrder = reader.GetInt32(6)
                });
            }
            return result;
        }

        public async Task<List<SessionMemo>> GetDeletedBySessionAsync(Guid sessionId)
        {
            var result = new List<SessionMemo>();
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            await using var reader = await connection.ExecuteReaderAsync(@"
                SELECT MemoId, SessionId, Title, Body, CreatedAt, UpdatedAt, SortOrder, DeletedAt
                FROM SessionMemos
                WHERE SessionId = @sessionId AND IsDeleted = 1
                ORDER BY DeletedAt DESC",
                ("@sessionId", sessionId.ToString()));

            while (await reader.ReadAsync())
            {
                result.Add(new SessionMemo
                {
                    MemoId = Guid.Parse(reader.GetString(0)),
                    SessionId = Guid.Parse(reader.GetString(1)),
                    Title = reader.GetString(2),
                    Body = reader.GetString(3),
                    CreatedAt = DateTime.Parse(reader.GetString(4)),
                    UpdatedAt = DateTime.Parse(reader.GetString(5)),
                    SortOrder = reader.GetInt32(6),
                    IsDeleted = true,
                    DeletedAt = reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7))
                });
            }
            return result;
        }

        public async Task<bool> AnyExistsAsync(Guid sessionId)
        {
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            var count = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM SessionMemos WHERE SessionId = @sessionId",
                ("@sessionId", sessionId.ToString()));

            return count > 0;
        }

        public async Task InsertAsync(SessionMemo memo)
        {
            try
            {
                await using var connection = _dbContext.CreateConnection();
                await connection.OpenAsync();

                await connection.ExecuteNonQueryAsync(@"
                    INSERT INTO SessionMemos (MemoId, SessionId, Title, Body, CreatedAt, UpdatedAt, SortOrder)
                    VALUES (@memoId, @sessionId, @title, @body, @createdAt, @updatedAt, @sortOrder)",
                    ("@memoId", memo.MemoId.ToString()),
                    ("@sessionId", memo.SessionId.ToString()),
                    ("@title", memo.Title),
                    ("@body", memo.Body),
                    ("@createdAt", memo.CreatedAt.ToString("o")),
                    ("@updatedAt", memo.UpdatedAt.ToString("o")),
                    ("@sortOrder", memo.SortOrder));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Memo] InsertAsync 失敗: MemoId={MemoId}, SessionId={SessionId}",
                    memo.MemoId, memo.SessionId);
                throw;
            }
        }

        public async Task UpdateTitleAsync(Guid memoId, string title)
        {
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            await connection.ExecuteNonQueryAsync(@"
                UPDATE SessionMemos SET Title = @title, UpdatedAt = @updatedAt
                WHERE MemoId = @memoId",
                ("@memoId", memoId.ToString()),
                ("@title", title),
                ("@updatedAt", DateTime.Now.ToString("o")));
        }

        public async Task UpdateBodyAsync(Guid memoId, string body)
        {
            try
            {
                await using var connection = _dbContext.CreateConnection();
                await connection.OpenAsync();

                await connection.ExecuteNonQueryAsync(@"
                    UPDATE SessionMemos SET Body = @body, UpdatedAt = @updatedAt
                    WHERE MemoId = @memoId",
                    ("@memoId", memoId.ToString()),
                    ("@body", body),
                    ("@updatedAt", DateTime.Now.ToString("o")));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Memo] UpdateBodyAsync 失敗: MemoId={MemoId}", memoId);
                throw;
            }
        }

        public async Task DeleteAsync(Guid memoId)
        {
            // うっかり × ボタンで消されても後から復元できるよう、論理削除に移行。
            // シグネチャは変えていないので、既存呼び出し側 (BottomPanel.RemoveTab 等) は無変更で動く。
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            await connection.ExecuteNonQueryAsync(@"
                UPDATE SessionMemos
                SET IsDeleted = 1, DeletedAt = @deletedAt
                WHERE MemoId = @memoId",
                ("@memoId", memoId.ToString()),
                ("@deletedAt", DateTime.Now.ToString("o")));
        }

        public async Task RestoreAsync(Guid memoId)
        {
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            await connection.ExecuteNonQueryAsync(@"
                UPDATE SessionMemos
                SET IsDeleted = 0, DeletedAt = NULL, UpdatedAt = @updatedAt
                WHERE MemoId = @memoId",
                ("@memoId", memoId.ToString()),
                ("@updatedAt", DateTime.Now.ToString("o")));
        }

        public async Task HardDeleteAsync(Guid memoId)
        {
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            await connection.ExecuteNonQueryAsync(
                "DELETE FROM SessionMemos WHERE MemoId = @memoId",
                ("@memoId", memoId.ToString()));
        }
    }
}
