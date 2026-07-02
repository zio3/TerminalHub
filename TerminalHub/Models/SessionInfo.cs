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
        CodexCLI,
        Antigravity,
        Grok
    }

    public enum BottomPanelTabType
    {
        TextInput,
        CommandPrompt,
        PowerShell,
        Memo,
        /// <summary>ゲームパッド（D-pad）: カーソル/Tab/Esc/Enter をアクティブセッションへ生キー送信</summary>
        Gamepad
    }

    public class BottomPanelTabInfo
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public BottomPanelTabType Type { get; set; }
        public string DisplayName { get; set; } = "";
        public bool IsDefault { get; set; } = false;
        /// <summary>Memo タブ用: 属するセッション (null なら全セッション共通タブ)</summary>
        public Guid? SessionId { get; set; }
        /// <summary>Memo タブ用: 対応する SessionMemo.MemoId</summary>
        public Guid? MemoId { get; set; }
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

        // このセッションでだけ表示・送信できるカスタムコマンド（グローバル設定の Commands とは別管理）。
        // クイック送信バーではグローバル分の後ろに連結して表示する。DB には JSON で永続化。
        public List<CustomCommand> SessionCommands { get; set; } = new();

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
        /// コンテキスト compact 実行中（PreCompact ～ PostCompact の間）。
        /// この間は実質「作業中（応答不可）」のため、UI でステータス表示する。
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsCompacting { get; set; }

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

        // スクロールバック保持用バッファ（常時有効、セッション切替時の復元用）。
        // 実体は TerminalHub.Terminal 側のVTエミュレータ（EmulatedStateBuffer）に委譲する。
        // repaint を上書きとして畳むため、復元時にスクロールバックが二重化しない。
        [System.Text.Json.Serialization.JsonIgnore]
        private readonly TerminalHub.Terminal.ITerminalStateBuffer _terminalBuffer
            = TerminalHub.Terminal.TerminalStateBufferFactory.Create();

        public void AppendToTerminalBuffer(string data)
        {
            _terminalBuffer.Append(data);
        }

        /// <summary>端末サイズ変更をバッファへ通知する。</summary>
        public void ResizeTerminalBuffer(int cols, int rows)
        {
            _terminalBuffer.Resize(cols, rows);
        }


        public string GetTerminalBuffer()
        {
            return _terminalBuffer.SerializeForReplay();
        }

        public void ClearTerminalBuffer()
        {
            _terminalBuffer.Clear();
        }

        public int TerminalBufferSize => _terminalBuffer.Size;

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

        // ===== サブエージェント実行追跡 =====
        // Claude Code の SubagentStart / SubagentStop hook を agent_id で突き合わせて
        // 「今このセッションで何個のサブエージェントが走っているか」を保持する。
        // 完了通知のゲートには使わず、UI 表示（稼働中バッジ）専用。
        [System.Text.Json.Serialization.JsonIgnore]
        // キー: agent_id、値: agent_type
        private readonly Dictionary<string, string?> _runningSubagents = new();

        [System.Text.Json.Serialization.JsonIgnore]
        private readonly object _subagentLock = new();

        /// <summary>サブエージェントを実行中として登録する（SubagentStart）。</summary>
        public void AddRunningSubagent(string agentId, string? agentType)
        {
            if (string.IsNullOrEmpty(agentId)) return;
            lock (_subagentLock)
            {
                _runningSubagents[agentId] = agentType;
            }
        }

        /// <summary>サブエージェントを実行中リストから除去する（SubagentStop）。除去できたら true。</summary>
        public bool RemoveRunningSubagent(string agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return false;
            lock (_subagentLock)
            {
                return _runningSubagents.Remove(agentId);
            }
        }

        /// <summary>実行中サブエージェント数。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public int RunningSubagentCount
        {
            get { lock (_subagentLock) { return _runningSubagents.Count; } }
        }

        /// <summary>実行中サブエージェントがあるか。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasRunningSubagents
        {
            get { lock (_subagentLock) { return _runningSubagents.Count > 0; } }
        }

        /// <summary>実行中サブエージェントの agent_type 一覧（表示用、null/空は除外）。</summary>
        public List<string> GetRunningSubagentTypes()
        {
            lock (_subagentLock)
            {
                var list = new List<string>();
                foreach (var type in _runningSubagents.Values)
                {
                    if (!string.IsNullOrEmpty(type)) list.Add(type!);
                }
                return list;
            }
        }

        /// <summary>サブエージェント追跡をリセットする（新しいターン開始時など、取りこぼし対策）。</summary>
        public void ClearRunningSubagents()
        {
            lock (_subagentLock)
            {
                _runningSubagents.Clear();
            }
        }

        // ===== Hook イベントログ（診断用: 何の hook が来たか） =====
        [System.Text.Json.Serialization.JsonIgnore]
        private readonly Queue<HookEventEntry> _hookEventLog = new();

        [System.Text.Json.Serialization.JsonIgnore]
        private readonly object _hookEventLogLock = new();

        private const int MaxHookEventLogCount = 300;

        public void RecordHookEvent(string eventName, string? agentId, string? agentType, int runningSubagentCount, string? message = null, string? toolName = null)
        {
            lock (_hookEventLogLock)
            {
                if (_hookEventLog.Count >= MaxHookEventLogCount)
                {
                    _hookEventLog.Dequeue();
                }
                _hookEventLog.Enqueue(new HookEventEntry
                {
                    Timestamp = DateTime.Now,
                    EventName = eventName,
                    AgentId = agentId,
                    AgentType = agentType,
                    RunningSubagentCount = runningSubagentCount,
                    Message = message,
                    ToolName = toolName
                });
            }
        }

        public List<HookEventEntry> GetHookEventLog()
        {
            lock (_hookEventLogLock)
            {
                return new List<HookEventEntry>(_hookEventLog);
            }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public int HookEventLogCount
        {
            get { lock (_hookEventLogLock) { return _hookEventLog.Count; } }
        }

        public void ClearHookEventLog()
        {
            lock (_hookEventLogLock)
            {
                _hookEventLog.Clear();
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

    /// <summary>
    /// Hook イベントログのエントリ（診断用）。受信した Claude Code hook の記録。
    /// </summary>
    public class HookEventEntry
    {
        public DateTime Timestamp { get; set; }
        /// <summary>hook_event_name（Stop / SubagentStart / SubagentStop / UserPromptSubmit / Notification 等）</summary>
        public string EventName { get; set; } = "";
        /// <summary>サブエージェント固有 ID（サブエージェント内 hook の時のみ）</summary>
        public string? AgentId { get; set; }
        /// <summary>サブエージェント種別（Explore など）</summary>
        public string? AgentType { get; set; }
        /// <summary>この hook 処理後の実行中サブエージェント数</summary>
        public int RunningSubagentCount { get; set; }
        /// <summary>通知メッセージ本文（Notification の message 等）</summary>
        public string? Message { get; set; }
        /// <summary>ツール名（PreToolUse の tool_name 等）</summary>
        public string? ToolName { get; set; }
    }
}