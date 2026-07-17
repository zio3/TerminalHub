using TerminalHub.Services;
using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// <see cref="SlashCommandMerge.Merge"/> のテスト。
/// 背景: headless の init が返す slash_commands には対話 TUI 専用の組み込み
/// （/resume・/rewind 等）が含まれない（claude v2.1.212 実測）ため、
/// 「動的リスト ∪ 静的辞書」の和集合になっていることを検証する。
/// </summary>
public class SlashCommandMergeTests
{
    private static readonly SlashCommandItem[] Catalog =
    {
        new("/resume", "Resume a previous conversation"),
        new("/review", "Fast single-pass PR review"),
        new("/clear", "Start a new conversation with empty context"),
    };

    [Fact]
    public void 静的辞書にしか無いコマンドが追補される()
    {
        // 動的リストに /resume が無い（headless init の実挙動）
        var dynamicNames = new[] { "review", "recap", "clear" };

        var merged = SlashCommandMerge.Merge(dynamicNames, Catalog);

        Assert.Contains(merged, x => x.Name == "/resume"); // 静的辞書から追補
        Assert.Contains(merged, x => x.Name == "/recap");  // 動的のみの新顔も残る
        Assert.Equal(4, merged.Count); // resume, review, recap, clear
    }

    [Fact]
    public void 動的リストの名前に静的辞書の説明が付与される()
    {
        var merged = SlashCommandMerge.Merge(new[] { "review", "recap" }, Catalog);

        Assert.Equal("Fast single-pass PR review",
            Assert.Single(merged, x => x.Name == "/review").Description);
        Assert.Null(Assert.Single(merged, x => x.Name == "/recap").Description); // 辞書に無い→説明なし
    }

    [Fact]
    public void 重複は大文字小文字無視で除去される()
    {
        var merged = SlashCommandMerge.Merge(new[] { "Clear", "/clear", "clear" }, Catalog);

        Assert.Single(merged, x => string.Equals(x.Name, "/clear", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void スラッシュ有無どちらの入力でも表示名はスラッシュ付きになる()
    {
        var merged = SlashCommandMerge.Merge(new[] { "recap", "/reload-skills" }, System.Array.Empty<SlashCommandItem>());

        Assert.All(merged, x => Assert.StartsWith("/", x.Name));
        Assert.Contains(merged, x => x.Name == "/reload-skills");
    }

    [Fact]
    public void 名前順にソートされる()
    {
        var merged = SlashCommandMerge.Merge(new[] { "zeta", "alpha" }, Catalog);

        var names = merged.Select(x => x.Name).ToArray();
        var sorted = names.OrderBy(x => x, System.StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(sorted, names);
    }

    [Fact]
    public void 空白や空文字の動的名は無視される()
    {
        var merged = SlashCommandMerge.Merge(new[] { "", "  ", "recap" }, System.Array.Empty<SlashCommandItem>());

        Assert.Single(merged);
        Assert.Equal("/recap", merged[0].Name);
    }

    [Fact]
    public void 動的リストが空でも静的辞書全体が返る()
    {
        var merged = SlashCommandMerge.Merge(System.Array.Empty<string>(), Catalog);

        Assert.Equal(Catalog.Length, merged.Count);
        Assert.Contains(merged, x => x.Name == "/resume");
    }
}
