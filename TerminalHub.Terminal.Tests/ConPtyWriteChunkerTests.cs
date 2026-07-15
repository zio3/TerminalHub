using System.Text;
using TerminalHub.Services;
using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// ConPTY 入力の分割境界の検証。
/// チャンクごとに FlushAsync でエンコーダ状態を強制フラッシュするため、
/// 境界でサロゲートペアを割ると U+FFFD に潰れて絵文字が文字化けする。
/// </summary>
public sealed class ConPtyWriteChunkerTests
{
    private const string Emoji = "\U0001F600"; // 😀 = サロゲートペア

    /// <summary>ConPtySession.WriteAsync と同じ手順で送信バイト列を組み立てる。</summary>
    private static byte[] WriteThroughChunks(string input)
    {
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 65536) { AutoFlush = true };

        var offset = 0;
        while (offset < input.Length)
        {
            var length = ConPtyWriteChunker.NextChunkLength(input, offset);
            writer.Write(input.Substring(offset, length));
            writer.Flush(); // ここでエンコーダ状態ごとフラッシュされる（本番と同じ）
            offset += length;
        }

        return stream.ToArray();
    }

    // 境界（256）の前後で、サロゲートペアがちょうど跨ぐケースを含めて総当たりする
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(253)]
    [InlineData(254)]
    [InlineData(255)] // 修正前はここだけ文字化けしていた（ASCII255 + 絵文字 = ペアが境界を跨ぐ）
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(511)]
    [InlineData(512)]
    public void 絵文字が境界を跨いでも文字化けしない(int asciiPrefixLength)
    {
        var input = new string('a', asciiPrefixLength) + Emoji;

        var actual = WriteThroughChunks(input);

        Assert.Equal(Encoding.UTF8.GetBytes(input), actual);
    }

    [Fact]
    public void 絵文字が連続していても文字化けしない()
    {
        var input = new string('a', 250) + string.Concat(Enumerable.Repeat(Emoji, 50));

        var actual = WriteThroughChunks(input);

        Assert.Equal(Encoding.UTF8.GetBytes(input), actual);
    }

    [Fact]
    public void 高位サロゲートで終わる境界は1文字手前で区切る()
    {
        // 255文字 + 絵文字 → 256文字目が高位サロゲートになるので 255 で切る
        var input = new string('a', 255) + Emoji;

        Assert.Equal(255, ConPtyWriteChunker.NextChunkLength(input, 0));
    }

    [Fact]
    public void 分断の必要がなければチャンクサイズをそのまま使う()
    {
        var input = new string('a', 1000);

        Assert.Equal(ConPtyWriteChunker.ChunkSize, ConPtyWriteChunker.NextChunkLength(input, 0));
    }

    [Fact]
    public void 残りがチャンクサイズ未満なら残り全部を返す()
    {
        var input = new string('a', 300);

        Assert.Equal(44, ConPtyWriteChunker.NextChunkLength(input, 256));
    }

    [Fact]
    public void 末尾の孤立した高位サロゲートは調整しない()
    {
        // 後続が無いので縮める意味がない（縮めると長さ0で無限ループになる）
        var input = new string('a', 255) + '\uD83D';

        Assert.Equal(256, ConPtyWriteChunker.NextChunkLength(input, 0));
    }
}
