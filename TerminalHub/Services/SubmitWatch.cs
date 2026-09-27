using System.Text.RegularExpressions;

namespace TerminalHub.Services;

/// <summary>
/// 配送1件が「提出された（相手 CLI が UserPromptSubmit を撃った）」ことの観測記録。
/// </summary>
/// <param name="TargetSessionId">宛先セッション</param>
/// <param name="Tag">ログで配送を特定するための札（エンベロープの #ID。無ければ本文の先頭）</param>
/// <param name="WrittenAt">Enter まで書き終えた時刻（UTC）</param>
/// <param name="TextLength">本文の文字数（エンベロープ込み）</param>
/// <param name="ResendAllowed">期限切れ時に Enter を再送してよいか（書き込み時に宛先が idle だった配送だけ true）</param>
/// <param name="Attempt">0=最初の書き込み、1=Enter 再送後の再監視</param>
public sealed record SubmitWatchEntry(
    Guid TargetSessionId, string Tag, DateTime WrittenAt, int TextLength,
    bool ResendAllowed = false, int Attempt = 0);

/// <summary>
/// 配送の「本文＋Enter を書いた」と「相手が提出を認めた（UserPromptSubmit hook）」を突き合わせる
/// 観測器。時刻の突き合わせと期限切れの検出だけを担い、**Enter の再送そのものは持ち主
/// （<see cref="SessionDeliveryService"/>）が行う**（ConPTY への書き込みと宛先の状態確認が要るため）。
///
/// 背景: 書き込みは成功したのに Enter だけが TUI に呑まれ、本文が入力欄に残ったまま
/// 提出されない事象が Claude Code / Codex 双方で確率的に起きる（2026-08-03 / 2026-09-27 観測）。
/// 2026-09-27 の実測では Codex 宛が顕著で、Codex 自身の提出記録（history.jsonl）と hook の
/// 到着時刻が一致した＝hook は健全で Enter が本当に落ちている。有力な原因は Codex TUI の
/// ペーストバースト判定（短間隔の連続入力をペースト扱いし、その最中の Enter を改行として
/// 取り込む。TUI が重いと本文と Enter がまとめて処理されて該当する）。
/// 本文は入力欄に残っているので Enter を1回だけ再送すれば提出される。既に提出済みなら
/// 空の Enter で無害。本文は再送しない（二重連結を防ぐ）。
///
/// 純ロジック（時刻は引数で受ける）なのでヘッドレステストの対象。スレッド安全性は呼び出し側の責務
/// （<see cref="SessionDeliveryService"/> は自前のロックで囲む）。
///
/// 突き合わせは宛先ごとの FIFO。同じ宛先へ短時間に2件書いた場合、UserPromptSubmit 1回で
/// 古い方から1件だけ確認済みにする（相手 CLI はプロンプトごとに UserPromptSubmit を撃つので
/// 件数は一致するのが正常。ずれた分は期限切れとして WARN に現れる）。
/// </summary>
public sealed class SubmitWatch
{
    /// <summary>
    /// 提出が確認できないとみなすまでの時間。人間の Enter 呑まれ観測（8〜19秒後に手押し）より
    /// 十分短く、相手 CLI が hook を撃つまでの通常遅延（1秒未満）より十分長い。
    /// 処理中の宛先へ送った場合は相手側のキューで ACK が遅れるのが正常なので、そのケースは
    /// 再送せず、期限切れの WARN に「処理中だった」旨が分かるよう、腕当て時の状態を呼び出し側がログに残す。
    /// Codex は健全でも 2〜3秒、重いときは 6秒かかった実測があるので、少し余裕を持たせる
    /// （早すぎる再送は「提出済みの相手に空の Enter」で無害だが、ログが紛らわしくなる）。
    /// </summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(6);

    private static readonly Regex EnvelopeIdPattern = new(@"#([0-9a-f]{12})\b", RegexOptions.Compiled);

    private readonly Dictionary<Guid, Queue<SubmitWatchEntry>> _pending = new();

    /// <summary>本文からログ用の札を作る（エンベロープの #ID があればそれ、無ければ先頭 24 文字）。</summary>
    public static string MakeTag(string text)
    {
        var m = EnvelopeIdPattern.Match(text);
        if (m.Success)
            return "#" + m.Groups[1].Value;
        var head = text.Length <= 24 ? text : text[..24] + "…";
        return "\"" + head + "\"";
    }

    /// <summary>本文＋Enter を書き終えた直後に呼ぶ。</summary>
    public SubmitWatchEntry Arm(Guid targetSessionId, string text, DateTime writtenAtUtc,
        bool resendAllowed = false, int attempt = 0)
    {
        var entry = new SubmitWatchEntry(
            targetSessionId, MakeTag(text), writtenAtUtc, text.Length, resendAllowed, attempt);
        if (!_pending.TryGetValue(targetSessionId, out var queue))
        {
            queue = new Queue<SubmitWatchEntry>();
            _pending[targetSessionId] = queue;
        }
        queue.Enqueue(entry);
        return entry;
    }

    /// <summary>
    /// Enter を再送したあとの再監視。札と文字数はそのまま、再送不可・試行回数+1 で積み直す。
    /// </summary>
    public SubmitWatchEntry Rearm(SubmitWatchEntry previous, DateTime writtenAtUtc)
    {
        var entry = previous with { WrittenAt = writtenAtUtc, ResendAllowed = false, Attempt = previous.Attempt + 1 };
        if (!_pending.TryGetValue(entry.TargetSessionId, out var queue))
        {
            queue = new Queue<SubmitWatchEntry>();
            _pending[entry.TargetSessionId] = queue;
        }
        queue.Enqueue(entry);
        return entry;
    }

    /// <summary>
    /// 宛先の UserPromptSubmit を受けたときに呼ぶ。待っている配送があれば最古の1件を確認済みにして返す。
    /// 待ちが無ければ null（人間が打ったプロンプト等。観測対象外）。
    /// </summary>
    public SubmitWatchEntry? Confirm(Guid targetSessionId)
    {
        if (!_pending.TryGetValue(targetSessionId, out var queue) || queue.Count == 0)
            return null;
        var entry = queue.Dequeue();
        if (queue.Count == 0)
            _pending.Remove(targetSessionId);
        return entry;
    }

    /// <summary>期限を過ぎても確認が来ていない配送を取り除いて返す（WARN ログ用）。</summary>
    public IReadOnlyList<SubmitWatchEntry> Expire(DateTime nowUtc)
    {
        var expired = new List<SubmitWatchEntry>();
        foreach (var (target, queue) in _pending.ToList())
        {
            while (queue.Count > 0 && nowUtc - queue.Peek().WrittenAt >= Threshold)
                expired.Add(queue.Dequeue());
            if (queue.Count == 0)
                _pending.Remove(target);
        }
        return expired;
    }

    /// <summary>テスト・診断用。宛先で確認待ちになっている件数。</summary>
    public int PendingCount(Guid targetSessionId) =>
        _pending.TryGetValue(targetSessionId, out var q) ? q.Count : 0;
}
