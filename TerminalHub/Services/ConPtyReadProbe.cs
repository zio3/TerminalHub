using System.Runtime.InteropServices;

namespace TerminalHub.Services
{
    /// <summary>
    /// ConPTY 読みループが ThreadPool スレッドを何本占有しているかの計測（実験コード）。
    ///
    /// 背景（2026-07-23 の調査）: ThreadPool starvation は再現・確認できたが、
    /// <see cref="OperationProbe"/> の in-flight ダンプが詰まりの最中でもほぼ空
    /// （＝計測点の外で誰かがスレッドを握っている）という強い陰性所見が出た。
    ///
    /// 犯人は ConPTY の読みループだった。<c>ConPtySession</c> のパイプは
    /// <c>new FileStream(pipeOut, FileAccess.Read)</c> と <c>isAsync</c> 未指定（＝同期モード）で
    /// 作られており、同期モードの FileStream の <c>ReadAsync</c> は overlapped I/O を使わず
    /// **ブロッキング読みを ThreadPool スレッドへ投げる**実装になる。つまり
    /// 起動済みセッション1本につきプールスレッド1本が <c>ReadFile</c> で張り付き続けていた。
    /// 実測でも <c>使用中ワーカー ≒ ConPTY読み + 1</c> が全域で成立し、min worker(16)到達で発症した。
    ///
    /// 対策として読みループは専用スレッド上の同期 <c>Read()</c> へ移した（<c>ConPtyService</c> 参照）。
    /// **したがって今のカウンタは ThreadPool 占有ではなく「専用スレッドが読み待ちしている本数」を数える。**
    /// このクラスは対策の回帰監視として残している:
    /// - <see cref="ActiveLoops"/>: 走っている読みループの本数（＝起動済み ConPTY 数の実測）
    /// - <see cref="BlockedInRead"/>: そのうち今まさに読み待ちで張り付いている本数
    ///
    /// 判定: **正常なら <c>ConPTY読み</c> が何本に増えても <c>使用中ワーカー</c> は連動しない**。
    /// 連動していたら読みループが ThreadPool へ戻った（対策が外れた）サインなので、
    /// <c>ConPtyService</c> の読みループが専用スレッドで同期 <c>Read()</c> を呼んでいるか確認すること。
    ///
    /// <see cref="OperationProbe"/> を再利用しない理由: あちらは閾値(1秒)超で完了した処理を必ず
    /// WARN に出す。読み待ちは「出力が来るまで分単位でブロックする」のが正常なので、そのまま使うと
    /// ログが洪水になる。ここは件数を数えるだけの軽量カウンタに留める（統合しないこと）。
    ///
    /// 設計方針: 平常時のコストは Interlocked の増減のみ。2つのカウンタは別々のキャッシュラインに
    /// 置く（多セッション同時出力時の false sharing で、計測が測定対象の性能を乱さないようにするため）。
    /// </summary>
    public static class ConPtyReadProbe
    {
        // 1要素で1キャッシュライン（64B）を占有する箱。配列要素は連続配置されるので、
        // Size=64 にしておけば 2つのカウンタが同じラインに乗ることはない。
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        private struct PaddedCounter
        {
            [FieldOffset(0)] public int Value;
        }

        private const int ActiveLoopsIndex = 0;
        private const int BlockedInReadIndex = 1;

        private static readonly PaddedCounter[] Counters = new PaddedCounter[2];

        /// <summary>走っている読みループの本数。</summary>
        public static int ActiveLoops => Volatile.Read(ref Counters[ActiveLoopsIndex].Value);

        /// <summary>今まさに読み待ちで張り付いている本数（専用スレッド化後は ThreadPool のワーカーではない）。</summary>
        public static int BlockedInRead => Volatile.Read(ref Counters[BlockedInReadIndex].Value);

        /// <summary>読みループの生存期間を数える。<c>using</c> で囲んで使う。</summary>
        public static LoopScope EnterLoop()
        {
            Interlocked.Increment(ref Counters[ActiveLoopsIndex].Value);
            return new LoopScope(armed: true);
        }

        /// <summary>1回の読み待ちを数える。<c>using</c> ブロックで <c>Read()</c> を囲んで使う。</summary>
        public static ReadScope EnterRead()
        {
            Interlocked.Increment(ref Counters[BlockedInReadIndex].Value);
            return new ReadScope(armed: true);
        }

        /// <summary>
        /// <see cref="EnterLoop"/> が返すスコープ。struct は既定値を作れてしまうので、
        /// factory を通ったものだけ減算する（default を Dispose してもカウンタを壊さない）。
        /// </summary>
        public readonly struct LoopScope : IDisposable
        {
            private readonly bool _armed;

            internal LoopScope(bool armed) => _armed = armed;

            public void Dispose()
            {
                if (_armed) Interlocked.Decrement(ref Counters[ActiveLoopsIndex].Value);
            }
        }

        /// <summary>
        /// <see cref="EnterRead"/> が返すスコープ。既定値を Dispose してもカウンタを壊さない
        /// （理由は <see cref="LoopScope"/> と同じ）。
        /// </summary>
        public readonly struct ReadScope : IDisposable
        {
            private readonly bool _armed;

            internal ReadScope(bool armed) => _armed = armed;

            public void Dispose()
            {
                if (_armed) Interlocked.Decrement(ref Counters[BlockedInReadIndex].Value);
            }
        }
    }
}
