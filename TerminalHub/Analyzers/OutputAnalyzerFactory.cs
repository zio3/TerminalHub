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
                { TerminalType.GeminiCLI, new GeminiCliAnalyzer() }
                // TerminalType.Terminal には解析器を設定しない
            };
        }

        public IOutputAnalyzer? GetAnalyzer(TerminalType terminalType)
        {
            return _analyzers.TryGetValue(terminalType, out var analyzer) ? analyzer : null;
        }
    }
}