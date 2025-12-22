using System.Text.RegularExpressions;
using TerminalHub.Helpers;

namespace TerminalHub.Analyzers
{
    public class ClaudeCodeAnalyzer : IOutputAnalyzer
    {
        // 処理中パターン
        // 例: ✶ Spellbinding… (esc to interrupt)
        //     ✢ Actualizing… (esc to interrupt · thinking)
        //     ✢ Pondering… (esc to interrupt · thought for 3s)
        // アニメーション記号: ✶ ✽ ✻ ✼ ✴ ✵ ✷ ✸ ✹ ✢ · ⋆ * 等
        private static readonly Regex ProcessingPattern = new Regex(
            @"[✶✽✻✼✴✵✷✸✹·⋆*✢]\s*([^\r\n()]+?)\s*\(esc to interrupt(?:\s*·\s*[^)]+)?\)",
            RegexOptions.Compiled);

        // 中断パターン
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
    }
}