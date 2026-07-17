namespace TerminalHub.Services;

/// <summary>
/// スラッシュコマンド1件（補完候補）。名前は先頭の "/" を含む。
/// </summary>
public record SlashCommandItem(string Name, string? Description);

/// <summary>
/// 動的取得した名前一覧と静的辞書のマージ（純粋ロジック。UI・プロセス起動に非依存でテスト可能）。
///
/// headless の init に載るのは「headless で使えるコマンド」だけで、対話 TUI 専用の
/// 組み込み（/resume・/rewind・/memory 等）は含まれない（claude v2.1.212 で実測）。
/// そのため候補は「動的リスト ∪ 静的辞書」の和集合を取る。
/// 説明文は init に含まれないので、静的辞書の説明を名前一致（大文字小文字無視）で付与する。
/// </summary>
public static class SlashCommandMerge
{
    /// <summary>
    /// 動的に取得した名前（"/" 有無どちらも可）と静的辞書項目の和集合を返す。
    /// - 動的リストの並びを基に、静的辞書にしか無い名前を追補して名前順にソート
    /// - 重複は大文字小文字無視で除去（動的側優先）
    /// - 説明は静的辞書から名前一致で付与（無ければ null）
    /// </summary>
    public static IReadOnlyList<SlashCommandItem> Merge(
        IReadOnlyList<string> dynamicNames, IReadOnlyList<SlashCommandItem> catalog)
    {
        // 「名前（"/" 無し）→説明」の辞書（説明の付与用）
        var descByName = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in catalog)
        {
            var key = item.Name.StartsWith("/") ? item.Name.Substring(1) : item.Name;
            descByName[key] = item.Description;
        }

        var items = new List<SlashCommandItem>(dynamicNames.Count + catalog.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in dynamicNames)
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            var display = name.StartsWith("/") ? name : "/" + name;
            if (!seen.Add(display)) continue; // 重複除去
            descByName.TryGetValue(name.TrimStart('/'), out var desc);
            items.Add(new SlashCommandItem(display, desc));
        }

        // 静的辞書にしか無い名前（対話専用組み込み等）を追補する
        foreach (var item in catalog)
        {
            if (seen.Add(item.Name))
                items.Add(item);
        }

        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return items;
    }
}
