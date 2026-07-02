using System.Text;
using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// 実環境キャプチャ（Fixtures/local/*.raw、git管理外）を使ったパリティ検証。
/// キャプチャはセッションの表示テキストを含むためコミットせず、存在する環境でのみ実行される。
/// </summary>
public class LocalCaptureParityTests
{
    private static string LocalDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "local");

    private static string? TryLoad(string fileName)
    {
        var path = Path.Combine(LocalDir, fileName);
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
    }

    /// <summary>
    /// Claude Code の repaint 型キャプチャ（ESC[H＋ESC[K フレーム×10）で、
    /// エミュレータ replay が生ストリームより大幅に縮む（フレームが畳まれている）ことを確認する。
    /// </summary>
    [SkippableFact]
    public void Claude_repaint_capture_is_collapsed()
    {
        var stream = TryLoad("claude-tui-repaint-dup.raw");
        Skip.If(stream == null, "ローカルキャプチャが無いためスキップ（Fixtures/local/claude-tui-repaint-dup.raw）");

        var raw = new RawStreamStateBuffer();
        raw.Append(stream!);

        // rows はキャプチャ時の実端末より大きめに取る。
        // 実端末より小さいと repaint フレーム自体がスクロールし、フレーム前半がスクロールバックに
        // 落ちて畳めなくなる（実運用ではセッションの実サイズを使うので起きない）。
        var emu = new EmulatedStateBuffer(cols: 200, rows: 100);
        emu.Append(stream!);

        var rawReplay = raw.SerializeForReplay();
        var emuReplay = emu.SerializeForReplay();

        // 生ストリームは repaint 10 フレームぶん膨張している。
        // エミュレータは最終状態に畳むので、大幅に小さくなるはず（フレーム数ぶんの重複がない）
        Assert.True(emuReplay.Length < rawReplay.Length / 3,
            $"emulator replay ({emuReplay.Length}) は raw ({rawReplay.Length}) の 1/3 未満に畳まれるはず");

        // このキャプチャ固有: 固定テキスト「recap:」が生では 20 回残るが、畳めば数回以下になる
        int emuCount = CountOccurrences(emuReplay, "recap:");
        int rawCount = CountOccurrences(rawReplay, "recap:");
        Assert.True(emuCount < rawCount,
            $"recap: の残存回数 emulator={emuCount} は raw={rawCount} より少ないはず");
        Assert.True(emuCount is >= 1 and <= 3,
            $"recap: は最終状態のぶん（1〜3回）だけ残るはず: {emuCount}");
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
