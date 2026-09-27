using TerminalHub.Services;
using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// 配送の提出観測（書き込み → UserPromptSubmit の突き合わせ）の純ロジック検証。
/// 実際のログ出力と hook 購読は SessionDeliveryService 側なのでここには出てこない。
/// </summary>
public sealed class SubmitWatchTests
{
    private static readonly DateTime Origin = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void 書き込み後に提出が来れば確認済みとして返る()
    {
        var watch = new SubmitWatch();
        var target = Guid.NewGuid();
        var armed = watch.Arm(target, "hello [TerminalHub 自動メッセージ #0123456789ab — x]", Origin);

        var confirmed = watch.Confirm(target);

        Assert.Same(armed, confirmed);
        Assert.Equal("#0123456789ab", confirmed!.Tag);
        Assert.Equal(0, watch.PendingCount(target));
    }

    [Fact]
    public void 待ちが無い宛先の提出は観測対象外でnull()
    {
        var watch = new SubmitWatch();
        Assert.Null(watch.Confirm(Guid.NewGuid()));
    }

    [Fact]
    public void 期限内は期限切れにならず期限を過ぎると返る()
    {
        var watch = new SubmitWatch();
        var target = Guid.NewGuid();
        watch.Arm(target, "a", Origin);

        Assert.Empty(watch.Expire(Origin + SubmitWatch.Threshold - TimeSpan.FromMilliseconds(1)));
        var expired = watch.Expire(Origin + SubmitWatch.Threshold);

        Assert.Single(expired);
        Assert.Equal(0, watch.PendingCount(target));
    }

    [Fact]
    public void 同じ宛先に2件書いたら提出1回で古い方だけ確認済みになる()
    {
        var watch = new SubmitWatch();
        var target = Guid.NewGuid();
        var first = watch.Arm(target, "1", Origin);
        var second = watch.Arm(target, "2", Origin + TimeSpan.FromSeconds(1));

        Assert.Same(first, watch.Confirm(target));
        Assert.Equal(1, watch.PendingCount(target));

        var expired = watch.Expire(Origin + TimeSpan.FromSeconds(1) + SubmitWatch.Threshold);
        Assert.Same(second, Assert.Single(expired));
    }

    [Fact]
    public void 宛先が違えば互いに影響しない()
    {
        var watch = new SubmitWatch();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        watch.Arm(a, "a", Origin);
        watch.Arm(b, "b", Origin);

        Assert.NotNull(watch.Confirm(a));
        Assert.Equal(0, watch.PendingCount(a));
        Assert.Equal(1, watch.PendingCount(b));
    }

    [Fact]
    public void エンベロープが無い本文は先頭を札にする()
    {
        Assert.Equal("\"short\"", SubmitWatch.MakeTag("short"));
        var tag = SubmitWatch.MakeTag(new string('x', 40));
        Assert.Equal("\"" + new string('x', 24) + "…\"", tag);
    }
}
