using System.Diagnostics;

namespace TerminalHub.Services
{
    /// <summary>
    /// プロセス全体フリーズの診断用プローブ（実験コード）。
    ///
    /// 背景: 2026-07-17 に「全ログ沈黙＋リクエスト受付停止」が数秒〜十数秒続く
    /// フリーズを2回観測（17:54 に8.3秒、18:50 に11.9秒）。原因候補は
    /// ①同期ログシンクの巻き添えブロック ②GC長時間ポーズ ③ThreadPool枯渇。
    ///
    /// 2種類のプローブで犯人を切り分ける:
    /// - 専用スレッドプローブ: ThreadPool に依存しない Thread.Sleep ループ。
    ///   ここが止まる = GC / OSサスペンド級のプロセス全体停止
    /// - ThreadPoolプローブ: Task.Delay ループ。専用スレッドが無事なのに
    ///   こちらだけ遅れる = ThreadPool 枯渇
    ///
    /// 遅延検知時に GC.GetTotalPauseDuration の差分を添えるので、
    /// 停止時間 ≒ GCポーズ差分 なら犯人はGC、そうでなければ別要因と判定できる。
    /// 平常時の出力は起動時の1行と、60秒毎の定期ゲージ（蓄積リーク監視＋ConPTY読み）のみ。
    ///
    /// ConPTY読み（<see cref="ConPtyReadProbe"/>）は、かつて starvation の犯人だった
    /// 「読みループによる ThreadPool 占有」の回帰監視。読みループは専用スレッドへ移したので、
    /// **正常なら ConPTY読み が何本に増えても 使用中ワーカー は連動しない**。
    /// 逆に 使用中ワーカー ≒ ConPTY読み + 1 のように連動していたら、読みループが
    /// ThreadPool へ戻ってしまった（対策が外れた）サイン。
    /// </summary>
    public class FreezeProbeService : IHostedService, IDisposable
    {
        // ティック間隔と、これを超えたら記録する閾値（体感できる遅さより十分小さく）
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(2.5);

        // 定期ゲージ（蓄積リーク検出用）の出力間隔。購読者数などが増え続けないか監視する。
        private static readonly TimeSpan GaugeInterval = TimeSpan.FromSeconds(60);

        private readonly ILogger<FreezeProbeService> _logger;
        private readonly IHookNotificationService _hookNotificationService;
        private readonly ISessionManager _sessionManager;
        private readonly CancellationTokenSource _cts = new();
        private Thread? _dedicatedThread;
        private Task? _threadPoolTask;

        // ストール中のピーク値。ThreadPool プローブは詰まっている間そもそも動けず、
        // 遅延に気づくのは「明けた後」なので、そこで読む値は復帰後のスナップショットになってしまう。
        // 詰まっている最中も回り続ける専用スレッド（starvation では無事なことを確認済み）で毎秒サンプルし、
        // 最大値を持ち回ることで「詰まっていた瞬間の値」を遅延検出行に添える。
        // 書き手は2つある（専用スレッドが最大値を更新し、ThreadPool プローブが毎ティックで 0 に畳む）ので、
        // 更新は CAS、畳みは Interlocked.Exchange で行う。素の read-then-write だと畳みと更新が
        // 競合してピークを取りこぼし、「詰まっていなかった」と誤読させうる。
        private int _peakBlockedInRead;
        private int _peakBusyWorkers;

        public FreezeProbeService(
            ILogger<FreezeProbeService> logger,
            ILoggerFactory loggerFactory,
            IHookNotificationService hookNotificationService,
            ISessionManager sessionManager)
        {
            _logger = logger;
            _hookNotificationService = hookNotificationService;
            _sessionManager = sessionManager;
            // 「詰まっている間に何が実行中だったか」を撮る OperationProbe に、専用カテゴリの
            // ロガーを渡す（[OpProbe] 行として grep しやすくする）。
            OperationProbe.SetLogger(loggerFactory.CreateLogger("TerminalHub.Diagnostics.OperationProbe"));
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("[FreezeProbe] 起動: 間隔={IntervalSec}s, 閾値={ThresholdSec}s", Interval.TotalSeconds, StallThreshold.TotalSeconds);

            _dedicatedThread = new Thread(() => ProbeLoopDedicated(_cts.Token))
            {
                IsBackground = true,
                Name = "FreezeProbe-Dedicated",
                // GC/OS停止の検出専用。優先度を上げてスケジューラ起因の誤検知を減らす
                Priority = ThreadPriority.AboveNormal,
            };
            _dedicatedThread.Start();

            _threadPoolTask = Task.Run(() => ProbeLoopThreadPoolAsync(_cts.Token), CancellationToken.None);
            return Task.CompletedTask;
        }

