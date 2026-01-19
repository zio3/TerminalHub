using TerminalHub.Models;

namespace TerminalHub.Analyzers
{
    public interface IOutputAnalyzer
    {
        /// <summary>
        /// 出力データを解析し、処理状態の更新が必要な場合はtrueを返す
        /// </summary>
        /// <param name="data">ターミナルからの出力データ</param>
        /// <param name="result">解析結果</param>
        /// <returns>処理状態の更新が必要な場合はtrue</returns>
        bool TryAnalyze(string data, out AnalysisResult result);

        /// <summary>
        /// データにアニメーションパターン（スピナー文字等）が含まれているかを判定
        /// ジッター対策で部分的な更新が送られる場合のタイムアウト延長に使用
        /// </summary>
        /// <param name="data">ターミナルからの出力データ</param>
        /// <returns>アニメーションパターンが含まれる場合はtrue</returns>
        bool ContainsAnimationPattern(string data);
    }

    public class AnalysisResult
    {
        public bool IsProcessing { get; set; }
        public bool IsInterrupted { get; set; }
        public bool IsWaitingForUser { get; set; }
        public int? ElapsedSeconds { get; set; }      // GeminiCLI用
        public string? StatusText { get; set; }       // GeminiCLI用
        public string? ProcessingText { get; set; }   // ClaudeCode用
    }
}