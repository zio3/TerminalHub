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

    public enum BottomPanelTabType
    {
        TextInput,
        CommandPrompt,
        PowerShell
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

        // ピン留め状態
        public bool IsPinned { get; set; } = false;
        public int? PinPriority { get; set; }

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

        /// <summary>
        /// 最後にセッションに接続した時刻（過去バッファの誤検出防止用）
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public DateTime? LastConnectionTime { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string? RemoteControlUrl { get; set; }

        /// <summary>
        /// 接続直後（5秒以内）かどうかを判定
        /// 過去バッファの誤検出を防ぐために使用
        /// </summary>
        private const double RecentConnectionThresholdSeconds = 5.0;

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsRecentConnection => LastConnectionTime.HasValue &&
            (DateTime.Now - LastConnectionTime.Value).TotalSeconds < RecentConnectionThresholdSeconds;

        [System.Text.Json.Serialization.JsonIgnore]
        public ConPtySession? ConPtySession { get; set; }

        /// <summary>
        /// ConPTY接続処理中かどうか
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsConnecting { get; set; } = false;

        [System.Text.Json.Serialization.JsonIgnore]
        public int LastKnownScrollPosition { get; set; } = 0;
        
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

        [System.Text.Json.Serialization.JsonIgnore]
        private readonly object _terminalBufferLock = new();

        private const int MaxTerminalBufferSize = 2 * 1024 * 1024; // 2MB上限

        public void AppendToTerminalBuffer(string data)
        {
            lock (_terminalBufferLock)
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
        }

        public string GetTerminalBuffer()
        {
            lock (_terminalBufferLock)
            {
                return _terminalBuffer?.ToString() ?? string.Empty;
            }
        }

        public void ClearTerminalBuffer()
        {
            lock (_terminalBufferLock)
            {
                _terminalBuffer?.Clear();
            }
        }

        public int TerminalBufferSize
        {
            get
            {
                lock (_terminalBufferLock)
                {
                    return _terminalBuffer?.Length ?? 0;
                }
            }
        }

        // ステータス変更履歴（診断用、Queue で O(1) eviction）
        [System.Text.Json.Serialization.JsonIgnore]
        private readonly Queue<StatusChangeEntry> _statusChangeHistory = new();

        [System.Text.Json.Serialization.JsonIgnore]
        private readonly object _statusHistoryLock = new();

        private const int MaxStatusHistoryCount = 500;

        public void RecordStatusChange(string? previousStatus, string? newStatus, string? matchedText)
        {
            lock (_statusHistoryLock)
            {
                if (_statusChangeHistory.Count >= MaxStatusHistoryCount)
                {
                    _statusChangeHistory.Dequeue();
                }
                _statusChangeHistory.Enqueue(new StatusChangeEntry
                {
                    Timestamp = DateTime.Now,
                    PreviousStatus = previousStatus,
                    NewStatus = newStatus,
                    MatchedText = matchedText
                });
            }
        }

        public List<StatusChangeEntry> GetStatusChangeHistory()
        {
            lock (_statusHistoryLock)
            {
                return new List<StatusChangeEntry>(_statusChangeHistory);
            }
        }

        public int StatusChangeHistoryCount
        {
            get
            {
                lock (_statusHistoryLock)
                {
                    return _statusChangeHistory.Count;
                }
            }
        }

        public void ClearStatusChangeHistory()
        {
            lock (_statusHistoryLock)
            {
                _statusChangeHistory.Clear();
            }
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

    /// <summary>
    /// ステータス変更履歴エントリ（診断用）
    /// </summary>
    public class StatusChangeEntry
    {
        public DateTime Timestamp { get; set; }
        public string? PreviousStatus { get; set; }
        public string? NewStatus { get; set; }
        /// <summary>
        /// ステータス変更のトリガーとなった正規表現マッチテキスト（ANSIクリーン済み）
        /// </summary>
        public string? MatchedText { get; set; }
    }
}