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
    /// 宛先セッションへ1件送る（本文＋Enter＝確定送信のみ。「流し込むだけ」は提供しない）。
    /// 宛先が入力待ちなら積んで、ready になってから配送する。
    /// </summary>
    Task<DeliveryOutcome> SendAsync(
        SessionInfo target,
        string text,
        string? contextId = null,
        Guid? requesterSessionId = null);

    /// <summary>
    /// ContextSummary に終端 status が書き込まれたことを依頼元セッションへ通知する
    /// （通知の要否＝遷移時のみか毎回かは呼び出し側が決める。update_context 経路は
    /// 再書き込みでも呼び、システムの失敗記録経路は遷移時のみ呼ぶ）。
    /// 依頼元が記録されていない（外部クライアントの依頼）場合は何もしない。
    /// writerSessionId を渡すと、それが依頼元自身のときは通知しない（自己再通知ループ防止。
    /// <see cref="ContextNotifyPolicy"/> 参照）。
    /// </summary>
    Task NotifyContextStatusAsync(string contextId, string status, Guid? writerSessionId = null);

    /// <summary>
    /// UI 操作から宛先セッションへ「1 行＋Enter」を書く（放置セッション整理の終了コマンド等）。
    /// MCP 配送と同じ宛先ロックを取るので、配送の本文と Enter の間に割り込まず、逆に配送から
    /// 割り込まれもしない。エンベロープは付けず、積まず（宛先が待ち状態なら false で即戻る）、
    /// 提出監視（SubmitWatch）にも載せない（/exit 等は UserPromptSubmit が来ないため誤 WARN になる）。
    /// 本文と Enter の両方が書けたときだけ true。
    /// </summary>
    Task<bool> WriteLineDirectAsync(SessionInfo target, string text);
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
    /// 送信確定されず入力欄で止まるため、間を挟む。
    ///
    /// UI の SendInput と同じ 0.2 秒だったが、その値でも取りこぼしが出る（本文は入力欄に
    /// 入っているのに未提出のまま止まり、人間が手で Enter を押すまで進まない。Claude Code /
    /// Codex どちらでも観測）。この経路は自動メッセージの配送で人間が待っていないので、
    /// 待ちを倍にしても誰も気づかない。UI 側は人が待つので伸ばさず、ここだけ 0.4 秒にする。
    /// 根治ではない（TUI 側の取りこぼしなので確率を下げるだけ）。頻発するなら
    /// UserPromptSubmit hook を ACK にした Enter 再送ウォッチドッグへ進む。
    /// </summary>
    private static readonly TimeSpan SubmitDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// システムが札へ書き込むときの記名。接続キー検証からしか設定されない値なので
    /// エージェントには詐称できず、null（無記名＝外部クライアント）とも区別できる。
    /// </summary>
    public const string SystemWriterName = "TerminalHub (system)";

    private readonly DeliveryQueue _queue = new();
    /// <summary>
    /// 配送の「書いた」と「相手が提出を認めた（UserPromptSubmit）」の突き合わせ。
    /// 期限切れで Enter を1回だけ再送する（Enter 再送ウォッチドッグ）。
    /// 書き込み側（宛先ロック内）と hook 側（イベントスレッド）と掃除タイマーから触るので
    /// <see cref="_submitWatchLock"/> で囲む。
    /// </summary>
    private readonly SubmitWatch _submitWatch = new();
    private readonly object _submitWatchLock = new();
    private readonly Dictionary<Guid, SemaphoreSlim> _writeLocks = new();
    private readonly ISessionManager _sessionManager;
    private readonly IContextRepository _contextRepository;
    private readonly IDeliveryRepository _deliveryRepository;
    private readonly IHookNotificationService _hookNotificationService;
    private readonly ILogger<SessionDeliveryService> _logger;
    private System.Threading.Timer? _sweepTimer;

    public SessionDeliveryService(
        ISessionManager sessionManager,
        IContextRepository contextRepository,
        IDeliveryRepository deliveryRepository,
        IHookNotificationService hookNotificationService,
        ILogger<SessionDeliveryService> logger)
    {
        _sessionManager = sessionManager;
        _contextRepository = contextRepository;
        _deliveryRepository = deliveryRepository;
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

        // **宛先ごとの SemaphoreSlim は破棄しない。** 破棄すると、進行中の配送が
        // gate.Release() で ObjectDisposedException を投げうる。それは「本文は書けたのに
        // 例外で失敗として扱われる」ことを意味し、SendToSession の後始末が
        // 配送済みの札を消してしまう（＝相手の入力欄には contextId 入りの本文が残るのに、
        // その ID がもう存在しない）。
        //
        // SemaphoreSlim は AvailableWaitHandle を使わない限りアンマネージド資源を持たないので、
        // 破棄しないことによる漏れは無い。持ち主はアプリと同じ寿命の Singleton で、
        // ここは終了時にしか呼ばれない。
        lock (_writeLocks)
        {
            _writeLocks.Clear();
        }
    }

    private void OnHookNotification(object? sender, HookNotificationEventArgs e)
    {
        ObserveSubmit(e.Notification);

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
        string? contextId = null,
        Guid? requesterSessionId = null)
    {
        // ConPTY 未接続は積んでも意味がない（起動は人間の操作が要る）ので即座に拒否する。
        if (target.ConPtySession == null)
            return DeliveryOutcome.Rejected;

        var item = new DeliveryItem(
            target.SessionId, text, DateTime.UtcNow, contextId, requesterSessionId,
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

    /// <inheritdoc />
    public async Task<bool> WriteLineDirectAsync(SessionInfo target, string text)
    {
        var gate = GetWriteLock(target.SessionId);
        await gate.WaitAsync();
        try
        {
            // ロック待ちの間に状態が変わりうるので、取ってから見る
            var conpty = target.ConPtySession;
            if (conpty == null || conpty.HasExited || target.IsWaitingForUserInput)
                return false;
            if (!await conpty.TryWriteAsync(text))
                return false;
            await Task.Delay(SubmitDelay);
            return await conpty.TryWriteAsync("\r");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[配送] 直接書き込みに失敗: {Target}", target.GetDisplayName());
            return false;
        }
        finally
        {
            gate.Release();
        }
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
            // 常に Enter で確定する。「流し込むだけ(submit=false)」は実利用ゼロで廃止した
            // （入力欄に置いて人間が確認する用途はセッション専用コマンドの insertToInputOnly が担う）。
            await Task.Delay(SubmitDelay);
            await conpty.WriteAsync("\r");
            ArmSubmitWatch(target, item);
            return WriteResult.Delivered;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[配送] 書き込みに失敗: {Target}", target.GetDisplayName());
            return WriteResult.Failed;
        }
    }

    /// <summary>
    /// 書き込み成功を Info で1行残し、提出（UserPromptSubmit）待ちとして記録する。
    /// 従来は成功時に何もログが無く、「Enter が呑まれて未提出」の事後調査で送った時刻すら
    /// 追えなかった。hook を持たない CLI は UserPromptSubmit が来ないので記録しない
    /// （WARN が必ず出る誤検知になる）。
    ///
    /// Enter の再送を許すのは**書き込み時に宛先が idle だった場合だけ**。処理中の宛先へ送ると
    /// 相手 CLI 側のキューに積まれて UserPromptSubmit が遅れるのが正常なので、そこへ Enter を
    /// 足すと誤再送になる（提出済みなら空 Enter で無害だが、判断材料を濁さないため送らない）。
    /// </summary>
    private void ArmSubmitWatch(SessionInfo target, DeliveryItem item)
    {
        var hookDriven = target.TerminalType is TerminalType.ClaudeCode or TerminalType.CodexCLI;
        var idle = IsIdle(target);
        SubmitWatchEntry? entry = null;
        if (hookDriven)
        {
            // 自分の Enter を書き終えた直後の通し番号。再送直前にこれが進んでいたら、人間のキー入力や
            // 別の送信が入力欄に触れているので再送しない（入力欄がまだこの配送のものである根拠）。
            var inputSequence = target.ConPtySession?.InputSequence ?? 0;
            lock (_submitWatchLock)
            {
                entry = _submitWatch.Arm(target.SessionId, item.Text, DateTime.UtcNow,
                    resendAllowed: idle, inputSequence: inputSequence);
            }
        }

        _logger.LogInformation(
            "[配送] 書き込み完了: {Target} {Tag} ({Length}文字, 宛先は{Status}{Watch})",
            target.GetDisplayName(),
            entry?.Tag ?? SubmitWatch.MakeTag(item.Text),
            item.Text.Length,
            idle ? "idle" : "処理中",
            !hookDriven ? "・hook無しのため提出確認なし"
                : idle ? "・提出確認待ち（未確認なら Enter 再送）"
                : "・提出確認待ち（処理中のため再送なし）");
    }

    /// <summary>
    /// 宛先が「何もしていない」か。処理中フラグ（hook / 出力解析）も許可・選択待ちも立っていない状態。
    /// SessionInfo に IsProcessing は無いので、処理開始時刻とステータス文字列の両方で見る。
    /// </summary>
    private static bool IsIdle(SessionInfo target) =>
        target.ProcessingStartTime == null
        && target.ProcessingStatus == null
        && !target.IsWaitingForUserInput;

    /// <summary>宛先の UserPromptSubmit を提出の ACK として突き合わせる。</summary>
    private void ObserveSubmit(HookNotification notification)
    {
        // サブエージェント内部の UserPromptSubmit（Codex が agent_id 付きで撃つ）はメインの提出ではない。
        if (notification.GetEventType() != HookEventType.UserPromptSubmit || notification.AgentId != null)
            return;

        SubmitWatchEntry? entry;
        lock (_submitWatchLock)
        {
            entry = _submitWatch.Confirm(notification.SessionId);
        }
        if (entry == null)
            return;

        var elapsed = DateTime.UtcNow - entry.WrittenAt;
        _logger.LogDebug(
            "[配送] 提出確認: {Target} {Tag} (書き込みから {ElapsedMs}ms)",
            _sessionManager.GetSessionInfo(entry.TargetSessionId)?.GetDisplayName() ?? entry.TargetSessionId.ToString(),
            entry.Tag,
            (long)elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// 期限を過ぎても提出確認が来ない配送を処理する（掃除タイマーから呼ぶ）。
    /// 再送を許された配送（書き込み時 idle・未再送）は行列に残したまま（<see cref="SubmitWatchEntry.Resending"/>）
    /// Enter だけを1回再送して再監視に入れる。それ以外（処理中だった / 再送済み）は既に行列から
    /// 外れているので WARN を出して終わる。
    /// </summary>
    private async Task HandleUnconfirmedSubmitsAsync()
    {
        IReadOnlyList<SubmitWatchEntry> expired;
        lock (_submitWatchLock)
        {
            expired = _submitWatch.Expire(DateTime.UtcNow);
        }
        foreach (var entry in expired)
        {
            var target = _sessionManager.GetSessionInfo(entry.TargetSessionId);
            var name = target?.GetDisplayName() ?? entry.TargetSessionId.ToString();
            var waited = (DateTime.UtcNow - entry.WrittenAt).TotalSeconds;

            if (entry.Resending)
            {
                if (target != null)
                {
                    await ResendEnterAsync(target, entry, waited);
                    continue;
                }
                lock (_submitWatchLock)
                {
                    _submitWatch.CancelResend(entry);
                }
            }

            _logger.LogWarning(
                "[配送] 提出未確認: {Target} {Tag} 書き込みから {Seconds:0}秒たっても UserPromptSubmit が来ない（{Reason}。相手の入力欄に本文が残っていないか確認）",
                name, entry.Tag, waited,
                entry.Attempt > 0 ? "Enter を再送しても提出されない"
                    : entry.Resending ? "宛先が消えたため再送できない"
                    : "宛先が処理中だったため再送しない");
        }
    }

    /// <summary>
    /// Enter だけを1回再送する（Enter 再送ウォッチドッグの本体）。
    /// - 本文は再送しない（入力欄に残っている本文と二重連結になる）。
    /// - 宛先ロックを取って送る（他の配送の本文と Enter の間に割り込まない）。
    /// - **書く直前に、ロックの中で全部の条件をもう一度見る**（期限切れ判定からここまでの間に
    ///   状態が変わりうる）:
    ///   1. 提出確認がまだ来ていない（遅れて届いた UserPromptSubmit は行列から外すので、ここで分かる）
    ///   2. 宛先が idle のまま（処理中に変わっていたら相手のキューに載っただけの可能性が高い）
    ///   3. 許可・選択待ちに変わっていない（Enter が承認や選択の確定になる）
    ///   4. 自分の Enter 以降、この ConPTY に誰も書いていない（人間のキー入力・UI 送信・別の配送が
    ///      入力欄に触れていたら、その内容を勝手に確定送信することになる）。これは宛先ロックでは
    ///      防げない（UI のキー入力は宛先ロックを通らず ConPTY に直接書く）ので、
    ///      <see cref="ConPtySession.TryWriteIfUnchangedAsync"/> で**番号の比較と Enter の書き込みを
    ///      ConPTY 側の同じ排他区間で**行う。
    ///   どれか1つでも崩れていたら再送せず監視を終える（WARN）。
    /// - 再送後は Attempt=1 で再監視し、それでも来なければ WARN で終わる（再送は1回だけ）。
    /// </summary>
    private async Task ResendEnterAsync(SessionInfo target, SubmitWatchEntry entry, double waited)
    {
        var gate = GetWriteLock(target.SessionId);
        await gate.WaitAsync();
        try
        {
            lock (_submitWatchLock)
            {
                if (!_submitWatch.IsPending(entry))
                {
                    _logger.LogDebug(
                        "[配送] 再送前に提出確認が届いたため Enter は再送しない: {Target} {Tag}",
                        target.GetDisplayName(), entry.Tag);
                    return;
                }
            }

            var conpty = target.ConPtySession;
            var reason =
                conpty == null ? "未接続"
                : target.IsWaitingForUserInput ? "許可/選択待ち"
                : !IsIdle(target) ? "処理中"
                : null;
            if (reason == null && !await conpty!.TryWriteIfUnchangedAsync("\r", entry.InputSequence))
                reason = "自分の Enter 以降に別の入力（人間のキー入力・UI 送信・別の配送）があった、または ConPTY へ書き込めなかった（破棄済み・パイプ切断）";
            if (reason != null)
            {
                lock (_submitWatchLock)
                {
                    _submitWatch.CancelResend(entry);
                }
                _logger.LogWarning(
                    "[配送] 提出未確認: {Target} {Tag} 書き込みから {Seconds:0}秒たっても UserPromptSubmit が来ないが、{Reason}ため Enter は再送しない（相手の入力欄に本文が残っていないか確認）",
                    target.GetDisplayName(), entry.Tag, waited, reason);
                return;
            }

            SubmitWatchEntry? rearmed;
            lock (_submitWatchLock)
            {
                rearmed = _submitWatch.FinishResend(entry, DateTime.UtcNow, conpty!.InputSequence);
            }
            if (rearmed == null)
            {
                _logger.LogDebug(
                    "[配送] Enter 再送と同時に提出確認が届いた（空の Enter が1回入っただけで害はない）: {Target} {Tag}",
                    target.GetDisplayName(), entry.Tag);
                return;
            }
            _logger.LogWarning(
                "[配送] Enter 再送: {Target} {Tag} 書き込みから {Seconds:0}秒たっても UserPromptSubmit が来ないため Enter だけを1回再送した",
                target.GetDisplayName(), entry.Tag, waited);
        }
        catch (Exception ex)
        {
            lock (_submitWatchLock)
            {
                _submitWatch.CancelResend(entry);
            }
            _logger.LogWarning(ex, "[配送] Enter 再送に失敗: {Target} {Tag}", target.GetDisplayName(), entry.Tag);
        }
        finally
        {
            gate.Release();
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
            // 提出未確認の処理（Enter 再送を含む）は待ち行列と無関係に毎回見る。
            await HandleUnconfirmedSubmitsAsync();

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
            // ここは**遷移を成立させたときだけ**撃つ: update_context 側は「同一終端への
            // 再書き込みでも通知する」（人間が意図した再完了の続報を届けるため）に緩めたが、
            // この経路は機械的な失敗記録なので、重複で依頼元を起こす価値がない。
            if (updated.StatusTransitioned)
                await NotifyContextStatusAsync(item.ContextId, "failed");
            return;
        }

        if (item.RequesterSessionId.HasValue)
        {
            await SendSystemCallbackAsync(item.RequesterSessionId.Value, $"[TerminalHub] {description}");
        }
    }

    public async Task NotifyContextStatusAsync(string contextId, string status, Guid? writerSessionId = null)
    {
        var record = await _contextRepository.GetAsync(contextId);
        if (record == null)
            return;

        // 依頼元が記録されていない＝外部クライアントからの依頼。ポーリングで取ってもらう。
        if (string.IsNullOrEmpty(record.RequesterSessionId) ||
            !Guid.TryParse(record.RequesterSessionId, out var requesterId))
            return;

        // 依頼元自身の書き込みには通知しない。「終端の書き込みで毎回通知」へ緩めた結果、
        // 依頼元が同じ札へ終端を書く → また通知、という自己励振ループが成立し得るため、
        // 書き込み元＝依頼元をここで断ち切る（レビュー指摘。かつては通知文面自体が
        // 「update_context に書いて終えよ」とループを誘発していた。文面は改めたが、
        // 依頼元＝キー付きセッションが札へ書けること自体は変わらないので防波堤として維持）。
        if (!ContextNotifyPolicy.ShouldNotifyRequester(record.RequesterSessionId, writerSessionId))
            return;

        // 通知は報告に徹する。受け手の作法（update_context に書く）をここに書くと、依頼元が
        // 従って受け手の結果を上書きする混入事故になる。「選択肢を出すな」もスコープが
        // 決められない指示になるため書かない（その案内は依頼開始時のエンベロープが
        // 「この依頼を終えるまで」とスコープ付きで持つ）。
        //
        // 「誰が終わらせたか」と「結果の要約」を本文に載せる。ID だけだと、札を複数抱えた
        // 依頼元は照合するまで何の通知か分からず、結果を見るのに get_context が必ず要る。
        // 担当名と要約が載っていれば大半はその場で済む（=無駄なツール呼び出しが減る）。
        //
        // 担当名も要約と同じサニタイズを通す。表示名は人間が自由入力するもので、`\r` が
        // 混ざれば受け手の TUI がそこを送信確定と解釈し、エンベロープより手前だけが
        // 「マーカー無し＝人間の指示」として実行される（PR #188 で本文について塞いだのと
        // 同じ迂回。本文に埋め込むものは経路を問わず1行へ潰す）。
        var worker = FlattenForNotice(record.UpdatedByName);
        if (worker.Length == 0)
            worker = "外部クライアント";
        var summary = FormatSummaryForNotice(record.Summary, out var truncated);

        var notice = $"[TerminalHub] 依頼(contextId: {contextId}) {status}（担当: {worker}）";
        if (summary.Length > 0)
            notice += $": {summary}";
        // 要約が無い/切り詰めたときだけ get_context へ誘導する。常に書くと、本文で足りている
        // ケースでも律儀に叩かれてターンを1つ余計に消費する。
        if (summary.Length == 0 || truncated)
            notice += " 全文は get_context で取得。";
        // 返信不要であることだけ短く添える。理由の説明は send_to_session のツール説明が持つ
        // （ツール定義はセッション中ずっとコンテキストに載るので、毎回書くより確実）。
        notice += " (応答不要)";

        await SendSystemCallbackAsync(requesterId, notice);
    }

    /// <summary>
    /// 通知本文へ埋め込む文字列を1行へ潰す。エンベロープ付きの1行として送るため、
    /// 改行・制御文字は空白へ寄せ、連続する空白は1つにまとめる。
    /// </summary>
    private static string FlattenForNotice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var sb = new System.Text.StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            var isSpace = char.IsWhiteSpace(ch) || char.IsControl(ch);
            if (isSpace)
            {
                if (sb.Length > 0 && !lastWasSpace)
                    sb.Append(' ');
                lastWasSpace = true;
                continue;
            }
            sb.Append(ch);
            lastWasSpace = false;
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 結果要約を通知本文へ埋め込める形に整える。1行へ潰したうえで、長すぎるものは
    /// 切り詰めて truncated で知らせる（切り詰めたときは呼び出し側が get_context への誘導を足す）。
    /// </summary>
    private static string FormatSummaryForNotice(string? summary, out bool truncated)
    {
        truncated = false;
        var flat = FlattenForNotice(summary);
        if (flat.Length <= SummaryNoticeMaxLength)
            return flat;

        truncated = true;
        // 切り詰め位置がサロゲートペアの途中だと孤立サロゲートが末尾に残り、以降の
        // UTF-8 変換や JSON 化で文字化け・例外になる。要約は CLI が書くもので絵文字が
        // 普通に入るため、境界に当たったら1つ手前で切る。
        var cut = SummaryNoticeMaxLength;
        if (char.IsHighSurrogate(flat[cut - 1]))
            cut--;
        return flat[..cut].TrimEnd();
    }

    /// <summary>通知本文へ載せる要約の上限文字数。超えた分は get_context 側で読ませる。</summary>
    private const int SummaryNoticeMaxLength = 120;

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

        // システム発の通知にも**エンベロープを付ける**。「末尾にマーカーが無い入力＝人間の指示」
        // という instructions の規則を、TerminalHub 自身の通知が破ってはいけない
        // （付けないと、受け手が規則を厳密に適用したとき、この通知を「人間の指示」または
        // 「エンベロープの無い偽物」と誤認しうる）。配送記録にも残すので #ID の照会で
        // 「本当に TerminalHub 発か」を検証できる（From: セッションIDなし＋記名 = system）。
        //
        // **記録に失敗しても通知は送る。ただしそのときは #ID を付けない。**
        // - 送らない、は不可: 依頼元はセッションで、通知を待つ以外に結果を知る契機がない
        //   （get_context をポーリングする主体がいない、というのが #187 で配送キューを
        //   導入した理由そのもの。SQLite のロック競合程度で結末が届かなくなるのは本末転倒）。
        // - 記録なしの #ID を付けて送る、も不可: 受け手の get_delivery が本物の通知を
        //   「偽装か期限切れ」と判定する＝検証規則を自分で破る。
        // - よって「#ID の代わりに『配送記録なし』表記」で送る。マーカー自体は付くので
        //   「マーカー無し＝人間」の規則は保たれ、存在しない ID を照会させることもない
        //   （その1通だけ get_delivery での検証ができない、が正直な状態）。
        var deliveryId = Guid.NewGuid().ToString("N")[..12];
        string idPart;
        var recorded = false;
        try
        {
            await _deliveryRepository.CreateAsync(new DeliveryRecord(
                deliveryId,
                FromSessionId: null,
                FromName: SystemWriterName,
                requesterSessionId.ToString(),
                requester.GetDisplayName(),
                ContextId: null,
                DateTime.UtcNow));
            recorded = true;
            idPart = $"#{deliveryId}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[配送] システム通知の配送記録に失敗（通知は「配送記録なし」表記で送る）: {DeliveryId}", deliveryId);
            idPart = "配送記録なし";
        }

        var marked = $"{text} [TerminalHub 自動メッセージ {idPart} — 送信元: {SystemWriterName}]";

        var item = new DeliveryItem(
            requesterSessionId, marked, DateTime.UtcNow,
            ContextId: null, RequesterSessionId: null, IsSystemCallback: true);

        var outcome = await EnqueueOrWriteAsync(requester, item);

        // 通常配送と同じ後始末をシステム通知にも適用する（send_to_session 側と対）。
        // Rejected は一度も書いていないことが確定しており、この記録は誰も参照できないため
        // 削除する（残すと Rejected の量産で真正な記録を押し出す口になる）。
        // それ以外は Pending → Committed へ確定してから上限掃除（掃除は Committed だけを数える）。
        if (recorded)
        {
            if (outcome == DeliveryOutcome.Rejected)
            {
                await _deliveryRepository.DeleteAsync(deliveryId);
            }
            else
            {
                await _deliveryRepository.CommitAsync(deliveryId);
                await _deliveryRepository.PruneToCapAsync();
            }
        }
    }
}
