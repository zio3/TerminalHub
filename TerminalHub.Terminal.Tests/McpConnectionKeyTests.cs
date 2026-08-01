using System.Text.Json;
using TerminalHub.Constants;
using TerminalHub.Services;
using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// MCP 接続キー（X-TerminalHub-Session-Key）の注入まわりの検証。
/// キーは「TerminalHub が配る設定だけが知る秘密」なので、
/// - 自前の terminalhub 定義がある起動には注入しない（別インスタンスへ秘密を送らない）
/// - URL とキーは必ずセットで出る（キー単独・URL 単独の中途半端な設定を作らない）
/// が守られていることを固定する。
/// </summary>
public sealed class McpConnectionKeyTests
{
    [Fact]
    public void BuildCodexArgs_UrlとキーをセットでHttpHeadersに注入する()
    {
        var actual = TerminalConstants.BuildCodexArgs(
            new Dictionary<string, string>(),
            terminalHubMcpUrl: "http://localhost:5081/mcp",
            terminalHubMcpConnectionKey: "abc123");

        Assert.Contains("-c mcp_servers.terminalhub.url=http://localhost:5081/mcp", actual);
        Assert.Contains($"-c mcp_servers.terminalhub.http_headers.{TerminalConstants.McpSessionKeyHeader}=abc123", actual);
    }

    [Fact]
    public void BuildCodexArgs_Url無しではキーも注入しない()
    {
        var actual = TerminalConstants.BuildCodexArgs(
            new Dictionary<string, string>(),
            terminalHubMcpUrl: null,
            terminalHubMcpConnectionKey: "abc123");

        Assert.DoesNotContain("http_headers", actual);
        Assert.DoesNotContain("abc123", actual);
    }

    [Fact]
    public void BuildCodexArgs_ユーザーがUrlを自前定義していたらキーも注入しない()
    {
        // 自前定義の接続先は別インスタンスの可能性があり、そこへこのインスタンスの秘密を送らない。
        var actual = TerminalConstants.BuildCodexArgs(
            new Dictionary<string, string>
            {
                ["extra-args"] = "-c mcp_servers.terminalhub.url=http://localhost:9999/mcp"
            },
            terminalHubMcpUrl: "http://localhost:5081/mcp",
            terminalHubMcpConnectionKey: "abc123");

        Assert.DoesNotContain("5081", actual);
        Assert.DoesNotContain("abc123", actual);
    }

    [Fact]
    public void BuildCodexArgs_ユーザーがヘッダ表ごと自前定義していたらキーを注入しない()
    {
        // http_headers= はインラインテーブルの全体代入。後からドット記法で足すと衝突するので触らない。
        var actual = TerminalConstants.BuildCodexArgs(
            new Dictionary<string, string>
            {
                ["custom-args"] = "-c mcp_servers.terminalhub.http_headers={\"X-Custom\"=\"v\"}"
            },
            terminalHubMcpUrl: "http://localhost:5081/mcp",
            terminalHubMcpConnectionKey: "abc123");

        Assert.Contains("-c mcp_servers.terminalhub.url=http://localhost:5081/mcp", actual);
        Assert.DoesNotContain("abc123", actual);
    }

    [Fact]
    public void BuildCodexArgs_ユーザーが同じキー名を自前定義していたらキーを注入しない()
    {
        var actual = TerminalConstants.BuildCodexArgs(
            new Dictionary<string, string>
            {
                ["extra-args"] = $"-c mcp_servers.terminalhub.http_headers.{TerminalConstants.McpSessionKeyHeader}=user"
            },
            terminalHubMcpUrl: "http://localhost:5081/mcp",
            terminalHubMcpConnectionKey: "abc123");

        Assert.DoesNotContain("abc123", actual);
    }

    [Fact]
    public void BuildCodexArgs_ユーザーがterminalhub定義を全体代入していたらURLもキーも注入しない()
    {
        // ドット記法(url=)ではなくインラインテーブルの全体代入でも自前定義は成立する。
        // 見落とすと、こちらの URL＋秘密キーの後にユーザーの代入が重なり、設定衝突や
        // 「自前 URL に秘密ヘッダーが混ざる」事故になる(レビュー指摘)。
        var actual = TerminalConstants.BuildCodexArgs(
            new Dictionary<string, string>
            {
                ["extra-args"] = "-c mcp_servers.terminalhub={url=\"http://localhost:9999/mcp\"}"
            },
            terminalHubMcpUrl: "http://localhost:5081/mcp",
            terminalHubMcpConnectionKey: "abc123");

        Assert.DoesNotContain("5081", actual);
        Assert.DoesNotContain("abc123", actual);
    }

    [Fact]
    public void BuildCodexArgs_ユーザーがMcpServersごと全体代入していたらURLもキーも注入しない()
    {
        var actual = TerminalConstants.BuildCodexArgs(
            new Dictionary<string, string>
            {
                ["custom-args"] = "-c mcp_servers={terminalhub={url=\"http://localhost:9999/mcp\"}}"
            },
            terminalHubMcpUrl: "http://localhost:5081/mcp",
            terminalHubMcpConnectionKey: "abc123");

        Assert.DoesNotContain("5081", actual);
        Assert.DoesNotContain("abc123", actual);
    }

    [Fact]
    public void BuildCodexArgs_ユーザーが別名ヘッダを足しているだけならキーは注入する()
    {
        // 別のヘッダ（X-Custom 等）の追加はドット記法同士でマージされ衝突しないので、注入を止めない。
        var actual = TerminalConstants.BuildCodexArgs(
            new Dictionary<string, string>
            {
                ["custom-args"] = "-c mcp_servers.terminalhub.http_headers.X-Custom=v"
            },
            terminalHubMcpUrl: "http://localhost:5081/mcp",
            terminalHubMcpConnectionKey: "abc123");

        Assert.Contains($"-c mcp_servers.terminalhub.http_headers.{TerminalConstants.McpSessionKeyHeader}=abc123", actual);
    }

    [Fact]
    public void BuildClaudeMcpConfigJson_HeadersにキーがのったHttpサーバー定義になる()
    {
        var json = McpConfigService.BuildClaudeMcpConfigJson("http://localhost:5081/mcp", "abc123");

        using var doc = JsonDocument.Parse(json);
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("terminalhub");
        Assert.Equal("http", server.GetProperty("type").GetString());
        Assert.Equal("http://localhost:5081/mcp", server.GetProperty("url").GetString());
        Assert.Equal("abc123",
            server.GetProperty("headers").GetProperty(TerminalConstants.McpSessionKeyHeader).GetString());
    }
}
