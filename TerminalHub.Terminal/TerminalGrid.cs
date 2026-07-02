namespace TerminalHub.Terminal;

/// <summary>
/// ターミナル画面の状態モデル。メイン画面グリッド＋スクロールバック＋カーソル＋現在SGRを保持する。
/// VTシーケンスの解釈は <see cref="VtParser"/> が行い、ここは純粋な状態操作のみ担当する。
/// </summary>
public sealed class TerminalGrid
{
    public int Cols { get; private set; }
    public int Rows { get; private set; }

    /// <summary>画面領域（Rows 行）。</summary>
    private List<Cell[]> _screen;

    /// <summary>スクロールバック（古い行が先頭）。alt-screen 中は追記しない。</summary>
    private readonly List<Cell[]> _scrollback = new();

    /// <summary>スクロールバック保持行数の上限。</summary>
    public int MaxScrollback { get; set; } = 5000;

    // カーソル（0-based）
    public int CursorRow { get; private set; }
    public int CursorCol { get; private set; }
    public bool CursorVisible { get; set; } = true;

    // 現在の描画スタイル（SGR）
    public CellColor CurrentFg;
    public CellColor CurrentBg;
    public CellAttributes CurrentAttrs;

    // 保存カーソル（DECSC / CSI s）
    private int _savedRow;
    private int _savedCol;

    // alt-screen（CSI ?1049h/l 等）
    public bool IsAltScreen { get; private set; }
    private List<Cell[]>? _mainScreenStash;
    private int _mainCursorRowStash;
    private int _mainCursorColStash;

    // 遅延ラップ（xterm 互換: 最終列に書いた直後はラップせず、次の印字で改行する）
    private bool _pendingWrap;

    public TerminalGrid(int cols = 120, int rows = 30)
    {
        Cols = Math.Max(1, cols);
        Rows = Math.Max(1, rows);
        _screen = CreateBlankScreen(Rows, Cols);
    }

    private static List<Cell[]> CreateBlankScreen(int rows, int cols)
    {
        var screen = new List<Cell[]>(rows);
        for (int i = 0; i < rows; i++)
        {
            screen.Add(CreateBlankRow(cols));
        }
        return screen;
    }

    private static Cell[] CreateBlankRow(int cols)
    {
        var row = new Cell[cols];
        for (int i = 0; i < cols; i++)
        {
            row[i] = Cell.Blank;
        }
        return row;
    }

    /// <summary>スクロールバックの読み取り（シリアライザ用）。</summary>
    public IReadOnlyList<Cell[]> Scrollback => _scrollback;

    /// <summary>画面行の読み取り（シリアライザ用）。</summary>
    public IReadOnlyList<Cell[]> Screen => _screen;

    // ---- 印字 ----

    /// <summary>書記素1つを現在位置へ書き込む（幅は呼び出し側で算出済み）。</summary>
    public void PutGrapheme(string text, int width)
    {
        if (width <= 0)
        {
            // 結合文字: 直前のセルに合成する
            AppendCombining(text);
            return;
        }

        // 遅延ラップの解決
        if (_pendingWrap)
        {
            _pendingWrap = false;
            CarriageReturn();
            LineFeed();
        }

        // 全角が最終列に来たら先にラップ（最終列は空白のまま）
        if (width == 2 && CursorCol >= Cols - 1)
        {
            _screen[CursorRow][CursorCol] = Cell.BlankWith(CurrentFg, CurrentBg, CurrentAttrs);
            CarriageReturn();
            LineFeed();
        }

        var row = _screen[CursorRow];

        // 既存の全角セルを上書きする場合、対になるセルを空白化して片割れを残さない
        ClearWidePairAt(CursorRow, CursorCol);
        if (width == 2 && CursorCol + 1 < Cols)
        {
            ClearWidePairAt(CursorRow, CursorCol + 1);
        }

        row[CursorCol] = new Cell
        {
            Text = text,
            Width = (byte)width,
            Foreground = CurrentFg,
            Background = CurrentBg,
            Attributes = CurrentAttrs,
        };

        if (width == 2 && CursorCol + 1 < Cols)
        {
            row[CursorCol + 1] = Cell.WideTrailer(CurrentFg, CurrentBg, CurrentAttrs);
        }

        // カーソル前進（最終列では遅延ラップ）
        if (CursorCol + width >= Cols)
        {
            CursorCol = Cols - 1;
            _pendingWrap = true;
        }
        else
        {
            CursorCol += width;
        }
    }

    private void AppendCombining(string text)
    {
        int col = CursorCol;
        int r = CursorRow;
        // 直前に印字したセルへ（遅延ラップ中はカーソル位置がそのセル）
        if (!_pendingWrap)
        {
            col--;
        }
        if (col < 0)
        {
            return;
        }
        // 全角の後続セルなら先頭セルへ
        if (_screen[r][col].IsWideTrailer && col > 0)
        {
            col--;
        }
        if (_screen[r][col].Text != null)
        {
            _screen[r][col].Text += text;
        }
    }

