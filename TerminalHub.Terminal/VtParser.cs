using System.Text;

namespace TerminalHub.Terminal;

/// <summary>
/// VT/ANSI エスケープシーケンスのパーサ（DEC 状態機械の簡易版）。
/// チャンク境界をまたぐシーケンス・サロゲートペアにも耐えるよう、状態をインスタンスに保持する。
/// 解釈結果は <see cref="TerminalGrid"/> への操作として適用する。
/// </summary>
public sealed class VtParser
{
    private enum State
    {
        Ground,
        Escape,             // ESC 受信直後
        EscapeIntermediate, // ESC ( など、次の1文字を読んで捨てる系
        Csi,                // ESC [ 〜 最終バイト
        Osc,                // ESC ] 〜 BEL / ST
        OscEsc,             // OSC 中に ESC を見た（ST 判定用）
        Dcs,                // ESC P / X / ^ / _ 〜 ST（内容は読み捨て）
        DcsEsc,
    }

    private readonly TerminalGrid _grid;
    private State _state = State.Ground;
    private readonly StringBuilder _csiParams = new();
    private char _pendingHighSurrogate;

    public VtParser(TerminalGrid grid)
    {
        _grid = grid;
    }

    /// <summary>出力チャンクを解釈してグリッドへ適用する。</summary>
    public void Feed(string data)
    {
        foreach (char ch in data)
        {
            switch (_state)
            {
                case State.Ground:
                    FeedGround(ch);
                    break;
                case State.Escape:
                    FeedEscape(ch);
                    break;
                case State.EscapeIntermediate:
                    // 文字集合指定（ESC ( B 等）の最終文字。読み捨てて ground へ
                    _state = State.Ground;
                    break;
                case State.Csi:
                    FeedCsi(ch);
                    break;
                case State.Osc:
                    if (ch == '\a')
                    {
                        _state = State.Ground;
                    }
                    else if (ch == '\x1b')
                    {
                        _state = State.OscEsc;
                    }
                    break;
                case State.OscEsc:
                    // ESC \ (ST) なら終了。それ以外は OSC 続行扱い
                    _state = ch == '\\' ? State.Ground : State.Osc;
                    break;
                case State.Dcs:
                    if (ch == '\x1b')
                    {
                        _state = State.DcsEsc;
                    }
                    break;
                case State.DcsEsc:
                    _state = ch == '\\' ? State.Ground : State.Dcs;
                    break;
            }
        }
    }

    private void FeedGround(char ch)
    {
        switch (ch)
        {
            case '\x1b':
                _pendingHighSurrogate = '\0';
                _state = State.Escape;
                return;
            case '\r':
                _grid.CarriageReturn();
                return;
            case '\n':
            case '\v':
            case '\f':
                _grid.LineFeed();
                return;
            case '\b':
                _grid.Backspace();
                return;
            case '\t':
                _grid.Tab();
                return;
            case '\a':
            case '\0':
                return; // BEL / NUL は無視
        }

        if (ch < 0x20)
        {
            return; // その他の C0 制御は無視
        }

        // サロゲートペアの合成
        if (char.IsHighSurrogate(ch))
        {
            _pendingHighSurrogate = ch;
            return;
        }

        string text;
        int cp;
        if (char.IsLowSurrogate(ch) && _pendingHighSurrogate != '\0')
        {
            text = new string(new[] { _pendingHighSurrogate, ch });
            cp = char.ConvertToUtf32(_pendingHighSurrogate, ch);
            _pendingHighSurrogate = '\0';
        }
        else
        {
            text = ch.ToString();
            cp = ch;
        }

        _grid.PutGrapheme(text, CharWidth.GetWidth(cp));
    }

