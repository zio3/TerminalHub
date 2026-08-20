namespace TerminalHub.Services
{
    /// <summary>
    /// リンクプラグインの書き方（link-plugin-authoring.md）の在り処。
    ///
    /// 正本はリポジトリの docs/ にあり、csproj のリンク参照で出力先の docs/ へ配られる。
    /// CLI の AI に書かせるときは「中身を貼る」より「絶対パスを渡す」ほうが確実なので、
    /// パスをそのまま画面に出せるようにしてある。読み取り専用の扱いで、
    /// ユーザーのフォルダへコピーはしない（勝手に書き込まない・編集を潰さないため）。
    /// </summary>
    public static class LinkPluginDocPath
    {
        /// <summary>存在すればフルパス、無ければ null（ソース構成によっては配られないため）</summary>
        public static string? Get()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "docs", "link-plugin-authoring.md");
            return File.Exists(path) ? path : null;
        }
    }
}
