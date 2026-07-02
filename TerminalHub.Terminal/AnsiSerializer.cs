using System.Text;

namespace TerminalHub.Terminal;

/// <summary>
/// <see cref="TerminalGrid"/> の確定状態を、新しい xterm へ流し込むための最小 ANSI 文字列に変換する。
/// スクロールバック → 画面の順で全行を出力し、最後にカーソル位置と表示状態を復元する。
/// </summary>
public static class AnsiSerializer
{
    public static string Serialize(TerminalGrid grid)
    {
        var sb = new StringBuilder(4096);
        var style = new StyleTracker();

        // スクロールバック（古い順）
        foreach (var row in grid.Scrollback)
        {
            WriteRow(sb, row, ref style);
            sb.Append("\r\n");
        }

        // 画面領域（全 Rows 行を出す — 末尾の空白行も改行として保持し、カーソル相対移動を成立させる）
        var screen = grid.Screen;
        for (int r = 0; r < screen.Count; r++)
        {
            WriteRow(sb, screen[r], ref style);
            if (r < screen.Count - 1)
            {
                sb.Append("\r\n");
            }
        }

        // スタイルをリセット
        sb.Append("\x1b[m");

        // カーソル位置の復元: 書き終わり位置は画面最終行。そこから相対移動する
        int upLines = (screen.Count - 1) - grid.CursorRow;
        if (upLines > 0)
        {
            sb.Append("\x1b[").Append(upLines).Append('A');
        }
        sb.Append('\r');
        if (grid.CursorCol > 0)
        {
            sb.Append("\x1b[").Append(grid.CursorCol).Append('C');
        }

        // カーソル表示状態
        sb.Append(grid.CursorVisible ? "\x1b[?25h" : "\x1b[?25l");

        return sb.ToString();
    }

    private static void WriteRow(StringBuilder sb, Cell[] row, ref StyleTracker style)
    {
        // 末尾の「既定スタイル空白」は出力しない（行を短く保つ）
        int last = row.Length - 1;
        while (last >= 0 && row[last].IsBlank && row[last].HasDefaultStyle)
        {
            last--;
        }

        for (int c = 0; c <= last; c++)
        {
            var cell = row[c];
            if (cell.IsWideTrailer)
            {
                continue; // 全角の後続セルは出力しない
            }

            style.Apply(sb, cell);
            sb.Append(cell.Text ?? " ");
        }
    }

    /// <summary>直前のスタイルを追跡し、変化があった時だけ SGR を出力する。</summary>
    private struct StyleTracker
    {
        private CellColor _fg;
        private CellColor _bg;
        private CellAttributes _attrs;

        public void Apply(StringBuilder sb, in Cell cell)
        {
            if (cell.Foreground == _fg && cell.Background == _bg && cell.Attributes == _attrs)
            {
                return;
            }

            // リセットしてから必要な属性を積む（差分計算より単純で確実）
            sb.Append("\x1b[0");

            var attrs = cell.Attributes;
            if (attrs.HasFlag(CellAttributes.Bold)) sb.Append(";1");
            if (attrs.HasFlag(CellAttributes.Faint)) sb.Append(";2");
            if (attrs.HasFlag(CellAttributes.Italic)) sb.Append(";3");
            if (attrs.HasFlag(CellAttributes.Underline)) sb.Append(";4");
            if (attrs.HasFlag(CellAttributes.Inverse)) sb.Append(";7");
            if (attrs.HasFlag(CellAttributes.Hidden)) sb.Append(";8");
            if (attrs.HasFlag(CellAttributes.Strikethrough)) sb.Append(";9");

            AppendColor(sb, cell.Foreground, isForeground: true);
            AppendColor(sb, cell.Background, isForeground: false);

            sb.Append('m');

            _fg = cell.Foreground;
            _bg = cell.Background;
            _attrs = cell.Attributes;
        }

        private static void AppendColor(StringBuilder sb, CellColor color, bool isForeground)
        {
            switch (color.Kind)
            {
                case CellColor.ColorKind.Indexed:
                    if (color.Index < 8)
                    {
                        sb.Append(';').Append((isForeground ? 30 : 40) + color.Index);
                    }
                    else if (color.Index < 16)
                    {
                        sb.Append(';').Append((isForeground ? 90 : 100) + color.Index - 8);
                    }
                    else
                    {
                        sb.Append(isForeground ? ";38;5;" : ";48;5;").Append(color.Index);
                    }
                    break;
                case CellColor.ColorKind.Rgb:
                    sb.Append(isForeground ? ";38;2;" : ";48;2;")
                      .Append(color.R).Append(';').Append(color.G).Append(';').Append(color.B);
                    break;
            }
        }
    }
}
