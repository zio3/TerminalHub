using TerminalHub.Constants;
using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// Claude Code の起動引数組み立て（TerminalConstants.BuildClaudeCodeArgs）のテスト。
/// 特に --mcp-config は、ConPtyService がコマンドラインを無加工で連結して CreateProcess に
/// 渡す都合上、引用符の付け方を間違えるとスペース入りパスで無言に壊れるため厚めに固定する。
/// </summary>
public sealed class ClaudeArgumentsTests
{
    [Fact]
    public void McpConfigPathを渡さなければオプションは付かない()
    {
        var options = new Dictionary<string, string>();

        Assert.Equal("", TerminalConstants.BuildClaudeCodeArgs(options));
        Assert.Equal("", TerminalConstants.BuildClaudeCodeArgs(options, null));
        Assert.Equal("", TerminalConstants.BuildClaudeCodeArgs(options, "   "));
    }

    [Fact]
    public void McpConfigPathは引用符で囲んで付与される()
    {
        var options = new Dictionary<string, string>();

        Assert.Equal(
            "--mcp-config \"C:\\Users\\info\\AppData\\Local\\TerminalHub\\mcp-config-5080.json\"",
            TerminalConstants.BuildClaudeCodeArgs(options, @"C:\Users\info\AppData\Local\TerminalHub\mcp-config-5080.json"));
    }

    [Fact]
    public void スペースを含むパスでも引用符で保護される()
    {
        // %LOCALAPPDATA% にスペースを含むユーザー名の環境。引用符が無いと claude 側で
        // 空白ごとに別ファイル扱いされ "MCP config file not found" になる（実測で確認済み）。
        var options = new Dictionary<string, string>();

        Assert.Equal(
            "--mcp-config \"C:\\Users\\John Smith\\AppData\\Local\\TerminalHub\\mcp-config-5080.json\"",
            TerminalConstants.BuildClaudeCodeArgs(options, @"C:\Users\John Smith\AppData\Local\TerminalHub\mcp-config-5080.json"));
    }

    [Fact]
    public void McpConfigはユーザー指定の引数より前に置かれる()
    {
        // --mcp-config は複数指定でマージされる。ユーザーが自分で書いた指定を後ろに置くことで、
        // 競合したときにユーザー側の意図が勝つ並びにしておく。
        var options = new Dictionary<string, string>
        {
            ["permission-mode"] = "auto",
            ["continue"] = "true",
            ["extra-args"] = "--verbose"
        };

        Assert.Equal(
            "--permission-mode auto --continue --mcp-config \"C:\\th\\mcp-config-5080.json\" --verbose",
            TerminalConstants.BuildClaudeCodeArgs(options, @"C:\th\mcp-config-5080.json"));
    }

    [Fact]
    public void 既存オプションの組み立ては従来どおり()
    {
        // --mcp-config 追加による退行が無いことの確認。
        var options = new Dictionary<string, string>
        {
            ["permission-mode"] = "bypass",
            ["chrome"] = "true"
        };

        Assert.Equal(
            "--dangerously-skip-permissions --chrome",
            TerminalConstants.BuildClaudeCodeArgs(options));
    }
}
