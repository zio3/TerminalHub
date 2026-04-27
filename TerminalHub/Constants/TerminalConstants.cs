namespace TerminalHub.Constants
{
    public static class TerminalConstants
    {
        // ターミナルサイズ
        public const int DefaultCols = 120;
        public const int DefaultRows = 30;
        public const int MinCols = 80;
        public const int MinRows = 24;
        
        // タイミング（ミリ秒）
        public const int ResizeDelay = 20;           // リサイズ後の待機（最小値）
        public const int DomUpdateDelay = 50;         // DOM更新待機
        public const int SessionCreationDelay = 100;  // セッション作成待機
        public const int InitializationDelay = 500;   // 初期化待機（必要最小限）
        public const int MinimalDelay = 10;           // 最小待機時間
        public const int ButtonPressAnimationDelay = 150; // ボタン押下アニメーション
        
        // プロセス
        public const uint ExtendedStartupinfoPresent = 0x00080000;
        public const int ProcThreadAttributePseudoConsole = 0x00020016;
        
        // セッション
        public const int SessionIdDisplayLength = 8;
        
        // ファイルパス
        public const string DefaultShell = @"C:\Windows\System32\cmd.exe";
        
        // Claude Codeのパス（ネイティブ版優先、npm版フォールバック）
        public static string GetDefaultClaudeCmdPath()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // 1. ネイティブ版を優先（v2.x以降）
            var nativePath = Path.Combine(userProfile, ".local", "bin", "claude.exe");
            if (File.Exists(nativePath))
                return nativePath;

            // 2. npm版にフォールバック
            var npmPath = Path.Combine(userProfile, "AppData", "Roaming", "npm", "claude.cmd");
            if (File.Exists(npmPath))
                return npmPath;

            // 3. どちらもなければネイティブ版のパスを返す（推奨インストール方法）
            return nativePath;
        }

        /// <summary>
        /// Claude Codeのインストール状態を確認
        /// </summary>
        public static (bool isInstalled, string path, string installType) GetClaudeCodeInstallInfo()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // ネイティブ版
            var nativePath = Path.Combine(userProfile, ".local", "bin", "claude.exe");
            if (File.Exists(nativePath))
                return (true, nativePath, "native");

            // npm版
            var npmPath = Path.Combine(userProfile, "AppData", "Roaming", "npm", "claude.cmd");
            if (File.Exists(npmPath))
                return (true, npmPath, "npm");

            return (false, nativePath, "not installed");
        }

        // コマンドライン引数の構築ヘルパー
        public static string BuildClaudeCodeArgs(Dictionary<string, string> options)
        {
            var args = new List<string>();

            // permission-mode: 新キー優先、旧キー(bypass-mode)フォールバック
            if (options.TryGetValue("permission-mode", out var permMode))
            {
                if (permMode == "bypass") args.Add("--dangerously-skip-permissions");
                else if (permMode == "auto") args.Add("--enable-auto-mode");
            }
            else if (options.ContainsKey("bypass-mode") && options["bypass-mode"] == "true")
            {
                // 旧データ互換
                args.Add("--dangerously-skip-permissions");
            }

            if (options.ContainsKey("continue") && options["continue"] == "true")
            {
                args.Add("--continue");
            }

            if (options.ContainsKey("chrome") && options["chrome"] == "true")
            {
                args.Add("--chrome");
            }

            if (options.ContainsKey("remote-control") && options["remote-control"] == "true")
            {
                args.Add("--remote-control");
            }

            if (options.TryGetValue("extra-args", out var extraArgs) && !string.IsNullOrWhiteSpace(extraArgs))
            {
                args.Add(extraArgs.Trim());
            }

            AppendCustomArgs(args, options);

            return string.Join(" ", args);
        }

        public static string BuildGeminiArgs(Dictionary<string, string> options)
        {
            var args = new List<string>();

            if (options.TryGetValue("approval-mode", out var mode) && mode != "default")
            {
                args.Add($"--approval-mode {mode}");
            }

            if (options.ContainsKey("sandbox") && options["sandbox"] == "true")
            {
                args.Add("-s");
            }

            if (options.ContainsKey("continue") && options["continue"] == "true")
            {
                args.Add("--resume latest");
            }

            if (options.TryGetValue("extra-args", out var extraArgs) && !string.IsNullOrWhiteSpace(extraArgs))
            {
                args.Add(extraArgs);
            }

            AppendCustomArgs(args, options);

            return string.Join(" ", args);
        }

        public static string BuildCodexArgs(Dictionary<string, string> options)
        {
            var args = new List<string>();

            if (options.ContainsKey("resume-last") && options["resume-last"] == "true")
            {
                args.Add("resume --last");
            }

            if (options.ContainsKey("search") && options["search"] == "true")
            {
                args.Add("--search");
            }

            if (options.TryGetValue("add-dir", out var addDirValue) && !string.IsNullOrWhiteSpace(addDirValue))
            {
                var directories = addDirValue
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                foreach (var directory in directories)
                {
                    args.Add($"--add-dir \"{directory}\"");
                }
            }

            // 実行モード: auto, standard, yolo
            // --full-auto は内部的に workspace-write サンドボックス + on-request approval をデフォルト適用するため、
            // sandbox-mode / ask-for-approval を UI で指定していない限りアプリ側で仮設定はしない。
            if (options.TryGetValue("mode", out var mode))
            {
                switch (mode)
                {
                    case "auto":
                        args.Add("--full-auto");
                        break;
                    case "yolo":
                        args.Add("--yolo");
                        break;
                    // standard はオプションなし
                }
            }

            // サンドボックスモード: read-only, workspace-write, danger-full-access
            if (options.TryGetValue("sandbox-mode", out var sandboxMode) && !string.IsNullOrEmpty(sandboxMode))
            {
                args.Add($"--sandbox {sandboxMode}");
            }

            if (options.TryGetValue("ask-for-approval", out var approvalPolicy) && !string.IsNullOrEmpty(approvalPolicy))
            {
                args.Add($"--ask-for-approval {approvalPolicy}");
            }

            if (options.TryGetValue("extra-args", out var extraArgs) && !string.IsNullOrWhiteSpace(extraArgs))
            {
                args.Add(extraArgs.Trim());
            }

            AppendCustomArgs(args, options);

            return string.Join(" ", args);
        }

        // ユーザー定義カスタムオプションで ON にされた行を、まとめて末尾に追記する。
        // SessionOptionsSelector が options["custom-args"] にスペース連結済みの文字列を入れる。
        private static void AppendCustomArgs(List<string> args, Dictionary<string, string> options)
        {
            if (options.TryGetValue("custom-args", out var custom) && !string.IsNullOrWhiteSpace(custom))
            {
                args.Add(custom.Trim());
            }
        }
    }
}
