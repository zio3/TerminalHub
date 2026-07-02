using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// VTエミュレータ方式（EmulatedStateBuffer）の基本動作テスト。
/// 「復元出力に何が残るか」を中心に検証する（セルレベルの厳密さより replay 内容のパリティ重視）。
/// </summary>
public class EmulatedStateBufferTests
{
    private static EmulatedStateBuffer Create(int cols = 40, int rows = 5) => new(cols, rows);

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

    // ---- 基本 ----

    [Fact]
    public void Plain_text_survives_replay()
    {
        var buf = Create();
        buf.Append("hello world");

        Assert.Contains("hello world", buf.SerializeForReplay());
    }

    [Fact]
    public void Multiline_text_keeps_line_order()
    {
        var buf = Create();
        buf.Append("first\r\nsecond\r\nthird");

        var replay = buf.SerializeForReplay();
        int i1 = replay.IndexOf("first", StringComparison.Ordinal);
        int i2 = replay.IndexOf("second", StringComparison.Ordinal);
        int i3 = replay.IndexOf("third", StringComparison.Ordinal);
        Assert.True(0 <= i1 && i1 < i2 && i2 < i3);
    }

    [Fact]
    public void Scrollback_preserves_lines_pushed_off_screen()
    {
        var buf = Create(cols: 40, rows: 3);
        // 3行画面に6行書く → 先頭3行はスクロールバックへ
        buf.Append("L1\r\nL2\r\nL3\r\nL4\r\nL5\r\nL6");

        var replay = buf.SerializeForReplay();
        foreach (var line in new[] { "L1", "L2", "L3", "L4", "L5", "L6" })
        {
            Assert.Contains(line, replay);
        }
    }

    [Fact]
    public void Clear_resets_state()
    {
        var buf = Create();
        buf.Append("data");
        buf.Clear();

        Assert.DoesNotContain("data", buf.SerializeForReplay());
    }

    // ---- 根治の受け入れ基準: repaint が二重化しない ----

    /// <summary>
    /// 生ストリーム方式では "line3" が2回残った合成 repaint フィクスチャが、
    /// エミュレータ方式では畳まれて1回だけになる（＝スクロールバック二重化の根治）。
    /// </summary>
    [Fact]
    public void Repaint_is_collapsed_not_duplicated()
    {
        var stream = FixtureLoader.LoadEscaped("synthetic-repaint.esc");
        var buf = Create(cols: 40, rows : 10);
        buf.Append(stream);

        var replay = buf.SerializeForReplay();
        Assert.Equal(1, CountOccurrences(replay, "line3"));
        Assert.Equal(1, CountOccurrences(replay, "line1"));
    }

    /// <summary>
    /// Claude Code 型の repaint（ESC[H で先頭へ → 各行 ESC[K → 再描画 ×N回）が
    /// 何度来ても replay には最終フレームの内容が1回だけ残る。
    /// </summary>
    [Fact]
    public void Home_and_erase_repaint_frames_leave_single_copy()
    {
        var buf = Create(cols: 40, rows: 5);
        for (int frame = 0; frame < 10; frame++)
        {
            buf.Append("\x1b[H");
            buf.Append($"\x1b[Kstatus frame\r\n");
            buf.Append($"\x1b[Kcounter {frame}\r\n");
        }

        var replay = buf.SerializeForReplay();
        Assert.Equal(1, CountOccurrences(replay, "status frame"));
        // 最終フレームの内容だけが残る
        Assert.Contains("counter 9", replay);
        Assert.DoesNotContain("counter 8", replay);
    }

    // ---- チャンク境界 ----

    [Fact]
    public void Escape_sequence_split_across_chunks_is_parsed()
    {
        var buf = Create();
        buf.Append("abc\x1b[");
        buf.Append("31mred");

        var replay = buf.SerializeForReplay();
        Assert.Contains("red", replay);
        Assert.Contains("[0;31m", replay); // 赤前景が復元される
        Assert.DoesNotContain("31mred", replay.Replace("\x1b[0;31mred", "")); // 生の断片が漏れない
    }

