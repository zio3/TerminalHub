using System.Text.RegularExpressions;

namespace TerminalHub.Analyzers
{
    public class ClaudeCodeAnalyzer : IOutputAnalyzer
    {
        private static readonly Regex ProcessingPattern = new Regex(
            @"[·✽]\s*(?:.*?ing[^(]*)\s*\((\d+)s\s*·\s*([↑↓⚒])\s*([\d.]+[kK]?)\s*tokens?\s*·\s*esc to interrupt\)",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex InterruptedPattern = new Regex(
            @"\[Request interrupted by user\]",
            RegexOptions.Compiled);

        public bool TryAnalyze(string data, out AnalysisResult result)
        {
            result = new AnalysisResult();

            // ANSIエスケープシーケンスと制御文字を除去
            var cleanedData = CleanAnsiSequences(data);

            // 中断パターンをチェック
            if (InterruptedPattern.IsMatch(cleanedData))
            {
                result.IsInterrupted = true;
                result.IsProcessing = false;
                return true;
            }

            // 処理状態パターンをチェック
            var match = ProcessingPattern.Match(cleanedData);
            if (match.Success && match.Groups.Count >= 4)
            {
                result.IsProcessing = true;
                result.ElapsedSeconds = int.TryParse(match.Groups[1].Value, out var seconds) ? seconds : (int?)null;
                result.Direction = match.Groups[2].Value;
                result.Tokens = match.Groups[3].Value;
                return true;
            }

            return false;
        }

        private string CleanAnsiSequences(string data)
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