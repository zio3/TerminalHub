using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// BeginReplay/EndReplay（リプレイキャプチャ）の検証。
/// 「スナップショット取得〜xterm書き込み完了」の間に届いたライブ出力が
/// 消失・二重化・順序逆転しないことを保証する（セッション切替/リサイズ再同期のレース対策）。
/// </summary>
public class ReplayCaptureTests
{
    [Fact]
    public void Append_outside_capture_returns_false()
    {
        var buffer = new EmulatedStateBuffer(80, 24);
        Assert.False(buffer.Append("hello"));
    }

    [Fact]
    public void Append_during_capture_returns_true_and_tail_preserves_order()
    {
        var buffer = new EmulatedStateBuffer(80, 24);
        buffer.Append("before\r\n");

        var snapshot = buffer.BeginReplay();
        Assert.Contains("before", snapshot.Content);

        Assert.True(buffer.Append("chunk1"));
        Assert.True(buffer.Append("chunk2"));

        var tail = buffer.EndReplay(snapshot);
        Assert.Equal("chunk1chunk2", tail);

        // キャプチャ終了後は通常動作に戻る
        Assert.False(buffer.Append("after"));
    }

    [Fact]
    public void Snapshot_content_does_not_include_tail_data()
    {
        var buffer = new EmulatedStateBuffer(80, 24);
        buffer.Append("line1\r\n");

        var snapshot = buffer.BeginReplay();
        buffer.Append("line2\r\n");

        Assert.Contains("line1", snapshot.Content);
        Assert.DoesNotContain("line2", snapshot.Content);

        var tail = buffer.EndReplay(snapshot);
        Assert.Contains("line2", tail);
    }

    [Fact]
    public void Snapshot_plus_tail_reconstructs_same_state()
    {
        // 連続して見ていた端末(A)と、キャプチャ中にライブ出力が届いた復元端末(B)の最終状態が一致すること
        var a = new EmulatedStateBuffer(80, 24);
        a.Append("line1\r\nline2\r\n");
        a.Append("live1\r\nlive2\r\n");

        var source = new EmulatedStateBuffer(80, 24);
        source.Append("line1\r\nline2\r\n");
        var snapshot = source.BeginReplay();
        source.Append("live1\r\nlive2\r\n"); // 書き込み中に届いたライブ出力
        var tail = source.EndReplay(snapshot);

        var b = new EmulatedStateBuffer(80, 24);
        b.Append(snapshot.Content);
        b.Append(tail);

        Assert.Equal(a.SerializeForReplay(), b.SerializeForReplay());
    }

    [Fact]
    public void Empty_buffer_snapshot_is_empty()
    {
        var buffer = new EmulatedStateBuffer(80, 24);
        var snapshot = buffer.BeginReplay();
        Assert.Equal(string.Empty, snapshot.Content);
        Assert.Equal(string.Empty, buffer.EndReplay(snapshot));
    }

    [Fact]
    public void Concurrent_captures_each_get_their_own_tail()
    {
        var buffer = new EmulatedStateBuffer(80, 24);
        buffer.Append("base\r\n");

        var first = buffer.BeginReplay();
        buffer.Append("x");
        var second = buffer.BeginReplay();
        buffer.Append("y");

        Assert.Equal("xy", buffer.EndReplay(first));
        Assert.Equal("y", buffer.EndReplay(second));
    }

    [Fact]
    public void RawStream_capture_has_same_semantics()
    {
        var buffer = new RawStreamStateBuffer();
        buffer.Append("before");

        var snapshot = buffer.BeginReplay();
        Assert.Equal("before", snapshot.Content);
        Assert.True(buffer.Append("tail"));
        Assert.Equal("tail", buffer.EndReplay(snapshot));
        Assert.False(buffer.Append("after"));
    }
}
