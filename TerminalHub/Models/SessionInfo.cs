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
        /// <summary>カーソルパッド: カーソル/Tab/Esc/Enter をアクティブセッションへ生キー送信</summary>
        CursorPad
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

        /// <summary>ユーザーの許可/承認待ち（Claude Notification permission / Codex PermissionRequest）。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsWaitingForPermission { get; set; }

        /// <summary>ユーザーへの質問/選択待ち（AskUserQuestion / request_user_input）。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsWaitingForSelection { get; set; }

        /// <summary>
        /// 許可待ち・選択待ちのいずれか＝ユーザー入力待ちの合成フラグ。
        /// send_to_session の送信ガード等、種別を問わない粗い判定に使う。
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsWaitingForUserInput => IsWaitingForPermission || IsWaitingForSelection;

        /// <summary>
        /// hook(PermissionRequest/PreToolUse/Notification)が最後に入力待ちを立てた時刻。
        /// OutputAnalyzer が「処理中検出」で待ちを解除する際、直前に hook が立てた待ちを
        /// 古いスピナー出力チャンクの遅延解析で誤クリアするレースを避けるためのクールダウン判定に使う。
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public DateTime? LastWaitingHookSetTime { get; set; }

        /// <summary>入力待ち状態（許可・選択の両方）を解除する。</summary>
        public void ClearWaitingForUserInput()
        {
            IsWaitingForPermission = false;
            IsWaitingForSelection = false;
            LastWaitingHookSetTime = null;
        }

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
        public bool McpConfigured { get; set; } // MCP 自動登録 設定済みかどうか（プロセス内のみ・再起動でリセット→ポート追従）
        
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsExpanded { get; set; } = true; // サブセッションの展開状態

        // スクロールバック保持用バッファ（常時有効、セッション切替時の復元用）。
        // 実体は TerminalHub.Terminal 側のVTエミュレータ（EmulatedStateBuffer）に委譲する。
        // repaint を上書きとして畳むため、復元時にスクロールバックが二重化しない。
        [System.Text.Json.Serialization.JsonIgnore]
        private readonly TerminalHub.Terminal.ITerminalStateBuffer _terminalBuffer
            = TerminalHub.Terminal.TerminalStateBufferFactory.Create();

        /// <summary>
        /// 出力チャンクをバッファへ取り込む。進行中のリプレイキャプチャに取り込まれた場合 true を返し、
        /// そのとき呼び出し側は xterm へ直接書き込んではならない（テール書き込みで届くため二重になる）。
        /// </summary>
        public bool AppendToTerminalBuffer(string data)
        {
            return _terminalBuffer.Append(data);
        }

        /// <summary>
        /// リプレイをアトミックに開始する（スナップショット生成＋以降のライブ出力のテール記録開始）。
        /// 書き込み完了後は必ず <see cref="EndTerminalBufferReplay"/> を呼ぶこと。
        /// </summary>
        public TerminalHub.Terminal.ReplaySnapshot BeginTerminalBufferReplay()
        {
            return _terminalBuffer.BeginReplay();
        }

        /// <summary>テール記録を終了し、スナップショット以降に届いた出力を順序どおり返す。</summary>
        public string EndTerminalBufferReplay(TerminalHub.Terminal.ReplaySnapshot snapshot)
        {
            return _terminalBuffer.EndReplay(snapshot);
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

        // 通知ベル（通知マーク）変更履歴（診断用）。
        // ベル自体は Root(Circuit=ブラウザ接続) ごとに独立管理のため「別ブラウザでは残っている」ことがある。
        // どのインスタンスが・何経由で 付けた/消した/抑止した かを記録し、意図しない再点灯の調査に使う。
        [System.Text.Json.Serialization.JsonIgnore]
        private readonly Queue<NotificationBellEntry> _bellChangeHistory = new();

        [System.Text.Json.Serialization.JsonIgnore]
        private readonly object _bellHistoryLock = new();

        private const int MaxBellHistoryCount = 200;

        public void RecordBellChange(string action, string? kind, string source, string instance)
        {
            lock (_bellHistoryLock)
            {
                if (_bellChangeHistory.Count >= MaxBellHistoryCount)
                {
                    _bellChangeHistory.Dequeue();
                }
                _bellChangeHistory.Enqueue(new NotificationBellEntry
                {
                    Timestamp = DateTime.Now,
                    Action = action,
                    Kind = kind,
                    Source = source,
                    Instance = instance
                });
            }
        }

        public List<NotificationBellEntry> GetBellChangeHistory()
        {
            lock (_bellHistoryLock)
            {
                return new List<NotificationBellEntry>(_bellChangeHistory);
            }
        }

        public int BellChangeHistoryCount
        {
            get
            {
                lock (_bellHistoryLock)
                {
                    return _bellChangeHistory.Count;
                }
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
    /// 通知ベル（通知マーク）変更履歴のエントリ（診断用）。
    /// </summary>
    public class NotificationBellEntry
    {
        public DateTime Timestamp { get; set; }
        /// <summary>ON（点灯）/ OFF（消灯）/ SUPPRESSED（上書き・点灯を抑止）</summary>
        public string Action { get; set; } = "";
        /// <summary>ベル種別（Stopped/Confirm/Select）。OFF のときは null</summary>
        public string? Kind { get; set; }
        /// <summary>何経由か（出力解析タイムアウト / Hook Stop / セッション選択 等）</summary>
        public string Source { get; set; } = "";
        /// <summary>Root インスタンス（Circuit=ブラウザ接続）の識別子。別ブラウザの残留を切り分ける</summary>
        public string Instance { get; set; } = "";
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