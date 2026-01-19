using System.Text.RegularExpressions;
using TerminalHub.Helpers;

namespace TerminalHub.Analyzers
{
    public class CodexCliAnalyzer : IOutputAnalyzer
    {
        // スピナー文字（アニメーションパターン）
        private static readonly char[] SpinnerCharacters = new[] { '•' };

        // 処理中パターン
        // 例: • Working (0s • esc to interrupt)
        //     • Running date command (5s • esc to interrupt)
        //     • Exploring alternate access (1m 01s • esc to interrupt)
        private static readonly Regex ProcessingPattern = new Regex(
            @"•\s*(.+?)\s*\((?:\d+m\s+)?\d+s\s*•\s*esc to interrupt\)",
            RegexOptions.Compiled);

        public bool TryAnalyze(string data, out AnalysisResult result)
        {
            result = new AnalysisResult();

            // ANSIエスケープシーケンスと制御文字を除去
            var cleanedData = AnsiHelper.CleanAnsiSequences(data);

            // 処理中パターンをチェック
            var match = ProcessingPattern.Match(cleanedData);
            if (match.Success)
            {
                result.IsProcessing = true;
                result.ProcessingText = match.Groups[1].Value.Trim();
                return true;
            }

            return false;
        }

        /// <summary>
        /// データにスピナー文字（アニメーションパターン）が含まれているかを判定
        /// </summary>
        public bool ContainsAnimationPattern(string data)
        {
            return data.IndexOfAny(SpinnerCharacters) >= 0;
        }
    }
}
