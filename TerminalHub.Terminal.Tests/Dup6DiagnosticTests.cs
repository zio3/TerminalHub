using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// 6つ目の実キャプチャ（dup6a、修正済みビルドで切替復帰時に Compacting が2行に見えた事象）の診断。
/// レース修正後も再現したため、エミュレータの保持状態そのものが壊れていないかを検証する。
/// 特徴: このキャプチャは初回全画面描画を含まず、セル差分描画（CUP＋ESC[1Cスキップ）主体。
/// </summary>
public class Dup6DiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public Dup6DiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string? TryLoad(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "local", fixture);
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
    [InlineData(160, 54)]
    [InlineData(120, 54)]
    [InlineData(160, 67)]
    public void Emulator_state_after_full_stream(int cols, int rows)
    {
        var stream = TryLoad("dup6a-20260703-082331.raw");
        Skip.If(stream == null, "ローカルキャプチャ dup6a が無いためスキップ");

        var emu = new EmulatedStateBuffer(cols, rows);
        emu.Append(stream!);

        int compactingScreen = 0, compactingScrollback = 0;
        for (int r = 0; r < rows; r++)
        {
            var text = RowText(emu.Grid.Screen[r]);
            if (text.Contains("Compacting"))
            {
                compactingScreen++;
                _output.WriteLine($"screen[{r}]: {text}");
            }
        }
        for (int i = 0; i < emu.Grid.Scrollback.Count; i++)
        {
            var text = RowText(emu.Grid.Scrollback[i]);
            if (text.Contains("Compacting"))
            {
                compactingScrollback++;
                _output.WriteLine($"scrollback[{i}]: {text}");
            }
        }
        _output.WriteLine($"grid {cols}x{rows}: screen内Compacting={compactingScreen}, scrollback内Compacting={compactingScrollback}, scrollback行数={emu.Grid.Scrollback.Count}");

        // 画面末尾付近も表示（スピナー行周辺の状態確認）
        for (int r = Math.Max(0, rows - 35); r < rows; r++)
        {
            var text = RowText(emu.Grid.Screen[r]);
            if (text.Length > 0)
            {
                _output.WriteLine($"  [{r:D2}] {text}");
            }
        }
    }

    /// <summary>
    /// 起動直後のサイズ不一致シナリオ: エミュレータが既定サイズのままストリーム前半を解釈し、
    /// 途中で実サイズに Resize された場合、状態が汚染されて残らないか。
    /// </summary>
    [SkippableFact]
    public void Startup_size_mismatch_then_resize()
    {
        var stream = TryLoad("dup6a-20260703-082331.raw");
        Skip.If(stream == null, "ローカルキャプチャ dup6a が無いためスキップ");

        // 前半3KBを既定サイズ(120x30)で解釈 → 実サイズ(160x54)へリサイズ → 残りを解釈
        var emu = new EmulatedStateBuffer(120, 30);
        emu.Append(stream!.Substring(0, 3000));
        emu.Resize(160, 54);
        emu.Append(stream.Substring(3000));

        int compacting = 0;
        for (int r = 0; r < 54; r++)
        {
            var text = RowText(emu.Grid.Screen[r]);
            if (text.Contains("Compacting"))
            {
                compacting++;
                _output.WriteLine($"screen[{r}]: {text}");
            }
        }
        foreach (var row in emu.Grid.Scrollback)
        {
            var text = RowText(row);
            if (text.Contains("Compacting"))
            {
                compacting++;
                _output.WriteLine($"scrollback: {text}");
            }
        }
        _output.WriteLine($"サイズ不一致→リサイズ後のCompacting総数: {compacting}, scrollback行数={emu.Grid.Scrollback.Count}");
    }
}
