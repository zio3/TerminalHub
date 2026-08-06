using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// VS16（U+FE0F 絵文字化セレクタ）による直前セルの拡幅（幅1→2）の検証。
/// ConPTY(conhost) 実測（experiments/ConPtyWidthProbe・2026-08-06）と xterm.js 6.0 の
/// join 機構に合わせた挙動。terminal.js の '11-vs16' プロバイダと同一規則であること。
/// </summary>
public class Vs16WidthTests
{
    private const string Vs16 = "\uFE0F";
    private const string Sun = "☀";       // ☀ EAW=Neutral → 幅1
    private const string Check = "✅";     // ✅ EAW=Wide → 幅2

    private static EmulatedStateBuffer Create(int cols = 20, int rows = 5) => new(cols, rows);

    [Fact]
    public void Vs16_widens_narrow_base_cell()
    {
        var buf = Create();
        buf.Append("A" + Sun + Vs16 + "B");

        var row = buf.Grid.Screen[0];
        Assert.Equal("A", row[0].Text);
        Assert.Equal(Sun + Vs16, row[1].Text);
        Assert.Equal(2, row[1].Width);
        Assert.True(row[2].IsWideTrailer);
        Assert.Equal("B", row[3].Text);
        Assert.Equal(4, buf.Grid.CursorCol);
    }

    [Fact]
    public void Vs16_after_wide_base_appends_without_extra_advance()
    {
        var buf = Create();
        buf.Append(Check + Vs16 + "B");

        var row = buf.Grid.Screen[0];
        Assert.Equal(Check + Vs16, row[0].Text);
        Assert.Equal(2, row[0].Width);
        Assert.True(row[1].IsWideTrailer);
        Assert.Equal("B", row[2].Text);
    }

    [Fact]
    public void Vs16_with_base_at_last_column_moves_cluster_to_next_line()
    {
        var buf = Create(cols: 4);
        buf.Append("abc" + Sun + Vs16);

        var row0 = buf.Grid.Screen[0];
        var row1 = buf.Grid.Screen[1];
        // 旧最終列は空白化され、クラスタごと次行の先頭へ移る（xterm 準拠）
        Assert.True(row0[3].IsBlank);
        Assert.Equal(Sun + Vs16, row1[0].Text);
        Assert.Equal(2, row1[0].Width);
        Assert.True(row1[1].IsWideTrailer);
        Assert.Equal(2, buf.Grid.CursorCol);
    }

    [Fact]
    public void Wrap_widening_clears_wide_pair_on_destination_row()
    {
        var buf = Create(cols: 4);
        // 次行(row1)の桁1に全角「あ」を置く（桁2がそのトレーラになる）
        buf.Append("\u001B[2;2Hあ");
        // row0 へ戻って行末拡幅を発生させ、クラスタを row1 の桁0-1 へ移す
        buf.Append("\u001B[1;1Habc" + Sun + Vs16);

        var row1 = buf.Grid.Screen[1];
        Assert.Equal(Sun + Vs16, row1[0].Text);
        Assert.Equal(2, row1[0].Width);
        Assert.True(row1[1].IsWideTrailer);
        // 「あ」のトレーラ（桁2）が孤児のまま残らないこと（Codex レビュー P1 指摘）
        Assert.False(row1[2].IsWideTrailer);
        Assert.True(row1[2].IsBlank);
    }

    [Fact]
    public void Vs16_does_not_join_across_escape_sequence()
    {
        var buf = Create();
        // 基底と VS16 の間に SGR を挟むと join 状態がリセットされる（xterm の
        // precedingJoinState と同じ）。拡幅せず、独立した1セル（空白へ正規化）として
        // 置かれ、カーソルが1桁進む（xterm の孤立幅0セルと同じ配置）
        buf.Append(Sun + "\u001B[31m" + Vs16 + "B");

        var row = buf.Grid.Screen[0];
        Assert.Equal(Sun, row[0].Text);
        Assert.Equal(1, row[0].Width);
        Assert.Equal(" ", row[1].Text);
        Assert.Equal("B", row[2].Text);
        Assert.Equal(3, buf.Grid.CursorCol);
    }

