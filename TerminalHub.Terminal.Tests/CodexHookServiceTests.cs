using System.Text;
using System.Text.RegularExpressions;
using TerminalHub.Services;
using Xunit;

namespace TerminalHub.Terminal.Tests;

public sealed class CodexHookServiceTests
{
    [Fact]
    public void BuildHookConfigArguments_起動時設定だけを生成する()
    {
        var actual = new CodexHookService()
            .BuildHookConfigArguments("http://localhost:6123");

        var expectedEvents = new[]
        {
            "Stop",
            "UserPromptSubmit",
            "SubagentStart",
            "SubagentStop",
            "PreCompact",
            "PostCompact",
            "PermissionRequest",
            "PreToolUse"
        };

        foreach (var eventName in expectedEvents)
        {
            Assert.Contains($"-c \"hooks.{eventName}=", actual);
        }

        Assert.Equal(expectedEvents.Length, Regex.Matches(actual, "-c \"hooks\\.").Count);
        Assert.Contains("matcher='^request_user_input$'", actual);
        Assert.DoesNotContain(".codex", actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hooks.json", actual, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildHookConfigArguments_セッションIDは環境変数から取得する()
    {
        var actual = new CodexHookService()
            .BuildHookConfigArguments("https://localhost:6123");
        var encodedCommand = Regex.Match(
            actual,
            @"-EncodedCommand (?<value>[A-Za-z0-9+/=]+)");

        Assert.True(encodedCommand.Success);

        var script = Encoding.Unicode.GetString(
            Convert.FromBase64String(encodedCommand.Groups["value"].Value));

        Assert.Contains("--notify --source codex", script);
        Assert.Contains("--session $env:TERMINALHUB_SESSION_ID", script);
        Assert.Contains("--port 6123", script);
        Assert.DoesNotMatch(
            @"--session\s+[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
            script);
    }

    [Fact]
    public void BuildHookConfigArguments_不正なURLでは既定ポートを使う()
    {
        var actual = new CodexHookService()
            .BuildHookConfigArguments("invalid");
        var encodedCommand = Regex.Match(
            actual,
            @"-EncodedCommand (?<value>[A-Za-z0-9+/=]+)");
        var script = Encoding.Unicode.GetString(
            Convert.FromBase64String(encodedCommand.Groups["value"].Value));

        Assert.Contains("--port 5081", script);
    }
}
