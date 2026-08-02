using Microsoft.Extensions.Logging.Abstractions;
using TerminalHub.Services;
using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// 依頼の状況札（ContextSummary）の status 遷移判定の検証。
///
/// 遷移が成立したかどうかは SQL の条件付き UPDATE に載っている（読んでから比べると、
/// 同時に書いた2者が両方「遷移させた」と誤認して依頼元を二度起こすため）。
/// つまりロジックが SQL 側にあるので、実 SQLite を相手に検証する。
/// 一時ファイルはテンポラリへ作り、後始末する（リポジトリ内には何も置かない）。
/// </summary>
public sealed class ContextRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ContextRepository _repository;

    public ContextRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"th-context-test-{Guid.NewGuid():N}.db");
        var dbContext = new SessionDbContext(_dbPath, NullLogger<SessionDbContext>.Instance);
        _repository = new ContextRepository(dbContext, NullLogger<ContextRepository>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* 後始末の失敗でテストを落とさない */ }
    }

    private async Task<string> CreateAsync(string? requesterId = null, string? requesterName = null)
    {
        var id = Guid.NewGuid().ToString("N");
        await _repository.CreateAsync(id, requesterId, requesterName);
        return id;
    }

    [Fact]
    public async Task 作成直後はsubmittedで依頼元が記録される()
    {
        var requesterId = Guid.NewGuid().ToString();
        var id = await CreateAsync(requesterId, "レーン1");

        var record = await _repository.GetAsync(id);

        Assert.NotNull(record);
        Assert.Equal("submitted", record!.Status);
        Assert.Equal(string.Empty, record.Summary);
        Assert.Equal(requesterId, record.RequesterSessionId);
        Assert.Equal("レーン1", record.RequesterName);
    }

    [Fact]
    public async Task 別のstatusへの更新は遷移として成立する()
    {
        var id = await CreateAsync();

        var result = await _repository.UpdateAsync(id, "着手した", "working", "sess-1", "レーン1");

        Assert.True(result.Found);
        Assert.True(result.StatusTransitioned);
        var record = await _repository.GetAsync(id);
        Assert.Equal("working", record!.Status);
        Assert.Equal("着手した", record.Summary);
        Assert.Equal("レーン1", record.UpdatedByName);
    }

    [Fact]
    public async Task 同じstatusへの書き直しは遷移にならないが要約は更新される()
    {
        // StatusTransitioned は「実際に status を変えたか」の事実を返す（同一 status の
        // 書き直しで true になってはいけない）。なお完了通知の条件はこのフラグから
        // 「終端 status の書き込み成功」へ緩められた（再完了の続報を届けるため。
        // working の書き直しは従来どおり通知されない＝このテストの前提は変わらない）。
        var id = await CreateAsync();
        await _repository.UpdateAsync(id, "1回目", "working", null, null);

        var result = await _repository.UpdateAsync(id, "2回目", "working", null, null);

        Assert.True(result.Found);
        Assert.False(result.StatusTransitioned);
        var record = await _repository.GetAsync(id);
        Assert.Equal("working", record!.Status);
        Assert.Equal("2回目", record.Summary);
    }

    [Fact]
    public async Task status省略なら要約だけ更新され遷移にならない()
    {
        var id = await CreateAsync();
        await _repository.UpdateAsync(id, "着手", "working", null, null);

        var result = await _repository.UpdateAsync(id, "続き", null, null, null);

        Assert.True(result.Found);
        Assert.False(result.StatusTransitioned);
        var record = await _repository.GetAsync(id);
        Assert.Equal("working", record!.Status);
        Assert.Equal("続き", record.Summary);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("canceled")]
    public async Task 終端状態から進行中へは戻せない(string terminal)
    {
        // 戻せてしまうと、依頼が閉じたのに一覧上は進行中に見え、TTL 掃除の対象からも外れる。
        var id = await CreateAsync();
        await _repository.UpdateAsync(id, "おわり", terminal, null, null);

        var result = await _repository.UpdateAsync(id, "やっぱり続ける", "working", null, null);

        Assert.True(result.Found);
        Assert.False(result.StatusTransitioned);
        Assert.True(result.RewindRejected);
        Assert.False(result.Conflicted);

        // 拒否したのだから要約も書き換わっていないこと。
        var record = await _repository.GetAsync(id);
        Assert.Equal(terminal, record!.Status);
        Assert.Equal("おわり", record.Summary);
    }

    [Fact]
    public async Task 終端から別の終端へは移せる()
    {
        // 「配送できなかった」と記録した後に、人間経由で実際に完了した、という正当な上書きがある。
        var id = await CreateAsync();
        await _repository.UpdateAsync(id, "配送できず", "failed", null, "TerminalHub (system)");

        var result = await _repository.UpdateAsync(id, "人が送って完了した", "completed", "sess-1", "レーン1");

        Assert.True(result.StatusTransitioned);
        Assert.False(result.RewindRejected);
        var record = await _repository.GetAsync(id);
        Assert.Equal("completed", record!.Status);
        Assert.Equal("人が送って完了した", record.Summary);
    }

    [Fact]
    public async Task 存在しない札の更新はFoundがfalse()
    {
        // 競合（Conflicted）と混ざらないこと。原因も対処も違う。
        var result = await _repository.UpdateAsync("nosuchid", "x", "completed", null, null);

        Assert.False(result.Found);
        Assert.False(result.Conflicted);
        Assert.False(result.RewindRejected);
    }

    [Fact]
    public async Task 記名は書き込みごとに置き換わる()
    {
        // proof 無しの書き込みは無記名になる＝前回の記名が残ってはいけない
        // （依頼元が updatedBy を見て「依頼先が書いた結果か」を判断するため）。
        var id = await CreateAsync();
        await _repository.UpdateAsync(id, "記名あり", "working", "sess-1", "レーン1");

        await _repository.UpdateAsync(id, "無記名", "completed", null, null);

        var record = await _repository.GetAsync(id);
        Assert.Null(record!.UpdatedBySessionId);
        Assert.Null(record.UpdatedByName);
    }

    [Fact]
    public async Task 削除した札は見つからない()
    {
        var id = await CreateAsync();

        await _repository.DeleteAsync(id);

        Assert.Null(await _repository.GetAsync(id));
    }
}