        /// <summary>専用スレッドプローブ: ここの停止はプロセス全体の停止（GC/OS）を意味する</summary>
        private void ProbeLoopDedicated(CancellationToken token)
        {
            var sw = Stopwatch.StartNew();
            var lastGcPause = GC.GetTotalPauseDuration();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    Thread.Sleep(Interval);
                }
                catch (ThreadInterruptedException)
                {
                    return;
                }

                // ThreadPool が詰まっている間もここは回るので、ピーク採取はこのループが担う
                SamplePeaks();

                var elapsed = sw.Elapsed;
                sw.Restart();
                var gcPause = GC.GetTotalPauseDuration();

                if (elapsed > StallThreshold)
                {
                    var gcDelta = gcPause - lastGcPause;
                    _logger.LogWarning(
                        "[FreezeProbe] 専用スレッド停止検出: 停止={StallSec:F1}s (開始≒{StallStart:HH:mm:ss.fff}), GCポーズ差分={GcPauseSec:F1}s, Gen0/1/2={Gen0}/{Gen1}/{Gen2}, ThreadPool待機={Pending}, スレッド数={Threads}, 実行中=[{InFlight}]",
                        elapsed.TotalSeconds,
                        DateTime.Now - elapsed,
                        gcDelta.TotalSeconds,
                        GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                        ThreadPool.PendingWorkItemCount,
                        ThreadPool.ThreadCount,
                        OperationProbe.DumpInFlight());
                }