    [Fact]
    public void Nonjoining_vs16_layout_survives_replay_round_trip()
    {
        var buf = Create();
        // 非結合 VS16 を含む行の再生パリティ。旧実装（直前セルへ合成）は
        // シリアライズで基底と VS16 が連続になり、再生時に '11-vs16' 規則が
        // 再拡幅してレイアウトが変わっていた（Codex レビュー P1 指摘）
        buf.Append(Sun + "\u001B[31m" + Vs16 + "B");

        var replay = buf.SerializeForReplay();
        var buf2 = Create();
        buf2.Append(replay);

        var row = buf.Grid.Screen[0];
        var row2 = buf2.Grid.Screen[0];
        for (int c = 0; c < buf.Grid.Cols; c++)
        {
            Assert.Equal(row[c].Text, row2[c].Text);
            Assert.Equal(row[c].Width, row2[c].Width);
        }
    }

    [Fact]
    public void Vs16_does_not_join_across_control_char()
    {
        var buf = Create();
        buf.Append(Sun + "\r" + Vs16);

        var row = buf.Grid.Screen[0];
        // CR で join が切れ、行頭の FE0F は独立セル化して桁0を上書きする
        // （xterm も同じ位置に孤立幅0セルを書き ☀ を潰す）
        Assert.Equal(" ", row[0].Text);
        Assert.Equal(1, row[0].Width);
        Assert.Equal(1, buf.Grid.CursorCol);
    }

    [Fact]
    public void Orphan_vs16_at_start_occupies_one_blank_cell()
    {
        var buf = Create();
        buf.Append(Vs16 + "A");

        var row = buf.Grid.Screen[0];
        Assert.Equal(" ", row[0].Text);
        Assert.Equal("A", row[1].Text);
        Assert.Equal(1, row[1].Width);
    }

    [Fact]
    public void Consecutive_orphan_vs16_each_occupy_one_cell()
    {
        var buf = Create();
        // 孤立 VS16 の連続は1個ずつ独立セル化（2個目が1個目の空白セルを拡幅しない）
        buf.Append(Vs16 + Vs16 + "A");

        var row = buf.Grid.Screen[0];
        Assert.Equal(" ", row[0].Text);
        Assert.Equal(1, row[0].Width);
        Assert.Equal(" ", row[1].Text);
        Assert.Equal(1, row[1].Width);
        Assert.Equal("A", row[2].Text);
    }

    [Fact]
    public void Vs16_does_not_join_across_c1_control()
    {
        var buf = Create();
        // C1 制御（NEL U+0085）は xterm では ESC E 等価として実行され（CR+LF）、
        // join もリセットされる。行が変わった先で VS16 は独立セル化する
        buf.Append(Sun + "\u0085" + Vs16 + "B");

        var row0 = buf.Grid.Screen[0];
        var row1 = buf.Grid.Screen[1];
        Assert.Equal(Sun, row0[0].Text);
        Assert.Equal(1, row0[0].Width);
        Assert.Equal(" ", row1[0].Text);
        Assert.Equal("B", row1[1].Text);
    }

    [Fact]
    public void Vs16_joins_across_del()
    {
        var buf = Create();
        // DEL (0x7F) は xterm の Ground では無視され join 状態を保持する → 拡幅される
        buf.Append(Sun + "\u007F" + Vs16 + "B");

        var row = buf.Grid.Screen[0];
        Assert.Equal(Sun + Vs16, row[0].Text);
        Assert.Equal(2, row[0].Width);
        Assert.True(row[1].IsWideTrailer);
        Assert.Equal("B", row[2].Text);
    }

    [Fact]
    public void Orphan_vs16_during_pending_wrap_is_consumed_without_effect()
    {
        var buf = Create(cols: 4);
        // 最終列に幅1の ☀ を書いて遅延ラップ状態にし、SGR で join を切ってから VS16。
        // xterm では仮想列（最終列の右）で消費されて画面に影響せず、遅延ラップも維持される。
        // 次の B は行頭へ折り返す
        buf.Append("abc" + Sun + "\u001B[31m" + Vs16 + "B");

        var row0 = buf.Grid.Screen[0];
        var row1 = buf.Grid.Screen[1];
        Assert.Equal(Sun, row0[3].Text);
        Assert.Equal(1, row0[3].Width);
        Assert.Equal("B", row1[0].Text);
    }

    [Fact]
    public void Eightbit_st_terminates_dcs_and_osc_strings()
    {
        var buf = Create();
        // 8-bit DCS/OSC 文字列は 8-bit ST (U+009C) で終端できる
        // （終端できないと以降の出力を永久に飲み込む）
        buf.Append("A\u0090foo\u009CB");
        buf.Append("\u009D0;title\u009CC");

        var row = buf.Grid.Screen[0];
        Assert.Equal("A", row[0].Text);
        Assert.Equal("B", row[1].Text);
        Assert.Equal("C", row[2].Text);
    }

