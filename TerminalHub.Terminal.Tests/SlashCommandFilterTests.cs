using TerminalHub.Services;
using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// <see cref="SlashCommandFilter"/> のテスト。
/// 背景: MCP を接続していると slash_commands に MCP ツール由来の <c>mcp__server__tool</c> が
/// 丸ごと載り、素の <c>/mcp</c> を打っただけで前方一致で混ざって組み込みコマンドを押し出す。
/// 「検索語に <c>__</c> があるときだけ MCP ツールを出す」住み分けを検証する。
/// </summary>
public class SlashCommandFilterTests
{
    private static readonly SlashCommandItem[] Catalog =
    {
        new("/mcp", "Manage MCP server connections"),
        new("/memory", "Edit memory files"),
        new("/mcp__conoha-vps-mcp__create_server", null),
        new("/mcp__conoha-vps-mcp__delete_server", null),
        new("/clear", "Start a new conversation"),
    };

    [Fact]
    public void 素のmcp検索ではMCPツール由来が混ざらない()
    {
        var result = SlashCommandFilter.Filter(Catalog, "mcp");

        Assert.Equal(new[] { "/mcp" }, result.Select(x => x.Name));
    }

    [Fact]
    public void アンダースコア2つを打てばMCPツール由来が出る()
    {
        var result = SlashCommandFilter.Filter(Catalog, "mcp__");

        Assert.Equal(2, result.Count);
        Assert.All(result, x => Assert.StartsWith("/mcp__", x.Name));
    }

    [Fact]
    public void サーバー名側からでもアンダースコア2つがあれば掘れる()
    {
        var result = SlashCommandFilter.Filter(Catalog, "conoha-vps-mcp__");

        Assert.Equal(2, result.Count); // 中間一致
    }

    [Fact]
    public void 検索語が空でもMCPツール由来は出ない()
    {
        var result = SlashCommandFilter.Filter(Catalog, "");

        Assert.DoesNotContain(result, x => SlashCommandFilter.IsMcpToolCommand(x.Name));
        Assert.Equal(3, result.Count); // mcp, memory, clear
    }

    [Fact]
    public void MCPツール名の一部に一致してもアンダースコア無しなら出ない()
    {
        // "create" は /mcp__conoha-vps-mcp__create_server にしか一致しない
        var result = SlashCommandFilter.Filter(Catalog, "create");

        Assert.Empty(result);
    }

    [Fact]
    public void 前方一致が中間一致より先に並ぶ()
    {
        var catalog = new[]
        {
            new SlashCommandItem("/ide", null),      // 中間一致（"de" が途中）
            new SlashCommandItem("/debug", null),    // 前方一致
        };

        var result = SlashCommandFilter.Filter(catalog, "de");

        Assert.Equal(new[] { "/debug", "/ide" }, result.Select(x => x.Name));
    }

    [Fact]
    public void 上限件数で打ち切られる()
    {
        var catalog = Enumerable.Range(0, 20)
            .Select(i => new SlashCommandItem($"/cmd{i:D2}", null))
            .ToArray();

        var result = SlashCommandFilter.Filter(catalog, "cmd");

        Assert.Equal(SlashCommandFilter.MaxSuggestions, result.Count);
    }

    [Theory]
    [InlineData("/mcp", false)]
    [InlineData("/mcp__a__b", true)]
    [InlineData("mcp__a__b", true)]   // "/" 無しでも判定できる
    [InlineData("/MCP__A__B", true)]  // 大文字小文字を無視する
    [InlineData("/memory", false)]
    public void MCPツール判定(string name, bool expected)
    {
        Assert.Equal(expected, SlashCommandFilter.IsMcpToolCommand(name));
    }
}
