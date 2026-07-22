using System.Threading;

namespace TerminalHub.Services
{
    /// <summary>
    /// ConPTY 読みループが ThreadPool スレッドを何本占有しているかの計測（実験コード）。
    ///
    /// 背景（2026-07-23 の調査）: ThreadPool starvation は再現・確認できたが、
    /// <see cref="OperationProbe"/> の in-flight ダンプが詰まりの最中でもほぼ空
    /// （＝計測点の外で誰かがスレッドを握っている）という強い陰性所見が出た。
    ///
    /// 有力容疑者が ConPTY の読みループ。<c>ConPtySession</c> のパイプは
    /// <c>new FileStream(pipeOut, FileAccess.Read)</c> と <c>isAsync</c> 未指定（＝同期モード）で
    /// 作られており、同期モードの FileStream の <c>ReadAsync</c> は overlapped I/O を使わず
    /// **ブロッキング読みを ThreadPool スレッドへ投げる**実装になる。つまり
    /// 起動済みセッション1本につきプールスレッド1本が <c>ReadFile</c> で張り付き続ける。
    ///
    /// この仮説を白黒つけるための2つのカウンタ:
    /// - <see cref="ActiveLoops"/>: 走っている読みループの本数（＝起動済み ConPTY 数の実測）
    /// - <see cref="BlockedInRead"/>: そのうち今まさに <c>ReadAsync</c> 待ちで張り付いている本数（本命）
    ///
    /// 判定: ストール時に <c>BlockedInRead</c> が <c>使用中ワーカー</c> の大半を占めていれば仮説は確定。
    /// 逆にほぼ 0 なら（＝待ちが overlapped で処理されている）容疑は晴れ、別の犯人を探す。
    ///
    /// 設計方針: 平常時のコストは Interlocked の増減のみ。
    /// </summary>
    public static class ConPtyReadProbe
    {
        private static int _activeLoops;
        private static int _blockedInRead;

        /// <summary>走っている読みループの本数。</summary>
        public static int ActiveLoops => Volatile.Read(ref _activeLoops);

        /// <summary>今まさに ReadAsync 待ちで張り付いている本数（同期 FileStream ならプールスレッドを握っている）。</summary>
        public static int BlockedInRead => Volatile.Read(ref _blockedInRead);

        /// <summary>読みループの生存期間を数える。<c>using</c> で囲んで使う。</summary>
        public static LoopScope EnterLoop()
        {
            Interlocked.Increment(ref _activeLoops);
            return default;
        }

        /// <summary>1回の ReadAsync 待ちを数える。<c>using</c> ブロックで await を囲んで使う。</summary>
        public static ReadScope EnterRead()
        {
            Interlocked.Increment(ref _blockedInRead);
            return default;
        }

        public readonly struct LoopScope : IDisposable
        {
            public void Dispose() => Interlocked.Decrement(ref _activeLoops);
        }

        public readonly struct ReadScope : IDisposable
        {
            public void Dispose() => Interlocked.Decrement(ref _blockedInRead);
        }
    }
}