                lastGcPause = gcPause;
            }
        }

        /// <summary>
        /// ストール中の値を取りこぼさないよう、専用スレッドから毎秒サンプルして最大値を更新する。
        /// ThreadPool プローブ側が同じフィールドを 0 に畳むため、CAS で更新する。
        /// </summary>
        private void SamplePeaks()
        {
            UpdatePeak(ref _peakBlockedInRead, ConPtyReadProbe.BlockedInRead);

            ThreadPool.GetMaxThreads(out var maxWorkers, out _);
            ThreadPool.GetAvailableThreads(out var availWorkers, out _);
            UpdatePeak(ref _peakBusyWorkers, maxWorkers - availWorkers);
        }

        /// <summary>現在値がピークを超えていれば CAS で更新する（畳み込みと競合しても取りこぼさない）。</summary>
        private static void UpdatePeak(ref int peak, int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref peak);
                if (value <= current) return;
                if (Interlocked.CompareExchange(ref peak, value, current) == current) return;
            }
        }

        /// <summary>ThreadPoolプローブ: 専用スレッドが無事でここだけ遅れる = ThreadPool枯渇</summary>
        private async Task ProbeLoopThreadPoolAsync(CancellationToken token)
        {
            var sw = Stopwatch.StartNew();
            var lastCompleted = ThreadPool.CompletedWorkItemCount;
            var lastGauge = Stopwatch.StartNew();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(Interval, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                var elapsed = sw.Elapsed;
                sw.Restart();

                // 定期ゲージ: 増え続けたらリーク。特に hook/sessions の購読者数は Circuit 破棄で
                // 減るはずなので、単調増加＝Circuit が破棄されず購読が漏れている証拠になる。
                if (lastGauge.Elapsed >= GaugeInterval)
                {
                    lastGauge.Restart();
                    LogGauge();
                }

                // 直近tickでの完了スループット（starvation中はほぼ0まで落ちる＝全ワーカーがブロック中の証拠）
                var completed = ThreadPool.CompletedWorkItemCount;
                var completedDelta = completed - lastCompleted;
                lastCompleted = completed;

                if (elapsed > StallThreshold)
                {
                    // ワーカースレッドの逼迫度: max - available = 使用中ワーカー
                    ThreadPool.GetMaxThreads(out var maxWorkers, out _);
                    ThreadPool.GetAvailableThreads(out var availWorkers, out _);
                    ThreadPool.GetMinThreads(out var minWorkers, out _);

                    // ConPTY読みは回帰監視。専用スレッド化後は BusyWorkers に算入されないはずなので、
                    // ピークの ConPTY読み が ピークの使用中 に迫っていたら対策が外れた疑い。
                    // 素の値はこの行を書いている「復帰後」のスナップショットなので、判定には
                    // 専用スレッドが詰まっている最中に採ったピーク値のほうを見ること。
                    _logger.LogWarning(
                        "[FreezeProbe] ThreadPool遅延検出: 遅延={StallSec:F1}s (開始≒{StallStart:HH:mm:ss.fff}), ThreadPool待機={Pending}, 完了スループット={Throughput:F0}/s, 使用中ワーカー={BusyWorkers}/{MaxWorkers}(min={MinWorkers}), スレッド数={Threads}, ConPTY読み={BlockedInRead}/{ReadLoops}, ストール中ピーク(ConPTY読み/使用中)={PeakBlockedInRead}/{PeakBusyWorkers}, 実行中=[{InFlight}]",
                        elapsed.TotalSeconds,
                        DateTime.Now - elapsed,
                        ThreadPool.PendingWorkItemCount,
                        completedDelta / elapsed.TotalSeconds,
                        maxWorkers - availWorkers,
                        maxWorkers,
                        minWorkers,
                        ThreadPool.ThreadCount,
                        ConPtyReadProbe.BlockedInRead,
                        ConPtyReadProbe.ActiveLoops,
                        Volatile.Read(ref _peakBlockedInRead),
                        Volatile.Read(ref _peakBusyWorkers),
                        OperationProbe.DumpInFlight());
                }

                // ピークは「前回このループが回ってから今まで」＝ストール区間を表すようにしたいので、
                // 遅延の有無に関わらず毎ティックで畳む。専用スレッド側の CAS 更新と競合しても
                // 落ちないよう Exchange で行う。
                Interlocked.Exchange(ref _peakBlockedInRead, 0);
                Interlocked.Exchange(ref _peakBusyWorkers, 0);
            }
        }

        /// <summary>
        /// 蓄積リーク検出用の定期ゲージ。単調増加する値があればそれが犯人候補。
        /// hook購読/sessions変更購読は Circuit 破棄で減るはずなので、増え続けたら購読漏れ。
        /// </summary>
        private void LogGauge()
        {
            int procThreads;
            try
            {
                // Process はハンドルを保持するので使い捨てにする（プローブ自身がハンドルを溜めないため）
                using var proc = Process.GetCurrentProcess();
                procThreads = proc.Threads.Count;
            }
            catch
            {
                procThreads = -1; // 取得失敗時も落とさない
            }

            // 使用中ワーカーも平常時から採る（ConPTY読み占有との比を時系列で追うため）
            ThreadPool.GetMaxThreads(out var maxWorkers, out _);
            ThreadPool.GetAvailableThreads(out var availWorkers, out _);

            _logger.LogInformation(
                "[FreezeProbe] 定期ゲージ: hook購読={HookSubs}, sessions変更購読={SessSubs}, セッション数={Sessions}, in-flight={InFlight}, ThreadPool待機={Pending}, ThreadPoolスレッド={TpThreads}, プロセススレッド={ProcThreads}, 使用中ワーカー={BusyWorkers}, ConPTY読み={BlockedInRead}/{ReadLoops}",
                _hookNotificationService.SubscriberCount,
                _sessionManager.SessionsChangedSubscriberCount,
                _sessionManager.GetAllSessions().Count(),
                OperationProbe.InFlightCount,
                ThreadPool.PendingWorkItemCount,
                ThreadPool.ThreadCount,
                procThreads,
                maxWorkers - availWorkers,
                ConPtyReadProbe.BlockedInRead,
                ConPtyReadProbe.ActiveLoops);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _cts.Cancel();
            _dedicatedThread?.Interrupt();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _cts.Dispose();
        }
    }
}
