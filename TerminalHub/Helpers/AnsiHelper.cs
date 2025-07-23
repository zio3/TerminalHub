using System.Text.RegularExpressions;

namespace TerminalHub.Helpers
{
    public static class AnsiHelper
    {
        public static string CleanAnsiSequences(string data)
        {
            var cleanedData = data;

            // CSI (Control Sequence Introducer) シーケンス
            cleanedData = Regex.Replace(cleanedData, @"\x1B\[[0-9;?]*[A-Za-z]", "", RegexOptions.None);

            // OSC (Operating System Command) シーケンス
            cleanedData = Regex.Replace(cleanedData, @"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)", "", RegexOptions.None);

            // その他のESCシーケンス（2文字のシーケンス）
            cleanedData = Regex.Replace(cleanedData, @"\x1B[^[\]()#;]", "", RegexOptions.None);

            // Private Mode シーケンス
            cleanedData = Regex.Replace(cleanedData, @"\x1B\[\?[0-9;]*[A-Za-z]", "", RegexOptions.None);

            // SGR (Select Graphic Rendition) パラメータを含む複雑なシーケンス
            cleanedData = Regex.Replace(cleanedData, @"\x1B\[[0-9;]*m", "", RegexOptions.None);

            // 制御文字（改行とタブ以外）
            cleanedData = Regex.Replace(cleanedData, @"[\x00-\x08\x0B-\x0C\x0E-\x1F\x7F]", " ", RegexOptions.None);

            return cleanedData;
        }
    }
}