namespace TerminalHub.Models
{
    /// <summary>
    /// ブラウザ(デバイス)ごとの表示設定。LocalStorage に保存する。
    /// PC とモバイルで別々の表示設定を持てるようにするため、
    /// app-settings.json (サーバー側・全デバイス共通) から分離した。
    ///
    /// 各プロパティは nullable で「未設定」を表し、未設定の項目は
    /// app-settings.json の値 (サーバーデフォルト) にフォールバックする。
    /// なおテーマはデバイス別にする需要がないため対象外 (app-settings 共通のまま)。
    /// </summary>
    public class LocalDisplaySettings
    {
        public double? SessionListScale { get; set; }
        public int? TerminalFontSize { get; set; }
        public int? SidebarWidthPercent { get; set; }
        public int? TerminalHeightPercent { get; set; }
    }
}
