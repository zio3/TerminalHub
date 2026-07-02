using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// 2つ目の実キャプチャ（claude-tui-repaint-dup2.raw, 実端末64行）の診断。
/// グリッドサイズと二重化の関係を実測する（原因切り分け用の一時診断）。
/// </summary>
public class Dup2DiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public Dup2DiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string? TryLoad()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "local", "claude-tui-repaint-dup2.raw");
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
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

    [SkippableFact]
    public void Measure_duplication_by_grid_size()
    {
        var stream = TryLoad();
        Skip.If(stream == null, "ローカルキャプチャ dup2 が無いためスキップ");

        var rawCount = Count(stream!, "Worktree");
        _output.WriteLine($"raw: Worktree={rawCount}, size={stream!.Length}");

        foreach (var (cols, rows) in new[] { (120, 30), (250, 64), (250, 70), (250, 100) })
        {
            var emu = new EmulatedStateBuffer(cols, rows);
            emu.Append(stream);
            var replay = emu.SerializeForReplay();
            _output.WriteLine($"grid {cols}x{rows}: Worktree={Count(replay, "Worktree")}, replaySize={replay.Length}, scrollback={emu.Grid.Scrollback.Count}");
        }
    }
}