    [Fact]
    public void Esc_then_eightbit_st_terminates_dcs_and_osc_strings()
    {
        var buf = Create();
        // 文字列内で ESC を受けた直後の 8-bit ST (U+009C) も終端として扱う
        // （ESC \ の7-bit STと8-bit STの混在ケース）
        buf.Append("A\u0090foo\u001B\u009CB");
        buf.Append("\u009D0;title\u001B\u009CC");

        var row = buf.Grid.Screen[0];
        Assert.Equal("A", row[0].Text);
        Assert.Equal("B", row[1].Text);
        Assert.Equal("C", row[2].Text);
    }

    [Fact]
    public void Standalone_st_preserves_join()
    {
        var buf = Create();
        // 単独の ST (U+009C) は xterm では無視され join を保持する → 拡幅される
        buf.Append(Sun + "\u009C" + Vs16 + "B");

        var row = buf.Grid.Screen[0];
        Assert.Equal(Sun + Vs16, row[0].Text);
        Assert.Equal(2, row[0].Width);
        Assert.Equal("B", row[2].Text);
    }

    [Fact]
    public void Sos_string_preserves_join()
    {
        var buf = Create();
        // SOS (U+0098) の読み捨て文字列は ST 後まで join を保持する → 拡幅される
        buf.Append(Sun + "\u0098ignored\u009C" + Vs16 + "B");

        var row = buf.Grid.Screen[0];
        Assert.Equal(Sun + Vs16, row[0].Text);
        Assert.Equal(2, row[0].Width);
        Assert.Equal("B", row[2].Text);
    }

    [Fact]
    public void Eightbit_csi_is_parsed_like_esc_bracket()
    {
        var buf = Create();
        // 8-bit CSI (U+009B) は ESC [ 等価としてシーケンス解釈される
        // （途中の "31m" が画面に印字されないこと）
        buf.Append("A\u009B31mB");

        var row = buf.Grid.Screen[0];
        Assert.Equal("A", row[0].Text);
        Assert.Equal("B", row[1].Text);
    }

    [Fact]
    public void Vs16_after_combining_mark_still_widens_base_cell()
    {
        var buf = Create();
        // 基底 + 結合文字（幅0）+ VS16 → クラスタ全体を保持したまま幅2へ
        buf.Append("é" + Vs16 + "B");

        var row = buf.Grid.Screen[0];
        Assert.Equal("é" + Vs16, row[0].Text);
        Assert.Equal(2, row[0].Width);
        Assert.True(row[1].IsWideTrailer);
        Assert.Equal("B", row[2].Text);
    }

    [Fact]
    public void Widened_cluster_survives_replay_round_trip()
    {
        var buf = Create();
        buf.Append("A" + Sun + Vs16 + "Bあ" + Check + Vs16);

        // 再生パリティ: シリアライズ結果を新しいエミュレータへ食わせて同一グリッドになること
        var replay = buf.SerializeForReplay();
        var buf2 = Create();
        buf2.Append(replay);

        var row = buf.Grid.Screen[0];
        var row2 = buf2.Grid.Screen[0];
        for (int c = 0; c < buf.Grid.Cols; c++)
        {
            Assert.Equal(row[c].Text, row2[c].Text);
            Assert.Equal(row[c].Width, row2[c].Width);
        }
    }

    [Fact]
    public void Vs16_trailer_overwriting_wide_pair_clears_orphan_half()
    {
        var buf = Create();
        // 全角「あ」(0-1) を書いた後、カーソルを桁1へ戻して幅1の ☀ を書く →「あ」は片割れ防止で消える。
        // 続く VS16 で ☀ が幅2に広がり、トレーラが桁2を上書きする
        buf.Append("あ");
        buf.Append("\u001B[1;2H");   // 1行2桁目（0-based col=1）
        buf.Append(Sun + Vs16 + "B");

        var row = buf.Grid.Screen[0];
        Assert.True(row[0].IsBlank);
        Assert.Equal(Sun + Vs16, row[1].Text);
        Assert.Equal(2, row[1].Width);
        Assert.True(row[2].IsWideTrailer);
        Assert.Equal("B", row[3].Text);
    }
}
