using TerminalHub.Services;
using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// 完了通知の要否判定の検証。規則:
/// (1) 終端 status の書き込みは（遷移でなく再書き込みでも）通知対象
/// (2) ただし依頼元自身の書き込みは通知しない（自己再通知ループ防止）
/// (3) working 等の非終端は通知しない
/// ※ (4) システム経路（配送失敗の failed 化）が遷移時のみなのは SessionDeliveryService 側の
///    呼び分け（StatusTransitioned ガード）で、このポリシーの対象外。
/// </summary>
public sealed class ContextNotifyPolicyTests
{
    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("canceled")]
    public void 終端statusは通知対象(string status)
    {
        Assert.True(ContextNotifyPolicy.IsTerminal(status));
    }

    [Theory]
    [InlineData("submitted")]
    [InlineData("working")]
    [InlineData("Completed")] // status は update_context 側で小文字化してから渡る契約（大文字は非終端扱い）
    [InlineData(null)]
    public void 非終端statusは通知しない(string? status)
    {
        Assert.False(ContextNotifyPolicy.IsTerminal(status));
    }

    [Fact]
    public void 依頼元自身の書き込みは通知しない()
    {
        var requester = Guid.NewGuid();
        Assert.False(ContextNotifyPolicy.ShouldNotifyRequester(requester.ToString(), requester));
    }

    [Fact]
    public void 依頼元自身の判定は大文字小文字を無視する()
    {
        var requester = Guid.NewGuid();
        Assert.False(ContextNotifyPolicy.ShouldNotifyRequester(
            requester.ToString().ToUpperInvariant(), requester));
    }

    [Fact]
    public void 受け手など別セッションの書き込みは通知する()
    {
        Assert.True(ContextNotifyPolicy.ShouldNotifyRequester(
            Guid.NewGuid().ToString(), Guid.NewGuid()));
    }

    [Fact]
    public void 無記名の書き込みは通知する()
    {
        // 依頼元セッション自身の書き込みは接続キーで必ず記名されるため、
        // 無記名（外部クライアント等）がループの起点になることはない。
        Assert.True(ContextNotifyPolicy.ShouldNotifyRequester(Guid.NewGuid().ToString(), null));
    }
}