    [Fact]
    public void Surrogate_pair_split_across_chunks_is_combined()
    {
        var buf = Create();
        var emoji = "😀"; // U+1F600（サロゲートペア）
        buf.Append(emoji[0].ToString());
        buf.Append(emoji[1].ToString());

        Assert.Contains(emoji, buf.SerializeForReplay());
    }

    // ---- CJK / 全角幅 ----

    [Fact]
    public void Cjk_text_survives_replay()
    {
        var buf = Create();
        buf.Append("日本語テキスト123");

        Assert.Contains("日本語テキスト123", buf.SerializeForReplay());
    }

    [Fact]
    public void Cjk_occupies_two_cells()
    {
        var buf = Create(cols: 10, rows: 3);
        buf.Append("あい"); // 2文字 × 幅2 = 4セル

        var grid = buf.Grid;
        Assert.Equal("あ", grid.Screen[0][0].Text);
        Assert.True(grid.Screen[0][1].IsWideTrailer);
        Assert.Equal("い", grid.Screen[0][2].Text);
        Assert.True(grid.Screen[0][3].IsWideTrailer);
        Assert.Equal(4, grid.CursorCol);
    }

    [Fact]
    public void Wide_char_wraps_at_last_column()
    {
        var buf = Create(cols: 5, rows: 3);
        buf.Append("abcd漢"); // "漢"(幅2) は col=4 に収まらないので次行へ

        var grid = buf.Grid;
        Assert.Equal("漢", grid.Screen[1][0].Text);
    }

    // ---- SGR / truecolor ----

    [Fact]
    public void Truecolor_is_preserved_in_replay()
    {
        var buf = Create();
        buf.Append("\x1b[38;2;153;99;51mcolored\x1b[m plain");

        var replay = buf.SerializeForReplay();
        Assert.Contains("38;2;153;99;51", replay);
        Assert.Contains("colored", replay);
    }

    // ---- カーソル / 消去 ----

    [Fact]
    public void Erase_in_line_removes_tail()
    {
        var buf = Create(cols: 20, rows: 3);
        buf.Append("hello world");
        buf.Append("\x1b[6G");   // col=6（"world" の先頭 w は col5、6Gは0-based col5）
        buf.Append("\x1b[K");    // 行末まで消去

        var replay = buf.SerializeForReplay();
        Assert.Contains("hello", replay);
        Assert.DoesNotContain("world", replay);
    }

    [Fact]
    public void Cursor_visibility_is_restored()
    {
        var buf = Create();
        buf.Append("\x1b[?25l"); // カーソル非表示

        Assert.EndsWith("\x1b[?25l", buf.SerializeForReplay());
    }

    // ---- alt-screen ----

    [Fact]
    public void Alt_screen_content_is_discarded_after_exit()
    {
        var buf = Create(cols: 40, rows: 5);
        buf.Append("main content\r\n");
        buf.Append("\x1b[?1049h");      // alt-screen へ
        buf.Append("ALT ONLY TEXT");
        buf.Append("\x1b[?1049l");      // メインへ復帰

        var replay = buf.SerializeForReplay();
        Assert.Contains("main content", replay);
        Assert.DoesNotContain("ALT ONLY TEXT", replay);
    }

    [Fact]
    public void Alt_screen_active_serializes_alt_content()
    {
        var buf = Create(cols: 40, rows: 5);
        buf.Append("main content\r\n");
        buf.Append("\x1b[?1049h");
        buf.Append("TUI SCREEN");

        // alt-screen 表示中はその画面が出る（スクロールバックにはメイン側の履歴なし＝二重化しない）
        var replay = buf.SerializeForReplay();
        Assert.Contains("TUI SCREEN", replay);
    }
}
