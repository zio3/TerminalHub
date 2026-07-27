using System.Text;
using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// 生チャンクリング（RawChunkRing）のテスト。
/// リング自体の動作に加え、このリングが捕まえたい現象そのもの
/// 「チャンク分断＋リプレイ注入で xterm 側にだけ孤児文字が印字される」を
/// VtParser を xterm 模擬として使い再現・検証する。
/// </summary>
public class RawChunkRingTests
{
    private static string GridText(TerminalGrid grid)
    {
        var sb = new StringBuilder();
        foreach (var row in grid.Scrollback)
        {
            foreach (var cell in row)
            {
                sb.Append(cell.Text);
            }
        }
        foreach (var row in grid.Screen)
        {
            foreach (var cell in row)
            {
                sb.Append(cell.Text);
            }
        }
        return sb.ToString();
    }

    // ---- リング自体の動作 ----

    [Fact]
    public void Chunks_are_recorded_in_order_with_sequence_numbers()
    {
        var ring = new RawChunkRing();
        ring.Add("aaa", "");
        ring.Mark("injection");
        ring.Add("bbb", "");

        var entries = ring.Snapshot();
        Assert.Equal(3, entries.Count);
        Assert.Equal(new[] { 0L, 1L, 2L }, entries.Select(e => e.Seq));
        Assert.Equal("aaa", entries[0].Data);
        Assert.Equal("injection", entries[1].Note);
        Assert.Equal("bbb", entries[2].Data);
    }

    [Fact]
    public void Old_chunks_are_evicted_when_total_chars_exceed_cap()
    {
        var ring = new RawChunkRing(maxTotalChars: 10);
        ring.Add("11111", "");
        ring.Add("22222", "");
        ring.Add("33333", ""); // 15文字 → 先頭を追い出して10文字以内へ

        var entries = ring.Snapshot();
        Assert.Equal(2, entries.Count);
        Assert.Equal("22222", entries[0].Data);
        Assert.Equal("33333", entries[1].Data);
        Assert.Equal(3, ring.TotalAdded); // 通し番号は追い出し後も進む
    }

    [Fact]
    public void Escape_makes_control_chars_visible()
    {
        Assert.Equal("\\e[2K", RawChunkRing.Escape("\x1b[2K"));
        Assert.Equal("a\\r\\n\nb", RawChunkRing.Escape("a\r\nb"));
        Assert.Equal("\\a", RawChunkRing.Escape("\a"));
        Assert.Equal("\\x01", RawChunkRing.Escape("\x01"));
    }

    // ---- エミュレータへの組み込み ----

    [Fact]
    public void Emulated_buffer_records_chunk_boundaries_and_pending_state()
    {
        var buf = new EmulatedStateBuffer(40, 5);
        buf.Append("hello\x1b[2"); // EL シーケンスが途中で切れたチャンク
        buf.Append("K");            // 続きが次チャンクで届く

        var entries = buf.RawRing.Snapshot();
        Assert.Equal(2, entries.Count);
        Assert.Equal("\x1b[2", entries[0].PendingAfter); // 分断チャンクに容疑者マーク
        Assert.Equal("", entries[1].PendingAfter);       // 完結後は空

        // エミュレータ自身は 'K' を EL として消費し、文字としては持たない
        Assert.DoesNotContain("K", GridText(buf.Grid));
    }

    [Fact]
    public void Replay_and_lifecycle_events_are_marked_in_ring()
    {
        var buf = new EmulatedStateBuffer(40, 5);
        buf.Append("data");
        var snapshot = buf.BeginReplay();
        buf.Append("tail-data");
        buf.EndReplay(snapshot);
        buf.Resize(80, 24);
        buf.Clear();

        var notes = buf.RawRing.Snapshot().Where(e => e.Note != null).Select(e => e.Note!).ToList();
        Assert.Contains(notes, n => n.StartsWith("BeginReplay"));
        Assert.Contains(notes, n => n.StartsWith("EndReplay"));
        Assert.Contains(notes, n => n.StartsWith("Resize 80x24"));
        Assert.Contains(notes, n => n == "Clear");
    }

    [Fact]
    public void DumpText_flags_split_chunk_and_shows_markers()
    {
        var buf = new EmulatedStateBuffer(40, 5);
        buf.Append("hello\x1b[2");
        var snapshot = buf.BeginReplay();
        buf.EndReplay(snapshot);

        var dump = buf.RawRing.DumpText();
        Assert.Contains("**PENDING=\\e[2**", dump);      // 分断チャンクの容疑者マーク
        Assert.Contains("---- BeginReplay", dump);       // 注入マーカー
    }

    // ---- このリングが捕まえたい現象の再現（孤児フラグメント仮説） ----

    /// <summary>
    /// チャンク分断＋リプレイ注入（pending 種付けなし）で、エミュレータは正しいのに
    /// xterm 側にだけ孤児文字が印字されることの再現。
    /// 「先頭に想定外の1文字」「再描画すると直る」の想定メカニズム。
    /// </summary>
    [Fact]
    public void Split_sequence_plus_injection_prints_orphan_char_on_xterm_only()
    {
        var buf = new EmulatedStateBuffer(40, 5);
        var xtermGrid = new TerminalGrid(40, 5);
        var xterm = new VtParser(xtermGrid); // xterm.js 模擬（同じくステートフルなパーサ）

        // チャンク1: SGR(色指定 ESC[31m)シーケンスの途中で切れる。live 書き込みで xterm にも届く
        buf.Append("hello\x1b[3");
        xterm.Feed("hello\x1b[3");

        // ここで再同期が全リセット＋スナップショットを注入（pending 種付けを使わない素朴な注入）
        xterm.Feed("\x1bc" + buf.SerializeForReplay());

        // チャンク2: 分断シーケンスの後半が届く
        buf.Append("1m");
        xterm.Feed("1m");

        // エミュレータ側: "1m" は SGR の一部として消費され、文字として存在しない
        Assert.DoesNotContain("1m", GridText(buf.Grid));
        // xterm 側: リセットでパーサ状態が破棄されたため "1m" が孤児化して印字される
        Assert.Contains("1m", GridText(xtermGrid));
    }

    /// <summary>
    /// 実際の BeginReplay/EndReplay 経路（pending をテール先頭に種付け）なら孤児化しないことの確認。
    /// ＝防御が正しく通ったケースの回帰テスト。
    /// </summary>
    [Fact]
    public void Pending_seeded_replay_tail_prevents_orphan_char()
    {
        var buf = new EmulatedStateBuffer(40, 5);
        var xtermGrid = new TerminalGrid(40, 5);
        var xterm = new VtParser(xtermGrid);

        buf.Append("hello\x1b[3");
        xterm.Feed("hello\x1b[3");

        var snapshot = buf.BeginReplay();
        xterm.Feed("\x1bc" + snapshot.Content);
        var captured = buf.Append("1m");
        Assert.True(captured); // リプレイ中は live 直接書き込みせずテール経由で届く
        var tail = buf.EndReplay(snapshot);
        xterm.Feed(tail); // pending("\x1b[3") + "1m" = 完結した SGR として消費される

        Assert.DoesNotContain("1m", GridText(xtermGrid));
        Assert.Contains("hello", GridText(xtermGrid));
    }
}
