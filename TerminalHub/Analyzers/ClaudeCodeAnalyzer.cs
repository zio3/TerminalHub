using System.Text.RegularExpressions;
using TerminalHub.Helpers;

namespace TerminalHub.Analyzers
{
    public class ClaudeCodeAnalyzer : IOutputAnalyzer
    {
        // 旧形式: · Concocting… (7s · ↓ 100 tokens · esc to interrupt)
        private static readonly Regex ProcessingPatternOld = new Regex(
            @"[·✽]\s*(?:[^\r\n()]*?ing[^\r\n()]*)\s*\((\d+)s\s*·\s*([↑↓⚒])\s*([\d.]+[kK]?)\s*tokens?\s*·\s*esc to interrupt\)",
            RegexOptions.Compiled);

        // 新形式1: ✶ Program.csでサービスを登録中… (esc to interrupt · ctrl+t to show todos)
        // 新形式2: ✶ Spellbinding… (esc to interrupt)
        // アニメーション記号: ✶ ✽ ✻ ✼ ✴ ✵ ✷ ✸ ✹ 等が使われる可能性
        private static readonly Regex ProcessingPatternNew = new Regex(
            @"[✶✽✻✼✴✵✷✸✹·⋆*]\s*([^\r\n()]+?)\s*\(esc to interrupt(?:\s*·\s*ctrl\+t to show todos)?\)",
            RegexOptions.Compiled);

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

            // 新形式の処理状態パターンをチェック
            var matchNew = ProcessingPatternNew.Match(cleanedData);
            if (matchNew.Success)
            {
                result.IsProcessing = true;
                result.ProcessingText = matchNew.Groups[1].Value.Trim();
                // 新形式では秒数とトークン数は含まれない
                result.ElapsedSeconds = null;
                result.Direction = null;
                result.Tokens = null;
                return true;
            }

            // 旧形式の処理状態パターンをチェック（後方互換性のため）
            var matchOld = ProcessingPatternOld.Match(cleanedData);
            if (matchOld.Success && matchOld.Groups.Count >= 4)
            {
                result.IsProcessing = true;
                result.ElapsedSeconds = int.TryParse(matchOld.Groups[1].Value, out var seconds) ? seconds : (int?)null;
                result.Direction = matchOld.Groups[2].Value;
                result.Tokens = matchOld.Groups[3].Value;
                return true;
            }

            return false;
        }

    }
}