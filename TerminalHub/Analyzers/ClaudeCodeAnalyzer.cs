using System.Text.RegularExpressions;
using TerminalHub.Helpers;

namespace TerminalHub.Analyzers
{
    public class ClaudeCodeAnalyzer : IOutputAnalyzer
    {
        // スピナー文字（アニメーションパターン）
        // ジッター対策により部分的な更新（スピナー文字のみ）が送られることがある
        // 注意: '·' (middle dot) はステータス行の区切り文字としても使われるため、スピナーから除外
        private static readonly char[] SpinnerCharacters = new[] { '✶', '✽', '✻', '✼', '✴', '✵', '✷', '✸', '✹', '⋆', '*', '✢' };

        // 処理中パターン（完全形）
        // 例: ✶ Spellbinding… (esc to interrupt)
        //     ✢ Actualizing… (esc to interrupt · thinking)
        //     ✢ Pondering… (esc to interrupt · thought for 3s)
        //     * Honking… (ctrl+c to interrupt · 39s · ↓ 941 tokens · thought for 16s)
        //     · Jitterbugging… (ctrl+c to interrupt)
        //     ✻ Docker ビルド & テスト中… (1m 24s · ↓ 0 tokens)
        //     ✻ Docker ビルド & テスト中… (running stop hook)
        private static readonly Regex ProcessingPatternFull = new Regex(
            @"[✶✽✻✼✴✵✷✸✹⋆*✢]\s*([^\r\n()]+?)\s*\((?:(?:esc|ctrl\+c) to interrupt(?:\s*·\s*[^)]+)?|[^)]+)\)",
            RegexOptions.Compiled);

        // 処理中パターン（簡易形）- ジッター対策で部分更新が来る場合用
        // スピナー文字 + ステータステキスト（…で終わる）でマッチ
        // 例: ✢ Boondoggling…
        //     * Harmonizing…
        //     ✶ Docker ビルド & テスト中…  (タスク名の場合は複数単語)
        private static readonly Regex ProcessingPatternSimple = new Regex(
            @"[✶✽✻✼✴✵✷✸✹⋆*✢]\s*(.+?…)",
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
            var interruptMatch = InterruptedPattern.Match(cleanedData);
            if (interruptMatch.Success)
            {
                result.IsInterrupted = true;
                result.IsProcessing = false;
                result.MatchedText = interruptMatch.Value;
                return true;
            }

            // スピナーアニメーション中のカーソル位置上書きがANSI除去で壊れたテキストを除外
            // 正常: "✻ Compacting conversation…" → スピナー1個
            // 異常: "✻✶*✢· CoC mpo  a✢ m cp t..." → スピナー複数（上書きフレームが連結）
            if (CountSpinnerCharacters(cleanedData) >= 2)
            {
                return false;
            }

            // 処理中パターンをチェック（完全形を優先）
            var match = ProcessingPatternFull.Match(cleanedData);
            if (match.Success)
            {
                var capturedText = match.Groups[1].Value.Trim();

                // チャンク分割による断片的なマッチを除外
                // 正規のステータステキストは必ず「…」を含み、文字で始まる
                if (!capturedText.Contains('…') || char.IsDigit(capturedText[0]))
                {
                    return false;
                }

                // ANSIクリーン後にカーソル前進がスペース化され、レイアウト情報が混入するケースに対応
                // 例: "Bloviating…          402        s…" → "Bloviating…"
                // 正規のステータスは最初の「…」で終わるため、それ以降を除去
                var ellipsisIndex = capturedText.IndexOf('…');
                if (ellipsisIndex >= 0 && ellipsisIndex < capturedText.Length - 1)
                {
                    capturedText = capturedText.Substring(0, ellipsisIndex + 1);
                }

                result.IsProcessing = true;
                result.ProcessingText = capturedText;
                result.MatchedText = match.Value;
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

                // ステータステキストは必ず文字で始まる（"Wandering…", "Docker ビルド…" 等）
                // "34.8k tokensWandering…" のように数字で始まるものは、
                // ステータス行内の区切り「·」がスピナーとして誤マッチしたケース
                if (char.IsDigit(capturedText[0]))
                {
                    return false;
                }

                result.IsProcessing = true;
                result.ProcessingText = capturedText;
                result.MatchedText = simpleMatch.Value;
                return true;
            }

            return false;
        }

        private static int CountSpinnerCharacters(string data)
        {
            var count = 0;
            foreach (var c in data)
            {
                if (Array.IndexOf(SpinnerCharacters, c) >= 0)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// データにスピナー文字（アニメーションパターン）が含まれているかを判定
        /// Synchronized Outputシーケンスも処理中の画面更新として検出
        /// </summary>
        public bool ContainsAnimationPattern(string data)
        {
            if (data.IndexOfAny(SpinnerCharacters) >= 0)
                return true;

            // Synchronized Output（処理中の画面更新に頻出）
            if (data.Contains("\x1B[?2026h") || data.Contains("\x1B[?2026l"))
                return true;

            return false;
        }
    }
}