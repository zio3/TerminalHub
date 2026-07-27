namespace TerminalHub.Terminal;

/// <summary>
/// VTエミュレータ方式のターミナル状態バッファ。
/// 出力を <see cref="VtParser"/> で解釈してグリッド状態として保持し、
/// 復元時は確定状態を最小 ANSI にシリアライズして返す。
/// repaint（全画面再描画）は同じセルへの上書きとして畳まれるため、
/// 生ストリーム方式で起きていたスクロールバック二重化が発生しない。
/// </summary>
public sealed class EmulatedStateBuffer : ITerminalStateBuffer
{
    private readonly TerminalGrid _grid;
    private readonly VtParser _parser;
    private readonly object _lock = new();
    private readonly List<ReplaySnapshot> _activeReplays = new();
    private readonly RawChunkRing _rawRing = new();
    private bool _hasData;

    public EmulatedStateBuffer(int cols = 120, int rows = 30)
    {
        _grid = new TerminalGrid(cols, rows);
        _parser = new VtParser(_grid);
    }

    /// <summary>内部グリッド（テスト・診断用）。ロック外で触らないこと。</summary>
    public TerminalGrid Grid => _grid;

    /// <summary>直近の生チャンク＋リプレイ/Clear/Resize マーカーの診断リング（xterm 崩れの事後調査用）。</summary>
    public RawChunkRing RawRing => _rawRing;

    public bool Append(string data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return false;
        }
        lock (_lock)
        {
            _hasData = true;
            _parser.Feed(data);
            // Feed 直後の Pending（＝このチャンクがシーケンス途中で終わったか）を添えて記録する。
            // これが非空のチャンクの直後にリプレイ注入マーカーが挟まっていたら、断片の孤児化が疑われる
            _rawRing.Add(data, _parser.Pending);

            if (_activeReplays.Count == 0)
            {
                return false;
            }
            foreach (var replay in _activeReplays)
            {
                replay.Tail.Append(data);
            }
            return true;
        }
    }

    public string SerializeForReplay()
    {
        lock (_lock)
        {
            return AnsiSerializer.Serialize(_grid);
        }
    }

    public ReplaySnapshot BeginReplay()
    {
        lock (_lock)
        {
            var snapshot = new ReplaySnapshot(_hasData ? AnsiSerializer.Serialize(_grid) : string.Empty);
            // チャンク境界で分断された未確定シーケンス（途中で切れたエスケープ/サロゲート前半）を
            // テールの先頭に種付けする。この続きは以降のライブ出力として届くため、
            // これが無いと xterm 側でシーケンスの後半だけが届きゴミ文字として表示される。
            // （スナップショット側でなくテール側に付けるのは、孤立サロゲートが
            // 単独の書き込み末尾になると JSON 化で化けるのを避けるため）
            snapshot.Tail.Append(_parser.Pending);
            _activeReplays.Add(snapshot);
            _rawRing.Mark($"BeginReplay snapshot={snapshot.Content.Length}文字 pending種付け={_parser.Pending.Length}文字");
            return snapshot;
        }
    }

    public string EndReplay(ReplaySnapshot snapshot)
    {
        lock (_lock)
        {
            _activeReplays.Remove(snapshot);
            var tail = snapshot.Tail.ToString();
            _rawRing.Mark($"EndReplay tail={tail.Length}文字");
            return tail;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _grid.Reset();
            _hasData = false;
            _rawRing.Mark("Clear");
        }
    }

    public void Resize(int cols, int rows)
    {
        lock (_lock)
        {
            _grid.Resize(cols, rows);
            _rawRing.Mark($"Resize {cols}x{rows}");
        }
    }

    public int Size
    {
        get
        {
            lock (_lock)
            {
                // 何も受信していなければ 0（UI のダウンロードボタン無効化判定に使われる）。
                // 受信済みなら保持量の目安（スクロールバック行数 × 列数 ＋ 画面分）を返す
                if (!_hasData)
                {
                    return 0;
                }
                return (_grid.Scrollback.Count + _grid.Screen.Count) * _grid.Cols;
            }
        }
    }
}
