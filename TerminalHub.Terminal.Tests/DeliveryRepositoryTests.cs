using Microsoft.Extensions.Logging.Abstractions;
using TerminalHub.Services;
using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// 配送記録（エンベロープ #ID の検証台帳）の検証。実 SQLite（テンポラリDB・後始末つき）。
/// </summary>
public sealed class DeliveryRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DeliveryRepository _repository;

    public DeliveryRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"th-delivery-test-{Guid.NewGuid():N}.db");
        var dbContext = new SessionDbContext(_dbPath, NullLogger<SessionDbContext>.Instance);
        _repository = new DeliveryRepository(dbContext, NullLogger<DeliveryRepository>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* 後始末の失敗でテストを落とさない */ }
    }

    private static DeliveryRecord Record(
        string id, string? fromId = null, string? fromName = null, string? contextId = null) =>
        new(id, fromId, fromName,
            ToSessionId: Guid.NewGuid().ToString(), ToName: "宛先レーン",
            ContextId: contextId, SentAt: DateTime.UtcNow);

    [Fact]
    public async Task 記名付きの配送記録を往復できる()
    {
        var fromId = Guid.NewGuid().ToString();
        await _repository.CreateAsync(Record("abc123def456", fromId, "レーン1", "ctx-1"));

        var record = await _repository.GetAsync("abc123def456");

        Assert.NotNull(record);
        Assert.Equal(fromId, record!.FromSessionId);
        Assert.Equal("レーン1", record.FromName);
        Assert.Equal("宛先レーン", record.ToName);
        Assert.Equal("ctx-1", record.ContextId);
        // 保存は UTC。ローカルへ化けないこと（RoundtripKind）。
        Assert.Equal(DateTimeKind.Utc, record.SentAt.Kind);
    }

    [Fact]
    public async Task 無記名の配送はFromがnullのまま返る()
    {
        // null = 無記名（外部クライアントの可能性を含む）。空文字などへ潰れると
        // 「無記名かどうか」の判定が呼び出し側で壊れる。
        await _repository.CreateAsync(Record("anon00000001"));

        var record = await _repository.GetAsync("anon00000001");

        Assert.NotNull(record);
        Assert.Null(record!.FromSessionId);
        Assert.Null(record.FromName);
    }

    [Fact]
    public async Task 存在しないIDはnull()
    {
        // 見つからない = 偽装（手書きエンベロープ）か期限切れ、と判定する根拠。
        Assert.Null(await _repository.GetAsync("nosuchid0000"));
    }

    [Fact]
    public async Task 上限掃除はRejectedの一時的な超過で真正な記録を押し出さない()
    {
        // 掃除の実行順序の固定（レビュー指摘）。CreateAsync が INSERT 直後に上限掃除をすると、
        // 「真正な記録が上限いっぱいの状態で Rejected になる送信を1回」で最古の真正な記録が
        // 押し出され、Rejected 側を後から消しても戻らない（押し出された配送は get_delivery で
        // 「偽装」と誤判定される）。CreateAsync は TTL 掃除のみ・上限掃除は
        // PruneToCapAsync（記録を残すことが確定した後）に分離されていることを検証する。
        var origin = DateTime.UtcNow.AddMinutes(-DeliveryRepository.MaxCount);
        for (var i = 0; i < DeliveryRepository.MaxCount; i++)
        {
            await _repository.CreateAsync(new DeliveryRecord(
                $"gen{i:D9}", null, null,
                ToSessionId: "to", ToName: "宛先",
                ContextId: null, SentAt: origin.AddMinutes(i)));
        }

        // 上限いっぱいの状態で「後で取り消される1件」を作っても、最古の真正な記録は消えない
        await _repository.CreateAsync(new DeliveryRecord(
            "rejected0001", null, null, "to", "宛先", null, DateTime.UtcNow));
        Assert.NotNull(await _repository.GetAsync("gen000000000"));

        // Rejected の後片付け → 上限掃除、の順でも真正な1000件は無傷
        await _repository.DeleteAsync("rejected0001");
        await _repository.PruneToCapAsync();
        Assert.NotNull(await _repository.GetAsync("gen000000000"));

        // 本当に超過した（残すことが確定した1001件目がある）ときだけ、最古が押し出される
        await _repository.CreateAsync(new DeliveryRecord(
            "kept00000001", null, null, "to", "宛先", null, DateTime.UtcNow));
        await _repository.PruneToCapAsync();
        Assert.Null(await _repository.GetAsync("gen000000000"));
        Assert.NotNull(await _repository.GetAsync("kept00000001"));
    }

    [Fact]
    public async Task 削除した記録は見つからない()
    {
        // Rejected（一度も書いていない）の後片付け経路。参照不能な記録を残すと
        // 総数上限の掃除で真正な記録を押し出す攻撃に使えるため、消せることが要件。
        await _repository.CreateAsync(Record("del000000001"));

        await _repository.DeleteAsync("del000000001");

        Assert.Null(await _repository.GetAsync("del000000001"));
    }
}
