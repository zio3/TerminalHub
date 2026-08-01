using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace TerminalHub.Services
{
    /// <summary>
    /// 配送記録1件分。エンベロープの #ID から「その配送が本当に TerminalHub を通ったか・
    /// どこからどこへか」を検証するための台帳。
    /// From* は proof 検証済みの送信元（null = 無記名＝外部クライアントの可能性を含む）。
    /// </summary>
    public record DeliveryRecord(
        string DeliveryId,
        string? FromSessionId,
        string? FromName,
        string ToSessionId,
        string ToName,
        string? ContextId,
        DateTime SentAt);

    /// <summary>
    /// 配送記録の永続化リポジトリ。
    /// deliveryId はエンベロープに埋まる短い ID で、照会の資格を兼ねる
    /// （エンベロープを受け取った者だけが ID を知っている。contextId と同じ capability 哲学）。
    /// </summary>
    public interface IDeliveryRepository
    {
        Task CreateAsync(DeliveryRecord record);
        Task<DeliveryRecord?> GetAsync(string deliveryId);

        /// <summary>
        /// 記録を削除する（受理されなかった送信の後片付け用）。
        /// 誰も ID を参照できない記録を残すと、Rejected を量産して総数上限の掃除を走らせ、
        /// **真正な記録を押し出す**攻撃に使える（押し出された配送は get_delivery で
        /// 「偽装か期限切れ」と誤判定される）ため、書いていないことが確定した記録は消す。
        /// 後片付けの失敗で本流を壊さないよう、例外は投げずログに残す。
        /// </summary>
        Task DeleteAsync(string deliveryId);
    }

    public class DeliveryRepository : IDeliveryRepository
    {
        private readonly SessionDbContext _dbContext;
        private readonly ILogger<DeliveryRepository> _logger;

        // TTL 掃除: 記録から14日で削除＋総数上限（超過は古い順に削除）。
        // 検証は通常「受け取った直後」に行われるので14日は十分に余裕がある。
        // Contexts と違い全行が終端（追記も更新もない）ため、無条件に古い順で消してよい。
        private const int TtlDays = 14;
        private const int MaxCount = 1000;

        public DeliveryRepository(SessionDbContext dbContext, ILogger<DeliveryRepository> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        public async Task CreateAsync(DeliveryRecord record)
        {
            await _dbContext.InitializeAsync();
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            await connection.ExecuteNonQueryAsync(@"
                INSERT INTO Deliveries (DeliveryId, FromSessionId, FromName, ToSessionId, ToName, ContextId, SentAt)
                VALUES (@deliveryId, @fromSessionId, @fromName, @toSessionId, @toName, @contextId, @sentAt)",
                ("@deliveryId", record.DeliveryId),
                ("@fromSessionId", record.FromSessionId),
                ("@fromName", record.FromName),
                ("@toSessionId", record.ToSessionId),
                ("@toName", record.ToName),
                ("@contextId", record.ContextId),
                ("@sentAt", record.SentAt.ToString("o")));

            await PruneAsync(connection);
        }

        public async Task<DeliveryRecord?> GetAsync(string deliveryId)
        {
            await _dbContext.InitializeAsync();
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            await using var reader = await connection.ExecuteReaderAsync(@"
                SELECT DeliveryId, FromSessionId, FromName, ToSessionId, ToName, ContextId, SentAt
                FROM Deliveries WHERE DeliveryId = @deliveryId",
                ("@deliveryId", deliveryId ?? ""));

            if (!await reader.ReadAsync())
                return null;

            return new DeliveryRecord(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                // RoundtripKind: 保存は "o" 形式の UTC。既定の Parse はローカルへ変換してしまう
                // （ContextRepository と同じ理由）。
                DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        }

        public async Task DeleteAsync(string deliveryId)
        {
            try
            {
                await _dbContext.InitializeAsync();
                await using var connection = _dbContext.CreateConnection();
                await connection.OpenAsync();

                await connection.ExecuteNonQueryAsync(
                    "DELETE FROM Deliveries WHERE DeliveryId = @deliveryId",
                    ("@deliveryId", deliveryId));
            }
            catch (Exception ex)
            {
                // 後片付けの失敗で送信エラー本体を握り潰さない（ContextRepository.DeleteAsync と同じ方針）。
                // 残っても TTL で消える追記のみの行が1件残るだけ。
                _logger.LogWarning(ex, "[Delivery] 記録の後片付けに失敗: {DeliveryId}", deliveryId);
            }
        }

        private async Task PruneAsync(SqliteConnection connection)
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-TtlDays).ToString("o");
                await connection.ExecuteNonQueryAsync(
                    "DELETE FROM Deliveries WHERE SentAt < @cutoff",
                    ("@cutoff", cutoff));

                await connection.ExecuteNonQueryAsync(@"
                    DELETE FROM Deliveries WHERE DeliveryId IN (
                        SELECT DeliveryId FROM Deliveries
                        ORDER BY SentAt ASC
                        LIMIT max(0, (SELECT COUNT(*) FROM Deliveries) - @maxCount)
                    )",
                    ("@maxCount", MaxCount));
            }
            catch (Exception ex)
            {
                // 掃除の失敗で本流（記録）を壊さない。ただし不可視化しない（レビュー指摘 #173 の教訓）
                _logger.LogWarning(ex, "[Delivery] TTL掃除に失敗");
            }
        }
    }
}
