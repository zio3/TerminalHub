using System.Text.RegularExpressions;
using TerminalHub.Helpers;

namespace TerminalHub.Analyzers
{
    /// <summary>
    /// GitHub Copilot CLI の出力を解析するアナライザー。
    /// 出力パターンは実際の動作確認後に調整すること。
    /// </summary>
    public class CopilotCliAnalyzer : IOutputAnalyzer
    {
        // TODO: 実際の出力を確認してスピナー文字を調整
        private static readonly char[] SpinnerCharacters = new[] { '⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏' };

        // TODO: 実際の出力パターンを確認して正規表現を調整
        // 処理中パターン (例: ⠧ Working on task...)
        private static readonly Regex ProcessingPattern = new Regex(
            @"[⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏]\s*(.+)",
            RegexOptions.Compiled);

        // 中断パターン
        // TODO: 実際の出力を確認して調整
        private static readonly Regex InterruptedPattern = new Regex(
            @"\[Request interrupted\]|\[Cancelled\]|Interrupted",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public bool TryAnalyze(string data, out AnalysisResult result)
        {
            result = new AnalysisResult();

            var cleanedData = AnsiHelper.CleanAnsiSequences(data);

            // 中断パターンをチェック
            var interruptMatch = InterruptedPattern.Match(cleanedData);
            if (interruptMatch.Success)
            {
                result.IsInterrupted = true;
                result.IsProcessing = false;
                result.MatchedText = interruptMatch.Value;
                return true;
            }

            // 処理中パターンをチェック
            var processingMatch = ProcessingPattern.Match(cleanedData);
            if (processingMatch.Success)
            {
                result.IsProcessing = true;
                result.StatusText = processingMatch.Groups[1].Value.Trim();
                result.MatchedText = processingMatch.Value;
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
