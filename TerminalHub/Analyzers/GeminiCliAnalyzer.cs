using System.Text.RegularExpressions;
using TerminalHub.Helpers;

namespace TerminalHub.Analyzers
{
    public class GeminiCliAnalyzer : IOutputAnalyzer
    {
        // Gemini CLIの処理パターン
        // 例: "⠧ Identifying the File Path (esc to cancel, 7s)"
        // 例: "⠙ Defining Current Status (esc to cancel, 1m 39s)"
        private static readonly Regex ProcessingPattern = new Regex(
            @"[⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏]\s*(.+?)\s*\(esc to cancel,",
            RegexOptions.Compiled);

        // 処理完了パターン（推定）
        // 例: "✓ Completed successfully"
        private static readonly Regex CompletePattern = new Regex(
            @"✓\s*(?:Completed|Done|Finished).*?(?:\((\d+)s\))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // キャンセル/エラーパターン
        // 例: "✗ Cancelled" または "✗ Error: ..."
        private static readonly Regex CancelledPattern = new Regex(
            @"✗\s*(?:Cancelled|Error|Failed|Interrupted)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        
        // ESCキーで中断されたパターン
        private static readonly Regex EscInterruptedPattern = new Regex(
            @"\[Request interrupted\]|\[Cancelled\]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ユーザー入力待ちパターン
        // 例: "⠏ Waiting for user confirmation..."
        private static readonly Regex WaitingForUserPattern = new Regex(
            @"[⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏]\s*Waiting\s+for\s+user\s+(?:confirmation|input|response)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public bool TryAnalyze(string data, out AnalysisResult result)
        {
            result = new AnalysisResult();

            // ANSIエスケープシーケンスと制御文字を除去
            var cleanedData = AnsiHelper.CleanAnsiSequences(data);

            // ESCキーでの中断パターンをチェック
            if (EscInterruptedPattern.IsMatch(cleanedData))
            {
                result.IsInterrupted = true;
                result.IsProcessing = false;
                return true;
            }

            // キャンセル/エラーパターンをチェック
            if (CancelledPattern.IsMatch(cleanedData))
            {
                result.IsInterrupted = true;
                result.IsProcessing = false;
                return true;
            }

            // 完了パターンをチェック
            var completeMatch = CompletePattern.Match(cleanedData);
            if (completeMatch.Success)
            {
                result.IsProcessing = false;
                // 秒数が含まれている場合は抽出
                if (completeMatch.Groups.Count > 1 && !string.IsNullOrEmpty(completeMatch.Groups[1].Value))
                {
                    result.ElapsedSeconds = int.TryParse(completeMatch.Groups[1].Value, out var seconds) ? seconds : (int?)null;
                }
                return true;
            }

            // ユーザー入力待ちパターンをチェック
            if (WaitingForUserPattern.IsMatch(cleanedData))
            {
                result.IsProcessing = true;
                result.IsWaitingForUser = true;
                result.StatusText = "ユーザー確認待ち";
                return true;
            }

            // 処理中パターンをチェック
            var processingMatch = ProcessingPattern.Match(cleanedData);
            if (processingMatch.Success)
            {
                result.IsProcessing = true;
                result.StatusText = processingMatch.Groups[1].Value.Trim();
                return true;
            }

            return false;
        }

    }
}