    private void FeedEscape(char ch)
    {
        switch (ch)
        {
            case '[':
                _csiParams.Clear();
                _state = State.Csi;
                return;
            case ']':
                _state = State.Osc;
                return;
            case 'P': // DCS
            case 'X': // SOS
            case '^': // PM
            case '_': // APC
                _state = State.Dcs;
                return;
            case '(': // 文字集合指定（次の1文字を読み捨て）
            case ')':
            case '*':
            case '+':
            case '#':
            case '%':
                _state = State.EscapeIntermediate;
                return;
            case '7':
                _grid.SaveCursor();
                break;
            case '8':
                _grid.RestoreCursor();
                break;
            case 'M':
                _grid.ReverseIndex();
                break;
            case 'D':
                _grid.LineFeed();
                break;
            case 'E':
                _grid.CarriageReturn();
                _grid.LineFeed();
                break;
            case 'c':
                _grid.Reset();
                break;
                // '=' '>'（キーパッドモード）等はそのまま無視
        }
        _state = State.Ground;
    }

    private void FeedCsi(char ch)
    {
        // 最終バイト（@〜~）でディスパッチ、それ以外はパラメータとして蓄積
        if (ch >= 0x40 && ch <= 0x7E)
        {
            DispatchCsi(ch, _csiParams.ToString());
            _state = State.Ground;
        }
        else if (ch >= 0x20)
        {
            // パラメータ/中間バイト（数字 ; ? > ! SP " 等）
            if (_csiParams.Length < 64)
            {
                _csiParams.Append(ch);
            }
        }
        else if (ch == '\x1b')
        {
            // 異常系: シーケンス中断
            _state = State.Escape;
        }
        // C0 制御は CSI 内では無視（厳密には実行だが簡易化）
    }

    private void DispatchCsi(char final, string raw)
    {
        bool isPrivate = raw.StartsWith('?');
        var paramText = isPrivate ? raw[1..] : raw;
        // 中間バイト付き（DECSCUSR "q" 等）は読み捨て対象: パラメータ部が数字と ; 以外を含むなら無視
        foreach (char c in paramText)
        {
            if (!char.IsAsciiDigit(c) && c != ';')
            {
                if (final != 'm')
                {
                    return;
                }
                break;
            }
        }

        var p = ParseParams(paramText);
        int P(int index, int fallback = 1) => index < p.Count && p[index] > 0 ? p[index] : fallback;

        switch (final)
        {
            case 'H':
            case 'f':
                _grid.MoveCursor(P(0) - 1, P(1) - 1);
                break;
            case 'A':
                _grid.CursorUp(P(0));
                break;
            case 'B':
            case 'e':
                _grid.CursorDown(P(0));
                break;
            case 'C':
            case 'a':
                _grid.CursorForward(P(0));
                break;
            case 'D':
                _grid.CursorBack(P(0));
                break;
            case 'G':
            case '`':
                _grid.CursorColumn(P(0) - 1);
                break;
            case 'd':
                _grid.CursorRowTo(P(0) - 1);
                break;
            case 'J':
                _grid.EraseInDisplay(p.Count > 0 ? p[0] : 0);
                break;
            case 'K':
                _grid.EraseInLine(p.Count > 0 ? p[0] : 0);
                break;
            case 'L':
                _grid.InsertLines(P(0));
                break;
            case 'M':
                _grid.DeleteLines(P(0));
                break;
            case '@':
                _grid.InsertChars(P(0));
                break;
            case 'P':
                _grid.DeleteChars(P(0));
                break;
            case 'X':
                _grid.EraseChars(P(0));
                break;
            case 'S':
                _grid.ScrollUp(P(0));
                break;
            case 'T':
                _grid.ScrollDown(P(0));
                break;
            case 'm':
                ApplySgr(p);
                break;
            case 's':
                _grid.SaveCursor();
                break;
            case 'u':
                _grid.RestoreCursor();
                break;
            case 'h':
                if (isPrivate)
                {
                    ApplyPrivateMode(p, set: true);
                }
                break;
            case 'l':
                if (isPrivate)
                {
                    ApplyPrivateMode(p, set: false);
                }
                break;
                // 'r'(DECSTBM) 't'(WindowOps) 'n'(DSR) 等は無視（Phase 2 以降で必要なら対応）
        }
    }

    private void ApplyPrivateMode(List<int> p, bool set)
    {
        foreach (int mode in p)
        {
            switch (mode)
            {
                case 25:
                    _grid.CursorVisible = set;
                    break;
                case 47:
                case 1047:
                case 1049:
                    if (set)
                    {
                        _grid.EnterAltScreen();
                    }
                    else
                    {
                        _grid.ExitAltScreen();
                    }
                    break;
                    // 2004(bracketed paste) 1000系(マウス) 等は状態に影響しないので無視
            }
        }
    }

