using TerminalHub.Services;
using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// 配送の提出観測（書き込み → UserPromptSubmit の突き合わせ）と Enter 再送ウォッチドッグの
/// 純ロジック検証。実際のログ出力・hook 購読・ConPTY 書き込みは SessionDeliveryService 側なので
/// ここには出てこない。
/// </summary>
public sealed class SubmitWatchTests
{
    private static readonly DateTime Origin = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Due = SubmitWatch.Threshold;

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

        Assert.Empty(watch.Expire(Origin + Due - TimeSpan.FromMilliseconds(1)));
        var expired = watch.Expire(Origin + Due);

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

        var expired = watch.Expire(Origin + TimeSpan.FromSeconds(1) + Due);
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
    public void 再送不可の配送は期限切れで外れる()
    {
        var watch = new SubmitWatch();
        var target = Guid.NewGuid();
        watch.Arm(target, "処理中宛て", Origin, resendAllowed: false);

        var expired = watch.Expire(Origin + Due);

        var e = Assert.Single(expired);
        Assert.False(e.ResendAllowed);
        Assert.False(e.Resending);
        Assert.Equal(0, watch.PendingCount(target));
    }

    [Fact]
    public void 再送可の配送は期限切れでも行列に残り再送中の印が付く()
    {
        var watch = new SubmitWatch();
        var target = Guid.NewGuid();
        var armed = watch.Arm(target, "idle宛て", Origin, resendAllowed: true, inputSequence: 10);

        var expired = watch.Expire(Origin + Due);

        var e = Assert.Single(expired);
        Assert.Equal(armed.Seq, e.Seq);
        Assert.True(e.Resending);
        Assert.Equal(10, e.InputSequence);
        Assert.Equal(1, watch.PendingCount(target));
        Assert.True(watch.IsPending(e));

        // 再送中のものは次の期限切れ判定で二重に返らない。
        Assert.Empty(watch.Expire(Origin + Due + TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void 再送中に遅れて提出確認が来たら再送は成立しない()
    {
        var watch = new SubmitWatch();
        var target = Guid.NewGuid();
        watch.Arm(target, "a", Origin, resendAllowed: true);
        var resending = Assert.Single(watch.Expire(Origin + Due));

        // 期限切れ直後（再送の書き込み前）に UserPromptSubmit が届いた。
        var confirmed = watch.Confirm(target);

        Assert.NotNull(confirmed);
        Assert.Equal(resending.Seq, confirmed!.Seq);
        Assert.False(watch.IsPending(resending));
        Assert.Null(watch.FinishResend(resending, Origin + Due + TimeSpan.FromSeconds(1), 11));
        Assert.Equal(0, watch.PendingCount(target));
    }

    [Fact]
    public void 再送完了で同じ位置のまま再監視に置き換わりFIFOが保たれる()
    {
        var watch = new SubmitWatch();
        var target = Guid.NewGuid();
        var first = watch.Arm(target, "x [TerminalHub 自動メッセージ #0123456789ab — y]", Origin, resendAllowed: true, inputSequence: 1);
        var second = watch.Arm(target, "2件目", Origin + TimeSpan.FromSeconds(3), resendAllowed: true, inputSequence: 2);

        var resending = Assert.Single(watch.Expire(Origin + Due));
        Assert.Equal(first.Seq, resending.Seq);

        var rearmed = watch.FinishResend(resending, Origin + Due + TimeSpan.FromSeconds(1), 3);

        Assert.NotNull(rearmed);
        Assert.Equal("#0123456789ab", rearmed!.Tag);
        Assert.Equal(first.Seq, rearmed.Seq);
        Assert.Equal(1, rearmed.Attempt);
        Assert.False(rearmed.ResendAllowed);
        Assert.False(rearmed.Resending);
        Assert.Equal(3, rearmed.InputSequence);
        Assert.Equal(2, watch.PendingCount(target));

        // 提出確認は最古（再送した1件目）に対応づく。2件目はそのまま残る。
        Assert.Equal(first.Seq, watch.Confirm(target)!.Seq);
        Assert.Equal(second.Seq, watch.Confirm(target)!.Seq);
    }

    [Fact]
    public void 再送後も確認が来なければ期限切れとして外れ再送は1回だけ()
    {
        var watch = new SubmitWatch();
        var target = Guid.NewGuid();
        watch.Arm(target, "a", Origin, resendAllowed: true);
        var resending = Assert.Single(watch.Expire(Origin + Due));
        var resentAt = Origin + Due + TimeSpan.FromSeconds(1);
        var rearmed = watch.FinishResend(resending, resentAt, 5)!;

        Assert.Empty(watch.Expire(resentAt + Due - TimeSpan.FromMilliseconds(1)));
        var expired = watch.Expire(resentAt + Due);

        var e = Assert.Single(expired);
        Assert.Equal(rearmed.Seq, e.Seq);
        Assert.Equal(1, e.Attempt);
        Assert.False(e.Resending);
        Assert.Equal(0, watch.PendingCount(target));
    }

    [Fact]
    public void 再送を取りやめると監視から外れる()
    {
        var watch = new SubmitWatch();
        var target = Guid.NewGuid();
        watch.Arm(target, "a", Origin, resendAllowed: true);
        var resending = Assert.Single(watch.Expire(Origin + Due));

        Assert.True(watch.CancelResend(resending));
        Assert.False(watch.CancelResend(resending));
        Assert.Equal(0, watch.PendingCount(target));
        Assert.Null(watch.Confirm(target));
    }

    [Fact]
    public void エンベロープが無い本文は先頭を札にする()
    {
        Assert.Equal("\"short\"", SubmitWatch.MakeTag("short"));
        var tag = SubmitWatch.MakeTag(new string('x', 40));
        Assert.Equal("\"" + new string('x', 24) + "…\"", tag);
    }
}
