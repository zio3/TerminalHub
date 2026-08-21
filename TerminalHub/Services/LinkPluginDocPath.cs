namespace TerminalHub.Services
{
    /// <summary>
    /// リンクプラグインの書き方（link-plugin-authoring.md）の在り処と、
    /// プラグインフォルダへの配置。
    ///
    /// 正本はリポジトリの docs/ にあり、csproj のリンク参照で出力先の docs/ へ配られる。
    /// さらに起動時、プラグインフォルダにも同じ内容を1枚置く。プラグインを書く作業は
    /// 「そのフォルダで Claude Code や Codex を動かす」のが実際の形になるため、
    /// 仕様がフォルダの中にあれば「これを読んで作って」で済む。
    /// </summary>
    public static class LinkPluginDocPath
    {
        /// <summary>プラグインフォルダへ置くときのファイル名（.js ではないのでローダーは無視する）</summary>
        public const string PluginsFolderFileName = "create-link-plugin.md";

        /// <summary>配布された仕様書のフルパス。無ければ null（ソース構成によっては配られないため）</summary>
        public static string? Get()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "docs", "link-plugin-authoring.md");
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// プラグインフォルダに仕様書を置く（既にあり内容が同じなら何もしない）。
        ///
        /// このファイルは TerminalHub が管理するものとして毎回更新する。ユーザーが書いた
        /// プラグイン（.js）には触らないが、この .md だけは版が古いと嘘の仕様を
        /// AI に読ませることになるため、更新のほうを優先する。
        /// </summary>
        public static void EnsureInPluginsFolder(ILogger? logger = null)
        {
            var source = Get();
            if (source == null) return;

            try
            {
                var target = Path.Combine(AppDataPaths.GetPluginsFolder(), PluginsFolderFileName);
                var body = File.ReadAllText(source);
                if (File.Exists(target) && File.ReadAllText(target) == body) return;

                File.WriteAllText(target, body);
                logger?.LogInformation("[LinkPlugins] 仕様書を配置: {Path}", target);
            }
            catch (Exception ex)
            {
                // 置けなくても本体の動作には影響しない（設定画面の「書き方を開く」は別経路）
                logger?.LogWarning(ex, "[LinkPlugins] 仕様書の配置に失敗");
            }
        }
    }
}
