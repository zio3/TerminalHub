using TerminalHub.Services;
using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// 配送待ちキューの純ロジック（FIFO・TTL・上限）の検証。
/// 「送れる状態か」の判定と実書き込みは SessionDeliveryService 側なのでここには出てこない。
/// </summary>
public sealed class DeliveryQueueTests
{
    private static readonly DateTime Origin = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DeliveryItem Item(Guid target, string text, DateTime? at = null) =>
        new(target, text, at ?? Origin, ContextId: null,
            RequesterSessionId: null, IsSystemCallback: false);

    [Fact]
    public void 同じ宛先には積んだ順に取り出される()
    {
        var queue = new DeliveryQueue();
        var target = Guid.NewGuid();
        queue.Enqueue(Item(target, "1"));
        queue.Enqueue(Item(target, "2"));
        queue.Enqueue(Item(target, "3"));

        var order = new List<string>();
        while (queue.TryPeek(target, out var item))
        {
            order.Add(item.Text);
            queue.RemoveHead(target);
        }

        Assert.Equal(new[] { "1", "2", "3" }, order);
    }

    [Fact]
    public void 覗いただけでは減らない()
    {
        // 「覗く→書けたら捨てる」で配送するため、書き込み失敗時に先頭が残ることが FIFO の前提。
        var queue = new DeliveryQueue();
        var target = Guid.NewGuid();
        queue.Enqueue(Item(target, "1"));

        Assert.True(queue.TryPeek(target, out var first));
        Assert.True(queue.TryPeek(target, out var again));
        Assert.Equal("1", first.Text);
        Assert.Equal("1", again.Text);
        Assert.Equal(1, queue.CountFor(target));
    }

    [Fact]
    public void 宛先ごとに独立している()
    {
        var queue = new DeliveryQueue();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        queue.Enqueue(Item(a, "a1"));
        queue.Enqueue(Item(b, "b1"));

        Assert.Equal(1, queue.CountFor(a));
        Assert.Equal(1, queue.CountFor(b));

        queue.RemoveHead(a);

        Assert.Equal(0, queue.CountFor(a));
        Assert.Equal(1, queue.CountFor(b));
    }

    [Fact]
    public void 上限を超えた投函は拒否される()
    {
        var queue = new DeliveryQueue();
        var target = Guid.NewGuid();
        for (var i = 0; i < DeliveryQueue.MaxPerTarget; i++)
        {
            Assert.True(queue.Enqueue(Item(target, $"{i}")));
        }

        Assert.False(queue.Enqueue(Item(target, "溢れる")));
        Assert.Equal(DeliveryQueue.MaxPerTarget, queue.CountFor(target));
    }

    [Fact]
    public void TTLを超えた古いものだけが剥がれる()
    {
        var queue = new DeliveryQueue();
        var target = Guid.NewGuid();
        queue.Enqueue(Item(target, "古い", Origin));
        queue.Enqueue(Item(target, "新しい", Origin.AddMinutes(4)));

        var expired = queue.RemoveExpiredFor(target, Origin.AddMinutes(5), TimeSpan.FromMinutes(5));

        Assert.Single(expired);
        Assert.Equal("古い", expired[0].Text);
        Assert.Equal(1, queue.CountFor(target));
    }

    [Fact]
    public void 全件が期限切れなら宛先ごと消える()
    {
        var queue = new DeliveryQueue();
        var target = Guid.NewGuid();
        queue.Enqueue(Item(target, "1", Origin));
        queue.Enqueue(Item(target, "2", Origin));

        var expired = queue.RemoveExpiredFor(target, Origin.AddMinutes(10), TimeSpan.FromMinutes(5));

        Assert.Equal(2, expired.Count);
        Assert.Empty(queue.PendingTargets());
    }

    [Fact]
    public void 失効は指定した宛先だけに効く()
    {
        // 全宛先を一括で剥がすと、配送中の宛先だけロックを取って失効させることができず、
        // 配送済みの項目が失敗と報告され次の項目が黙って消える（RemoveExpiredFor の存在理由）。
        var queue = new DeliveryQueue();
        var 配送中 = Guid.NewGuid();
        var その他 = Guid.NewGuid();
        queue.Enqueue(Item(配送中, "配送中", Origin));
        queue.Enqueue(Item(その他, "放置", Origin));

        var expired = queue.RemoveExpiredFor(その他, Origin.AddMinutes(10), TimeSpan.FromMinutes(5));

        Assert.Single(expired);
        Assert.Equal("放置", expired[0].Text);
        Assert.Equal(1, queue.CountFor(配送中));
    }

    [Fact]
    public void 期限切れが無い宛先を失効させても何も起きない()
    {
        var queue = new DeliveryQueue();
        var target = Guid.NewGuid();
        queue.Enqueue(Item(target, "新しい", Origin.AddMinutes(4)));

        var expired = queue.RemoveExpiredFor(target, Origin.AddMinutes(5), TimeSpan.FromMinutes(5));

        Assert.Empty(expired);
        Assert.Equal(1, queue.CountFor(target));
    }

    [Fact]
    public void 存在しない宛先の失効は空を返す()
    {
        var queue = new DeliveryQueue();

        var expired = queue.RemoveExpiredFor(Guid.NewGuid(), Origin.AddMinutes(10), TimeSpan.FromMinutes(5));

        Assert.Empty(expired);
    }

    [Fact]
    public void 空のキューからの取り出しは安全に空振りする()
    {
        var queue = new DeliveryQueue();
        var target = Guid.NewGuid();

        Assert.False(queue.TryPeek(target, out _));
        queue.RemoveHead(target); // 例外にならない
        Assert.Equal(0, queue.CountFor(target));
    }
}
