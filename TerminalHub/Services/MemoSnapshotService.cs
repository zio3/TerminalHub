using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TerminalHub.Models;

namespace TerminalHub.Services
{
    /// <summary>
    /// メモの編集履歴 (スナップショット) を 10 分毎に自動保存するバックグラウンドサービス。
    /// 変更のあったメモにだけ INSERT するため、idle なメモは履歴が増えない。
    /// </summary>
    public class MemoSnapshotService : IHostedService, IDisposable
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<MemoSnapshotService> _logger;
        // WinForms の Timer と衝突するので System.Threading.Timer を明示
        private System.Threading.Timer? _timer;

        /// <summary>
        /// スナップショット取得間隔。
        /// 初回 Tick も同じ遅延を使う (= 起動から 10 分は snapshot を作らない)。
        /// これは設計上の意図で、「10 分間維持されていないテキストは保護対象外」と割り切っている。
        /// </summary>
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

        public MemoSnapshotService(IServiceScopeFactory scopeFactory, ILogger<MemoSnapshotService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("[MemoSnapshot] サービス開始 (間隔={Interval})", Interval);
            _timer = new System.Threading.Timer(OnTimerTick, null, Interval, Interval);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("[MemoSnapshot] サービス停止");
            _timer?.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        private void OnTimerTick(object? state)
        {
            _ = TickAsync();
        }

        private async Task TickAsync()
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var sessionManager = scope.ServiceProvider.GetRequiredService<ISessionManager>();
                var memoRepo = scope.ServiceProvider.GetRequiredService<ISessionMemoRepository>();
                var snapshotRepo = scope.ServiceProvider.GetRequiredService<ISessionMemoSnapshotRepository>();

                int inserted = 0;
                int scanned = 0;

                foreach (var session in sessionManager.GetActiveSessions())
                {
                    List<SessionMemo> memos;
                    try
                    {
                        memos = await memoRepo.GetBySessionAsync(session.SessionId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[MemoSnapshot] メモ一覧取得失敗 SessionId={SessionId}", session.SessionId);
                        continue;
                    }

                    foreach (var memo in memos)
                    {
                        scanned++;
                        try
                        {
                            var last = await snapshotRepo.GetLatestAsync(memo.MemoId);
                            // 初回 or 本文変更ありのみ記録。Title の変更だけでは snapshot を作らない
                            // (Title は UpdatedAt を進めるが、本文保護の観点で意味は薄い)
                            if (last is null || !string.Equals(last.Body, memo.Body, StringComparison.Ordinal))
                            {
                                await snapshotRepo.InsertAsync(new SessionMemoSnapshot
                                {
                                    MemoId = memo.MemoId,
                                    Title = memo.Title,
                                    Body = memo.Body,
                                    SavedAt = DateTime.Now,
                                    Trigger = SessionMemoSnapshot.TriggerAuto
                                });
                                await snapshotRepo.TrimAutoSnapshotsAsync(memo.MemoId, SessionMemoSnapshot.MaxAutoKeep);
                                inserted++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[MemoSnapshot] 個別メモの snapshot 失敗 MemoId={MemoId}", memo.MemoId);
                        }
                    }
                }

                if (inserted > 0)
                {
                    _logger.LogInformation("[MemoSnapshot] Tick 完了: スキャン={Scanned}, 新規={Inserted}", scanned, inserted);
                }
                else
                {
                    _logger.LogDebug("[MemoSnapshot] Tick 完了: スキャン={Scanned}, 変更なし", scanned);
                }
            }
            catch (Exception ex)
            {
                // Timer コールバックから例外が漏れるとプロセス終了につながるため、握り潰して継続
                _logger.LogError(ex, "[MemoSnapshot] Tick で未捕捉の例外");
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
