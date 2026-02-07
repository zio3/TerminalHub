using System.Text.RegularExpressions;

namespace TerminalHub.Helpers
{
    public static class AnsiHelper
    {
        // 事前コンパイル済み正規表現パターン
        private static readonly Regex CursorForwardRegex = new(@"\x1B\[(\d*)C", RegexOptions.Compiled);
        private static readonly Regex CsiRegex = new(@"\x1B\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);
        private static readonly Regex OscRegex = new(@"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)", RegexOptions.Compiled);
        private static readonly Regex EscTwoCharRegex = new(@"\x1B[^[\]()#;]", RegexOptions.Compiled);
        private static readonly Regex PrivateModeRegex = new(@"\x1B\[\?[0-9;]*[A-Za-z]", RegexOptions.Compiled);
        private static readonly Regex SgrRegex = new(@"\x1B\[[0-9;]*m", RegexOptions.Compiled);
        private static readonly Regex ControlCharRegex = new(@"[\x00-\x08\x0B-\x0C\x0E-\x1F\x7F]", RegexOptions.Compiled);

        public static string CleanAnsiSequences(string data)
        {
            var cleanedData = data;

            // Cursor Forward (CUF) ESC[nC → スペースに変換（CSI汎用パターンの前に実行）
            cleanedData = CursorForwardRegex.Replace(cleanedData, m =>
            {
                var count = string.IsNullOrEmpty(m.Groups[1].Value) ? 1 : int.Parse(m.Groups[1].Value);
                return new string(' ', Math.Min(count, 10));
            });

            // CSI (Control Sequence Introducer) シーケンス（CUFは上で処理済み）
            cleanedData = CsiRegex.Replace(cleanedData, "");

            // OSC (Operating System Command) シーケンス
            cleanedData = OscRegex.Replace(cleanedData, "");

            // その他のESCシーケンス（2文字のシーケンス）
            cleanedData = EscTwoCharRegex.Replace(cleanedData, "");

            // Private Mode シーケンス
            cleanedData = PrivateModeRegex.Replace(cleanedData, "");

            // SGR (Select Graphic Rendition) パラメータを含む複雑なシーケンス
            cleanedData = SgrRegex.Replace(cleanedData, "");

            // 制御文字（改行とタブ以外）
            cleanedData = ControlCharRegex.Replace(cleanedData, " ");

            return cleanedData;
        }
    }
}
