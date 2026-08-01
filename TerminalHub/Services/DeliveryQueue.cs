namespace TerminalHub.Services;

/// <summary>
/// 配送1件分。宛先が入力待ちで書き込めないときに積まれる。
///
/// ContextId / RequesterSessionId は「配送に失敗したことを誰に知らせるか」のためだけに持つ
/// （配送そのものには使わない）。IsSystemCallback=true はサーバー自身が発した通知で、
/// これの配送失敗は二次通知を作らずログだけに留める＝失敗通知の連鎖を止める終端条件。
/// </summary>
public sealed record DeliveryItem(
    Guid TargetSessionId,
    string Text,
    DateTime EnqueuedAt,
    string? ContextId,
    Guid? RequesterSessionId,
    bool IsSystemCallback);

/// <summary>
/// 宛先セッションごとの配送待ちキュー（インメモリ・揮発）。
///
/// 設計:
/// - **揮発でよい**。持つのは高々数分の TTL しかない「配りそびれた1通」で、永続化すると
///   再起動後に古い依頼が唐突に届くほうが害になる。誰に返すか（Contexts.RequesterSessionId）は
///   札の属性として別途永続化されるので、再起動後の新しい status 変化はちゃんと発火する。
/// - **宛先ごとに FIFO**。2通が入れ違いに届くのを防ぐ。
/// - UI・ConPTY・SessionInfo に依存しない純ロジックに保つ（テストからリンクして検証するため）。
///   「送れる状態か」の判定と実際の書き込みは <see cref="SessionDeliveryService"/> 側の責務。
/// </summary>
public sealed class DeliveryQueue
{
    /// <summary>
    /// 宛先1つあたりの上限。入力待ちのまま放置されたセッションへ送り続けられても、
    /// 溜め込まずに投函時点で失敗を返せるようにするための歯止め。
    /// </summary>
    public const int MaxPerTarget = 20;

    private readonly Dictionary<Guid, Queue<DeliveryItem>> _queues = new();
    private readonly object _lock = new();

    /// <summary>積む。宛先の待ち行列が上限に達していれば false（呼び出し側が失敗として扱う）。</summary>
    public bool Enqueue(DeliveryItem item)
    {
        lock (_lock)
        {
            if (!_queues.TryGetValue(item.TargetSessionId, out var queue))
            {
                queue = new Queue<DeliveryItem>();
                _queues[item.TargetSessionId] = queue;
            }

            if (queue.Count >= MaxPerTarget)
                return false;

            queue.Enqueue(item);
            return true;
        }
    }

    /// <summary>
    /// 先頭を覗く（取り出さない）。空なら false。
    /// 配送は「覗く → 書けたら <see cref="RemoveHead"/>」の順で行う。取り出してから書くと、
    /// 書き込みに失敗したときに戻す先が末尾になり FIFO が壊れるため。
    /// </summary>
    public bool TryPeek(Guid targetSessionId, out DeliveryItem item)
    {
        lock (_lock)
        {
            if (_queues.TryGetValue(targetSessionId, out var queue) && queue.Count > 0)
            {
                item = queue.Peek();
                return true;
            }
        }

        item = null!;
        return false;
    }

    /// <summary>先頭を捨てる（配送が成功したとき）。空なら何もしない。</summary>
    public void RemoveHead(Guid targetSessionId)
    {
        lock (_lock)
        {
            if (!_queues.TryGetValue(targetSessionId, out var queue) || queue.Count == 0)
                return;

            queue.Dequeue();
            if (queue.Count == 0)
                _queues.Remove(targetSessionId);
        }
    }

    /// <summary>
    /// 指定宛先の、TTL を超えた項目を取り除いて返す。FIFO かつ EnqueuedAt が単調増加なので、
    /// 先頭から期限切れが続く間だけ剥がせばよい。
    ///
    /// **宛先ごとに分けてあるのは意図的**。全宛先を一括で剥がすと、呼び出し側が
    /// 「配送中の宛先だけロックを取ってから失効させる」ことができない。配送は
    /// 「覗く→書く→捨てる」で進むため、書いている最中（先頭に残っている状態）に
    /// 別スレッドが同じ項目を失効させると、配送済みの項目が失敗と報告され、
    /// さらに直後の RemoveHead が次の項目を消してしまう（＝1通が黙って消える）。
    /// </summary>
    public IReadOnlyList<DeliveryItem> RemoveExpiredFor(Guid targetSessionId, DateTime now, TimeSpan ttl)
    {
        var expired = new List<DeliveryItem>();
        lock (_lock)
        {
            if (!_queues.TryGetValue(targetSessionId, out var queue))
                return expired;

            while (queue.Count > 0 && now - queue.Peek().EnqueuedAt >= ttl)
            {
                expired.Add(queue.Dequeue());
            }
            if (queue.Count == 0)
                _queues.Remove(targetSessionId);
        }
        return expired;
    }

    /// <summary>配送待ちが残っている宛先の一覧（掃除タイマーが再送を試みる対象）。</summary>
    public IReadOnlyList<Guid> PendingTargets()
    {
        lock (_lock)
        {
            return _queues.Keys.ToList();
        }
    }

    /// <summary>指定宛先の待ち件数。</summary>
    public int CountFor(Guid targetSessionId)
    {
        lock (_lock)
        {
            return _queues.TryGetValue(targetSessionId, out var queue) ? queue.Count : 0;
        }
    }
}
