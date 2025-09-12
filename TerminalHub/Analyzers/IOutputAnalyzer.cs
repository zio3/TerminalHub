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
    }

    public class AnalysisResult
    {
        public bool IsProcessing { get; set; }
        public int? ElapsedSeconds { get; set; }
        public string? Tokens { get; set; }
        public string? Direction { get; set; }
        public string? StatusText { get; set; }
        public string? ProcessingText { get; set; }  // 新形式で処理中の内容
        public bool IsInterrupted { get; set; }
        public bool IsWaitingForUser { get; set; }
    }
}