namespace TerminalHub.Models
{
    public enum TerminalType
    {
        Terminal,
        ClaudeCode,
        GeminiCLI
    }

    public class SessionInfo
    {
        public Guid SessionId { get; set; } = Guid.NewGuid();
        public string? DisplayName { get; set; }
        public string FolderPath { get; set; } = string.Empty;
        public string FolderName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime LastAccessedAt { get; set; } = DateTime.Now;
        public bool IsActive { get; set; }
        public TerminalType TerminalType { get; set; } = TerminalType.Terminal;
        public Dictionary<string, string> Options { get; set; } = new();
        public string Memo { get; set; } = string.Empty;
        
        [System.Text.Json.Serialization.JsonIgnore]
        public string? ProcessingStatus { get; set; }
        
        [System.Text.Json.Serialization.JsonIgnore]
        public DateTime? ProcessingStartTime { get; set; }
        
        [System.Text.Json.Serialization.JsonIgnore]
        public int? ProcessingElapsedSeconds { get; set; }
        
        [System.Text.Json.Serialization.JsonIgnore]
        public string? ProcessingTokens { get; set; }
        
        [System.Text.Json.Serialization.JsonIgnore]
        public string? ProcessingDirection { get; set; }
        
        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasNotificationPending { get; set; }
        
        [System.Text.Json.Serialization.JsonIgnore]
        public CircularLineBuffer OutputBuffer { get; set; } = new CircularLineBuffer(10000);
        
        [System.Text.Json.Serialization.JsonIgnore]
        public ConPtyWithBuffer? ConPtyBuffer { get; set; }
        
        [System.Text.Json.Serialization.JsonIgnore]
        public int LastKnownScrollPosition { get; set; } = 0;
        
        [System.Text.Json.Serialization.JsonIgnore]
        public DateTime? LastProcessingUpdateTime { get; set; }
        
        [System.Text.Json.Serialization.JsonIgnore]
        public string? LastProcessingSeconds { get; set; }
        
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsWaitingForUserInput { get; set; }
        
        // Git関連プロパティ
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsGitRepository { get; set; }
        
        [System.Text.Json.Serialization.JsonIgnore]
        public string? GitBranch { get; set; }
        
        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasUncommittedChanges { get; set; }
        
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsWorktree { get; set; }
        
        // ParentSessionIdは保存して復元時に親子関係を維持
        public Guid? ParentSessionId { get; set; } // Worktreeの場合の親セッション
        
        [System.Text.Json.Serialization.JsonIgnore]
        public string? WorktreeMainPath { get; set; } // Worktreeの場合のメインリポジトリパス
        
        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasContinueErrorOccurred { get; set; } // --continueエラーが発生済みかどうか
        
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsExpanded { get; set; } = true; // サブセッションの展開状態
        
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsInitializing { get; set; } // ConPty初期化中かどうか
        
        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasReceivedFirstData { get; set; } // 最初のデータを受信したかどうか
        
        public string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(DisplayName))
                return DisplayName;
            
            // フォルダパスからフォルダ名を抽出
            if (!string.IsNullOrEmpty(FolderPath))
            {
                var folderName = GetFolderNameFromPath(FolderPath);
                if (!string.IsNullOrEmpty(folderName))
                    return folderName;
            }
                
            return $"セッション {CreatedAt:HH:mm}";
        }

        private string GetFolderNameFromPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            // 末尾の \ や / を除去
            path = path.TrimEnd('\\', '/');

            // パスが空になった場合（ルートディレクトリなど）
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            // 最後のディレクトリ名を取得
            var lastSeparatorIndex = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            
            if (lastSeparatorIndex >= 0 && lastSeparatorIndex < path.Length - 1)
            {
                return path.Substring(lastSeparatorIndex + 1);
            }

            // セパレータがない場合はパス全体をフォルダ名とする
            return path;
        }
    }
}