using TerminalHub.Analyzers;
using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// ClaudeCodeAnalyzer.TryAnalyze / ContainsAnimationPattern のテーブル駆動テスト。
/// ケースは解析器コメント内の実 CLI 出力例と、コードが明示的にガードしている
/// エッジ（スピナー二重化・数字始まり・断片マッチ・中断検出）から作成。
/// </summary>
public sealed class ClaudeCodeAnalyzerTests
{
    // ESC(0x1B)。ソースに生の制御文字を混ぜないよう ASCII だけで生成する。
    private static readonly string Esc = ((char)0x1b).ToString();

    private static readonly ClaudeCodeAnalyzer Analyzer = new();

    // --- 処理中（完全形: スピナー + テキスト + (... to interrupt ...)） ---
    [Theory]
    [InlineData("✶ Spellbinding… (esc to interrupt)", "Spellbinding…")]
    [InlineData("✢ Actualizing… (esc to interrupt · thinking)", "Actualizing…")]
    [InlineData("✢ Pondering… (esc to interrupt · thought for 3s)", "Pondering…")]
    [InlineData("* Honking… (ctrl+c to interrupt · 39s · ↓ 941 tokens · thought for 16s)", "Honking…")]
    [InlineData("✻ Docker ビルド & テスト中… (1m 24s · ↓ 0 tokens)", "Docker ビルド & テスト中…")]
    [InlineData("✻ Docker ビルド & テスト中… (running stop hook)", "Docker ビルド & テスト中…")]
    public void TryAnalyze_ProcessingFullForm_ReturnsProcessingWithText(string input, string expectedText)
    {
        var ok = Analyzer.TryAnalyze(input, out var result);

        Assert.True(ok);
        Assert.True(result.IsProcessing);
        Assert.False(result.IsInterrupted);
        Assert.Equal(expectedText, result.ProcessingText);
    }

    // --- 処理中（簡易形: ジッター対策の部分更新。スピナー + テキスト…） ---
    [Theory]
    [InlineData("✢ Boondoggling…", "Boondoggling…")]
    [InlineData("* Harmonizing…", "Harmonizing…")]
    [InlineData("✶ Docker ビルド & テスト中…", "Docker ビルド & テスト中…")]
    public void TryAnalyze_ProcessingSimpleForm_ReturnsProcessingWithText(string input, string expectedText)
    {
        var ok = Analyzer.TryAnalyze(input, out var result);

        Assert.True(ok);
        Assert.True(result.IsProcessing);
        Assert.Equal(expectedText, result.ProcessingText);
    }

    // --- 中断（旧/新パターン） ---
    [Theory]
    [InlineData("[Request interrupted by user]")]
    [InlineData("Interrupted · What should Claude do instead?")]
    public void TryAnalyze_Interrupted_ReturnsInterrupted(string input)
    {
        var ok = Analyzer.TryAnalyze(input, out var result);

        Assert.True(ok);
        Assert.True(result.IsInterrupted);
        Assert.False(result.IsProcessing);
    }

    // --- 非処理（ガード各種） ---
    [Theory]
    // スピナー2個以上 = ANSI除去で壊れた上書きフレームの連結 → 破棄
    [InlineData("✻✶*✢ CoC mpo a✢ m cp t...")]
    // 簡易形の断片（"king…" は5文字で6文字未満）→ 破棄
    [InlineData("✶ king…")]
    // 数字始まり = ステータス行の区切りをスピナー誤マッチしたケース → 破棄
    [InlineData("✶ 34.8k tokensWandering…")]
    // スピナーもテキストも無い通常出力
    [InlineData("Hello world")]
    [InlineData("")]
    public void TryAnalyze_NonProcessing_ReturnsFalse(string input)
    {
        var ok = Analyzer.TryAnalyze(input, out var result);

        Assert.False(ok);
        Assert.False(result.IsProcessing);
        Assert.False(result.IsInterrupted);
    }

    // --- ANSI エスケープに包まれても解析できる ---
    [Fact]
    public void TryAnalyze_AnsiWrapped_IsCleanedBeforeMatching()
    {
        var input = Esc + "[2m✶ Spellbinding… (esc to interrupt)" + Esc + "[0m";

        var ok = Analyzer.TryAnalyze(input, out var result);

        Assert.True(ok);
        Assert.True(result.IsProcessing);
        Assert.Equal("Spellbinding…", result.ProcessingText);
    }

    // --- アニメーションパターン検出: スピナー文字 ---
    [Theory]
    [InlineData("✶ working", true)]
    [InlineData("plain text", false)]
    public void ContainsAnimationPattern_DetectsSpinner(string input, bool expected)
    {
        Assert.Equal(expected, Analyzer.ContainsAnimationPattern(input));
    }

    // --- アニメーションパターン検出: Synchronized Output（処理中の画面更新に頻出） ---
    [Fact]
    public void ContainsAnimationPattern_DetectsSynchronizedOutput()
    {
        Assert.True(Analyzer.ContainsAnimationPattern(Esc + "[?2026h"));
        Assert.True(Analyzer.ContainsAnimationPattern(Esc + "[?2026l"));
    }
}
