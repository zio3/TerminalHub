using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// 3つ目の実キャプチャ（dup3-20260703-074713.raw）の診断。
/// セッション切替→復帰でリプレイ後の表示が1行ズレて重複した事象の切り分け。
/// リプレイのラウンドトリップ（A→serialize→B）が完全一致するかを検証する（一時診断）。
/// </summary>
public class Dup3DiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public Dup3DiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string? TryLoad()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "local", "dup3-20260703-074713.raw");
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
    }

    private static string RowText(Cell[] row)
    {
        var sb = new StringBuilder();
        foreach (var cell in row)
        {
            if (cell.IsWideTrailer) continue;
            sb.Append(cell.Text ?? " ");
        }
        return sb.ToString().TrimEnd();
    }

    [SkippableTheory]
    [InlineData(120, 67)]
    [InlineData(160, 67)]
    [InlineData(200, 67)]
    [InlineData(120, 40)]
    public void Roundtrip_replay_is_identical(int cols, int rows)
    {
        var stream = TryLoad();
        Skip.If(stream == null, "ローカルキャプチャ dup3 が無いためスキップ");

        var a = new EmulatedStateBuffer(cols, rows);
        a.Append(stream!);
        var replay = a.SerializeForReplay();

        var b = new EmulatedStateBuffer(cols, rows);
        b.Append(replay);

        _output.WriteLine($"grid {cols}x{rows}: A.scrollback={a.Grid.Scrollback.Count}, B.scrollback={b.Grid.Scrollback.Count}, " +
                          $"A.cursor=({a.Grid.CursorRow},{a.Grid.CursorCol}), B.cursor=({b.Grid.CursorRow},{b.Grid.CursorCol})");

        int mismatches = 0;

        // スクロールバック行数と内容
        if (a.Grid.Scrollback.Count != b.Grid.Scrollback.Count)
        {
            _output.WriteLine($"!! scrollback行数不一致: A={a.Grid.Scrollback.Count} B={b.Grid.Scrollback.Count}");
            mismatches++;
        }
        int sbCount = Math.Min(a.Grid.Scrollback.Count, b.Grid.Scrollback.Count);
        for (int i = 0; i < sbCount; i++)
        {
            var ta = RowText(a.Grid.Scrollback[i]);
            var tb = RowText(b.Grid.Scrollback[i]);
            if (ta != tb && mismatches < 10)
            {
                _output.WriteLine($"!! scrollback[{i}]:\n  A: {ta}\n  B: {tb}");
                mismatches++;
            }
        }

        // 画面行の内容
        for (int r = 0; r < rows; r++)
        {
            var ta = RowText(a.Grid.Screen[r]);
            var tb = RowText(b.Grid.Screen[r]);
            if (ta != tb && mismatches < 20)
            {
                _output.WriteLine($"!! screen[{r}]:\n  A: {ta}\n  B: {tb}");
                mismatches++;
            }
        }

        // カーソル位置
        if (a.Grid.CursorRow != b.Grid.CursorRow || a.Grid.CursorCol != b.Grid.CursorCol)
        {
            _output.WriteLine($"!! cursor不一致: A=({a.Grid.CursorRow},{a.Grid.CursorCol}) B=({b.Grid.CursorRow},{b.Grid.CursorCol})");
            mismatches++;
        }

        Assert.Equal(0, mismatches);
    }

    /// <summary>
    /// 実際の切替復帰シナリオ: キャプチャ前半で切替（リプレイ）、後半をライブ出力として
    /// 「リプレイから再構築した端末(B)」と「連続して見ていた端末(A)」の最終画面が一致するか。
    /// </summary>
    [SkippableTheory]
    [InlineData(160, 67)]
    public void Switchback_midstream_replay_then_live_matches(int cols, int rows)
    {
        var stream = TryLoad();
        Skip.If(stream == null, "ローカルキャプチャ dup3 が無いためスキップ");

        // 切替ポイントをストリームの中間付近（チャンク境界を意識せず素朴に半分）にする
        int mid = stream!.Length / 2;
        var first = stream.Substring(0, mid);
        var rest = stream.Substring(mid);

        // A: ずっと接続していた端末（連続適用）
        var a = new EmulatedStateBuffer(cols, rows);
        a.Append(stream);

        // B: 前半まで見た状態でリプレイ復元し、後半をライブで受けた端末
        var recorder = new EmulatedStateBuffer(cols, rows);
        recorder.Append(first);
        var replay = recorder.SerializeForReplay();
        var b = new EmulatedStateBuffer(cols, rows);
        b.Append(replay);
        b.Append(rest);

        int mismatches = 0;
        for (int r = 0; r < rows; r++)
        {
            var ta = RowText(a.Grid.Screen[r]);
            var tb = RowText(b.Grid.Screen[r]);
            if (ta != tb && mismatches < 20)
            {
                _output.WriteLine($"!! screen[{r}]:\n  A: {ta}\n  B: {tb}");
                mismatches++;
            }
        }
        _output.WriteLine($"A.scrollback={a.Grid.Scrollback.Count} B.scrollback={b.Grid.Scrollback.Count}");
        Assert.Equal(0, mismatches);
    }
}
