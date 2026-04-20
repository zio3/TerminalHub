namespace TerminalHub.Services
{
    /// <summary>
    /// アプリのユーザーデータ (DB / ログ / 設定ファイル) の保存先パスを一元解決する。
    /// 実行ファイル配下ではなく %LOCALAPPDATA%\TerminalHub\ を使うことで、
    /// 全ユーザー向けインストール (C:\Program Files 配下) でも書き込み権限の問題を避ける。
    /// dev/prod は IsDevelopment フラグで既定値を切り替え、configuration からの上書きも受け付ける。
    /// </summary>
    public static class AppDataPaths
    {
        /// <summary>
        /// %LOCALAPPDATA%\TerminalHub\ を返す。型初期化時にディレクトリ存在を保証する。
        /// </summary>
        public static string UserDataRoot { get; } = InitializeUserDataRoot();

        private static string InitializeUserDataRoot()
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TerminalHub");
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>
        /// ログ出力先フォルダのフルパス。dev では "logs-dev"、prod では "logs"。
        /// configOverride が指定されていればそちらを優先する (Logging:FolderName 等)。
        /// </summary>
        public static string GetLogsFolder(bool isDevelopment, string? configOverride = null)
        {
            var folderName = !string.IsNullOrWhiteSpace(configOverride)
                ? configOverride
                : (isDevelopment ? "logs-dev" : "logs");
            return Path.Combine(UserDataRoot, folderName);
        }

        /// <summary>
        /// app-settings.json のフルパス。dev では "app-settings-dev.json"、prod では "app-settings.json"。
        /// configOverride が指定されていればそちらを優先する (AppSettings:FileName 等)。
        /// </summary>
        public static string GetAppSettingsFilePath(bool isDevelopment, string? configOverride = null)
        {
            var fileName = !string.IsNullOrWhiteSpace(configOverride)
                ? configOverride
                : (isDevelopment ? "app-settings-dev.json" : "app-settings.json");
            return Path.Combine(UserDataRoot, fileName);
        }
    }
}
