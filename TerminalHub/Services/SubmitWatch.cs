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
/// <param name="InputSequence">
/// 自分の Enter を書き終えた時点の ConPTY 入力通し番号。再送直前にこれが進んでいれば、
/// 人間のキー入力や別の送信が入力欄に触れているので再送しない（入力欄の所有権の証拠）。
/// </param>
public sealed record SubmitWatchEntry(
    Guid TargetSessionId, string Tag, DateTime WrittenAt, int TextLength,
    bool ResendAllowed = false, int Attempt = 0, long InputSequence = 0)
{
    /// <summary>監視器が振る一意番号。再送中の同一性判定に使う（本文や時刻が同じでも区別する）。</summary>
    public long Seq { get; init; }

    /// <summary>
    /// 期限切れで Enter 再送の処理に入っているが、まだ再送が完了していない。
    /// この間も待ち行列に残しておく（遅れて届いた提出確認を取りこぼさないため）。
    /// </summary>
    public bool Resending { get; init; }
}

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
///
/// **期限切れ→再送→再監視の間も項目は行列に残す**。外してしまうと、その隙に届いた提出確認が
/// 行き場を失い（1件なら取りこぼして誤 WARN、複数件なら後続を誤って確認済みにして FIFO がずれる）、
/// 不要な再送や再送漏れにつながる。再送対象は <see cref="SubmitWatchEntry.Resending"/> を立てて
/// 保持し、<see cref="Confirm"/> はその状態でも最古の1件として外す（＝再送の中止条件になる）。
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

    private readonly Dictionary<Guid, List<SubmitWatchEntry>> _pending = new();
    private long _nextSeq;

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
        bool resendAllowed = false, long inputSequence = 0)
    {
        var entry = new SubmitWatchEntry(
            targetSessionId, MakeTag(text), writtenAtUtc, text.Length, resendAllowed, 0, inputSequence)
        {
            Seq = ++_nextSeq,
        };
        ListFor(targetSessionId).Add(entry);
        return entry;
    }

    /// <summary>
    /// 宛先の UserPromptSubmit を受けたときに呼ぶ。待っている配送があれば最古の1件（再送中でも）を
    /// 確認済みにして返す。待ちが無ければ null（人間が打ったプロンプト等。観測対象外）。
    /// </summary>
    public SubmitWatchEntry? Confirm(Guid targetSessionId)
    {
        if (!_pending.TryGetValue(targetSessionId, out var list) || list.Count == 0)
            return null;
        var entry = list[0];
        list.RemoveAt(0);
        if (list.Count == 0)
            _pending.Remove(targetSessionId);
        return entry;
    }

    /// <summary>
    /// 期限を過ぎても確認が来ていない配送を返す。
    /// - 再送を許された配送（初回・未再送）は**行列に残したまま**再送中の印を付けて返す。
    ///   再送の完了は <see cref="FinishResend"/>、取りやめは <see cref="CancelResend"/> で伝える。
    ///   既に再送中のものは二度と返さない。
    /// - それ以外は行列から外して返す（呼び出し側は WARN を出すだけ）。
    /// </summary>
    public IReadOnlyList<SubmitWatchEntry> Expire(DateTime nowUtc)
    {
        var expired = new List<SubmitWatchEntry>();
        foreach (var (target, list) in _pending.ToList())
        {
            for (var i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e.Resending || nowUtc - e.WrittenAt < Threshold)
                    continue;

                if (e.ResendAllowed && e.Attempt == 0)
                {
                    var marked = e with { Resending = true };
                    list[i] = marked;
                    expired.Add(marked);
                }
                else
                {
                    list.RemoveAt(i);
                    i--;
                    expired.Add(e);
                }
            }
            if (list.Count == 0)
                _pending.Remove(target);
        }
        return expired;
    }

    /// <summary>その配送がまだ確認待ちに残っているか（再送直前の「まだ提出確認が来ていない」判定）。</summary>
    public bool IsPending(SubmitWatchEntry entry) => Find(entry, out _, out _) >= 0;

    /// <summary>
    /// Enter を再送し終えたときに呼ぶ。行列内の同じ位置で再監視（再送不可・試行回数+1・時刻更新）に
    /// 置き換えて返す。既に提出確認で外れていれば null（再送と提出確認が同時だった。害はない）。
    /// </summary>
    public SubmitWatchEntry? FinishResend(SubmitWatchEntry entry, DateTime writtenAtUtc, long inputSequence)
    {
        var i = Find(entry, out _, out var list);
        if (i < 0)
            return null;
        var rearmed = list![i] with
        {
            WrittenAt = writtenAtUtc,
            ResendAllowed = false,
            Attempt = entry.Attempt + 1,
            Resending = false,
            InputSequence = inputSequence,
        };
        list[i] = rearmed;
        return rearmed;
    }

    /// <summary>再送を取りやめて監視を終える（宛先が処理中・許可待ち・他の入力あり等）。外せたら true。</summary>
    public bool CancelResend(SubmitWatchEntry entry)
    {
        var i = Find(entry, out var target, out var list);
        if (i < 0)
            return false;
        list!.RemoveAt(i);
        if (list.Count == 0)
            _pending.Remove(target);
        return true;
    }

    /// <summary>テスト・診断用。宛先で確認待ちになっている件数（再送中を含む）。</summary>
    public int PendingCount(Guid targetSessionId) =>
        _pending.TryGetValue(targetSessionId, out var list) ? list.Count : 0;

    private List<SubmitWatchEntry> ListFor(Guid targetSessionId)
    {
        if (!_pending.TryGetValue(targetSessionId, out var list))
        {
            list = new List<SubmitWatchEntry>();
            _pending[targetSessionId] = list;
        }
        return list;
    }

    private int Find(SubmitWatchEntry entry, out Guid target, out List<SubmitWatchEntry>? list)
    {
        target = entry.TargetSessionId;
        if (!_pending.TryGetValue(target, out list))
            return -1;
        return list.FindIndex(e => e.Seq == entry.Seq);
    }
}
