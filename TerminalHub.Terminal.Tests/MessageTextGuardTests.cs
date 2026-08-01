using TerminalHub.Services;
using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// 本文の制御文字拒否の検証。これは「マーカー無し＝人間の指示」というセキュリティ境界の
/// 一部で、\r を通すとエンベロープ迂回（本文だけ先に送信確定させる）が成立してしまう。
/// </summary>
public sealed class MessageTextGuardTests
{
    [Theory]
    [InlineData("指示\rを埋める", '\r')]           // 送信確定の注入＝エンベロープ迂回の本丸
    [InlineData("指示\nを埋める", '\n')]           // 改行も TUI によっては確定扱い
    [InlineData("エスケープ\x1b[A", '\x1b')]       // ESC＝キー操作の注入
    [InlineData("タブ\t補完", '\t')]               // 補完等の UI 操作
    [InlineData("\x7f", '\x7f')]                   // DEL
    [InlineData("\0先頭ヌル", '\0')]               // default(char) と衝突しないこと
    public void 制御文字は検出される(string message, char expected)
    {
        Assert.True(MessageTextGuard.TryFindControlChar(message, out var found));
        Assert.Equal(expected, found);
    }

    [Theory]
    [InlineData("普通の日本語の指示です。C:\\work\\spec.md を読んで")]
    [InlineData("emoji 😀 とサロゲートペア 𠮷 も通す")]
    [InlineData("/context")]
    [InlineData("スペース と 記号 [] {} | ~ も可")]
    public void 通常のテキストは通る(string message)
    {
        Assert.False(MessageTextGuard.TryFindControlChar(message, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 空とnullは制御文字なし扱い(string? message)
    {
        Assert.False(MessageTextGuard.TryFindControlChar(message, out _));
    }
}
