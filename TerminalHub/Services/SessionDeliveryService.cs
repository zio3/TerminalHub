using Microsoft.Extensions.Logging;
using TerminalHub.Models;

namespace TerminalHub.Services;

/// <summary>配送の結果。success=true でも「届いた」とは限らないことを呼び出し側へ伝えるために分ける。</summary>
public enum DeliveryOutcome
{
    /// <summary>ConPTY へ書き込んだ。</summary>
    Delivered,
    /// <summary>宛先が入力待ちのため積んだ。ready になり次第配送される。</summary>
    Queued,
    /// <summary>受理できなかった（未起動・待ち行列が上限）。**同じ内容で再送してよい**。</summary>
    Rejected,
    /// <summary>
    /// 書き込みの途中で失敗した。**同じ内容で再送してはいけない**（本文だけ相手の入力欄へ
    /// 届いている可能性があり、再送すると二重に連結される）。Rejected と分けているのは、
    /// 呼び出し側に伝えるべき次の行動が正反対だから。
    /// </summary>
    Failed,
}

public interface ISessionDeliveryService
{
    /// <summary>
    /// 宛先セッションへ1件送る。宛先が入力待ちなら積んで、ready になってから配送する。
    /// </summary>
    Task<DeliveryOutcome> SendAsync(
        SessionInfo target,
        string text,
        bool submit,
        string? contextId = null,
        Guid? requesterSessionId = null);

    /// <summary>
    /// ContextSummary が終端 status へ遷移したことを依頼元セッションへ通知する。
    /// 依頼元が記録されていない（外部クライアントの依頼）場合は何もしない。
    /// </summary>
    Task NotifyContextStatusAsync(string contextId, string status);
}

/// <summary>
/// 送信の配送を担うサービス。MCP の送信・コールバック通知はすべてここを通る。
///
/// 設計（壁打ちで確定）:
/// - **リトライは呼び出し側の責務、という当初方針を撤回した**。send_to_session が
///   「宛先が入力待ち」で失敗しても、呼び出し元がセッションならその直後にターンが終わるため、
///   再試行する契機が存在しない（結果のポーリングが成立しないのと同じ構造の穴）。
///   「ready になったら送る」に推論は一切要らないので、システムへ移譲する。
/// - 待ちの解消は hook イベントで拾う。ただし待ちフラグは SessionInfo の素のフィールドで
///   変更通知がなく（SessionInfo.IsWaitingForUserInput）、hook を持たない CLI・
///   タイムアウト解除・ConPTY 起動は hook を通らないため、低頻度の掃除タイマーを安全網に置く。
/// - **本文と Enter は1単位**。間に別の送信が割り込むと入力が混線するので宛先ごとに直列化する。
/// - 配送に失敗したら、依頼元が「届かなかった」と分かるようにする（札に failed を書くか、
///   依頼元へ直接通知）。ただし**システム発の通知の失敗は二次通知を作らない**（連鎖の終端）。
/// </summary>
public sealed class SessionDeliveryService : ISessionDeliveryService, IHostedService, IDisposable
{
    /// <summary>積んだまま配送できなかった項目を諦めるまでの時間。</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    /// <summary>hook を通らない待ち解消（タイムアウト解除・非hook CLI・ConPTY起動）を拾う安全網の間隔。</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 本文送信から Enter までの待ち。Codex 等の TUI CLI は本文取り込み前に \r が来ると
    /// 送信確定されず入力欄で止まるため、UI の SendInput と同じく 0.2 秒挟む。
    /// </summary>
    private static readonly TimeSpan SubmitDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// システムが札へ書き込むときの記名。proof からしか設定されない値なので
    /// エージェントには詐称できず、null（無記名＝外部クライアント）とも区別できる。
    /// </summary>
    public const string SystemWriterName = "TerminalHub (system)";

    private readonly DeliveryQueue _queue = new();
    private readonly Dictionary<Guid, SemaphoreSlim> _writeLocks = new();
    private readonly ISessionManager _sessionManager;
    private readonly IContextRepository _contextRepository;
    private readonly IHookNotificationService _hookNotificationService;
    private readonly ILogger<SessionDeliveryService> _logger;
    private System.Threading.Timer? _sweepTimer;

