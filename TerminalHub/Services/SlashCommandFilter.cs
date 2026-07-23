namespace TerminalHub.Services;

/// <summary>
/// スラッシュコマンド補完の絞り込み・並べ替え（純粋ロジック。UI 非依存でテスト可能）。
///
/// MCP を接続していると、Claude Code の slash_commands には MCP ツール由来の
/// <c>mcp__&lt;サーバー名&gt;__&lt;ツール名&gt;</c> が丸ごと載る（ユーザースコープの MCP も含む）。
/// これらは補完から呼び出す機会がほとんど無いのに、素の <c>/mcp</c> を打っただけで
/// 前方一致で大量に混ざり、本来出したい組み込みコマンドを押し出してしまう
/// （候補は上限 8 件で打ち切るため、サーバーが増えるほど実害が出る）。
///
/// そこで MCP ツール由来だけ特別扱いし、**検索語に <c>__</c> が含まれるとき＝
/// 利用者が明示的に MCP ツールを掘りに行ったときだけ**候補に出す。
/// 素の <c>/mcp</c> では組み込みの <c>/mcp</c> だけが出て、<c>/mcp__</c> まで打てば
/// ツール一覧が出る、という住み分けになる（＝到達不能にはしない）。
/// </summary>
public static class SlashCommandFilter
{
    /// <summary>補完ポップアップに出す候補の上限。</summary>
    public const int MaxSuggestions = 8;

    /// <summary>
    /// MCP ツール由来のコマンドか（<c>/mcp__server__tool</c> 形式）。
    /// 組み込みの <c>/mcp</c> は該当しない。
    /// </summary>
    public static bool IsMcpToolCommand(string name)
    {
        var n = name.StartsWith("/") ? name.Substring(1) : name;
        return n.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 検索語（"/" を除いた文字列）で候補を絞り込む。
    /// - 名前（"/" を除く）への部分一致。前方一致を優先（rank 0）、中間一致は後（rank 1）
    /// - 同ランク内は名前順
    /// - MCP ツール由来は検索語に <c>__</c> が無い限り除外する
    /// - 上限 <see cref="MaxSuggestions"/> 件で打ち切る
    /// </summary>
    public static IReadOnlyList<SlashCommandItem> Filter(
        IReadOnlyList<SlashCommandItem> catalog, string query, int max = MaxSuggestions)
    {
        // 利用者が明示的に MCP ツールを指定しに来たかどうか。
        var wantsMcpTools = query.Contains("__", StringComparison.Ordinal);

        var matches = new List<(SlashCommandItem Item, int Rank)>();
        foreach (var item in catalog)
        {
            if (!wantsMcpTools && IsMcpToolCommand(item.Name)) continue;

            var nameNoSlash = item.Name.StartsWith("/") ? item.Name.Substring(1) : item.Name;
            if (query.Length == 0)
            {
                matches.Add((item, 1));
                continue;
            }
            var idx = nameNoSlash.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            matches.Add((item, idx == 0 ? 0 : 1)); // 前方一致=0, 中間一致=1
        }

        return matches
            .OrderBy(m => m.Rank)
            .ThenBy(m => m.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => m.Item)
            .Take(max)
            .ToList();
    }
}
