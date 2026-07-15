namespace TerminalHub.Services
{
    /// <summary>
    /// ConPTY への入力を分割する際の境界を決める。
    /// 長い文字列を一括で流し込むと受け手（conhost の入力バッファ / CLI）が取りこぼすため、
    /// <see cref="ChunkSize"/> 文字ずつ間隔を空けて送る必要がある（ConPtySession.WriteAsync 参照）。
    /// </summary>
    internal static class ConPtyWriteChunker
    {
        /// <summary>1チャンクの最大文字数。実測で調整された値で理論的な裏付けはない。</summary>
        public const int ChunkSize = 256;

        /// <summary>
        /// <paramref name="offset"/> から送るべきチャンクの長さを返す。
        ///
        /// UTF-16 サロゲートペア（絵文字等の非BMP文字）を境界で分断しない。
        /// StreamWriter の Encoder はチャンクをまたいで高位サロゲートを保持できるが、
        /// 呼び出し側はチャンクごとに FlushAsync でエンコーダ状態ごと強制フラッシュするため、
        /// 宙に浮いた高位サロゲートはその場で U+FFFD に潰されてしまう
        /// （例: ASCII 255文字 + 😀 → F0 9F 98 80 ではなく EF BF BD が2つ）。
        /// 末尾が高位サロゲートになる場合は1文字手前で区切り、ペアを次のチャンクへ送る。
        /// </summary>
        public static int NextChunkLength(string input, int offset)
        {
            // chunkSize は引数にしない。1 を渡されると高位サロゲート先頭の入力で length が 0 になり、
            // 呼び出し側の offset が進まず無限ループになる。可変にする理由も無いので固定にしておく。
            var length = System.Math.Min(ChunkSize, input.Length - offset);

            // 後続がある場合のみ調整すればよい（末尾の孤立サロゲートは分割とは無関係）
            if (offset + length < input.Length && char.IsHighSurrogate(input[offset + length - 1]))
            {
                length--;
            }

            return length;
        }
    }
}
