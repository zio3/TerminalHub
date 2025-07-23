using System.Text.RegularExpressions;
using TerminalHub.Helpers;

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
            var cleanedData = AnsiHelper.CleanAnsiSequences(data);

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

    }
}