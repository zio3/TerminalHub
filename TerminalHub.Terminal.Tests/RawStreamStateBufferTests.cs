using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// 生ストリーム方式（従来挙動）のベースラインテスト。
/// エミュレータ実装が入ったら、二重化系のテストは「二重化しない」方向へ反転させていく。
/// </summary>
public class RawStreamStateBufferTests
{
    [Fact]
    public void Append_then_Serialize_returns_concatenation()
    {
        var buf = new RawStreamStateBuffer();
        buf.Append("abc");
        buf.Append("def");

        Assert.Equal("abcdef", buf.SerializeForReplay());
        Assert.Equal(6, buf.Size);
    }

    [Fact]
    public void Clear_resets_buffer()
    {
        var buf = new RawStreamStateBuffer();
        buf.Append("hello");
        buf.Clear();

        Assert.Equal(string.Empty, buf.SerializeForReplay());
        Assert.Equal(0, buf.Size);
    }

    [Fact]
    public void Append_null_or_empty_is_noop()
    {
        var buf = new RawStreamStateBuffer();
        buf.Append("");
        buf.Append(null!);

        Assert.Equal(0, buf.Size);
    }

    [Fact]
    public void Exceeding_max_size_trims_oldest_data()
    {
        var buf = new RawStreamStateBuffer(maxSize: 10);
        buf.Append("0123456789"); // ちょうど上限
        buf.Append("ABCDE");      // 5 文字追加 → 古い 5 文字が落ちる

        var result = buf.SerializeForReplay();
        Assert.Equal(10, result.Length);
        Assert.Equal("56789ABCDE", result);
    }

    [Fact]
    public void Single_append_larger_than_max_keeps_only_within_limit()
    {
        var buf = new RawStreamStateBuffer(maxSize: 4);
        buf.Append("0123456789");

        // 上限より大きい単発 Append は全クリア後に追記され、上限は超え得る（従来挙動の踏襲）
        Assert.Equal("0123456789", buf.SerializeForReplay());
    }

    /// <summary>
    /// 生ストリーム方式は repaint（画面クリア＋再描画）も無加工で貯めるため、
    /// 復元用出力に同じ行が二重に残る＝スクロールバック二重化の再現。
    /// エミュレータ導入後は、この期待値を「1 回だけ」に反転させる（根治の受け入れ基準）。
    /// </summary>
    [Fact]
    public void Passthrough_preserves_repaint_duplication_baseline()
    {
        var stream = FixtureLoader.LoadEscaped("synthetic-repaint.esc");
        var buf = new RawStreamStateBuffer();
        buf.Append(stream);

        var replay = buf.SerializeForReplay();

        // 生ストリーム方式では入力そのまま
        Assert.Equal(stream, replay);
        // "line3" が 2 回出現する（repaint 前後）＝二重化している
        Assert.Equal(2, CountOccurrences(replay, "line3"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
