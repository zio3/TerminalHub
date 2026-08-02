using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace TerminalHub.Services
{
    /// <summary>
    /// ContextSummary（依頼の状況札）1件分。
    /// UpdatedBy* は最終書き込み者。本人確定済み（接続キー検証）のセッションが書いた場合のみ入り、
    /// 無記名の書き込み（外部クライアント等）では null（=無記名と区別できる）。
    /// Requester* は依頼元。send_to_session の呼び出し元が本人確定できたときだけ入り、
    /// 終端 status の書き込みをその相手へ通知するために使う（外部クライアントの依頼では null）。
    /// </summary>
    public record ContextRecord(
        string ContextId,
        string Status,
        string Summary,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        string? UpdatedBySessionId,
        string? UpdatedByName,
        string? RequesterSessionId,
        string? RequesterName);

    /// <summary>
    /// 更新の結果。
    /// StatusTransitioned は「この呼び出しが status を実際に変えた」ことを表す。
    /// 遷移の成立判定は SQL 側（条件付き UPDATE）で行う（読み取り→判定→更新を別々に行うと、
    /// 同じ札へ同時に書いた2者が両方とも「working から completed へ変えた」と誤認するため）。
    /// なお完了通知の条件は当初「StatusTransitioned のときだけ」だったが、
    /// 「終端 status の書き込み成功（同一終端への再書き込み含む）」へ緩めた
    /// （再完了の続報が依頼元に伝わらない実害があったため。判定は呼び出し側=UpdateContext）。
    /// RewindRejected は「終端状態の札を進行中へ戻そうとして拒否した」ことを表す
    /// （黙って無視すると、書けたつもりの呼び出し側が気づけないため結果で返す）。
    /// Conflicted は「他の書き込みと競合し続けて更新できなかった」ことを表す。
    /// **Found=false（札が無い）と混ぜないこと**。札は存在しており、原因も対処も違う
    /// （前者は諦める・後者は再試行する）。
    /// </summary>
    public record ContextUpdateResult(
        bool Found,
        bool StatusTransitioned,
        bool RewindRejected = false,
        bool Conflicted = false);

    /// <summary>
    /// ContextSummary の永続化リポジトリ。
    /// contextId は send_to_session が発行する推測不能な値で capability を兼ねる
    /// （知っている=読み書きできる。deliveryId と同じ哲学で認証を別途作らない）。
    /// </summary>
    public interface IContextRepository
    {
        /// <summary>
        /// 札を作る。requester* は依頼元（接続キーで検証済みのセッション）。
        /// 記録があれば終端 status の書き込みをその相手へ通知でき、無ければ依頼元は
        /// get_context のポーリングで結果を取る（受信箱を持たない外部クライアント）。
        /// </summary>
        Task CreateAsync(string contextId, string? requesterSessionId = null, string? requesterName = null);
        Task<ContextRecord?> GetAsync(string contextId);
        /// <summary>
        /// 要約を全体上書きし、status 指定があれば併せて更新する。
        /// updatedBy* は接続キー検証済みの書き込み元（無記名なら null を渡す＝前回の記名は消える）。
        /// </summary>
        Task<ContextUpdateResult> UpdateAsync(string contextId, string summary, string? status,
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

        /// <summary>SQL に直接埋める終端状態のリテラル一覧（値は定数なので注入の余地はない）。</summary>
        private static readonly string TerminalStatusList = $"'{string.Join("','", TerminalStatuses)}'";

        /// <summary>status 更新が競合したときのやり直し上限。1回でも十分だが余裕を見る。</summary>
        private const int MaxUpdateAttempts = 3;

        public ContextRepository(SessionDbContext dbContext, ILogger<ContextRepository> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        public async Task CreateAsync(string contextId, string? requesterSessionId = null, string? requesterName = null)
        {
            await _dbContext.InitializeAsync();
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            var now = DateTime.UtcNow.ToString("o");
            await connection.ExecuteNonQueryAsync(@"
                INSERT INTO Contexts (ContextId, Status, Summary, CreatedAt, UpdatedAt, RequesterSessionId, RequesterName)
                VALUES (@contextId, 'submitted', '', @now, @now, @requesterSessionId, @requesterName)",
                ("@contextId", contextId),
                ("@now", now),
                ("@requesterSessionId", requesterSessionId),
                ("@requesterName", requesterName));

            await PruneAsync(connection);
        }

        public async Task<ContextRecord?> GetAsync(string contextId)
        {
            await _dbContext.InitializeAsync();
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            await using var reader = await connection.ExecuteReaderAsync(@"
                SELECT ContextId, Status, Summary, CreatedAt, UpdatedAt, UpdatedBySessionId, UpdatedByName,
                       RequesterSessionId, RequesterName
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
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8));
        }

        public async Task<ContextUpdateResult> UpdateAsync(string contextId, string summary, string? status,
            string? updatedBySessionId, string? updatedByName)
        {
            await _dbContext.InitializeAsync();
            await using var connection = _dbContext.CreateConnection();
            await connection.OpenAsync();

            var now = DateTime.UtcNow.ToString("o");

            if (string.IsNullOrEmpty(status))
            {
                var affected = await connection.ExecuteNonQueryAsync(@"
                    UPDATE Contexts SET Summary = @summary, UpdatedAt = @now,
                        UpdatedBySessionId = @updatedBySessionId, UpdatedByName = @updatedByName
                    WHERE ContextId = @contextId",
                    ("@contextId", contextId),
                    ("@summary", summary),
                    ("@now", now),
                    ("@updatedBySessionId", updatedBySessionId),
                    ("@updatedByName", updatedByName));
                return new ContextUpdateResult(affected > 0, StatusTransitioned: false);
            }

            // status 指定ありは「読まずに条件付き UPDATE で決める」のが基本。
            // どの UPDATE も **status を条件に含める**ので、判定してから書くまでの間に
            // 別の呼び出しが status を動かしても、こちらの書き込みが後乗せで通ることはない。
            // 競合して0行になったら、新しい現在値でやり直す（数回で収束する）。
            var isTerminal = TerminalStatuses.Contains(status, StringComparer.Ordinal);

            for (var attempt = 0; attempt < MaxUpdateAttempts; attempt++)
            {
                // まず「status が今と違う場合だけ」更新する。1行更新できた＝**この呼び出しが遷移を
                // 成立させた**ということなので、完了通知を撃つ資格があるのはこの呼び出しだけになる。
                // 読み取ってから判定すると、同時に書いた2者が両方とも遷移したと誤認する。
                //
                // あわせて**終端状態からの巻き戻しを拒む**。一度 completed になった札へ working を
                // 書けてしまうと、依頼が閉じたのに一覧上は進行中に戻り、TTL 掃除の対象からも外れる。
                // 終端 → 別の終端（completed と failed の競合等）は許す。片方が「配送できなかった」を
                // 書いた後に人間経由で実際に完了した、といった正当な上書きがあるため。
                var transitioned = await connection.ExecuteNonQueryAsync($@"
                    UPDATE Contexts SET Summary = @summary, Status = @status, UpdatedAt = @now,
                        UpdatedBySessionId = @updatedBySessionId, UpdatedByName = @updatedByName
                    WHERE ContextId = @contextId AND Status <> @status
                      AND (@isTerminal = 1 OR Status NOT IN ({TerminalStatusList}))",
                    ("@contextId", contextId),
                    ("@summary", summary),
                    ("@status", status),
                    ("@now", now),
                    ("@isTerminal", isTerminal ? 1 : 0),
                    ("@updatedBySessionId", updatedBySessionId),
                    ("@updatedByName", updatedByName));

                if (transitioned > 0)
                    return new ContextUpdateResult(true, StatusTransitioned: true);

                // 0行だったのは「札が無い」「同じ status への書き直し」「終端からの巻き戻しを拒否」の
                // いずれか。どれなのかは呼び出し側へ返す必要があるので現在値を読む。
                var current = await connection.ExecuteScalarAsync<string>(
                    "SELECT Status FROM Contexts WHERE ContextId = @contextId",
                    ("@contextId", contextId));

                if (current == null)
                    return new ContextUpdateResult(false, StatusTransitioned: false);

                if (!isTerminal && TerminalStatuses.Contains(current, StringComparer.Ordinal))
                    return new ContextUpdateResult(true, StatusTransitioned: false, RewindRejected: true);

                // 同じ status への書き直し。要約と記名を更新する（終端 status なら呼び出し側が
                // 通知も撃つ＝「再完了」の続報を依頼元へ届けるため）。
                //
                // **ここでも Status を条件に入れる**のが要点。付けずに書くと、現在値を読んでから
                // この UPDATE が走るまでの間に別の呼び出しが completed → failed を成立させた場合、
                // 古い completed 側の要約だけが後から乗り、「status=failed / summary=完了内容」
                // という食い違った札になる。
                var rewritten = await connection.ExecuteNonQueryAsync(@"
                    UPDATE Contexts SET Summary = @summary, UpdatedAt = @now,
                        UpdatedBySessionId = @updatedBySessionId, UpdatedByName = @updatedByName
                    WHERE ContextId = @contextId AND Status = @status",
                    ("@contextId", contextId),
                    ("@summary", summary),
                    ("@status", status),
                    ("@now", now),
                    ("@updatedBySessionId", updatedBySessionId),
                    ("@updatedByName", updatedByName));

                if (rewritten > 0)
                    return new ContextUpdateResult(true, StatusTransitioned: false);

                // 0行＝読んだ直後に status が動いた。新しい現在値でやり直す。
            }

            // ここへ来るのは、毎回その隙間で status が動き続けた場合だけ（現実には起きない）。
            // 黙って成功扱いにすると書けていないのに書けたつもりになるので、失敗として返す。
            // **Found=true のまま Conflicted で返す**。札は存在しているので、
            // 「contextId が見つかりません」と報告すると原因も対処も誤って伝わる。
            _logger.LogWarning(
                "[Context] status の競合が続いて更新できませんでした: {ContextId} (試行={Attempts})",
                contextId, MaxUpdateAttempts);
            return new ContextUpdateResult(true, StatusTransitioned: false, Conflicted: true);
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
                var terminalList = TerminalStatusList;
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
