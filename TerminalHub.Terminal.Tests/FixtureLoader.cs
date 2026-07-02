using System.Text;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// Fixtures フォルダから ConPTY 出力ストリームを読み込むヘルパー。
/// </summary>
/// <remarks>
/// - <c>*.raw</c>: 実環境キャプチャ。UTF-8 のバイト列をそのまま文字列として読む（エスケープ解釈なし）。
/// - <c>*.esc</c>: 手書き用の可読フィクスチャ。<c>\e</c>(ESC) / <c>\r</c> / <c>\n</c> / <c>\t</c> / <c>\\</c> を実文字へ復元する。
/// </remarks>
public static class FixtureLoader
{
    private static string FixturesDir =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>実キャプチャ（*.raw）をそのまま読む。</summary>
    public static string LoadRaw(string fileName)
    {
        var path = Path.Combine(FixturesDir, fileName);
        return File.ReadAllText(path, Encoding.UTF8);
    }

    /// <summary>可読フィクスチャ（*.esc）を読み、エスケープを実文字へ復元する。</summary>
    public static string LoadEscaped(string fileName)
    {
        var path = Path.Combine(FixturesDir, fileName);
        var text = File.ReadAllText(path, Encoding.UTF8);
        return Unescape(text);
    }

    /// <summary><c>\e \r \n \t \\</c> を実文字へ復元する。末尾に付く改行はフィクスチャの一部とみなさず落とす。</summary>
    public static string Unescape(string text)
    {
        // ファイル末尾の改行（エディタが付ける分）はデータに含めない
        text = text.TrimEnd('\r', '\n');

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\\' || i + 1 >= text.Length)
            {
                sb.Append(c);
                continue;
            }

            char next = text[++i];
            switch (next)
            {
                case 'e': sb.Append('\x1b'); break;
                case 'r': sb.Append('\r'); break;
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case '\\': sb.Append('\\'); break;
                default:
                    // 未知のエスケープはそのまま残す
                    sb.Append('\\');
                    sb.Append(next);
                    break;
            }
        }
        return sb.ToString();
    }
}
