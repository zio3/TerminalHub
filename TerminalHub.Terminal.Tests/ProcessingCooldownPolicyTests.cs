using TerminalHub.Services;
using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// ProcessingCooldownPolicy.IsWithinCooldown の境界テスト。
/// send_to_session の誤送信/誤ブロックに直結するレース回避ロジックのため、
/// null・窓内・境界・窓外の各ケースを固定する。
/// </summary>
public sealed class ProcessingCooldownPolicyTests
{
    // 基準時刻はテスト内で固定（Date.Now を使わないので再現性がある）
    private static readonly DateTime Now = new(2026, 7, 17, 12, 0, 0, DateTimeKind.Local);

    [Fact]
    public void IsWithinCooldown_NullEventTime_ReturnsFalse()
    {
        // イベント未発生（一度も Stop / hook 待ちが立っていない）なら常にクールダウン外
        Assert.False(ProcessingCooldownPolicy.IsWithinCooldown(null, Now, 3.0));
    }

    [Fact]
    public void IsWithinCooldown_JustInsideWindow_ReturnsTrue()
    {
        // 2.9 秒前 → 3.0 秒窓の内側
        var last = Now.AddSeconds(-2.9);
        Assert.True(ProcessingCooldownPolicy.IsWithinCooldown(last, Now, 3.0));
    }

    [Fact]
    public void IsWithinCooldown_ExactlyAtBoundary_ReturnsFalse()
    {
        // ちょうど 3.0 秒経過 → 「未満」なので false（境界は窓外扱い）
        var last = Now.AddSeconds(-3.0);
        Assert.False(ProcessingCooldownPolicy.IsWithinCooldown(last, Now, 3.0));
    }

    [Fact]
    public void IsWithinCooldown_PastWindow_ReturnsFalse()
    {
        // 5 秒前 → 窓外
        var last = Now.AddSeconds(-5.0);
        Assert.False(ProcessingCooldownPolicy.IsWithinCooldown(last, Now, 3.0));
    }

    [Fact]
    public void IsWithinCooldown_WaitingHookWindow_UsesShorterWindow()
    {
        // hook 待ちクールダウン(1.5秒)の内外を確認
        var justInside = Now.AddSeconds(-1.4);
        var justOutside = Now.AddSeconds(-1.6);

        Assert.True(ProcessingCooldownPolicy.IsWithinCooldown(
            justInside, Now, ProcessingCooldownPolicy.WaitingHookCooldownSeconds));
        Assert.False(ProcessingCooldownPolicy.IsWithinCooldown(
            justOutside, Now, ProcessingCooldownPolicy.WaitingHookCooldownSeconds));
    }

    [Fact]
    public void IsWithinCooldown_FutureEventTime_ReturnsTrue()
    {
        // 時計のずれ等で last が now より未来 → 負の経過 → 「未満」なので窓内扱い（誤クリア抑止側に倒す）
        var future = Now.AddSeconds(1.0);
        Assert.True(ProcessingCooldownPolicy.IsWithinCooldown(future, Now, 3.0));
    }

    [Fact]
    public void Constants_MatchDocumentedValues()
    {
        // 定数がドキュメント（CLAUDE.md / コメント）と一致していることを固定
        Assert.Equal(3.0, ProcessingCooldownPolicy.StopEventCooldownSeconds);
        Assert.Equal(1.5, ProcessingCooldownPolicy.WaitingHookCooldownSeconds);
    }
}
