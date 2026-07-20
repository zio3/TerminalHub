using System.Collections.Concurrent;
using System.Diagnostics;

namespace TerminalHub.Services
{
    /// <summary>
    /// 「反応が重い / 詰まる」調査用の軽量オペレーショントラッカー（実験コード）。
    ///
    /// 背景: FreezeProbe は「ThreadPool が遅れた事実・待機数・GC差分」までは出せるが、
    /// 「その間どの処理が実行中でスレッドを握っていたか」が分からない。starvation の犯人を
    /// 特定するには、ホットパスを <see cref="Track"/> で囲み、
    /// (1) 単発で閾値超に遅い処理はその場で WARN、
    /// (2) FreezeProbe がストール検知した瞬間に実行中(in-flight)の一覧をダンプする、の2つで攻める。
    ///
    /// 設計方針: 平常時はほぼゼロコスト（ConcurrentDictionary への add/remove と Dispose 時の経過判定のみ）。
    /// ロガー未設定でも安全に動く（SetLogger 前は WARN を出さないだけ）。
    /// </summary>
    public static class OperationProbe
    {
        private sealed record Entry(string Name, long StartTimestamp, int ThreadId);

        private static readonly ConcurrentDictionary<long, Entry> InFlight = new();
        private static long _seq;
        private static ILogger? _logger;

        // 単発の処理がこの時間を超えて完了したら、その場で遅い処理として記録する
        private static readonly TimeSpan SlowOpThreshold = TimeSpan.FromSeconds(1.0);

        /// <summary>FreezeProbe 起動時に一度だけ設定する。未設定でも Track 自体は動く。</summary>
        public static void SetLogger(ILogger logger) => _logger = logger;

        /// <summary>
        /// 処理を計測開始する。戻り値を using で受けると、スコープを抜けた時点で完了扱いになる。
        /// 例: <c>using var _ = OperationProbe.Track("HookNotification:Stop");</c>
        /// </summary>
        public static Scope Track(string name)
        {
            var id = Interlocked.Increment(ref _seq);
            InFlight[id] = new Entry(name, Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId);
            return new Scope(id);
        }

        private static void Complete(long id)
        {
            if (!InFlight.TryRemove(id, out var e)) return;
            var elapsed = Stopwatch.GetElapsedTime(e.StartTimestamp);
            if (elapsed >= SlowOpThreshold)
            {
                _logger?.LogWarning(
                    "[OpProbe] 遅い処理: {Name} = {Ms}ms (thread={ThreadId})",
                    e.Name, (long)elapsed.TotalMilliseconds, e.ThreadId);
            }
        }

        /// <summary>
        /// 現在実行中の処理を経過時間の長い順に最大 <paramref name="max"/> 件、1行文字列で返す。
        /// FreezeProbe がストール検知時に添えて出す用。追跡対象が無ければその旨を返す。
        /// </summary>
        public static string DumpInFlight(int max = 8)
        {
            var snapshot = InFlight.Values
                .Select(e => (e.Name, Ms: (long)Stopwatch.GetElapsedTime(e.StartTimestamp).TotalMilliseconds, e.ThreadId))
                .OrderByDescending(x => x.Ms)
                .Take(max)
                .ToList();

            if (snapshot.Count == 0) return "(実行中の追跡対象なし)";
            return string.Join(", ", snapshot.Select(x => $"{x.Name}:{x.Ms}ms(t{x.ThreadId})"));
        }

        /// <summary>Track の戻り値。Dispose で完了扱いにする。</summary>
        public readonly struct Scope : IDisposable
        {
            private readonly long _id;
            internal Scope(long id) => _id = id;
            public void Dispose() => Complete(_id);
        }
    }
}
