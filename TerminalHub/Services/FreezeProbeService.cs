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
    /// 平常時は起動時の1行以外何も出力しない。
    /// </summary>
    public class FreezeProbeService : IHostedService, IDisposable
    {
        // ティック間隔と、これを超えたら記録する閾値（体感できる遅さより十分小さく）
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(2.5);

        private readonly ILogger<FreezeProbeService> _logger;
        private readonly CancellationTokenSource _cts = new();
        private Thread? _dedicatedThread;
        private Task? _threadPoolTask;

        public FreezeProbeService(ILogger<FreezeProbeService> logger)
        {
            _logger = logger;
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
                        "[FreezeProbe] 専用スレッド停止検出: 停止={StallSec:F1}s (開始≒{StallStart:HH:mm:ss.fff}), GCポーズ差分={GcPauseSec:F1}s, Gen0/1/2={Gen0}/{Gen1}/{Gen2}, ThreadPool待機={Pending}, スレッド数={Threads}",
                        elapsed.TotalSeconds,
                        DateTime.Now - elapsed,
                        gcDelta.TotalSeconds,
                        GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                        ThreadPool.PendingWorkItemCount,
                        ThreadPool.ThreadCount);
                }

                lastGcPause = gcPause;
            }
        }

        /// <summary>ThreadPoolプローブ: 専用スレッドが無事でここだけ遅れる = ThreadPool枯渇</summary>
        private async Task ProbeLoopThreadPoolAsync(CancellationToken token)
        {
            var sw = Stopwatch.StartNew();

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

                if (elapsed > StallThreshold)
                {
                    _logger.LogWarning(
                        "[FreezeProbe] ThreadPool遅延検出: 遅延={StallSec:F1}s (開始≒{StallStart:HH:mm:ss.fff}), ThreadPool待機={Pending}, スレッド数={Threads}",
                        elapsed.TotalSeconds,
                        DateTime.Now - elapsed,
                        ThreadPool.PendingWorkItemCount,
                        ThreadPool.ThreadCount);
                }
            }
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