    /// <summary>指定位置が全角ペアの一部なら、ペア両方を空白化する（片割れ防止）。</summary>
    private void ClearWidePairAt(int rowIndex, int colIndex)
    {
        var row = _screen[rowIndex];
        var cell = row[colIndex];
        if (cell.Width == 2 && colIndex + 1 < Cols)
        {
            row[colIndex + 1] = Cell.Blank;
        }
        else if (cell.IsWideTrailer && colIndex > 0)
        {
            row[colIndex - 1] = Cell.Blank;
        }
    }

    // ---- 制御文字 ----

    public void CarriageReturn()
    {
        CursorCol = 0;
        _pendingWrap = false;
    }

    public void LineFeed()
    {
        _pendingWrap = false;
        if (CursorRow >= Rows - 1)
        {
            ScrollUp(1);
        }
        else
        {
            CursorRow++;
        }
    }

    public void Backspace()
    {
        _pendingWrap = false;
        if (CursorCol > 0)
        {
            CursorCol--;
        }
    }

    public void Tab()
    {
        _pendingWrap = false;
        int next = (CursorCol / 8 + 1) * 8;
        CursorCol = Math.Min(next, Cols - 1);
    }

    /// <summary>RI（Reverse Index）: 上端なら逆スクロール、それ以外はカーソルを1行上へ。</summary>
    public void ReverseIndex()
    {
        _pendingWrap = false;
        if (CursorRow <= 0)
        {
            ScrollDown(1);
        }
        else
        {
            CursorRow--;
        }
    }

    // ---- カーソル移動 ----

    public void MoveCursor(int row, int col)
    {
        CursorRow = Math.Clamp(row, 0, Rows - 1);
        CursorCol = Math.Clamp(col, 0, Cols - 1);
        _pendingWrap = false;
    }

    public void CursorUp(int n) => MoveCursor(CursorRow - Math.Max(1, n), CursorCol);
    public void CursorDown(int n) => MoveCursor(CursorRow + Math.Max(1, n), CursorCol);
    public void CursorForward(int n) => MoveCursor(CursorRow, CursorCol + Math.Max(1, n));
    public void CursorBack(int n) => MoveCursor(CursorRow, CursorCol - Math.Max(1, n));
    public void CursorColumn(int col) => MoveCursor(CursorRow, col);
    public void CursorRowTo(int row) => MoveCursor(row, CursorCol);

    public void SaveCursor()
    {
        _savedRow = CursorRow;
        _savedCol = CursorCol;
    }

    public void RestoreCursor()
    {
        MoveCursor(_savedRow, _savedCol);
    }

    // ---- 消去 ----

