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
        /// <summary>
        /// 記録を **Pending（未確定）** として追加する。掃除は **TTL のみ**行い、総数上限の掃除はしない。
        /// 上限掃除を INSERT 直後にやると、Rejected（後で削除される参照不能な記録）の
        /// INSERT が引き金になって**真正な最古の記録が先に押し出され**、その後 Rejected 側を
        /// 削除しても戻らない。配送結果が出たら <see cref="CommitAsync"/> →
        /// <see cref="PruneToCapAsync"/>（Rejected なら <see cref="DeleteAsync"/>）の順で確定させる。
        /// </summary>
        Task CreateAsync(DeliveryRecord record);

        /// <summary>
        /// 記録を **Committed（確定）** にする。配送結果が Rejected 以外
        /// （Delivered / Queued / Failed＝部分配送の可能性あり）と判明した時点で呼ぶ。
        /// 例外は投げない（失敗すると行は Pending のまま＝上限の数えから外れ続けるが、
        /// TTL で消えるので実害は「上限が実効的に少し緩む」だけ）。
        /// </summary>
        Task CommitAsync(string deliveryId);

        /// <summary>
        /// 総数上限の掃除（**Committed の行だけ**を数え、超過分を古い順に削除）。例外は投げない。
        /// Pending を判断から外すのは並行送信対策: 「A（後で Rejected で削除）が行を作る →
        /// B が行を作って掃除 → B の掃除が A の一時的な +1 を数えて真正な最古を余分に押し出す →
        /// A が自分の行を消す」というインターリーブを、状態で数えることで原理的に防ぐ
        /// （時刻ベースの猶予期間では、配送時間に上限が無い＝本文長×チャンク間隔・ロック待ち
        /// 次第で猶予を超えられるため不十分だった）。
        /// </summary>
        Task PruneToCapAsync();

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
        // Contexts と違い行の内容は変わらない（更新は Pending→Committed の確定フラグのみ）ため、
        // Committed の行を無条件に古い順で消してよい。
        // MaxCount が public なのはテスト（上限掃除の実行順序の検証）が参照するため。
        private const int TtlDays = 14;
        public const int MaxCount = 1000;


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

            // Committed=0（Pending）で作る。配送結果が出たら CommitAsync で確定する。
            await connection.ExecuteNonQueryAsync(@"
                INSERT INTO Deliveries (DeliveryId, FromSessionId, FromName, ToSessionId, ToName, ContextId, SentAt, Committed)
                VALUES (@deliveryId, @fromSessionId, @fromName, @toSessionId, @toName, @contextId, @sentAt, 0)",
                ("@deliveryId", record.DeliveryId),
                ("@fromSessionId", record.FromSessionId),
                ("@fromName", record.FromName),
                ("@toSessionId", record.ToSessionId),
                ("@toName", record.ToName),
                ("@contextId", record.ContextId),
                ("@sentAt", record.SentAt.ToString("o")));

            // TTL 掃除のみ。総数上限の掃除は PruneToCapAsync（記録を残すことが確定した後）で行う
            // （ここでやると Rejected の一時的な +1 が真正な最古の記録を押し出す）。
            await PruneExpiredAsync(connection);
        }

        public async Task CommitAsync(string deliveryId)
        {
            try
            {
                await _dbContext.InitializeAsync();
                await using var connection = _dbContext.CreateConnection();
                await connection.OpenAsync();

                await connection.ExecuteNonQueryAsync(
                    "UPDATE Deliveries SET Committed = 1 WHERE DeliveryId = @deliveryId",
                    ("@deliveryId", deliveryId));
            }
            catch (Exception ex)
            {
                // 確定の失敗で本流（送信）を壊さない。行は Pending のまま＝上限の数えから
                // 外れ続けるが、TTL で消える（実害は上限が実効的に少し緩むだけ）。
                _logger.LogWarning(ex, "[Delivery] 記録の確定に失敗: {DeliveryId}", deliveryId);
            }
        }

        public async Task PruneToCapAsync()
        {
            try
            {
                await _dbContext.InitializeAsync();
                await using var connection = _dbContext.CreateConnection();
                await connection.OpenAsync();

                // Committed の行だけを数え、超過分を古い順に削除（Pending は並行送信の
                // 未確定行なので判断から外す。インターフェースのコメント参照）。
                await connection.ExecuteNonQueryAsync(@"
                    DELETE FROM Deliveries WHERE DeliveryId IN (
                        SELECT DeliveryId FROM Deliveries
                        WHERE Committed = 1
                        ORDER BY SentAt ASC
                        LIMIT max(0, (SELECT COUNT(*) FROM Deliveries WHERE Committed = 1) - @maxCount)
                    )",
                    ("@maxCount", MaxCount));
            }
            catch (Exception ex)
            {
                // 掃除の失敗で本流（送信）を壊さない。ただし不可視化しない（レビュー指摘 #173 の教訓）
                _logger.LogWarning(ex, "[Delivery] 上限掃除に失敗");
            }
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
                // 残っても TTL で消える行が1件残るだけ（Pending なら上限の数えにも入らない）。
                _logger.LogWarning(ex, "[Delivery] 記録の後片付けに失敗: {DeliveryId}", deliveryId);
            }
        }

        private async Task PruneExpiredAsync(SqliteConnection connection)
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-TtlDays).ToString("o");
                await connection.ExecuteNonQueryAsync(
                    "DELETE FROM Deliveries WHERE SentAt < @cutoff",
                    ("@cutoff", cutoff));
            }
            catch (Exception ex)
            {
                // 掃除の失敗で本流（記録）を壊さない。ただし不可視化しない（レビュー指摘 #173 の教訓）
                _logger.LogWarning(ex, "[Delivery] TTL掃除に失敗");
            }
        }
    }
}
