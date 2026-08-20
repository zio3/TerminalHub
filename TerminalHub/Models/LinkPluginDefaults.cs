namespace TerminalHub.Models
{
    /// <summary>
    /// リンクプラグインの既定採用。
    ///
    /// 設定が空（＝一度も設定画面を触っていない既存セッション・新規セッション）のとき、
    /// 同梱プラグインのうちここに挙げたものを「有効」として扱う。
    /// 従来 terminal.js に直書きだった PR 番号（#123）のリンク化はプラグインへ移したため、
    /// 既定で入れておかないと何も設定していないユーザーから機能が消えてしまう。
    ///
    /// 一度でも保存すれば設定は空でなくなるので、そこから先はユーザーの指定が優先される
    /// （全部オフにした状態も保存されて、この既定で復活したりはしない）。
    /// </summary>
    public static class LinkPluginDefaults
    {
        /// <summary>設定が空のときに有効として扱う同梱プラグインの id（この順で登録する）</summary>
        public static readonly string[] EnabledIds = ["github-pr"];

        public static bool IsDefaultEnabled(string id) => Array.IndexOf(EnabledIds, id) >= 0;

        /// <summary>
        /// セッションの保存済み設定から、実際に採用するものを順に返す。
        /// 保存が無ければ既定セットへフォールバックする。
        /// </summary>
        public static IEnumerable<LinkPluginSetting> Resolve(List<LinkPluginSetting> saved)
        {
            if (saved.Count > 0)
            {
                return saved.Where(p => p.Enabled).OrderBy(p => p.Order);
            }

            return EnabledIds.Select((id, i) => new LinkPluginSetting { Id = id, Enabled = true, Order = i });
        }
    }
}
