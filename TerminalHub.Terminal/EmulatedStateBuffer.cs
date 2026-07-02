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
    private bool _hasData;

    public EmulatedStateBuffer(int cols = 120, int rows = 30)
    {
        _grid = new TerminalGrid(cols, rows);
        _parser = new VtParser(_grid);
    }

    /// <summary>内部グリッド（テスト・診断用）。ロック外で触らないこと。</summary>
    public TerminalGrid Grid => _grid;

    public void Append(string data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return;
        }
        lock (_lock)
        {
            _hasData = true;
            _parser.Feed(data);
        }
    }

    public string SerializeForReplay()
    {
        lock (_lock)
        {
            return AnsiSerializer.Serialize(_grid);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _grid.Reset();
            _hasData = false;
        }
    }

    public void Resize(int cols, int rows)
    {
        lock (_lock)
        {
            _grid.Resize(cols, rows);
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
