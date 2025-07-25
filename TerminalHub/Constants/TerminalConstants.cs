namespace TerminalHub.Constants
{
    public static class TerminalConstants
    {
        // ターミナルサイズ
        public const int DefaultCols = 120;
        public const int DefaultRows = 30;
        public const int MinCols = 80;
        public const int MinRows = 24;
        
        // バッファサイズ
        public const int DefaultBufferSize = 4096;
        public const int MaxBufferSize = 65536;
        
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
        public const int DefaultMaxSessions = 25; // 20セッション + 余裕
        public const int SessionIdDisplayLength = 8;
        
        // ファイルパス
        public const string DefaultShell = @"C:\Windows\System32\cmd.exe";
        
        // Claude Codeのデフォルトパス（ユーザー固有）
        public static string GetDefaultClaudeCmdPath()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, "AppData", "Roaming", "npm", "claude.cmd");
        }

        // コマンドライン引数の構築ヘルパー
        public static string BuildClaudeCodeArgs(Dictionary<string, string> options)
        {
            var args = new List<string>();
            
            
            if (options.ContainsKey("bypass-mode") && options["bypass-mode"] == "true")
            {
                args.Add("--dangerously-skip-permissions");
            }
            
            if (options.ContainsKey("continue") && options["continue"] == "true")
            {
                args.Add("--continue");
            }
            
            return string.Join(" ", args);
        }

        public static string BuildGeminiArgs(Dictionary<string, string> options)
        {
            var args = new List<string>();
            
            if (options.ContainsKey("yolo") && options["yolo"] == "true")
            {
                args.Add("-y");
            }
            
            if (options.ContainsKey("sandbox") && options["sandbox"] == "true")
            {
                args.Add("-s");
            }
            
            return string.Join(" ", args);
        }
    }
}