    public SessionDeliveryService(
        ISessionManager sessionManager,
        IContextRepository contextRepository,
        IHookNotificationService hookNotificationService,
        ILogger<SessionDeliveryService> logger)
    {
        _sessionManager = sessionManager;
        _contextRepository = contextRepository;
        _hookNotificationService = hookNotificationService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 主トリガ: hook で待ちフラグが動いた直後に配送を試みる。
        _hookNotificationService.OnHookNotification += OnHookNotification;
        // 安全網: hook を通らない待ち解消を拾う。
        _sweepTimer = new System.Threading.Timer(_ => _ = SweepAsync(), null, SweepInterval, SweepInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _hookNotificationService.OnHookNotification -= OnHookNotification;
        _sweepTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _sweepTimer?.Dispose();
        lock (_writeLocks)
        {
            foreach (var semaphore in _writeLocks.Values)
                semaphore.Dispose();
            _writeLocks.Clear();
        }
    }

    private void OnHookNotification(object? sender, HookNotificationEventArgs e)
    {
        // hook ハンドラ本体が待ちフラグを更新した後に呼ばれる想定。配送は投げっぱなしで良い
        // （失敗しても次の掃除タイマーが拾う）。
        //
        // ただし**例外は必ずここで捕まえてログに残す**。投げっぱなしのまま外へ出すと
        // unobserved task exception になって何も記録されず、「配送が静かに効かない」状態に
        // なる（掃除タイマー側は try/catch 済みなので、ここだけ無防備だった）。
        _ = Task.Run(async () =>
        {
            try
            {
                await FlushAllAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[配送] hook 起点の配送で例外");
            }
        });
    }

    public async Task<DeliveryOutcome> SendAsync(
        SessionInfo target,
        string text,
        bool submit,
        string? contextId = null,
        Guid? requesterSessionId = null)
    {
        // ConPTY 未接続は積んでも意味がない（起動は人間の操作が要る）ので即座に拒否する。
        if (target.ConPtySession == null)
            return DeliveryOutcome.Rejected;

        var item = new DeliveryItem(
            target.SessionId, text, submit, DateTime.UtcNow, contextId, requesterSessionId,
            IsSystemCallback: false);

        return await EnqueueOrWriteAsync(target, item);
    }

    /// <summary>
    /// 直接書くか積むかを決めて実行する。
    ///
    /// **判定・投函・直接書き込みをすべて同じ宛先ロックの中で行う**。待ち行列の件数を
    /// ロックの外で見ると、同時に来た2件がどちらも「行列は空」と判断して直接書き込み経路へ入り、
    /// 書き込み自体は直列化されるものの、どちらが先になるかがタスクのスケジューリング任せになる。
    /// ロック内で判定すれば、2件目は必ず「行列に1件ある」を見て積むので到着順が保たれる。
    /// </summary>
    private async Task<DeliveryOutcome> EnqueueOrWriteAsync(SessionInfo target, DeliveryItem item)
    {
        var gate = GetWriteLock(target.SessionId);
        await gate.WaitAsync();
        try
        {
            // 先着の待ち行列が無いときだけ直接書く。行列があるのに割り込むと FIFO が壊れる。
            // 入力待ちかどうかは WritePairAsync が書き込み直前に見る（NotReady が返る）。
            if (_queue.CountFor(target.SessionId) == 0)
            {
                switch (await WritePairAsync(target, item))
                {
                    case WriteResult.Delivered:
                        return DeliveryOutcome.Delivered;

                    // 書き込みが失敗した宛先は積まない。ConPTY が死んでいれば ready には戻らず、
                    // TTL の5分ぶん失敗を隠すだけになる。呼び出し元へその場で返す
                    // （旧実装が例外で即座に失敗を伝えていたのと同じ即時性を保つ）。
                    case WriteResult.Failed:
                        return DeliveryOutcome.Failed;
                }
            }

            if (!_queue.Enqueue(item))
            {
                _logger.LogWarning(
                    "[配送] 待ち行列が上限のため受理できません: {Target} (上限={Max})",
                    target.GetDisplayName(), DeliveryQueue.MaxPerTarget);
                return DeliveryOutcome.Rejected;
            }

            _logger.LogInformation(
                "[配送] 宛先が入力待ちのため積みました: {Target} (待ち={Count})",
                target.GetDisplayName(), _queue.CountFor(target.SessionId));
            return DeliveryOutcome.Queued;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 書き込みの結果。**「今は書けない」と「書き込みが失敗した」を混ぜない**のが要点。
    /// 混ぜると、ConPTY が死んでいる宛先へ TTL いっぱい再試行し続けたうえ、
    /// 本文だけ書けて Enter で失敗したケースで本文をもう一度頭から書いてしまう。
    /// </summary>
    private enum WriteResult
    {
        /// <summary>本文（と Enter）を書き終えた。</summary>
        Delivered,
        /// <summary>いま書ける状態にない（入力待ち・ConPTY 未接続）。キューに残して後で再試行してよい。</summary>
        NotReady,
        /// <summary>書き込み自体が失敗した。**再試行しない**（本文が途中まで届いている可能性がある）。</summary>
        Failed,
    }

    /// <summary>
    /// 本文＋Enter を1単位として書き込む（ロックは呼び出し側が保持している前提）。
    /// 書き込み直前に状態を再確認するのは、積んでから ready を確認するまでの間に
    /// また待ちへ入っていることがあるため。
    ///
    /// 例外時に <see cref="WriteResult.Failed"/> を返して**再試行させない**のは、
    /// 本文の書き込みが成功したあと Enter で失敗した場合に、同じ項目を積み直すと
    /// 本文が二重に連結されて届くため。「1単位」を守れなかった時点で、やり直しではなく
    /// 失敗として報告するのが正しい（本文は相手の入力欄に残っている可能性がある）。
    /// </summary>
    private async Task<WriteResult> WritePairAsync(SessionInfo target, DeliveryItem item)
    {
        var conpty = target.ConPtySession;
        if (conpty == null || target.IsWaitingForUserInput)
            return WriteResult.NotReady;

        try
        {
            await conpty.WriteAsync(item.Text);
            if (item.Submit)
            {
                await Task.Delay(SubmitDelay);
                await conpty.WriteAsync("\r");
            }
            return WriteResult.Delivered;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[配送] 書き込みに失敗: {Target}", target.GetDisplayName());
            return WriteResult.Failed;
        }
    }

    private SemaphoreSlim GetWriteLock(Guid sessionId)
    {
        lock (_writeLocks)
        {
            if (!_writeLocks.TryGetValue(sessionId, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _writeLocks[sessionId] = gate;
            }
            return gate;
        }
    }

    /// <summary>
    /// 待ちが解消した宛先へ、積んである順に配送する。
    /// 宛先ごとのロックを掴んだまま「覗く→書く→捨てる」を回すので、hook イベントと
    /// 掃除タイマーが同時に走っても同じ項目を二重に配送しない。
    /// </summary>
    private async Task FlushAllAsync()
    {
        foreach (var targetId in _queue.PendingTargets())
        {
            var target = _sessionManager.GetSessionInfo(targetId);
            if (target == null || target.ConPtySession == null || target.IsWaitingForUserInput)
                continue;

            // 失敗の通知はロックを解放してから行う。通知先が宛先自身のこともあり
            // （自分に依頼して自分が詰まっている場合）、掴んだまま送ると
            // SemaphoreSlim は再入不可なので自分自身とデッドロックする。
            var failures = new List<DeliveryItem>();

            var gate = GetWriteLock(targetId);
            await gate.WaitAsync();
            try
            {
                while (_queue.TryPeek(targetId, out var item))
                {
                    var result = await WritePairAsync(target, item);

                    if (result == WriteResult.Delivered)
                    {
                        _queue.RemoveHead(targetId);
                        continue;
                    }

                    if (result == WriteResult.Failed)
                    {
                        // 再試行しない（本文が途中まで届いている可能性がある）。
                        // 書き込み経路自体が壊れている見込みなので、残りは次の掃除に任せる。
                        _queue.RemoveHead(targetId);
                        failures.Add(item);
                    }

                    // NotReady なら先頭に残したまま打ち切る＝順序が保たれる。
                    break;
                }
            }
            finally
            {
                gate.Release();
            }

            foreach (var item in failures)
            {
                await HandleFailureAsync(item, FailureKind.WriteFailed);
            }
        }
    }

    private async Task SweepAsync()
    {
        try
        {
            foreach (var targetId in _queue.PendingTargets())
            {
                // **配送中の項目を失効させないため、宛先のロックを取ってから剥がす。**
                // 配送は「覗く→書く→捨てる」で進むので、書いている最中の項目は
                // まだキューの先頭に居る。ここで排他しないと、配送済みの項目が
                // 失敗と報告され、直後の RemoveHead が次の項目を消してしまう。
                IReadOnlyList<DeliveryItem> expired;
                var gate = GetWriteLock(targetId);
                await gate.WaitAsync();
                try
                {
                    expired = _queue.RemoveExpiredFor(targetId, DateTime.UtcNow, Ttl);
                }
                finally
                {
                    gate.Release();
                }

                // 通知はロック解放後（FlushAllAsync と同じくデッドロック回避のため）。
                foreach (var item in expired)
                {
                    await HandleFailureAsync(item, FailureKind.Expired);
                }
            }

            await FlushAllAsync();
        }
        catch (Exception ex)
        {
            // 掃除の失敗でタイマーを死なせない。
            _logger.LogWarning(ex, "[配送] 掃除処理で例外");
        }
    }

    /// <summary>
    /// 配送できなかった理由。**「届いていない」と言い切れるかどうかが違う**ので必ず区別する。
    /// 依頼元はこの文面を読んで再送するかを決めるため、届いた可能性があるのに
    /// 「届いていません」と書くと、本文の重複・二重実行を招く。
    /// </summary>
    private enum FailureKind
    {
        /// <summary>TTL 超過。一度も書いていないので**確実に届いていない**。</summary>
        Expired,
        /// <summary>書き込みの途中で失敗した。**届いたかどうか確認できない**（本文だけ届いた可能性がある）。</summary>
        WriteFailed,
    }

    private static string DescribeFailure(FailureKind kind, string targetName) => kind switch
    {
        FailureKind.Expired =>
            $"宛先「{targetName}」へ配送できませんでした（{Ttl.TotalMinutes:0} 分間 宛先の入力待ちが解消しなかったため配送を諦めました）。" +
            "一度も書き込んでいないので、メッセージは届いていません。同じ内容を送り直して構いません。",
        _ =>
            $"宛先「{targetName}」への書き込みが途中で失敗しました。**届いたかどうか確認できません**" +
            "（本文だけが相手の入力欄に残っている可能性があります）。" +
            "同じ内容を再送する前に、人間に宛先の入力欄を確認してもらってください。",
    };

    /// <summary>
    /// 配送できなかったことを依頼元へ伝える。
    /// システム発の通知（コールバック）の失敗はログだけに留める＝失敗通知の連鎖を止める終端条件。
    /// </summary>
    private async Task HandleFailureAsync(DeliveryItem item, FailureKind kind)
    {
        var targetName = _sessionManager.GetSessionInfo(item.TargetSessionId)?.GetDisplayName()
            ?? item.TargetSessionId.ToString();
        var description = DescribeFailure(kind, targetName);

        if (item.IsSystemCallback)
        {
            _logger.LogWarning("[配送] システム通知の配送に失敗（二次通知はしない）: {Target} — {Kind}",
                targetName, kind);
            return;
        }

        _logger.LogWarning("[配送] 配送に失敗: {Target} — {Kind}", targetName, kind);

        if (!string.IsNullOrEmpty(item.ContextId))
        {
            // 札を failed にする。終端状態にしておかないと TTL 掃除の対象外になり、
            // 使われない行が永久に残る（ContextRepository の後片付けと同じ理由）。
            var updated = await _contextRepository.UpdateAsync(
                item.ContextId, description, "failed", null, SystemWriterName);

            // 既に終端状態だった（受け手が先に書いた等）なら通知しない。
            // 遷移を成立させた側だけが撃つ、という規則は update_context と共通。
            if (updated.StatusTransitioned)
                await NotifyContextStatusAsync(item.ContextId, "failed");
            return;
        }

        if (item.RequesterSessionId.HasValue)
        {
            await SendSystemCallbackAsync(item.RequesterSessionId.Value, $"[TerminalHub] {description}");
        }
    }

    public async Task NotifyContextStatusAsync(string contextId, string status)
    {
        var record = await _contextRepository.GetAsync(contextId);
        if (record == null)
            return;

        // 依頼元が記録されていない＝外部クライアントからの依頼。ポーリングで取ってもらう。
        if (string.IsNullOrEmpty(record.RequesterSessionId) ||
            !Guid.TryParse(record.RequesterSessionId, out var requesterId))
            return;

        await SendSystemCallbackAsync(requesterId,
            $"[TerminalHub] 依頼(contextId: {contextId})が {status} になりました。詳細は get_context で取得してください。" +
            "処理中は選択肢を出さず、必要なことは update_context に書いて終えてください。");
    }

    private async Task SendSystemCallbackAsync(Guid requesterSessionId, string text)
    {
        var requester = _sessionManager.GetSessionInfo(requesterSessionId);
        if (requester == null || requester.ConPtySession == null)
        {
            // 依頼元が消えている/未起動なら諦める（依頼元は get_context で取れる）。
            _logger.LogInformation(
                "[配送] 依頼元へ通知できません（セッションが無い/未起動）: {Requester}", requesterSessionId);
            return;
        }

        var item = new DeliveryItem(
            requesterSessionId, text, Submit: true, DateTime.UtcNow,
            ContextId: null, RequesterSessionId: null, IsSystemCallback: true);

        await EnqueueOrWriteAsync(requester, item);
    }
}
