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
    /// 平常時の出力は起動時の1行と、60秒毎の定期ゲージ（蓄積リーク監視＋ConPTY読み占有）のみ。
    /// ConPTY読み占有（<see cref="ConPtyReadProbe"/>）は starvation の本命容疑者の計測で、
    /// 「使用中ワーカーのうち何本が ConPTY の ReadAsync 待ちで張り付いているか」を出す。
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

                    // ConPTY読み占有: BusyWorkers の大半が ReadAsync待ちなら starvation の犯人が確定する
                    _logger.LogWarning(
                        "[FreezeProbe] ThreadPool遅延検出: 遅延={StallSec:F1}s (開始≒{StallStart:HH:mm:ss.fff}), ThreadPool待機={Pending}, 完了スループット={Throughput:F0}/s, 使用中ワーカー={BusyWorkers}/{MaxWorkers}(min={MinWorkers}), スレッド数={Threads}, ConPTY読み={BlockedInRead}/{ReadLoops}, 実行中=[{InFlight}]",
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
                        OperationProbe.DumpInFlight());
                }
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
