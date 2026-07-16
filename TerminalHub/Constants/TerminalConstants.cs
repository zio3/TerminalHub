namespace TerminalHub.Constants
{
    public static class TerminalConstants
    {
        // ターミナルサイズ
        public const int DefaultCols = 120;
        public const int DefaultRows = 30;

        // タイミング（ミリ秒）
        public const int DomUpdateDelay = 50;         // DOM更新待機

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

        // コマンドライン引数の構築ヘルパー
        /// <param name="mcpConfigPath">
        /// TerminalHub のローカル MCP(terminalhub) を繋ぐ JSON のパス。null なら付与しない。
        /// options ではなく引数で受けるのは、これがセッションに保存される設定ではなく
        /// 「起動時のポートに依存する一時的な値」だから（SessionInfo.Options は永続化される）。
        /// </param>
        public static string BuildClaudeCodeArgs(Dictionary<string, string> options, string? mcpConfigPath = null)
        {
            var args = new List<string>();

            // permission-mode: 新キー優先、旧キー(bypass-mode)フォールバック
            if (options.TryGetValue("permission-mode", out var permMode))
            {
                if (permMode == "bypass") args.Add("--dangerously-skip-permissions");
                else if (permMode == "auto") args.Add("--permission-mode auto");
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

            // ユーザー指定(extra-args/custom-args)より前に置く。--mcp-config は複数指定でマージされるので、
            // ユーザーが自分で --mcp-config を書いていても衝突しない。
            // パスは必ず引用符で囲む: ConPtyService はコマンドラインを無加工で連結するため、
            // %LOCALAPPDATA% にスペースを含むユーザー名だと引用符無しでは空白で分割されて壊れる
            // （実測で確認済み）。Codex の --add-dir と同じ流儀。
            if (!string.IsNullOrWhiteSpace(mcpConfigPath))
            {
                args.Add($"--mcp-config \"{mcpConfigPath}\"");
            }

            if (options.TryGetValue("extra-args", out var extraArgs) && !string.IsNullOrWhiteSpace(extraArgs))
            {
                args.Add(extraArgs.Trim());
            }

            AppendCustomArgs(args, options);

            return string.Join(" ", args);
        }

        // BuildGeminiArgs は廃止 (GeminiCLI 起動経路は撤去済み)

        public static string BuildCodexArgs(
            Dictionary<string, string> options,
            string? terminalHubMcpUrl = null)
        {
            var args = new List<string>();

            // extra-args / custom-args に手書きで --no-alt-screen を入れている既存セッションが
            // あり得るため、UI側の明示指定と重なっても二重指定しない。
            var userSuppliedNoAltScreen =
                ContainsArgToken(options.GetValueOrDefault("extra-args"), "--no-alt-screen") ||
                ContainsArgToken(options.GetValueOrDefault("custom-args"), "--no-alt-screen");
            var userSuppliedSearch = ContainsCodexArgToken(options, "--search");
            var userSuppliedSandbox = ContainsCodexArgOption(options, "--sandbox", "-s");
            var userSuppliedApprovalPolicy = ContainsCodexArgOption(options, "--ask-for-approval", "-a");
            var userSuppliedDangerousBypass = ContainsCodexArgOption(
                options,
                "--dangerously-bypass-approvals-and-sandbox");
            var userSuppliedApprovalsReviewer = ContainsCodexConfigOverride(options, "approvals_reviewer");
            var userSuppliedWindowsSandbox = ContainsCodexConfigOverride(options, "windows.sandbox");
            var userSuppliedNetworkAccess = ContainsCodexConfigOverride(options, "sandbox_workspace_write.network_access");
            var userSuppliedWebSearch = ContainsCodexConfigOverride(options, "web_search");
            var userSuppliedTerminalHubMcpUrl = ContainsCodexConfigOverride(options, "mcp_servers.terminalhub.url");
            if (options.TryGetValue("no-alt-screen", out var noAltScreen) && noAltScreen == "true" &&
                !userSuppliedNoAltScreen)
            {
                args.Add("--no-alt-screen");
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

            var mode = options.GetValueOrDefault("mode");
            var sandboxMode = options.GetValueOrDefault("sandbox-mode");
            var approvalPolicy = options.GetValueOrDefault("ask-for-approval");
            var approvalsReviewer = options.GetValueOrDefault("approvals-reviewer");
            var windowsSandbox = options.GetValueOrDefault("windows-sandbox");
            var networkAccess = options.GetValueOrDefault("network-access");
            var webSearchMode = options.GetValueOrDefault("web-search-mode");
            var permissionPreset = options.GetValueOrDefault("permission-preset");

            // プリセットを保存値の寄せ集めではなく、起動時の契約として扱う。
            // これにより古い詳細値が残っていても、各プリセットは常に文書化された
            // 組み合わせで起動する。codex-default は旧保存値との互換用。
            if (permissionPreset == "codex-default")
            {
                sandboxMode = "";
                approvalPolicy = "";
                approvalsReviewer = "";
                windowsSandbox = "";
                networkAccess = "";
                webSearchMode = "";
            }
            else if (permissionPreset == "ask-for-approval")
            {
                sandboxMode = "workspace-write";
                approvalPolicy = "on-request";
                approvalsReviewer = "user";
                windowsSandbox = "elevated";
                networkAccess = "true";
                webSearchMode = "live";
            }
            else if (permissionPreset == "recommended")
            {
                sandboxMode = "workspace-write";
                approvalPolicy = "on-request";
                approvalsReviewer = "auto_review";
                windowsSandbox = "elevated";
                networkAccess = "true";
                webSearchMode = "live";
            }

            // 保存済みのモード名は維持し、現行CLIの明示的な設定へ変換する。
            if (mode == "yolo")
            {
                if (!userSuppliedDangerousBypass)
                {
                    args.Add("--dangerously-bypass-approvals-and-sandbox");
                }
            }
            else
            {
                // Auto は workspace-write + on-request。詳細設定がある場合はユーザー指定を優先する。
                if (mode == "auto")
                {
                    sandboxMode = string.IsNullOrEmpty(sandboxMode) ? "workspace-write" : sandboxMode;
                    approvalPolicy = string.IsNullOrEmpty(approvalPolicy) ? "on-request" : approvalPolicy;
                }

                if (!string.IsNullOrEmpty(sandboxMode) && !userSuppliedSandbox)
                {
                    args.Add($"--sandbox {sandboxMode}");
                }

                if (!string.IsNullOrEmpty(approvalPolicy) && !userSuppliedApprovalPolicy)
                {
                    args.Add($"--ask-for-approval {approvalPolicy}");
                }

                // 承認を一切求めない設定ではレビュー対象が存在しないため付与しない。
                if (approvalsReviewer == "auto_review" && approvalPolicy != "never" && !userSuppliedApprovalsReviewer)
                {
                    args.Add("-c approvals_reviewer=auto_review");
                }
                else if (approvalsReviewer == "user" && approvalPolicy != "never" && !userSuppliedApprovalsReviewer)
                {
                    args.Add("-c approvals_reviewer=user");
                }

                if (windowsSandbox is ("elevated" or "unelevated") && !userSuppliedWindowsSandbox)
                {
                    args.Add($"-c windows.sandbox={windowsSandbox}");
                }

                if (networkAccess is ("true" or "false") && !userSuppliedNetworkAccess)
                {
                    args.Add($"-c sandbox_workspace_write.network_access={networkAccess}");
                }

                if (webSearchMode == "live" && !userSuppliedSearch && !userSuppliedWebSearch)
                {
                    args.Add("--search");
                }
                else if (webSearchMode is ("disabled" or "cached" or "indexed") && !userSuppliedWebSearch)
                {
                    args.Add($"-c web_search={webSearchMode}");
                }
                else if (permissionPreset != "codex-default" &&
                         options.ContainsKey("search") && options["search"] == "true" &&
                         !userSuppliedSearch && !userSuppliedWebSearch)
                {
                    // 旧保存形式との互換性。
                    args.Add("--search");
                }
            }

            // TerminalHub のローカル MCP は実行中のポートに依存するため、プロジェクト設定へ
            // 永続化せず Codex の起動時設定として渡す。手書き指定があればそちらを優先する。
            if (!string.IsNullOrWhiteSpace(terminalHubMcpUrl) && !userSuppliedTerminalHubMcpUrl)
            {
                args.Add($"-c mcp_servers.terminalhub.url={terminalHubMcpUrl}");
            }

            if (options.TryGetValue("extra-args", out var extraArgs) && !string.IsNullOrWhiteSpace(extraArgs))
            {
                args.Add(extraArgs.Trim());
            }

            AppendCustomArgs(args, options);

            // 設定上書きはサブコマンドより前へ置く。resume --last は必ず最後に追加する。
            if (options.ContainsKey("resume-last") && options["resume-last"] == "true")
            {
                args.Add("resume --last");
            }

            return string.Join(" ", args);
        }

        /// <summary>
        /// TerminalHub の保存値と手書き引数から、Codex が PermissionRequest を
        /// ユーザーへ回す構成かを判定する。未指定値は Codex の既定（user）として扱う。
        /// </summary>
        public static bool CodexPermissionRequestRequiresUserInput(Dictionary<string, string> options)
        {
            if (options.GetValueOrDefault("mode") == "yolo" ||
                ContainsCodexArgOption(options, "--dangerously-bypass-approvals-and-sandbox"))
            {
                return false;
            }

            var approvalPolicy = options.GetValueOrDefault("ask-for-approval") ?? "";
            var approvalsReviewer = options.GetValueOrDefault("approvals-reviewer") ?? "";
            var permissionPreset = options.GetValueOrDefault("permission-preset");

            if (permissionPreset == "codex-default")
            {
                approvalPolicy = "";
                approvalsReviewer = "";
            }
            else if (permissionPreset == "ask-for-approval")
            {
                approvalPolicy = "on-request";
                approvalsReviewer = "user";
            }
            else if (permissionPreset == "recommended")
            {
                approvalPolicy = "on-request";
                approvalsReviewer = "auto_review";
            }

            if (options.GetValueOrDefault("mode") == "auto" && string.IsNullOrEmpty(approvalPolicy))
            {
                approvalPolicy = "on-request";
            }

            approvalPolicy = GetLastCodexArgOptionValue(
                options, approvalPolicy, "--ask-for-approval", "-a");
            approvalsReviewer = GetLastCodexConfigOverrideValue(
                options, "approvals_reviewer") ?? approvalsReviewer;

            if (approvalPolicy == "never")
            {
                return false;
            }

            // config.toml に委ねる未指定ケースは実効値を観測できないため、
            // 誤ってユーザー待ちを抑制しないよう Codex 既定の user として扱う。
            return approvalsReviewer != "auto_review";
        }

        // Antigravity CLI (agy): 承認スキップと会話継続を Claude/Codex と同じ形で扱う。
        public static string BuildAntigravityArgs(Dictionary<string, string> options)
        {
            var args = new List<string>();

            if (options.ContainsKey("skip-permissions") && options["skip-permissions"] == "true")
            {
                args.Add("--dangerously-skip-permissions");
            }

            if (options.ContainsKey("continue") && options["continue"] == "true")
            {
                args.Add("-c");
            }

            if (options.TryGetValue("extra-args", out var extraArgs) && !string.IsNullOrWhiteSpace(extraArgs))
            {
                args.Add(extraArgs.Trim());
            }

            AppendCustomArgs(args, options);

            return string.Join(" ", args);
        }

        // Grok CLI: --always-approve で承認スキップ、--continue で直近会話の継続。
        // 認証は環境変数 GROK_CODE_XAI_API_KEY か初回ブラウザ OAuth に任せる。
        public static string BuildGrokArgs(Dictionary<string, string> options)
        {
            var args = new List<string>();

            if (options.ContainsKey("always-approve") && options["always-approve"] == "true")
            {
                args.Add("--always-approve");
            }

            if (options.ContainsKey("continue") && options["continue"] == "true")
            {
                args.Add("-c");
            }

            if (options.TryGetValue("extra-args", out var extraArgs) && !string.IsNullOrWhiteSpace(extraArgs))
            {
                args.Add(extraArgs.Trim());
            }

            AppendCustomArgs(args, options);

            return string.Join(" ", args);
        }

        // 空白区切りの引数文字列（extra-args / custom-args）に、指定トークンが
        // 独立した引数として含まれるかを判定する（--no-alt-screen の二重指定防止に使用）。
        private static bool ContainsArgToken(string? rawArgs, string token)
        {
            if (string.IsNullOrWhiteSpace(rawArgs))
            {
                return false;
            }

            var tokens = rawArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return Array.IndexOf(tokens, token) >= 0;
        }

        private static bool ContainsCodexArgToken(Dictionary<string, string> options, string token)
        {
            return ContainsArgToken(options.GetValueOrDefault("extra-args"), token) ||
                   ContainsArgToken(options.GetValueOrDefault("custom-args"), token);
        }

        private static bool ContainsCodexArgOption(Dictionary<string, string> options, params string[] optionNames)
        {
            return ContainsArgOption(options.GetValueOrDefault("extra-args"), optionNames) ||
                   ContainsArgOption(options.GetValueOrDefault("custom-args"), optionNames);
        }

        private static bool ContainsArgOption(string? rawArgs, params string[] optionNames)
        {
            if (string.IsNullOrWhiteSpace(rawArgs))
            {
                return false;
            }

            var tokens = rawArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return tokens.Any(token => optionNames.Any(optionName =>
                token == optionName || token.StartsWith(optionName + "=", StringComparison.Ordinal)));
        }

        private static bool ContainsCodexConfigOverride(Dictionary<string, string> options, string settingName)
        {
            return ContainsConfigOverride(options.GetValueOrDefault("extra-args"), settingName) ||
                   ContainsConfigOverride(options.GetValueOrDefault("custom-args"), settingName);
        }

        private static bool ContainsConfigOverride(string? rawArgs, string settingName)
        {
            if (string.IsNullOrWhiteSpace(rawArgs))
            {
                return false;
            }

            var prefix = settingName + "=";
            return rawArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Any(token => token.StartsWith(prefix, StringComparison.Ordinal));
        }

        private static string GetLastCodexArgOptionValue(
            Dictionary<string, string> options,
            string fallback,
            params string[] optionNames)
        {
            var value = fallback;
            foreach (var rawArgs in new[]
                     {
                         options.GetValueOrDefault("extra-args"),
                         options.GetValueOrDefault("custom-args")
                     })
            {
                if (string.IsNullOrWhiteSpace(rawArgs)) continue;

                var tokens = rawArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < tokens.Length; i++)
                {
                    var token = tokens[i].Trim('"', '\'');
                    foreach (var optionName in optionNames)
                    {
                        if (token == optionName && i + 1 < tokens.Length)
                        {
                            value = tokens[i + 1].Trim('"', '\'');
                        }
                        else if (token.StartsWith(optionName + "=", StringComparison.Ordinal))
                        {
                            value = token[(optionName.Length + 1)..].Trim('"', '\'');
                        }
                    }
                }
            }

            return value;
        }

        private static string? GetLastCodexConfigOverrideValue(
            Dictionary<string, string> options,
            string settingName)
        {
            string? value = null;
            var prefix = settingName + "=";
            foreach (var rawArgs in new[]
                     {
                         options.GetValueOrDefault("extra-args"),
                         options.GetValueOrDefault("custom-args")
                     })
            {
                if (string.IsNullOrWhiteSpace(rawArgs)) continue;

                foreach (var token in rawArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    var normalizedToken = token.Trim('"', '\'');
                    if (normalizedToken.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        value = normalizedToken[prefix.Length..].Trim('"', '\'');
                    }
                }
            }

            return value;
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
