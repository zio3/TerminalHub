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
    public void Vs16_does_not_join_across_escape_sequence()
    {
        var buf = Create();
        // 基底と VS16 の間に SGR を挟むと join 状態がリセットされる（xterm の
        // precedingJoinState と同じ）。拡幅せず、従来どおり結合文字として合成のみ
        buf.Append(Sun + "[31m" + Vs16 + "B");

        var row = buf.Grid.Screen[0];
        Assert.Equal(1, row[0].Width);
        Assert.Equal("B", row[1].Text);
    }

    [Fact]
    public void Vs16_does_not_join_across_control_char()
    {
        var buf = Create();
        buf.Append(Sun + "\r" + Vs16);

        var row = buf.Grid.Screen[0];
        // CR で join が切れ、行頭の FE0F は直前印字セルが無い扱い（拡幅されない）
        Assert.Equal(1, row[0].Width);
    }

    [Fact]
    public void Orphan_vs16_at_start_is_harmless()
    {
        var buf = Create();
        buf.Append(Vs16 + "A");

        var row = buf.Grid.Screen[0];
        Assert.Equal("A", row[0].Text);
        Assert.Equal(1, row[0].Width);
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
        buf.Append("[1;2H");   // 1行2桁目（0-based col=1）
        buf.Append(Sun + Vs16 + "B");

        var row = buf.Grid.Screen[0];
        Assert.True(row[0].IsBlank);
        Assert.Equal(Sun + Vs16, row[1].Text);
        Assert.Equal(2, row[1].Width);
        Assert.True(row[2].IsWideTrailer);
        Assert.Equal("B", row[3].Text);
    }
}
