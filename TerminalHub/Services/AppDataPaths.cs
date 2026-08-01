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
        /// Claude Code へ --mcp-config で渡す JSON のフルパス。"mcp-configs\mcp-{ポート}-{セッションGUID}.json"。
        ///
        /// かつては「ポート毎の共有ファイル」だったが、中身に MCP 接続キー
        /// (X-TerminalHub-Session-Key＝そのセッションの本人証明)が入るようになり共有できないため、
        /// hook 設定と同じ<b>セッション毎</b>に分ける。さらに<b>ポートも名前に含める</b>:
        /// dev/prod は DB が別で GUID 空間は通常重ならないが、DB をコピーした環境では同じ GUID を
        /// 2つのインスタンス(5080/5082)が持ち得て、共有すると後勝ちで上書きし合う
        /// (旧「ポート毎」ファイルが担っていた同時起動の混線防止を、セッション毎になっても保つ)。
        /// セッション完全削除時にファイルも消す(全ポート分。hook 設定と同じライフサイクル)。
        /// </summary>
        public static string GetMcpConfigFilePath(int port, Guid sessionId)
        {
            return Path.Combine(UserDataRoot, "mcp-configs", $"mcp-{port}-{sessionId}.json");
        }

        /// <summary>
        /// 指定セッションの MCP 設定 JSON を全ポート分列挙する(削除用)。ディレクトリ不在なら空。
        /// </summary>
        public static IEnumerable<string> EnumerateMcpConfigFilePaths(Guid sessionId)
        {
            var dir = Path.Combine(UserDataRoot, "mcp-configs");
            if (!Directory.Exists(dir))
                return Array.Empty<string>();
            return Directory.EnumerateFiles(dir, $"mcp-*-{sessionId}.json");
        }

        /// <summary>
        /// 旧方式(〜今回の変更前)の「ポート毎共有」MCP 設定 JSON のフルパス。残骸掃除専用。
        /// </summary>
        public static string GetLegacyMcpConfigFilePath(int port)
        {
            return Path.Combine(UserDataRoot, $"mcp-config-{port}.json");
        }

        /// <summary>
        /// Claude Code へ --settings で渡す hook 設定 JSON のフルパス。"claude-hooks\hooks-{セッションGUID}.json"。
        ///
        /// MCP設定（ポート毎）と違い<b>セッション毎</b>に分けるのが要点。hook の送信先 URL には
        /// セッション GUID が入る（/api/hook/claude/{sessionId}）ため、ポート単位では共有できない。
        /// 中身のポートは実行中の値で毎起動時に上書きされ、セッション完全削除時にファイルも消す。
        /// dev/prod は DB が別で GUID 空間が重ならないため、ディレクトリは共有でよい。
        /// </summary>
        public static string GetClaudeHookConfigFilePath(Guid sessionId)
        {
            return Path.Combine(UserDataRoot, "claude-hooks", $"hooks-{sessionId}.json");
        }
    }
}
