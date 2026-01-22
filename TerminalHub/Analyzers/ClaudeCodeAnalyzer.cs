using System.Text.RegularExpressions;
using TerminalHub.Helpers;

namespace TerminalHub.Analyzers
{
    public class ClaudeCodeAnalyzer : IOutputAnalyzer
    {
        // スピナー文字（アニメーションパターン）
        // ジッター対策により部分的な更新（スピナー文字のみ）が送られることがある
        private static readonly char[] SpinnerCharacters = new[] { '✶', '✽', '✻', '✼', '✴', '✵', '✷', '✸', '✹', '·', '⋆', '*', '✢' };

        // 処理中パターン（完全形）
        // 例: ✶ Spellbinding… (esc to interrupt)
        //     ✢ Actualizing… (esc to interrupt · thinking)
        //     ✢ Pondering… (esc to interrupt · thought for 3s)
        //     * Honking… (ctrl+c to interrupt · 39s · ↓ 941 tokens · thought for 16s)
        //     · Jitterbugging… (ctrl+c to interrupt)
        private static readonly Regex ProcessingPatternFull = new Regex(
            @"[✶✽✻✼✴✵✷✸✹·⋆*✢]\s*([^\r\n()]+?)\s*\((?:esc|ctrl\+c) to interrupt(?:\s*·\s*[^)]+)?\)",
            RegexOptions.Compiled);

        // 処理中パターン（簡易形）- ジッター対策で部分更新が来る場合用
        // スピナー文字 + ステータステキスト（…で終わる）のみでマッチ
        // 例: ✢ Boondoggling…
        //     * Harmonizing…
        private static readonly Regex ProcessingPatternSimple = new Regex(
            @"[✶✽✻✼✴✵✷✸✹·⋆*✢]\s*(\S+…)",
            RegexOptions.Compiled);

        // 中断パターン
        // 旧: [Request interrupted by user]
        // 新: Interrupted · What should Claude do instead?
        private static readonly Regex InterruptedPattern = new Regex(
            @"\[Request interrupted by user\]|Interrupted\s*·\s*What should Claude do",
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

            // 処理中パターンをチェック（完全形を優先）
            var match = ProcessingPatternFull.Match(cleanedData);
            if (match.Success)
            {
                result.IsProcessing = true;
                result.ProcessingText = match.Groups[1].Value.Trim();
                return true;
            }

            // 完全形にマッチしない場合、簡易形を試す（ジッター対策の部分更新用）
            var simpleMatch = ProcessingPatternSimple.Match(cleanedData);
            if (simpleMatch.Success)
            {
                var capturedText = simpleMatch.Groups[1].Value.Trim();

                // アニメーション更新による断片的なマッチを除外
                // 6文字以上（…を含む）
                // "g…"(2), "in…"(3), "kig…"(4), "king…"(5) → フィルタ
                // "sking…"(6), "Whisking…"(9) → OK
                if (capturedText.Length < 6)
                {
                    return false;
                }

                result.IsProcessing = true;
                result.ProcessingText = capturedText;
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