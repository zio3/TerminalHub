using System.Text;

namespace TerminalHub.Terminal;

/// <summary>
/// ConPTY から届いた生の出力チャンクを「チャンク境界を保ったまま」直近ぶんだけ保持するリングバッファ。
/// 目的は xterm 側の表示崩れ（分断されたエスケープシーケンスの断片が印字される等）の事後診断。
/// エミュレータのグリッドはパース済みで境界情報が消えているため、どのバイトで切れて届いたかを
/// ここに残す。各チャンクには取り込み直後のパーサ未確定シーケンス（<see cref="VtParser.Pending"/>）を
/// 記録するので、「シーケンス途中で終わったチャンクの直後にリプレイ注入が挟まった」を正確に特定できる。
/// </summary>
public sealed class RawChunkRing
{
    /// <summary>リングの1エントリ。Note が非 null ならデータではなくイベントマーカー。</summary>
    public sealed record Entry(long Seq, DateTime TimestampUtc, string Data, string PendingAfter, string? Note);

    private readonly object _lock = new();
    private readonly Queue<Entry> _entries = new();
    private readonly int _maxTotalChars;
    private int _totalChars;
    private long _nextSeq;

    /// <param name="maxTotalChars">保持する総文字数の上限（チャンクデータの合計）。既定 512K 文字 ≒ 1MB。</param>
    public RawChunkRing(int maxTotalChars = 512 * 1024)
    {
        _maxTotalChars = maxTotalChars;
    }

    /// <summary>これまでに取り込んだチャンク総数（マーカー含む・追い出し分も含む通し番号の次値）。</summary>
    public long TotalAdded
    {
        get { lock (_lock) { return _nextSeq; } }
    }

    /// <summary>現在保持しているエントリ数。</summary>
    public int Count
    {
        get { lock (_lock) { return _entries.Count; } }
    }

    /// <summary>出力チャンクを記録する。pendingAfter にはこのチャンク取り込み直後のパーサ未確定文字列を渡す。</summary>
    public void Add(string data, string pendingAfter)
    {
        lock (_lock)
        {
            _entries.Enqueue(new Entry(_nextSeq++, DateTime.UtcNow, data, pendingAfter, Note: null));
            _totalChars += data.Length;
            while (_totalChars > _maxTotalChars && _entries.Count > 1)
            {
                _totalChars -= _entries.Dequeue().Data.Length;
            }
        }
    }

    /// <summary>
    /// イベントマーカー（リプレイ開始/終了・Clear・Resize 等）を記録する。
    /// ダンプ単体で「どのチャンクの直後に注入が挟まったか」を読めるようにするためのもの。
    /// </summary>
    public void Mark(string note)
    {
        lock (_lock)
        {
            _entries.Enqueue(new Entry(_nextSeq++, DateTime.UtcNow, string.Empty, string.Empty, note));
        }
    }

    /// <summary>現在の保持内容のコピーを古い順で返す。</summary>
    public IReadOnlyList<Entry> Snapshot()
    {
        lock (_lock)
        {
            return _entries.ToArray();
        }
    }

    /// <summary>
    /// 人間可読なダンプを生成する。制御文字は <c>\e</c> <c>\r</c> <c>\n</c> 等にエスケープし、
    /// シーケンス途中で終わったチャンク（xterm 崩れの容疑者）には PENDING 印を付ける。
    /// </summary>
    public string DumpText()
    {
        var entries = Snapshot();
        var sb = new StringBuilder();
        sb.AppendLine($"# RawChunkRing dump  entries={entries.Count} totalAdded={TotalAdded}  ({DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z)");
        sb.AppendLine("# PendingAfter が非空 = そのチャンクがエスケープシーケンス/サロゲート途中で終わった");
        sb.AppendLine("# 直後に ---- マーカー（リプレイ注入等）が挟まっていれば、断片が孤児化して xterm に印字された疑い");
        foreach (var e in entries)
        {
            if (e.Note != null)
            {
                sb.AppendLine($"#{e.Seq} {e.TimestampUtc:HH:mm:ss.fff} ---- {e.Note} ----");
                continue;
            }
            var pending = e.PendingAfter.Length > 0
                ? $" **PENDING={Escape(e.PendingAfter)}**"
                : string.Empty;
            sb.AppendLine($"#{e.Seq} {e.TimestampUtc:HH:mm:ss.fff} len={e.Data.Length}{pending}");
            sb.AppendLine(Escape(e.Data));
        }
        return sb.ToString();
    }

    /// <summary>制御文字を可視化する（\e \r \n \t \a \\ と \xNN。\n の後には読みやすさのため実改行を入れる）。</summary>
    public static string Escape(string data)
    {
        var sb = new StringBuilder(data.Length + 16);
        foreach (var ch in data)
        {
            switch (ch)
            {
                case '\x1b': sb.Append("\\e"); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\a': sb.Append("\\a"); break;
                case '\\': sb.Append("\\\\"); break;
                default:
                    if (ch < ' ' || ch == '\x7f')
                    {
                        sb.Append($"\\x{(int)ch:x2}");
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        return sb.ToString();
    }
}