    private void ApplySgr(List<int> p)
    {
        if (p.Count == 0)
        {
            p.Add(0);
        }

        for (int i = 0; i < p.Count; i++)
        {
            int code = p[i];
            switch (code)
            {
                case 0:
                    _grid.CurrentFg = CellColor.Default;
                    _grid.CurrentBg = CellColor.Default;
                    _grid.CurrentAttrs = CellAttributes.None;
                    break;
                case 1: _grid.CurrentAttrs |= CellAttributes.Bold; break;
                case 2: _grid.CurrentAttrs |= CellAttributes.Faint; break;
                case 3: _grid.CurrentAttrs |= CellAttributes.Italic; break;
                case 4: _grid.CurrentAttrs |= CellAttributes.Underline; break;
                case 7: _grid.CurrentAttrs |= CellAttributes.Inverse; break;
                case 8: _grid.CurrentAttrs |= CellAttributes.Hidden; break;
                case 9: _grid.CurrentAttrs |= CellAttributes.Strikethrough; break;
                case 22: _grid.CurrentAttrs &= ~(CellAttributes.Bold | CellAttributes.Faint); break;
                case 23: _grid.CurrentAttrs &= ~CellAttributes.Italic; break;
                case 24: _grid.CurrentAttrs &= ~CellAttributes.Underline; break;
                case 27: _grid.CurrentAttrs &= ~CellAttributes.Inverse; break;
                case 28: _grid.CurrentAttrs &= ~CellAttributes.Hidden; break;
                case 29: _grid.CurrentAttrs &= ~CellAttributes.Strikethrough; break;
                case >= 30 and <= 37:
                    _grid.CurrentFg = CellColor.FromIndex(code - 30);
                    break;
                case 39:
                    _grid.CurrentFg = CellColor.Default;
                    break;
                case >= 40 and <= 47:
                    _grid.CurrentBg = CellColor.FromIndex(code - 40);
                    break;
                case 49:
                    _grid.CurrentBg = CellColor.Default;
                    break;
                case >= 90 and <= 97:
                    _grid.CurrentFg = CellColor.FromIndex(code - 90 + 8);
                    break;
                case >= 100 and <= 107:
                    _grid.CurrentBg = CellColor.FromIndex(code - 100 + 8);
                    break;
                case 38:
                case 48:
                    {
                        var color = ParseExtendedColor(p, ref i);
                        if (color.HasValue)
                        {
                            if (code == 38)
                            {
                                _grid.CurrentFg = color.Value;
                            }
                            else
                            {
                                _grid.CurrentBg = color.Value;
                            }
                        }
                        break;
                    }
            }
        }
    }

    /// <summary>SGR 38/48 の拡張色（5;n = 256色 / 2;r;g;b = truecolor）を読み取る。</summary>
    private static CellColor? ParseExtendedColor(List<int> p, ref int i)
    {
        if (i + 1 >= p.Count)
        {
            return null;
        }
        int kind = p[i + 1];
        if (kind == 5 && i + 2 < p.Count)
        {
            var color = CellColor.FromIndex(p[i + 2]);
            i += 2;
            return color;
        }
        if (kind == 2 && i + 4 < p.Count)
        {
            var color = CellColor.FromRgb(p[i + 2], p[i + 3], p[i + 4]);
            i += 4;
            return color;
        }
        // 不正形式: 以降のパラメータを打ち切り
        i = p.Count;
        return null;
    }

    private static List<int> ParseParams(string text)
    {
        var result = new List<int>(8);
        if (text.Length == 0)
        {
            return result;
        }
        int value = 0;
        bool has = false;
        foreach (char c in text)
        {
            if (char.IsAsciiDigit(c))
            {
                value = Math.Min(value * 10 + (c - '0'), 65535);
                has = true;
            }
            else if (c == ';')
            {
                result.Add(has ? value : 0);
                value = 0;
                has = false;
            }
        }
        result.Add(has ? value : 0);
        return result;
    }
}
