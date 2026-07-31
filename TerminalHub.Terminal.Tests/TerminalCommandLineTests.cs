using TerminalHub.Services;
using Xunit;

namespace TerminalHub.Terminal.Tests;

public sealed class TerminalCommandLineTests
{
    [Theory]
    [InlineData(null, "/k codex")]
    [InlineData("", "/k codex")]
    [InlineData("--search resume --last", "/k codex --search resume --last")]
    public void KeepOpenは空の引数による余分な空白を作らない(string? arguments, string expected)
    {
        var actual = TerminalCommandLine.KeepOpen("codex", arguments);

        Assert.Equal(("cmd.exe", expected), actual);
    }

    [Fact]
    public void ExecuteQuotedはスペース入りパスと引用符付き引数を二重に保護する()
    {
        var actual = TerminalCommandLine.ExecuteQuoted(
            @"C:\Users\John Smith\claude.cmd",
            @"--settings ""C:\Users\John Smith\hooks.json""");

        Assert.Equal(
            ("cmd.exe", @"/c """"C:\Users\John Smith\claude.cmd"" --settings ""C:\Users\John Smith\hooks.json"""""),
            actual);
    }

    [Fact]
    public void ExecuteQuotedは引数なしなら実行ファイルだけを引用する()
    {
        var actual = TerminalCommandLine.ExecuteQuoted(@"C:\Program Files\claude.exe", null);

        Assert.Equal(("cmd.exe", @"/c ""C:\Program Files\claude.exe"""), actual);
    }

    [Fact]
    public void ResolveShellは空白のカスタムコマンドを既定値へ戻す()
    {
        var actual = TerminalCommandLine.ResolveShell(
            new Dictionary<string, string> { ["command"] = "  " },
            "powershell.exe");

        Assert.Equal(("powershell.exe", string.Empty), actual);
    }

    [Fact]
    public void ResolveShellはカスタムコマンドを採用する()
    {
        var actual = TerminalCommandLine.ResolveShell(
            new Dictionary<string, string> { ["command"] = "pwsh.exe" },
            "powershell.exe");

        Assert.Equal(("pwsh.exe", string.Empty), actual);
    }
}
