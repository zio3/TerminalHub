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
    public void Pending_escape_fragment_at_snapshot_is_seeded_into_tail()
    {
        // チャンク境界がエスケープシーケンスの途中で切れた状態で BeginReplay した場合、
        // 未確定の前半をテールへ種付けし、後続チャンクと合わせて完全なシーケンスとして届くこと
        var buffer = new EmulatedStateBuffer(80, 24);
        buffer.Append("abc\x1b[3"); // CSI 途中で分断
        var snapshot = buffer.BeginReplay();
        buffer.Append("1mred");     // 続き（合わせて ESC[31m red）
        var tail = buffer.EndReplay(snapshot);

        var restored = new EmulatedStateBuffer(80, 24);
        restored.Append(snapshot.Content);
        restored.Append(tail);

        var truth = new EmulatedStateBuffer(80, 24);
        truth.Append("abc\x1b[31mred");

        Assert.Equal(truth.SerializeForReplay(), restored.SerializeForReplay());
    }

    [Fact]
    public void Pending_high_surrogate_at_snapshot_is_seeded_into_tail()
    {
        // サロゲートペアの前半でチャンクが切れた状態で BeginReplay した場合も同様
        var emoji = "😀"; // U+1F600（サロゲートペア）
        var buffer = new EmulatedStateBuffer(80, 24);
        buffer.Append("x" + emoji[0]); // 前半のみ
        var snapshot = buffer.BeginReplay();
        buffer.Append(emoji[1].ToString() + "y"); // 後半＋続き
        var tail = buffer.EndReplay(snapshot);

        // スナップショット側は孤立サロゲートを含まない（JS interop の JSON 化で化けるため）
        Assert.DoesNotContain(emoji[0], snapshot.Content);
        // テール先頭で前半＋後半が揃う
        Assert.StartsWith(emoji, tail);

        var restored = new EmulatedStateBuffer(80, 24);
        restored.Append(snapshot.Content);
        restored.Append(tail);

        var truth = new EmulatedStateBuffer(80, 24);
        truth.Append("x" + emoji + "y");

        Assert.Equal(truth.SerializeForReplay(), restored.SerializeForReplay());
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
