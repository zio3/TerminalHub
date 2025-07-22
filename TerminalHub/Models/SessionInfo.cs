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
        public string DisplayName { get; set; } = string.Empty;
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