    /// <summary>ED（Erase in Display）。mode: 0=カーソル以降, 1=カーソル以前, 2=全画面, 3=全画面＋スクロールバック。</summary>
    public void EraseInDisplay(int mode)
    {
        _pendingWrap = false;
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (int r = CursorRow + 1; r < Rows; r++)
                {
                    FillRowBlank(r);
                }
                break;
            case 1:
                EraseInLine(1);
                for (int r = 0; r < CursorRow; r++)
                {
                    FillRowBlank(r);
                }
                break;
            case 2:
                for (int r = 0; r < Rows; r++)
                {
                    FillRowBlank(r);
                }
                break;
            case 3:
                for (int r = 0; r < Rows; r++)
                {
                    FillRowBlank(r);
                }
                _scrollback.Clear();
                break;
        }
    }

    /// <summary>EL（Erase in Line）。mode: 0=カーソル以降, 1=カーソル以前, 2=行全体。</summary>
    public void EraseInLine(int mode)
    {
        _pendingWrap = false;
        var row = _screen[CursorRow];
        int from = mode switch { 0 => CursorCol, 1 => 0, _ => 0 };
        int to = mode switch { 0 => Cols - 1, 1 => CursorCol, _ => Cols - 1 };
        // 全角ペアの片割れ防止（消去範囲の境界）
        ClearWidePairAt(CursorRow, from);
        if (to < Cols - 1)
        {
            ClearWidePairAt(CursorRow, to);
        }
        for (int c = from; c <= to; c++)
        {
            row[c] = Cell.BlankWith(CurrentFg, CurrentBg, CurrentAttrs);
        }
    }

    /// <summary>ECH（Erase Characters）: カーソル位置から n セルを空白化。</summary>
    public void EraseChars(int n)
    {
        n = Math.Max(1, n);
        var row = _screen[CursorRow];
        int end = Math.Min(CursorCol + n, Cols);
        ClearWidePairAt(CursorRow, CursorCol);
        if (end - 1 < Cols)
        {
            ClearWidePairAt(CursorRow, end - 1);
        }
        for (int c = CursorCol; c < end; c++)
        {
            row[c] = Cell.BlankWith(CurrentFg, CurrentBg, CurrentAttrs);
        }
    }

    private void FillRowBlank(int rowIndex)
    {
        var row = _screen[rowIndex];
        for (int c = 0; c < Cols; c++)
        {
            row[c] = Cell.BlankWith(CurrentFg, CurrentBg, CurrentAttrs);
        }
    }

    // ---- 行/文字の挿入・削除 ----

    public void InsertLines(int n)
    {
        n = Math.Clamp(n, 1, Rows - CursorRow);
        for (int i = 0; i < n; i++)
        {
            _screen.RemoveAt(Rows - 1);
            _screen.Insert(CursorRow, CreateBlankRow(Cols));
        }
        CursorCol = 0;
        _pendingWrap = false;
    }

    public void DeleteLines(int n)
    {
        n = Math.Clamp(n, 1, Rows - CursorRow);
        for (int i = 0; i < n; i++)
        {
            _screen.RemoveAt(CursorRow);
            _screen.Add(CreateBlankRow(Cols));
        }
        CursorCol = 0;
        _pendingWrap = false;
    }

    public void InsertChars(int n)
    {
        n = Math.Clamp(n, 1, Cols - CursorCol);
        var row = _screen[CursorRow];
        for (int c = Cols - 1; c >= CursorCol + n; c--)
        {
            row[c] = row[c - n];
        }
        for (int c = CursorCol; c < CursorCol + n; c++)
        {
            row[c] = Cell.BlankWith(CurrentFg, CurrentBg, CurrentAttrs);
        }
    }

    public void DeleteChars(int n)
    {
        n = Math.Clamp(n, 1, Cols - CursorCol);
        var row = _screen[CursorRow];
        for (int c = CursorCol; c < Cols - n; c++)
        {
            row[c] = row[c + n];
        }
        for (int c = Cols - n; c < Cols; c++)
        {
            row[c] = Cell.BlankWith(CurrentFg, CurrentBg, CurrentAttrs);
        }
    }

    // ---- スクロール ----

    /// <summary>画面を n 行上へスクロール（上端行はスクロールバックへ、alt-screen 中は破棄）。</summary>
    public void ScrollUp(int n)
    {
        n = Math.Clamp(n, 1, Rows);
        for (int i = 0; i < n; i++)
        {
            var top = _screen[0];
            _screen.RemoveAt(0);
            _screen.Add(CreateBlankRow(Cols));
            if (!IsAltScreen)
            {
                _scrollback.Add(top);
                if (_scrollback.Count > MaxScrollback)
                {
                    _scrollback.RemoveAt(0);
                }
            }
        }
    }

    /// <summary>画面を n 行下へスクロール（下端行は破棄、上端に空白行）。</summary>
    public void ScrollDown(int n)
    {
        n = Math.Clamp(n, 1, Rows);
        for (int i = 0; i < n; i++)
        {
            _screen.RemoveAt(Rows - 1);
            _screen.Insert(0, CreateBlankRow(Cols));
        }
    }

    // ---- alt-screen ----

    public void EnterAltScreen()
    {
        if (IsAltScreen)
        {
            return;
        }
        _mainScreenStash = _screen;
        _mainCursorRowStash = CursorRow;
        _mainCursorColStash = CursorCol;
        _screen = CreateBlankScreen(Rows, Cols);
        CursorRow = 0;
        CursorCol = 0;
        _pendingWrap = false;
        IsAltScreen = true;
    }

    public void ExitAltScreen()
    {
        if (!IsAltScreen)
        {
            return;
        }
        _screen = _mainScreenStash ?? CreateBlankScreen(Rows, Cols);
        _mainScreenStash = null;
        CursorRow = Math.Clamp(_mainCursorRowStash, 0, Rows - 1);
        CursorCol = Math.Clamp(_mainCursorColStash, 0, Cols - 1);
        _pendingWrap = false;
        IsAltScreen = false;
    }

    // ---- リセット ----

    /// <summary>フルリセット（RIS）。スクロールバックも破棄する。</summary>
    public void Reset()
    {
        _screen = CreateBlankScreen(Rows, Cols);
        _scrollback.Clear();
        CursorRow = 0;
        CursorCol = 0;
        CursorVisible = true;
        CurrentFg = CellColor.Default;
        CurrentBg = CellColor.Default;
        CurrentAttrs = CellAttributes.None;
        _pendingWrap = false;
        IsAltScreen = false;
        _mainScreenStash = null;
    }
}
