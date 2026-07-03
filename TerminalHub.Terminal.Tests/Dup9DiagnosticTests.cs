using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// カニ（Claude Code バナー）がスクロールバックに複数残る事象の診断。
/// 生ストリームには全画面再描画によりバナーが12回含まれるが、
/// エミュレータの最終状態では1回に畳まれているべき。
/// 複数回残る場合、「再描画と再描画の間のスクロール」で上端行が
/// 繰り返しアーカイブされている（畳み込みの構造的な穴）。
/// </summary>
public class Dup9DiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public Dup9DiagnosticTests(ITestOutputHelper output)
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
    [InlineData("dup9a.raw", 160, 36)]
    [InlineData("dup9a.raw", 160, 54)]
    [InlineData("dup9a.raw", 160, 71)]
    [InlineData("dup9a.raw", 160, 72)]
    [InlineData("dup9b.raw", 160, 70)]
    [InlineData("dup9b.raw", 160, 71)]
    [InlineData("dup9b.raw", 160, 72)]
    [InlineData("dup9b.raw", 160, 54)]
    public void Count_banner_occurrences_in_final_state(string fixture, int cols, int rows)
    {
        var stream = TryLoad(fixture);
        Skip.If(stream == null, $"ローカルキャプチャ {fixture} が無いためスキップ");

        var emu = new EmulatedStateBuffer(cols, rows);
        emu.Append(stream!);

        int InState(string probe)
        {
            int count = 0;
            foreach (var row in emu.Grid.Scrollback)
            {
                if (RowText(row).Contains(probe)) count++;
            }
            for (int r = 0; r < rows; r++)
            {
                if (RowText(emu.Grid.Screen[r]).Contains(probe)) count++;
            }
            return count;
        }

        _output.WriteLine($"{fixture} grid {cols}x{rows}: scrollback={emu.Grid.Scrollback.Count}行");
        foreach (var probe in new[] { "MCP servers need", "Conversation compacted", "Claude Code" })
        {
            _output.WriteLine($"  '{probe}' 最終状態出現数: {InState(probe)}（生ストリーム: {Count(stream!, probe)}）");
        }

        // スクロールバック内でバナー行がどこにあるか
        for (int i = 0; i < emu.Grid.Scrollback.Count; i++)
        {
            var text = RowText(emu.Grid.Scrollback[i]);
            if (text.Contains("MCP servers need") || text.Contains("Conversation compacted"))
            {
                _output.WriteLine($"  scrollback[{i}]: {text}");
            }
        }
    }

    private static int Count(string haystack, string needle)
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
