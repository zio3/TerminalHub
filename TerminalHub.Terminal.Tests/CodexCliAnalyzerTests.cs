using TerminalHub.Analyzers;
using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// CodexCliAnalyzer.TryAnalyze / ContainsAnimationPattern のテーブル駆動テスト。
/// Codex の処理中行「• &lt;テキスト&gt; (&lt;経過&gt; • esc to interrupt)」を検証。
/// </summary>
public sealed class CodexCliAnalyzerTests
{
    // ESC(0x1B)。ソースに生の制御文字を混ぜないよう ASCII だけで生成する。
    private static readonly string Esc = ((char)0x1b).ToString();

    private static readonly CodexCliAnalyzer Analyzer = new();

    [Theory]
    [InlineData("• Working (0s • esc to interrupt)", "Working")]
    [InlineData("• Running date command (5s • esc to interrupt)", "Running date command")]
    [InlineData("• Exploring alternate access (1m 01s • esc to interrupt)", "Exploring alternate access")]
    public void TryAnalyze_Processing_ReturnsProcessingWithText(string input, string expectedText)
    {
        var ok = Analyzer.TryAnalyze(input, out var result);

        Assert.True(ok);
        Assert.True(result.IsProcessing);
        Assert.Equal(expectedText, result.ProcessingText);
    }

    [Theory]
    [InlineData("• Done")]                          // "esc to interrupt" 節が無い
    [InlineData("Working (0s • esc to interrupt)")] // 先頭のバレットが無い
    [InlineData("plain text")]
    [InlineData("")]
    public void TryAnalyze_NonProcessing_ReturnsFalse(string input)
    {
        var ok = Analyzer.TryAnalyze(input, out var result);

        Assert.False(ok);
        Assert.False(result.IsProcessing);
    }

    [Fact]
    public void TryAnalyze_AnsiWrapped_IsCleanedBeforeMatching()
    {
        var input = Esc + "[36m• Working (0s • esc to interrupt)" + Esc + "[0m";

        var ok = Analyzer.TryAnalyze(input, out var result);

        Assert.True(ok);
        Assert.Equal("Working", result.ProcessingText);
    }

    [Theory]
    [InlineData("• Working", true)]
    [InlineData("plain text", false)]
    public void ContainsAnimationPattern_DetectsBullet(string input, bool expected)
    {
        Assert.Equal(expected, Analyzer.ContainsAnimationPattern(input));
    }
}
