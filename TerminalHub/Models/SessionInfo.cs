using Microsoft.JSInterop;
using TerminalHub.Services;
using System.Text;

namespace TerminalHub.Models
{
    public enum TerminalType
    {
        Terminal,
        ClaudeCode,
        GeminiCLI,
        CodexCLI
    }

    public enum BottomPanelTab
    {
        TextInput,
        DosTerminal
    }

    public enum BottomPanelTabType
    {
        TextInput,
        DosTerminal
    }

    public class BottomPanelTabInfo
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public BottomPanelTabType Type { get; set; }
        public string DisplayName { get; set; } = "";
        public bool IsDefault { get; set; } = false;
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

        // アーカイブ状態
        public bool IsArchived { get; set; } = false;
        public DateTime? ArchivedAt { get; set; }

        // チェックされたnpmスクリプト
        public HashSet<string> CheckedScripts { get; set; } = new HashSet<string>();
        
        [System.Text.Json.Serialization.JsonIgnore]
        public string? ProcessingStatus { get; set; }
        
        [System.Text.Json.Serialization.JsonIgnore]
        public DateTime? ProcessingStartTime { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public int? ProcessingElapsedSeconds { get; set; }

        /// <summary>
        /// Hook Stop イベントを受信した時刻（OutputAnalyzerからの更新を一時的にスキップするため）
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public DateTime? LastStopEventTime { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public DateTime? LastProcessingUpdateTime { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsWaitingForUserInput { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasNotificationPending { get; set; }

        /// <summary>
        /// 最後にセッションに接続した時刻（過去バッファの誤検出防止用）
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public DateTime? LastConnectionTime { get; set; }

        /// <summary>
        /// 接続直後（10秒以内）かどうかを判定
        /// 過去バッファの誤検出を防ぐために使用
        /// </summary>
        private const double RecentConnectionThresholdSeconds = 10.0;

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsRecentConnection => LastConnectionTime.HasValue &&
            (DateTime.Now - LastConnectionTime.Value).TotalSeconds < RecentConnectionThresholdSeconds;

        [System.Text.Json.Serialization.JsonIgnore]
        public ConPtySession? ConPtySession { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public int LastKnownScrollPosition { get; set; } = 0;
        
        // DOSターミナル関連プロパティ
        [System.Text.Json.Serialization.JsonIgnore]
        public ConPtySession? DosTerminalConPtySession { get; set; }
        
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
        public bool HasContinueErrorOccurred { get; set; } // --continueエラーが発生済みかどうか

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HookConfigured { get; set; } // Claude Code hook 設定済みかどうか
        
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsExpanded { get; set; } = true; // サブセッションの展開状態

        // スクロールバック保持用バッファ（常時有効、セッション切替時の復元用）
        [System.Text.Json.Serialization.JsonIgnore]
        private StringBuilder? _terminalBuffer;

        private const int MaxTerminalBufferSize = 2 * 1024 * 1024; // 2MB上限

        public void AppendToTerminalBuffer(string data)
        {
            _terminalBuffer ??= new StringBuilder();

            // 上限チェック
            if (_terminalBuffer.Length + data.Length > MaxTerminalBufferSize)
            {
                // 古いデータを削除して新しいデータを追加
                var overflow = _terminalBuffer.Length + data.Length - MaxTerminalBufferSize;
                if (overflow < _terminalBuffer.Length)
                {
                    _terminalBuffer.Remove(0, (int)overflow);
                }
                else
                {
                    _terminalBuffer.Clear();
                }
            }
            _terminalBuffer.Append(data);
        }

        public string GetTerminalBuffer()
        {
            return _terminalBuffer?.ToString() ?? string.Empty;
        }

        public void ClearTerminalBuffer()
        {
            _terminalBuffer?.Clear();
        }

        public int TerminalBufferSize => _terminalBuffer?.Length ?? 0;

        // バッファキャプチャ機能（デバッグ用、再起動時にリセット）
        [System.Text.Json.Serialization.JsonIgnore]
        public bool EnableBufferCapture { get; set; } = false;

        [System.Text.Json.Serialization.JsonIgnore]
        public StringBuilder? CapturedBuffer { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public DateTime? BufferCaptureStartTime { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public int CapturedBufferSize => CapturedBuffer?.Length ?? 0;

        private const int MaxBufferCaptureSize = 1024 * 1024; // 1MB上限

        public void StartBufferCapture()
        {
            EnableBufferCapture = true;
            CapturedBuffer = new StringBuilder();
            BufferCaptureStartTime = DateTime.Now;
        }

        public void StopBufferCapture()
        {
            EnableBufferCapture = false;
        }

        public void ClearCapturedBuffer()
        {
            CapturedBuffer?.Clear();
            CapturedBuffer = null;
            BufferCaptureStartTime = null;
            EnableBufferCapture = false;
        }

        public void AppendToBuffer(string data)
        {
            if (!EnableBufferCapture || CapturedBuffer == null) return;

            // 上限チェック
            if (CapturedBuffer.Length + data.Length > MaxBufferCaptureSize)
            {
                // 古いデータを削除して新しいデータを追加
                var overflow = CapturedBuffer.Length + data.Length - MaxBufferCaptureSize;
                if (overflow < CapturedBuffer.Length)
                {
                    CapturedBuffer.Remove(0, overflow);
                }
                else
                {
                    CapturedBuffer.Clear();
                }
            }
            CapturedBuffer.Append(data);
        }

        public string GetCapturedBuffer()
        {
            return CapturedBuffer?.ToString() ?? string.Empty;
        }

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

        /// <summary>
        /// 通知用の軽量コピーを作成
        /// 非同期処理中にセッション情報が変更されても影響を受けないようにするため
        /// </summary>
        public SessionInfo CloneForNotification()
        {
            return new SessionInfo
            {
                SessionId = SessionId,
                DisplayName = DisplayName,
                FolderPath = FolderPath,
                FolderName = FolderName,
                TerminalType = TerminalType
            };
        }
    }
}