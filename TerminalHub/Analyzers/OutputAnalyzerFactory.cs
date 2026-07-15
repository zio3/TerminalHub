using TerminalHub.Models;

namespace TerminalHub.Analyzers
{
    public interface IOutputAnalyzerFactory
    {
        IOutputAnalyzer? GetAnalyzer(TerminalType terminalType);
    }

    public class OutputAnalyzerFactory : IOutputAnalyzerFactory
    {
        private readonly Dictionary<TerminalType, IOutputAnalyzer> _analyzers;

        public OutputAnalyzerFactory()
        {
            _analyzers = new Dictionary<TerminalType, IOutputAnalyzer>
            {
                { TerminalType.ClaudeCode, new ClaudeCodeAnalyzer() },
                { TerminalType.CodexCLI, new CodexCliAnalyzer() }
                // TerminalType.Terminal には解析器を設定しない
                // TerminalType.GeminiCLI は廃止（起動経路は SessionManager から撤去済み。
                // 既存セッションは通常ターミナルとして起動するため解析器も不要）
            };
        }

        public IOutputAnalyzer? GetAnalyzer(TerminalType terminalType)
        {
            return _analyzers.TryGetValue(terminalType, out var analyzer) ? analyzer : null;
        }
    }
}