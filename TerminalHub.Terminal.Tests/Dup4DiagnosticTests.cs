using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// 4つ目の実キャプチャ（dup4-20260703-080615.raw、切替復帰で行内に文字が混ざる事象）の診断。
/// 修正前の順序（ライブチャンクがリプレイより先に空のxtermへ書かれる）で表示が壊れること、
/// 修正後の順序（スナップショット→テール）で壊れないことを、エミュレータをxterm代役として実証する。
/// </summary>
public class Dup4DiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public Dup4DiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string? TryLoad()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "local", "dup4-20260703-080615.raw");
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

    private static int CompareScreens(EmulatedStateBuffer a, EmulatedStateBuffer b, int rows, ITestOutputHelper output, int maxReport)
    {
        int mismatches = 0;
        for (int r = 0; r < rows; r++)
        {
            var ta = RowText(a.Grid.Screen[r]);
            var tb = RowText(b.Grid.Screen[r]);
            if (ta != tb)
            {
                if (mismatches < maxReport)
                {
                    output.WriteLine($"!! screen[{r}]:\n  正: {ta}\n  誤: {tb}");
                }
                mismatches++;
            }
        }
        return mismatches;
    }

    [SkippableTheory]
    [InlineData(160, 40)]
    public void Old_order_live_chunk_before_replay_corrupts_but_fixed_order_does_not(int cols, int rows)
    {
        var stream = TryLoad();
        Skip.If(stream == null, "ローカルキャプチャ dup4 が無いためスキップ");

        // 実際の事象は「切替後は差分描画しか来ない」ため壊れたまま自己修復されない。
        // それを再現するため、切替ポイントを末尾付近の複数箇所で試す
        // （切替直後に届くライブチャンク＝レースチャンクの後、残りが少ないケース）
        int anyBroken = 0;
        foreach (var fromEnd in new[] { 1024, 2048, 4096, 8192 })
        {
            if (stream!.Length <= fromEnd) continue;
            int split = stream.Length - fromEnd;
            int chunkLen = Math.Min(1024, stream.Length - split);
            var beforeSwitch = stream.Substring(0, split);
            var raceChunk = stream.Substring(split, chunkLen);
            var afterChunk = stream.Substring(split + chunkLen);

            // 正解: 連続して見ていた端末
            var truth = new EmulatedStateBuffer(cols, rows);
            truth.Append(stream);

            // 【修正前の順序】スナップショットを取ってから、空のxtermへ raceChunk → リプレイ → 残り
            // （beforeSwitch がエスケープ途中で切れていても旧実装はケアしないので素の SerializeForReplay）
            var oldRecorder = new EmulatedStateBuffer(cols, rows);
            oldRecorder.Append(beforeSwitch);
            var oldSnapshot = oldRecorder.SerializeForReplay();
            var broken = new EmulatedStateBuffer(cols, rows);
            broken.Append(raceChunk);
            broken.Append(oldSnapshot);
            broken.Append(afterChunk);
            int brokenMismatches = CompareScreens(truth, broken, rows, _output, maxReport: 3);
            anyBroken += brokenMismatches;

            // 【修正後のフロー】BeginReplay（未確定シーケンスをテールに種付け）→
            // 復元中に届くライブ出力(raceChunk)はテールへ → スナップショット→テール→残り の順で書き込み
            var recorder = new EmulatedStateBuffer(cols, rows);
            recorder.Append(beforeSwitch);
            var snap = recorder.BeginReplay();
            Assert.True(recorder.Append(raceChunk)); // 復元中のライブ出力はキャプチャされる
            var tail = recorder.EndReplay(snap);

            var fixedOrder = new EmulatedStateBuffer(cols, rows);
            fixedOrder.Append(snap.Content);
            fixedOrder.Append(tail);
            fixedOrder.Append(afterChunk);
            int fixedMismatches = CompareScreens(truth, fixedOrder, rows, _output, maxReport: 3);

            _output.WriteLine($"末尾-{fromEnd}で切替: 修正前順序の不一致行={brokenMismatches}, 修正後順序の不一致行={fixedMismatches}");

            // 修正後の順序はどの分割位置でも壊れないこと（本命のアサーション）
            Assert.Equal(0, fixedMismatches);
        }

        _output.WriteLine(anyBroken > 0
            ? $"→ 修正前順序で表示崩れを再現（計{anyBroken}行）、修正後順序では全位置で一致"
            : "→ このキャプチャでは修正前順序でも偶然一致（レース窓に依存）");
    }
}
