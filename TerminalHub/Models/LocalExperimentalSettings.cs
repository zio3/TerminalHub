namespace TerminalHub.Models
{
    /// <summary>
    /// ブラウザ(デバイス)ごとの試験機能・デバイス依存設定。LocalStorage に保存する。
    /// PC とモバイルで別々に持てるようにするため app-settings.json (全デバイス共通) から分離した。
    /// デバイス別に保存されるので、各デバイスでユーザーが任意に ON/OFF すればよい
    /// (例: 音声入力は PC では ON、モバイルでは OS 側の音声入力を使うため OFF)。
    ///
    /// nullable の項目は「未設定」を表し、未設定なら app-settings.json の値へフォールバックする。
    /// </summary>
    public class LocalExperimentalSettings
    {
        /// <summary>音声入力の有効/無効(デバイス別)。未設定なら app-settings の Special.VoiceInputEnabled にフォールバック。</summary>
        public bool? VoiceInputEnabled { get; set; }

        /// <summary>セッションが入力待ち(許可/選択)になった時、既存のカーソルパッドタブへ自動で切り替える(デバイス別)。</summary>
        public bool AutoSwitchToCursorPadOnWait { get; set; } = false;
    }
}
