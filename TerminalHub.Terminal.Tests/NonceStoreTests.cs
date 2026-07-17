using TerminalHub.Services;
using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// NonceStore の期限・ワンタイム消費・リプレイ拒否の境界テスト。
/// 時刻は引数で渡すので再現性がある（DateTime.UtcNow を使わない）。
/// </summary>
public sealed class NonceStoreTests
{
    private static readonly DateTime T0 = new(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ValidateAndConsume_FreshNonce_Succeeds()
    {
        var store = new NonceStore(TimeSpan.FromSeconds(30));
        var nonce = store.Generate(T0);

        Assert.True(store.ValidateAndConsume(nonce, T0));
    }

    [Fact]
    public void ValidateAndConsume_SameNonceTwice_SecondFails()
    {
        // ワンタイム: 一度消費した nonce は再利用不可（リプレイ攻撃防止）
        var store = new NonceStore(TimeSpan.FromSeconds(30));
        var nonce = store.Generate(T0);

        Assert.True(store.ValidateAndConsume(nonce, T0));
        Assert.False(store.ValidateAndConsume(nonce, T0));
    }

    [Fact]
    public void ValidateAndConsume_UnknownNonce_Fails()
    {
        var store = new NonceStore(TimeSpan.FromSeconds(30));
        store.Generate(T0);

        Assert.False(store.ValidateAndConsume("deadbeef", T0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateAndConsume_NullOrEmpty_Fails(string? nonce)
    {
        var store = new NonceStore(TimeSpan.FromSeconds(30));
        store.Generate(T0);

        Assert.False(store.ValidateAndConsume(nonce, T0));
    }

    [Fact]
    public void ValidateAndConsume_JustBeforeExpiry_Succeeds()
    {
        var store = new NonceStore(TimeSpan.FromSeconds(30));
        var nonce = store.Generate(T0);

        // 29.9 秒後 → 期限内
        Assert.True(store.ValidateAndConsume(nonce, T0.AddSeconds(29.9)));
    }

    [Fact]
    public void ValidateAndConsume_AfterExpiry_Fails()
    {
        var store = new NonceStore(TimeSpan.FromSeconds(30));
        var nonce = store.Generate(T0);

        // 31 秒後 → 期限切れ
        Assert.False(store.ValidateAndConsume(nonce, T0.AddSeconds(31)));
    }

    [Fact]
    public void ValidateAndConsume_ExpiredNonce_IsDiscardedEvenIfClockGoesBack()
    {
        // 期限切れ検証で保持中 nonce は破棄される。以後、期限内の時刻で再検証しても通らない。
        var store = new NonceStore(TimeSpan.FromSeconds(30));
        var nonce = store.Generate(T0);

        Assert.False(store.ValidateAndConsume(nonce, T0.AddSeconds(31))); // 期限切れで破棄
        Assert.False(store.ValidateAndConsume(nonce, T0.AddSeconds(5)));  // 破棄済みなので通らない
    }

    [Fact]
    public void Generate_OverwritesPreviousNonce()
    {
        // 新規発行で旧 nonce は無効化される（有効なのは常に最新1個）
        var store = new NonceStore(TimeSpan.FromSeconds(30));
        var first = store.Generate(T0);
        var second = store.Generate(T0);

        Assert.NotEqual(first, second);
        Assert.False(store.ValidateAndConsume(first, T0));
        Assert.True(store.ValidateAndConsume(second, T0));
    }
}
