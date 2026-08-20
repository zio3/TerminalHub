namespace TerminalHub.Models
{
    /// <summary>
    /// セッションごとのリンクプラグイン設定。
    ///
    /// プラグイン本体（.js）はシステムが持ち、「どのセッションで使うか・どの順で効かせるか・
    /// どんな値を渡すか」だけをセッション側が持つ。環境や案件で変わるのは後者だけのため。
    /// </summary>
    public class LinkPluginSetting
    {
        /// <summary>プラグインの id（.js が自己申告する識別子）</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>このセッションで有効にするか</summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// 適用順。小さいほど先に登録され、同じ位置を取り合ったときに勝つ。
        /// xterm のリンクプロバイダは登録順で優先されるため、順序がそのまま優先度になる。
        /// </summary>
        public int Order { get; set; }

        /// <summary>プラグインへ渡す変数（.js の vars 宣言に対応）。URL の組み立て等に使う</summary>
        public Dictionary<string, string> Vars { get; set; } = new();

        /// <summary>
        /// 同じ内容の別インスタンスを作る。セッションのクローンで使う。
        /// Vars も新しい辞書に写す（共有すると片方の編集がもう片方に及ぶため）。
        /// </summary>
        public LinkPluginSetting Clone() => new()
        {
            Id = Id,
            Enabled = Enabled,
            Order = Order,
            Vars = new Dictionary<string, string>(Vars)
        };
    }
}
