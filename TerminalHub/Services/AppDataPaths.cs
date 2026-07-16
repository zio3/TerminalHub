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

        /// <summary>
        /// SQLite DB のフルパス。dev では "sessions-dev.db"、prod では "sessions.db"。
        /// configOverride が指定されていればそちらを優先する (Database:FileName 等)。
        ///
        /// logs / app-settings と同じく IsDevelopment で既定を切り替えることで、
        /// appsettings.Development.json (gitignore 対象) が無い環境 (別 worktree / 新規クローン) で
        /// Development 実行しても本番 DB (sessions.db) を触らない。設定ファイルの有無に DB 安全性を依存させない。
        /// </summary>
        public static string GetDatabaseFilePath(bool isDevelopment, string? configOverride = null)
        {
            var fileName = !string.IsNullOrWhiteSpace(configOverride)
                ? configOverride
                : (isDevelopment ? "sessions-dev.db" : "sessions.db");
            return Path.Combine(UserDataRoot, fileName);
        }

        /// <summary>
        /// Claude Code へ --mcp-config で渡す JSON のフルパス。"mcp-config-{ポート}.json"。
        ///
        /// dev/prod ではなく<b>ポートで分ける</b>のが要点。このファイルの中身は実質ポートそのもので、
        /// 5080(常用) と 5082(開発版) を同時に起動する運用があるため、共有すると後勝ちで
        /// 上書きし合い、セッションが意図しない方のインスタンスへ繋がる。過去に terminalhub が
        /// 5080/5081 の二重定義で別インスタンスへ繋がった実害があるので、同じ轍を踏まない。
        /// </summary>
        public static string GetMcpConfigFilePath(int port)
        {
            return Path.Combine(UserDataRoot, $"mcp-config-{port}.json");
        }
    }
}
