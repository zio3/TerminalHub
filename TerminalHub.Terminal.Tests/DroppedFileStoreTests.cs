using System.Text;
using TerminalHub.Services;
using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// 入力欄へのファイルドロップの受け皿（<see cref="DroppedFileStore"/>）の検証。
/// ブラウザ由来の名前を安全なファイル名に整えること、同名を番号で避けること、
/// 上限超えを申告でも実サイズでも弾いて書きかけを残さないこと、古い日付フォルダだけ掃除することを固定する。
/// </summary>
public sealed class DroppedFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "th-drops-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("image.png", "image.png")]
    [InlineData("C:\\Users\\x\\a.txt", "a.txt")]
    [InlineData("dir/sub/b.txt", "b.txt")]
    [InlineData("bad:name?.txt", "bad_name_.txt")]
    [InlineData("  trailing. . ", "trailing")]
    [InlineData("", "file")]
    [InlineData(null, "file")]
    [InlineData("..", "file")]
    [InlineData("日本語 名前.pdf", "日本語 名前.pdf")]
    public void SanitizeFileName_ブラウザ由来の名前を安全な形にする(string? input, string expected)
    {
        Assert.Equal(expected, DroppedFileStore.SanitizeFileName(input));
    }

    [Fact]
    public void SanitizeFileName_長い名前は拡張子を残して切る()
    {
        var name = new string('a', 300) + ".png";
        var result = DroppedFileStore.SanitizeFileName(name);
        Assert.True(result.Length <= 120);
        Assert.EndsWith(".png", result);
    }

    [Fact]
    public void ReserveUniquePath_同名があれば番号を付ける()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "");
        File.WriteAllText(Path.Combine(_root, "a-2.txt"), "");

        Assert.Equal(Path.Combine(_root, "a-3.txt"), DroppedFileStore.ReserveUniquePath(_root, "a.txt"));
        Assert.Equal(Path.Combine(_root, "b.txt"), DroppedFileStore.ReserveUniquePath(_root, "b.txt"));
    }

    [Fact]
    public async Task SaveAsync_日付フォルダへ保存しフルパスを返す()
    {
        var now = new DateTime(2026, 10, 29, 12, 0, 0);
        var body = new MemoryStream(Encoding.UTF8.GetBytes("hello"));

        var path = await DroppedFileStore.SaveAsync(_root, "note.txt", body, body.Length, now, CancellationToken.None);

        Assert.Equal(Path.Combine(_root, "20261029", "note.txt"), path);
        Assert.Equal("hello", File.ReadAllText(path));

        // 2 回目は番号付き
        var path2 = await DroppedFileStore.SaveAsync(_root, "note.txt", new MemoryStream(new byte[1]), 1, now, CancellationToken.None);
        Assert.Equal(Path.Combine(_root, "20261029", "note-2.txt"), path2);
    }

    [Fact]
    public async Task SaveAsync_申告サイズが上限超えなら読まずに弾く()
    {
        var body = new MemoryStream(new byte[1]);
        await Assert.ThrowsAsync<DroppedFileTooLargeException>(() =>
            DroppedFileStore.SaveAsync(_root, "big.bin", body, DroppedFileStore.MaxBytes + 1, DateTime.Now, CancellationToken.None));
        Assert.Equal(0, body.Position);
    }

    [Fact]
    public async Task SaveAsync_申告が無くても実サイズが上限超えなら弾いて書きかけを消す()
    {
        var now = new DateTime(2026, 10, 29);
        var body = new ZeroStream(DroppedFileStore.MaxBytes + 1);

        await Assert.ThrowsAsync<DroppedFileTooLargeException>(() =>
            DroppedFileStore.SaveAsync(_root, "big.bin", body, null, now, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(_root, "20261029", "big.bin")));
    }

    [Fact]
    public void CleanupOldFolders_古い日付フォルダだけ消す()
    {
        var now = new DateTime(2026, 10, 29);
        Directory.CreateDirectory(Path.Combine(_root, "20261021")); // 8 日前: 消える
        Directory.CreateDirectory(Path.Combine(_root, "20261022")); // 7 日前: 残る（閾値ちょうど）
        Directory.CreateDirectory(Path.Combine(_root, "20261029")); // 今日: 残る
        Directory.CreateDirectory(Path.Combine(_root, "notes"));    // 日付でない: 触らない
        File.WriteAllText(Path.Combine(_root, "20261021", "x.txt"), "x");

        DroppedFileStore.CleanupOldFolders(_root, now);

        Assert.False(Directory.Exists(Path.Combine(_root, "20261021")));
        Assert.True(Directory.Exists(Path.Combine(_root, "20261022")));
        Assert.True(Directory.Exists(Path.Combine(_root, "20261029")));
        Assert.True(Directory.Exists(Path.Combine(_root, "notes")));
    }

    [Fact]
    public void CleanupOldFolders_ルートが無ければ何もしない()
    {
        DroppedFileStore.CleanupOldFolders(Path.Combine(_root, "missing"), DateTime.Now);
    }

    /// <summary>指定バイト数のゼロを返すだけの読み取り専用ストリーム（巨大なメモリを確保しないため）。</summary>
    private sealed class ZeroStream : Stream
    {
        private readonly long _length;
        private long _position;
        public ZeroStream(long length) { _length = length; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var remain = _length - _position;
            if (remain <= 0) return 0;
            var n = (int)Math.Min(count, remain);
            Array.Clear(buffer, offset, n);
            _position += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
