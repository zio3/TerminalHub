using TerminalHub.Models;
using TerminalHub.Constants;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using TerminalHub.Components.Shared;

namespace TerminalHub.Services
{
    public interface ISessionManager
    {
        /// <summary>
        /// セッションが変更されたときに発火するイベント（追加・削除・アーカイブなど）
        /// </summary>
        event EventHandler? OnSessionsChanged;

        Task<SessionInfo> CreateSessionAsync(string folderPath, string sessionName, TerminalType terminalType, Dictionary<string, string> options);
        Task<SessionInfo> CreateSessionAsync(Guid sessionGuid, string folderPath, string sessionName, TerminalType terminalType, Dictionary<string, string> options, bool skipGitInfo = false);

        /// <summary>
        /// セッションのGit情報を非同期で取得する
        /// </summary>
        Task PopulateGitInfoForSessionAsync(Guid sessionId);
        Task<bool> RemoveSessionAsync(Guid sessionId);
        Task<ConPtySession?> GetSessionAsync(Guid sessionId);

        /// <summary>
        /// 全セッションを取得（アーカイブ含む）
        /// </summary>
        IEnumerable<SessionInfo> GetAllSessions();

        /// <summary>
        /// アクティブなセッションのみ取得（アーカイブ除外）
        /// </summary>
        IEnumerable<SessionInfo> GetActiveSessions();

        SessionInfo? GetSessionInfo(Guid sessionId);

        /// <summary>
        /// セッションのメモ(一覧表示用の短い注釈)をインメモリで更新し、一覧の再描画を促す。
        /// MCP 等の非Circuitコンテキストから呼ぶ用途。永続化は呼び出し側で別途行う。
        /// 対象が見つかれば true。
        /// </summary>
        bool UpdateMemo(Guid sessionId, string memo);

        /// <summary>
        /// ConPTYからの最初のデータ受信時に呼び出し、接続処理中フラグを解除する
        /// </summary>
        void MarkSessionConnected(Guid sessionId);

        Task<bool> SetActiveSessionAsync(Guid sessionId);
        Guid? GetActiveSessionId();
        Task SaveSessionInfoAsync(SessionInfo sessionInfo);
        Task<SessionInfo?> CreateWorktreeSessionAsync(Guid parentSessionId, string branchName);
        Task<SessionInfo?> CreateWorktreeSessionAsync(Guid parentSessionId, string branchName, TerminalType terminalType, Dictionary<string, string>? options);
        Task<SessionInfo?> CreateWorktreeSessionAsync(Guid parentSessionId, string branchName, TerminalType terminalType, Dictionary<string, string>? options, string? folderSuffix, bool detached);
        Task<SessionInfo?> CreateSamePathSessionAsync(Guid parentSessionId, string folderPath, TerminalType terminalType, Dictionary<string, string>? options);
        Task<ConPtySession?> RecreateSessionAsync(Guid sessionId, bool removeContinueOption = false);
        Task<bool> RestartSessionAsync(Guid sessionId);

        /// <summary>
        /// セッションが存在するかどうか
        /// </summary>
        bool HasSessions();

        /// <summary>
        /// アーカイブセッションを追加（LocalStorageからの復元用）
        /// </summary>
        void AddArchivedSession(SessionInfo sessionInfo);

        /// <summary>
        /// セッションをアーカイブ
        /// </summary>
        void ArchiveSession(Guid sessionId);

        /// <summary>
        /// アーカイブからセッションを復元
        /// </summary>
        Task<SessionInfo?> RestoreArchivedSessionAsync(Guid sessionId);

        /// <summary>
        /// セッションを完全削除（アーカイブ含む）
        /// </summary>
        void DeleteSession(Guid sessionId);
    }

    public class SessionManager : ISessionManager, IDisposable
    {
        private readonly ConcurrentDictionary<Guid, ConPtySession> _sessions = new();
        private readonly ConcurrentDictionary<Guid, SessionInfo> _sessionInfos = new();
        private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _initializationLocks = new();
        private readonly IConPtyService _conPtyService;
        private readonly ILogger<SessionManager> _logger;
        private readonly IConfiguration _configuration;
        private readonly IGitService _gitService;
        private readonly IClaudeHookService? _claudeHookService;
        private readonly ICodexHookService? _codexHookService;
        private readonly IMcpConfigService? _mcpConfigService;
        private readonly IAppSettingsService? _appSettingsService;
        private readonly IServer? _server;
        private Guid? _activeSessionId;
        private readonly object _lockObject = new();

        /// <summary>
        /// セッションが変更されたときに発火するイベント
        /// </summary>
        public event EventHandler? OnSessionsChanged;

        public SessionManager(IConPtyService conPtyService, ILogger<SessionManager> logger, IConfiguration configuration, IGitService gitService, IClaudeHookService? claudeHookService = null, IAppSettingsService? appSettingsService = null, IServer? server = null, ICodexHookService? codexHookService = null, IRawStreamCaptureService? rawStreamCapture = null, IMcpConfigService? mcpConfigService = null)
        {
            _conPtyService = conPtyService;
            _logger = logger;
            _configuration = configuration;
            _gitService = gitService;
            _claudeHookService = claudeHookService;
            _appSettingsService = appSettingsService;
            _server = server;
            _codexHookService = codexHookService;
            _rawStreamCapture = rawStreamCapture;
            _mcpConfigService = mcpConfigService;
        }

        private readonly IRawStreamCaptureService? _rawStreamCapture;

        /// <summary>
        /// ConPTY 出力へのサーバー側シングルタップを取り付ける。
        /// 状態バッファへの Append と生ストリームキャプチャは、ここで（チャンクごとに1回だけ）行う。
        /// Circuit（ブラウザ接続）側で行うと、複数接続や切断後も残存する Circuit の数だけ
        /// 同じチャンクが多重に Append され、リプレイ時に行の重複・表示ズレが起きる。
        /// ConPtySession 生成直後（Circuit の購読より先）に登録するため、マルチキャストの
        /// 呼び出し順により Circuit のハンドラより先に実行され、args.CapturedByReplay を
        /// Circuit 側が参照できる。
        /// </summary>
        private void AttachServerSideBufferTap(SessionInfo sessionInfo, ConPtySession session)
        {
            session.DataReceived += (sender, args) =>
            {
                try
                {
                    if (_rawStreamCapture?.Enabled == true)
                    {
                        _rawStreamCapture.Capture(sessionInfo.SessionId, args.Data);
                    }
                    args.CapturedByReplay = sessionInfo.AppendToTerminalBuffer(args.Data);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "状態バッファへの取り込みに失敗: SessionId={SessionId}", sessionInfo.SessionId);
                }
            };
        }

        /// <summary>
        /// サーバーのベース URL を取得する（スキーム + ホスト + ポート）
        /// 複数アドレスがある場合は HTTP を優先する（Claude Code hook は自己署名 HTTPS 証明書を扱えないケースがあるため）
        /// 0.0.0.0 等のワイルドカードホストは localhost に置換する（LAN バインド対策）
        /// </summary>
        private string GetServerBaseUrl()
        {
            const string defaultUrl = "http://localhost:5081";

            if (_server == null)
            {
                _logger.LogWarning("IServer が利用できません。デフォルト URL {Url} を使用します", defaultUrl);
                return defaultUrl;
            }

            var addressesFeature = _server.Features.Get<IServerAddressesFeature>();
            if (addressesFeature == null || !addressesFeature.Addresses.Any())
            {
                _logger.LogWarning("サーバーアドレスが取得できません。デフォルト URL {Url} を使用します", defaultUrl);
                return defaultUrl;
            }

            // HTTP を優先して選ぶ。HTTPS しかなければ HTTPS を使う
            var address = addressesFeature.Addresses
                .OrderBy(a => a.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .First();

            try
            {
                var uri = new Uri(address);
                // 0.0.0.0, +, *, [::] などのワイルドカードホストは localhost に置換
                var host = uri.Host;
                if (host == "0.0.0.0" || host == "+" || host == "*" || host == "[::]" || string.IsNullOrEmpty(host))
                {
                    host = "localhost";
                }
                var baseUrl = $"{uri.Scheme}://{host}:{uri.Port}";
                _logger.LogDebug("サーバー URL を取得: {BaseUrl} (from {Address})", baseUrl, address);
                return baseUrl;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "アドレスから URL を解析できません: {Address}。デフォルト URL {Url} を使用します", address, defaultUrl);
                return defaultUrl;
            }
        }
        
        private string GetClaudeCmdPath()
        {
            var configuredPath = _configuration.GetValue<string>("ExternalTools:ClaudeCmdPath");
            return !string.IsNullOrEmpty(configuredPath) ? configuredPath : TerminalConstants.GetDefaultClaudeCmdPath();
        }

        private (string command, string args) BuildTerminalCommand(TerminalType terminalType, Dictionary<string, string> options)
        {
            switch (terminalType)
            {
                case TerminalType.ClaudeCode:
                    var claudeArgs = TerminalConstants.BuildClaudeCodeArgs(options);
                    var claudeCmdPath = GetClaudeCmdPath();

                    // ネイティブ版(.exe)は直接実行、npm版(.cmd)はcmd.exe経由
                    if (claudeCmdPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        // ネイティブ版: 直接実行
                        return (claudeCmdPath, claudeArgs);
                    }
                    else
                    {
                        // npm版: cmd.exe経由で実行
                        var args = string.IsNullOrWhiteSpace(claudeArgs)
                            ? $"/c \"{claudeCmdPath}\""
                            : $"/c \"{claudeCmdPath}\" {claudeArgs}";
                        return ("cmd.exe", args);
                    }

                // GeminiCLI は廃止: 既存セッションは default 分岐で通常ターミナルとして起動する

                case TerminalType.CodexCLI:
                    var codexArgs = TerminalConstants.BuildCodexArgs(options);
                    var codexArgsString = string.IsNullOrWhiteSpace(codexArgs)
                        ? "/k codex"
                        : $"/k codex {codexArgs}";
                    return ("cmd.exe", codexArgsString);

                case TerminalType.Antigravity:
                    var agyArgs = TerminalConstants.BuildAntigravityArgs(options);
                    var agyArgsString = string.IsNullOrWhiteSpace(agyArgs)
                        ? "/k agy"
                        : $"/k agy {agyArgs}";
                    return ("cmd.exe", agyArgsString);

                case TerminalType.Grok:
                    var grokArgs = TerminalConstants.BuildGrokArgs(options);
                    var grokArgsString = string.IsNullOrWhiteSpace(grokArgs)
                        ? "/k grok"
                        : $"/k grok {grokArgs}";
                    return ("cmd.exe", grokArgsString);

                default:
                    if (options.ContainsKey("command") && !string.IsNullOrWhiteSpace(options["command"]))
                    {
                        return (options["command"], "");
                    }
                    else
                    {
                        return (TerminalConstants.DefaultShell, "");
                    }
            }
        }


        public async Task<SessionInfo> CreateSessionAsync(string folderPath, string sessionName, TerminalType terminalType, Dictionary<string, string> options)
        {
            return await CreateSessionAsync(Guid.NewGuid(), folderPath, sessionName, terminalType, options, skipGitInfo: false);
        }

        public async Task<SessionInfo> CreateSessionAsync(Guid sessionGuid, string folderPath, string sessionName, TerminalType terminalType, Dictionary<string, string> options, bool skipGitInfo = false)
        {
            try
            {
                // フォルダパスが指定されていない場合はデフォルトを使用
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                folderPath = _configuration.GetValue<string>("SessionSettings:BasePath") 
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            
                // フォルダが存在しない場合は警告ログ（セッション情報は登録する）
                if (!Directory.Exists(folderPath))
                {
                    _logger.LogWarning("Directory not found: {FolderPath} - セッション情報は登録しますが、接続時にエラーになります", folderPath);
                }
            
            var sessionInfo = new SessionInfo
            {
                SessionId = sessionGuid,
                FolderPath = folderPath,
                FolderName = Path.GetFileName(folderPath),
                DisplayName = sessionName,
                TerminalType = terminalType,
                Options = options
            };

            // Git情報を非同期で取得してセッション情報に設定（skipGitInfo が false の場合のみ）
            if (!skipGitInfo)
            {
                await PopulateGitInfoAsync(sessionInfo);
            }

            // Note: ClaudeCode の hook 設定は GetSessionAsync で遅延セットアップされる

                // SessionInfoのみを登録（ConPtyセッションは遅延初期化）
                _sessionInfos[sessionInfo.SessionId] = sessionInfo;
                _initializationLocks[sessionInfo.SessionId] = new SemaphoreSlim(1, 1);

                NotifySessionsChanged();
                return sessionInfo;
            }
            catch (DirectoryNotFoundException)
            {
                // フォルダが存在しないエラーはそのまま再スロー
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create session for {FolderPath} with type {TerminalType}", folderPath, terminalType);
                
                // エラーメッセージをより具体的に
                string errorMessage = terminalType switch
                {
                    TerminalType.ClaudeCode => $"Claude Codeの起動に失敗しました。Claude Codeがインストールされているか確認してください。",
                    TerminalType.CodexCLI => $"Codex CLIの起動に失敗しました。Codex CLIがインストールされているか確認してください。",
                    TerminalType.Antigravity => $"Antigravity CLI (agy) の起動に失敗しました。インストールされているか確認してください。",
                    TerminalType.Grok => $"Grok CLI (grok) の起動に失敗しました。インストールされているか確認してください。",
                    _ => $"ターミナルの起動に失敗しました。"
                };
                
                if (ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
                {
                    errorMessage += " アクセスが拒否されました。管理者権限が必要かもしれません。";
                }
                
                throw new InvalidOperationException(errorMessage, ex);
            }
        }

        public Task<bool> RemoveSessionAsync(Guid sessionId)
        {
            ConPtySession? sessionToDispose = null;
            ConPtySession? sessionInfoConPtyToDispose = null;
            SemaphoreSlim? initLockToDispose = null;
            bool removed = false;

            lock (_lockObject)
            {
                if (_sessions.TryRemove(sessionId, out var session))
                {
                    sessionToDispose = session;
                    removed = true;
                }

                // SessionInfoを削除（ConPtySessionも破棄）
                if (_sessionInfos.TryRemove(sessionId, out var sessionInfo))
                {
                    // ConPtySessionを破棄（二重Dispose防止）
                    if (sessionInfo.ConPtySession != null &&
                        !ReferenceEquals(sessionInfo.ConPtySession, sessionToDispose))
                    {
                        sessionInfoConPtyToDispose = sessionInfo.ConPtySession;
                    }
                    sessionInfo.ConPtySession = null;
                    removed = true;
                }

                // 初期化ロックを削除
                if (_initializationLocks.TryRemove(sessionId, out var initLock))
                {
                    initLockToDispose = initLock;
                }

                // アクティブセッションの更新
                if (removed && _activeSessionId == sessionId)
                {
                    _activeSessionId = _sessionInfos.Keys.FirstOrDefault();
                }
            }

            // ロック外でDispose（デッドロック防止）
            sessionToDispose?.Dispose();
            sessionInfoConPtyToDispose?.Dispose();
            initLockToDispose?.Dispose();

            if (removed)
            {
                // 生ストリームキャプチャのライターも閉じる（ハンドルリーク防止）
                _rawStreamCapture?.CloseSession(sessionId);
                NotifySessionsChanged();
            }

            return Task.FromResult(removed);
        }

        public async Task<ConPtySession?> GetSessionAsync(Guid sessionId)
        {
            SessionInfo? sessionInfo;
            SemaphoreSlim? initLock;

            // SessionInfoとinitLockの取得をロックで保護
            lock (_lockObject)
            {
                // SessionInfoの存在を確認
                if (!_sessionInfos.TryGetValue(sessionId, out sessionInfo))
                {
                    return null;
                }

                // 既にConPtyセッションが存在する場合はそれを返す
                if (_sessions.TryGetValue(sessionId, out var existingSession))
                {
                    _logger.LogDebug("既存セッションを再利用: SessionId={SessionId}", sessionId);
                    return existingSession;
                }

                // 初期化ロックを取得
                initLock = _initializationLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
            }

            // ConPtyセッションがまだ初期化されていない場合は遅延初期化を実行
            try
            {
                await initLock.WaitAsync();

                // セマフォ取得後、セッションが削除されていないか再確認
                lock (_lockObject)
                {
                    if (!_sessionInfos.ContainsKey(sessionId))
                    {
                        _logger.LogWarning("セマフォ取得後にセッションが削除されていました: SessionId={SessionId}", sessionId);
                        return null;
                    }

                    // ダブルチェックロッキング
                    if (_sessions.TryGetValue(sessionId, out var existingSession))
                    {
                        _logger.LogDebug("既存セッションを再利用 (ダブルチェック): SessionId={SessionId}", sessionId);
                        return existingSession;
                    }

                    // 接続処理中フラグを立てる
                    sessionInfo.IsConnecting = true;
                }

                NotifySessionsChanged();

                // 新規セッション起動時は HasContinueErrorOccurred フラグをリセット
                // （新しいセッションで --continue を再度試行できるようにする）
                if (sessionInfo.HasContinueErrorOccurred)
                {
                    sessionInfo.HasContinueErrorOccurred = false;
                }

                // ClaudeCode セッションの場合、ConPty 起動前に hook 設定をセットアップ
                await SetupClaudeHookIfNeededAsync(sessionInfo, isResetup: false);
                await SetupCodexHookIfNeededAsync(sessionInfo, isResetup: false);
                await SetupMcpConfigIfNeededAsync(sessionInfo, isResetup: false);

                // ConPtyセッションを初期化
                var cols = _configuration.GetValue<int>("SessionSettings:DefaultCols", TerminalConstants.DefaultCols);
                var rows = _configuration.GetValue<int>("SessionSettings:DefaultRows", TerminalConstants.DefaultRows);

                var (command, args) = BuildTerminalCommand(sessionInfo.TerminalType, sessionInfo.Options);

                _logger.LogInformation("新規セッション接続開始: SessionId={SessionId}", sessionId);
                var newSession = await _conPtyService.CreateSessionAsync(command, args, sessionInfo.FolderPath, cols, rows, sessionInfo.SessionId);
                AttachServerSideBufferTap(sessionInfo, newSession);

                // ConPtySession登録をロック内で実行
                lock (_lockObject)
                {
                    // 登録前に再度セッションが削除されていないか確認
                    if (!_sessionInfos.ContainsKey(sessionId))
                    {
                        _logger.LogWarning("ConPtySession作成後にセッションが削除されていました: SessionId={SessionId}", sessionId);
                        newSession.Dispose();
                        return null;
                    }

                    _sessions[sessionId] = newSession;
                    // ConPtySessionをSessionInfoに設定
                    sessionInfo.ConPtySession = newSession;
                }

                // Startメソッドを呼ぶ
                newSession.Start();
                // 初期サイズを設定
                newSession.Resize(cols, rows);

                _logger.LogInformation("新規セッション接続完了: SessionId={SessionId}", sessionId);
                _logger.LogInformation($"ConPty session with buffer initialized on-demand: {sessionId}");

                return newSession;
            }
            catch (Exception ex)
            {
                // 接続処理中フラグをリセット（UIが「接続中...」のまま残るのを防止）
                lock (_lockObject)
                {
                    if (_sessionInfos.TryGetValue(sessionId, out var failedSession))
                    {
                        failedSession.IsConnecting = false;
                    }
                }
                NotifySessionsChanged();
                _logger.LogError(ex, "Failed to initialize ConPty session on-demand for {SessionId}", sessionId);
                throw;
            }
            finally
            {
                initLock.Release();
            }
        }

        public IEnumerable<SessionInfo> GetAllSessions()
        {
            return _sessionInfos.Values.OrderBy(s => s.CreatedAt);
        }

        public IEnumerable<SessionInfo> GetActiveSessions()
        {
            return _sessionInfos.Values.Where(s => !s.IsArchived).OrderBy(s => s.CreatedAt);
        }

        public SessionInfo? GetSessionInfo(Guid sessionId)
        {
            return _sessionInfos.TryGetValue(sessionId, out var info) ? info : null;
        }

        public bool UpdateMemo(Guid sessionId, string memo)
        {
            if (!_sessionInfos.TryGetValue(sessionId, out var info))
                return false;

            info.Memo = memo ?? string.Empty;
            // 開いている一覧を再描画させる（Root.razor が OnSessionsChanged を購読し StateHasChanged する）。
            NotifySessionsChanged();
            return true;
        }

        /// <summary>
        /// ConPTYからの最初のデータ受信時に呼び出し、接続処理中フラグを解除する
        /// </summary>
        public void MarkSessionConnected(Guid sessionId)
        {
            if (_sessionInfos.TryGetValue(sessionId, out var sessionInfo) && sessionInfo.IsConnecting)
            {
                sessionInfo.IsConnecting = false;
                NotifySessionsChanged();
            }
        }

        public Task<bool> SetActiveSessionAsync(Guid sessionId)
        {
            lock (_lockObject)
            {
                if (_sessionInfos.ContainsKey(sessionId))
                {
                    if (_activeSessionId.HasValue && _sessionInfos.TryGetValue(_activeSessionId.Value, out var oldActive))
                    {
                        oldActive.IsActive = false;
                    }

                    _activeSessionId = sessionId;
                    if (_sessionInfos.TryGetValue(sessionId, out var newActive))
                    {
                        newActive.IsActive = true;
                    }

                    return Task.FromResult(true);
                }
                return Task.FromResult(false);
            }
        }

        public Guid? GetActiveSessionId()
        {
            lock (_lockObject)
            {
                return _activeSessionId;
            }
        }

        public Task SaveSessionInfoAsync(SessionInfo sessionInfo)
        {
            if (_sessionInfos.ContainsKey(sessionInfo.SessionId))
            {
                _sessionInfos[sessionInfo.SessionId] = sessionInfo;
            }
            return Task.CompletedTask;
        }

        private async Task PopulateGitInfoAsync(SessionInfo sessionInfo)
        {
            try
            {
                var gitInfo = await _gitService.GetGitInfoAsync(sessionInfo.FolderPath);
                if (gitInfo != null)
                {
                    sessionInfo.IsGitRepository = true;
                    sessionInfo.GitBranch = gitInfo.CurrentBranch;
                    sessionInfo.HasUncommittedChanges = gitInfo.HasUncommittedChanges;
                    sessionInfo.IsWorktree = gitInfo.IsWorktree;

                    _logger.LogDebug("Git情報を取得しました: {Path}, ブランチ: {Branch}, Worktree: {IsWorktree}",
                        sessionInfo.FolderPath, gitInfo.CurrentBranch, gitInfo.IsWorktree);
                }
                else
                {
                    sessionInfo.IsGitRepository = false;
                    _logger.LogDebug("Gitリポジトリではありません: {Path}", sessionInfo.FolderPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Git情報の取得に失敗しました: {Path}", sessionInfo.FolderPath);
                sessionInfo.IsGitRepository = false;
            }
        }

        /// <summary>
        /// セッションのGit情報を非同期で取得する（公開メソッド）
        /// </summary>
        public async Task PopulateGitInfoForSessionAsync(Guid sessionId)
        {
            var sessionInfo = GetSessionInfo(sessionId);
            if (sessionInfo != null)
            {
                await PopulateGitInfoAsync(sessionInfo);
            }
        }

        public async Task<SessionInfo?> CreateWorktreeSessionAsync(Guid parentSessionId, string branchName)
        {
            return await CreateWorktreeSessionAsync(parentSessionId, branchName, TerminalType.Terminal, null, null, false);
        }

        public async Task<SessionInfo?> CreateWorktreeSessionAsync(Guid parentSessionId, string branchName, TerminalType terminalType = TerminalType.Terminal, Dictionary<string, string>? options = null)
        {
            return await CreateWorktreeSessionAsync(parentSessionId, branchName, terminalType, options, null, false);
        }

        public async Task<SessionInfo?> CreateWorktreeSessionAsync(Guid parentSessionId, string branchName, TerminalType terminalType, Dictionary<string, string>? options, string? folderSuffix, bool detached)
        {
            try
            {
                // 親セッションを取得
                var parentSession = GetSessionInfo(parentSessionId);
                if (parentSession == null)
                {
                    _logger.LogWarning("親セッションが見つかりません: {ParentSessionId}", parentSessionId);
                    return null;
                }

                if (!parentSession.IsGitRepository)
                {
                    _logger.LogWarning("親セッションはGitリポジトリではありません: {ParentSessionId}", parentSessionId);
                    return null;
                }

                // detached以外のときだけ、既存ブランチに紐づくworktreeを再利用
                if (!detached)
                {
                    var existingWorktrees = await _gitService.GetWorktreeListAsync(parentSession.FolderPath);
                    var existingWorktree = existingWorktrees.FirstOrDefault(w => w.BranchName == branchName);
                    if (existingWorktree != null)
                    {
                        _logger.LogInformation("既存のWorktreeを使用します: ブランチ={Branch}, パス={Path}", branchName, existingWorktree.Path);

                        var existingWorktreeSessionInfo = new SessionInfo
                        {
                            SessionId = Guid.NewGuid(),
                            FolderPath = existingWorktree.Path,
                            FolderName = Path.GetFileName(existingWorktree.Path),
                            DisplayName = branchName,
                            TerminalType = terminalType,
                            Options = options ?? new Dictionary<string, string>(),
                            ParentSessionId = parentSessionId
                        };

                        await PopulateGitInfoAsync(existingWorktreeSessionInfo);

                        _sessionInfos.TryAdd(existingWorktreeSessionInfo.SessionId, existingWorktreeSessionInfo);
                        _initializationLocks[existingWorktreeSessionInfo.SessionId] = new SemaphoreSlim(1, 1);

                        _logger.LogInformation("既存Worktreeセッション情報作成成功: ブランチ={Branch}, パス={Path}, セッションID={SessionId}",
                            branchName, existingWorktree.Path, existingWorktreeSessionInfo.SessionId);

                        return existingWorktreeSessionInfo;
                    }
                }

                // Worktreeの作成先パスを決定（常に親と同じ階層に作成）
                var parentPath = parentSession.FolderPath;
                if (parentPath.EndsWith(Path.DirectorySeparatorChar) || parentPath.EndsWith(Path.AltDirectorySeparatorChar))
                {
                    parentPath = parentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }

                var parentName = Path.GetFileName(parentPath);
                var parentDir = Path.GetDirectoryName(parentPath);
                if (string.IsNullOrEmpty(parentDir))
                {
                    parentDir = Path.GetDirectoryName(Path.GetFullPath(parentPath)) ?? parentPath;
                }

                // フォルダ名サフィックスを決定:
                //  ユーザー入力 > (detachedかつ未入力なら "worktree-{序数}") > ブランチ名
                string folderSegment;
                if (!string.IsNullOrWhiteSpace(folderSuffix))
                {
                    folderSegment = folderSuffix.Trim();
                }
                else if (detached)
                {
                    // 親階層内で未使用の "worktree-{N}" を探す
                    int idx = 1;
                    while (Directory.Exists(Path.Combine(parentDir, $"{parentName}-worktree-{idx}")))
                    {
                        idx++;
                    }
                    folderSegment = $"worktree-{idx}";
                }
                else
                {
                    // ブランチ名の "/" はフォルダ名として不正なので "-" に置換する
                    folderSegment = branchName.Replace('/', '-').Replace('\\', '-');
                }

                var worktreeName = $"{parentName}-{folderSegment}";
                var worktreePath = Path.Combine(parentDir, worktreeName);

                // 念のため衝突した場合の連番フォールバック
                int counter = 1;
                var originalWorktreePath = worktreePath;
                while (Directory.Exists(worktreePath))
                {
                    worktreePath = $"{originalWorktreePath}-{counter}";
                    counter++;
                }

                // Worktreeを作成
                var success = await _gitService.CreateWorktreeAsync(parentPath, branchName, worktreePath, detached);
                if (!success)
                {
                    _logger.LogWarning("Worktree作成に失敗しました: detach={Detach}, ブランチ={Branch}, パス={Path}", detached, branchName, worktreePath);
                    return null;
                }

                // 表示名: detached モードはフォルダ末尾セグメント (worktree-1 等)、ブランチモードはブランチ名
                var worktreeSessionInfo = new SessionInfo
                {
                    SessionId = Guid.NewGuid(),
                    FolderPath = worktreePath,
                    FolderName = Path.GetFileName(worktreePath),
                    DisplayName = detached ? folderSegment : branchName,
                    TerminalType = terminalType,
                    Options = options ?? new Dictionary<string, string>(),
                    ParentSessionId = parentSessionId
                };

                await PopulateGitInfoAsync(worktreeSessionInfo);

                _sessionInfos.TryAdd(worktreeSessionInfo.SessionId, worktreeSessionInfo);
                _initializationLocks[worktreeSessionInfo.SessionId] = new SemaphoreSlim(1, 1);

                _logger.LogInformation("Worktreeセッション情報作成成功: detach={Detach}, ブランチ={Branch}, パス={Path}, セッションID={SessionId}",
                    detached, branchName, worktreePath, worktreeSessionInfo.SessionId);

                return worktreeSessionInfo;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worktreeセッション作成でエラーが発生しました: 親セッション={ParentSessionId}, ブランチ={Branch}",
                    parentSessionId, branchName);
                return null;
            }
        }

        public async Task<SessionInfo?> CreateSamePathSessionAsync(Guid parentSessionId, string folderPath, TerminalType terminalType, Dictionary<string, string>? options = null)
        {
            try
            {
                // 親セッションを取得
                var parentSession = GetSessionInfo(parentSessionId);
                if (parentSession == null)
                {
                    _logger.LogWarning("親セッションが見つかりません: {ParentSessionId}", parentSessionId);
                    return null;
                }

                // 新しいセッション情報を作成
                var sessionInfo = new SessionInfo
                {
                    SessionId = Guid.NewGuid(),
                    FolderPath = folderPath,
                    FolderName = Path.GetFileName(folderPath),
                    DisplayName = Path.GetFileName(folderPath),
                    TerminalType = terminalType,
                    Options = options ?? new Dictionary<string, string>(),
                    ParentSessionId = parentSessionId
                };

                // セッション種類に応じた表示名を設定
                sessionInfo.DisplayName = terminalType switch
                {
                    TerminalType.ClaudeCode => $"{sessionInfo.FolderName} (Claude)",
                    TerminalType.CodexCLI => $"{sessionInfo.FolderName} (Codex)",
                    TerminalType.Antigravity => $"{sessionInfo.FolderName} (Antigravity)",
                    TerminalType.Grok => $"{sessionInfo.FolderName} (Grok)",
                    _ => sessionInfo.FolderName
                };

                // Git情報を設定
                await PopulateGitInfoAsync(sessionInfo);

                // SessionInfoのみを登録（ConPtyセッションは遅延初期化）
                _sessionInfos.TryAdd(sessionInfo.SessionId, sessionInfo);
                _initializationLocks[sessionInfo.SessionId] = new SemaphoreSlim(1, 1);

                _logger.LogInformation("同じパスでセッション情報作成成功: パス={Path}, タイプ={Type}, セッションID={SessionId}",
                    folderPath, terminalType, sessionInfo.SessionId);

                return sessionInfo;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同じパスでのセッション作成でエラーが発生しました: 親セッション={ParentSessionId}, パス={Path}",
                    parentSessionId, folderPath);
                return null;
            }
        }

        /// <summary>
        /// 既存セッションを破棄する共通処理
        /// </summary>
        private async Task DisposeExistingSessionAsync(Guid sessionId, SessionInfo sessionInfo)
        {
            ConPtySession? sessionToDispose = null;
            ConPtySession? sessionInfoConPtyToDispose = null;

            lock (_lockObject)
            {
                if (_sessions.TryRemove(sessionId, out var currentSession))
                {
                    sessionToDispose = currentSession;
                }

                if (sessionInfo.ConPtySession != null)
                {
                    // 二重Dispose防止
                    if (!ReferenceEquals(sessionInfo.ConPtySession, sessionToDispose))
                    {
                        sessionInfoConPtyToDispose = sessionInfo.ConPtySession;
                    }
                    sessionInfo.ConPtySession = null;
                }
            }

            // ロック外でDispose（デッドロック防止）
            if (sessionToDispose != null || sessionInfoConPtyToDispose != null)
            {
                sessionToDispose?.Dispose();
                sessionInfoConPtyToDispose?.Dispose();
                _logger.LogInformation("既存のConPtySessionを破棄しました: {SessionId}", sessionId);

                // リソースのクリーンアップを待つ
                await Task.Delay(100);
            }
        }

        /// <summary>
        /// ClaudeCode セッション初期化／再起動の直前に呼び出し、
        /// <c>.claude/settings.local.json</c> に hook 設定を追加する共通処理（Hook は常時有効）。
        ///
        /// 反映タイミング: このメソッドはセッション初期化・再作成・再起動の 3 箇所からしか
        /// 呼ばれないので、設定トグルを切り替えた瞬間には反映されない。
        /// 各ワークスペースは対応するセッションが次に起動／再起動された時点で追従する。
        /// UI 側 (SettingsDialog) でもその旨を利用者に告知している。
        /// </summary>
        /// <param name="sessionInfo">セッション情報</param>
        /// <param name="isResetup">再セットアップかどうか（true: 常に実行、false: HookConfigured が false の場合のみ実行）</param>
        private async Task SetupClaudeHookIfNeededAsync(SessionInfo sessionInfo, bool isResetup = false)
        {
            if (sessionInfo.TerminalType != TerminalType.ClaudeCode || _claudeHookService == null)
                return;

            // Hook は常時有効（UI トグルは廃止）。聞かずに自動セットアップする。
            // 初回セットアップの場合は既に設定済みならスキップ
            if (!isResetup && sessionInfo.HookConfigured)
                return;

            try
            {
                var baseUrl = GetServerBaseUrl();
                await _claudeHookService.SetupHooksAsync(sessionInfo.SessionId, sessionInfo.FolderPath, baseUrl);
                sessionInfo.HookConfigured = true;
                var action = isResetup ? "再セットアップ" : "セットアップ";
                _logger.LogInformation($"Hook 設定を{action}: SessionId={{SessionId}}, BaseUrl={{BaseUrl}}", sessionInfo.SessionId, baseUrl);
            }
            catch (Exception ex)
            {
                var action = isResetup ? "再セットアップ" : "セットアップ";
                _logger.LogWarning(ex, $"Hook 設定の{action}に失敗しましたが、セッション処理は続行します: SessionId={{SessionId}}", sessionInfo.SessionId);
            }
        }

        /// <summary>
        /// CodexCLI セッション初期化／再起動の直前に呼び出し、
        /// <c>.codex/hooks.json</c> に hook 設定を追加する。Claude 版（SetupClaudeHookIfNeededAsync）と対称。
        /// Hook は常時有効（UI トグルは廃止）。
        /// </summary>
        private async Task SetupCodexHookIfNeededAsync(SessionInfo sessionInfo, bool isResetup = false)
        {
            if (sessionInfo.TerminalType != TerminalType.CodexCLI || _codexHookService == null)
                return;

            // Hook は常時有効（UI トグルは廃止）。聞かずに自動セットアップする。
            if (!isResetup && sessionInfo.HookConfigured)
                return;

            try
            {
                var baseUrl = GetServerBaseUrl();
                await _codexHookService.SetupHooksAsync(sessionInfo.SessionId, sessionInfo.FolderPath, baseUrl);
                sessionInfo.HookConfigured = true;
                var action = isResetup ? "再セットアップ" : "セットアップ";
                _logger.LogInformation($"Codex Hook 設定を{action}: SessionId={{SessionId}}, BaseUrl={{BaseUrl}}", sessionInfo.SessionId, baseUrl);
            }
            catch (Exception ex)
            {
                var action = isResetup ? "再セットアップ" : "セットアップ";
                _logger.LogWarning(ex, $"Codex Hook 設定の{action}に失敗しましたが、セッション処理は続行します: SessionId={{SessionId}}", sessionInfo.SessionId);
            }
        }

        /// <summary>
        /// 試験機能: セッション生成/再起動時に、対応CLI(Claude Code / Codex)のフォルダへ TerminalHub の
        /// ローカル MCP サーバー(terminalhub)を自動登録する。設定 Experimental.AutoRegisterMcp が ON の
        /// ときのみ実行（既定OFF）。Hook セットアップと対称。失敗してもセッション処理は続行する。
        /// </summary>
        private async Task SetupMcpConfigIfNeededAsync(SessionInfo sessionInfo, bool isResetup = false)
        {
            if (_mcpConfigService == null || _appSettingsService == null)
                return;

            // Claude Code / Codex のみサポート。
            if (sessionInfo.TerminalType != TerminalType.ClaudeCode &&
                sessionInfo.TerminalType != TerminalType.CodexCLI)
                return;

            if (!_appSettingsService.GetSettings().Experimental.AutoRegisterMcp)
                return;

            // Hook と同じく、プロセス内では初回のみ書く。McpConfigured は [JsonIgnore] で
            // TerminalHub 再起動時に false へ戻るため、ポートが変わっても再起動後の初回で追従する。
            if (!isResetup && sessionInfo.McpConfigured)
                return;

            try
            {
                var baseUrl = GetServerBaseUrl();
                await _mcpConfigService.SetupAsync(sessionInfo.FolderPath, sessionInfo.TerminalType, baseUrl);
                sessionInfo.McpConfigured = true;
                var action = isResetup ? "再登録" : "登録";
                _logger.LogInformation("MCP自動{Action}: SessionId={SessionId}, Type={Type}, BaseUrl={BaseUrl}",
                    action, sessionInfo.SessionId, sessionInfo.TerminalType, baseUrl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MCP自動登録に失敗しましたが、セッション処理は続行します: SessionId={SessionId}", sessionInfo.SessionId);
            }
        }

        /// <summary>
        /// セッションオプションを準備（--continueオプションの除外判定含む）
        /// </summary>
        private Dictionary<string, string> PrepareSessionOptions(SessionInfo sessionInfo, bool removeContinueOption = false)
        {
            var options = sessionInfo.Options ?? new Dictionary<string, string>();
            var hasContinueOption = options.ContainsKey("continue");

            if ((removeContinueOption || sessionInfo.HasContinueErrorOccurred) && hasContinueOption)
            {
                options = new Dictionary<string, string>(options);
                options.Remove("continue");
            }
            return options;
        }

        public async Task<ConPtySession?> RecreateSessionAsync(Guid sessionId, bool removeContinueOption = false)
        {
            try
            {
                var sessionInfo = GetSessionInfo(sessionId);
                if (sessionInfo == null)
                {
                    _logger.LogWarning("再作成するセッションが見つかりません: {SessionId}", sessionId);
                    return null;
                }

                await DisposeExistingSessionAsync(sessionId, sessionInfo);

                // セッション再作成時はバッファと状態をクリア
                sessionInfo.ClearTerminalBuffer();
                sessionInfo.ProcessingStatus = null;
                sessionInfo.ProcessingStartTime = null;
                sessionInfo.ProcessingElapsedSeconds = null;
                sessionInfo.LastProcessingUpdateTime = null;
                sessionInfo.ClearWaitingForUserInput();
                // 再起動で全サブエージェントは消えるため、稼働追跡もリセット（取りこぼし時の復旧経路）
                sessionInfo.ClearRunningSubagents();

                // ClaudeCode セッションの場合、Hook 設定を再セットアップ
                await SetupClaudeHookIfNeededAsync(sessionInfo, isResetup: true);
                await SetupCodexHookIfNeededAsync(sessionInfo, isResetup: true);
                await SetupMcpConfigIfNeededAsync(sessionInfo, isResetup: true);

                var cols = _configuration.GetValue<int>("SessionSettings:DefaultCols", TerminalConstants.DefaultCols);
                var rows = _configuration.GetValue<int>("SessionSettings:DefaultRows", TerminalConstants.DefaultRows);
                var options = PrepareSessionOptions(sessionInfo, removeContinueOption);
                var (command, args) = BuildTerminalCommand(sessionInfo.TerminalType, options);

                // 新しいセッションを作成（Startは呼ばない）
                ConPtySession newSession = await _conPtyService.CreateSessionAsync(command, args, sessionInfo.FolderPath, cols, rows, sessionInfo.SessionId);
                AttachServerSideBufferTap(sessionInfo, newSession);
                _sessions[sessionId] = newSession;
                sessionInfo.ConPtySession = newSession;

                _logger.LogInformation("新しいConPtySessionを作成しました（未起動）: {SessionId}", sessionId);
                return newSession;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "セッション再作成でエラーが発生しました: {SessionId}", sessionId);
                return null;
            }
        }

        public async Task<bool> RestartSessionAsync(Guid sessionId)
        {
            try
            {
                var sessionInfo = GetSessionInfo(sessionId);
                if (sessionInfo == null)
                {
                    _logger.LogWarning("再起動するセッションが見つかりません: {SessionId}", sessionId);
                    return false;
                }

                await DisposeExistingSessionAsync(sessionId, sessionInfo);

                // セッション再起動時はバッファと状態をクリア
                sessionInfo.ClearTerminalBuffer();
                sessionInfo.ProcessingStatus = null;
                sessionInfo.ProcessingStartTime = null;
                sessionInfo.ProcessingElapsedSeconds = null;
                sessionInfo.LastProcessingUpdateTime = null;
                sessionInfo.ClearWaitingForUserInput();
                // 再起動で全サブエージェントは消えるため、稼働追跡もリセット（取りこぼし時の復旧経路）
                sessionInfo.ClearRunningSubagents();

                // セッション再起動時は HasContinueErrorOccurred フラグをリセット
                // （新しいセッションで --continue を再度試行できるようにする）
                if (sessionInfo.HasContinueErrorOccurred)
                {
                    sessionInfo.HasContinueErrorOccurred = false;
                }

                // ClaudeCode セッションの場合、Hook 設定を再セットアップ
                await SetupClaudeHookIfNeededAsync(sessionInfo, isResetup: true);
                await SetupCodexHookIfNeededAsync(sessionInfo, isResetup: true);
                await SetupMcpConfigIfNeededAsync(sessionInfo, isResetup: true);

                var cols = _configuration.GetValue<int>("SessionSettings:DefaultCols", TerminalConstants.DefaultCols);
                var rows = _configuration.GetValue<int>("SessionSettings:DefaultRows", TerminalConstants.DefaultRows);
                var options = PrepareSessionOptions(sessionInfo);
                var (command, args) = BuildTerminalCommand(sessionInfo.TerminalType, options);

                // 新しいセッションを作成して起動
                ConPtySession newSession = await _conPtyService.CreateSessionAsync(command, args, sessionInfo.FolderPath, cols, rows, sessionInfo.SessionId);
                AttachServerSideBufferTap(sessionInfo, newSession);
                _sessions[sessionId] = newSession;
                sessionInfo.ConPtySession = newSession;
                newSession.Start();
                // 状態バッファのグリッドも ConPTY と同サイズに揃える（寸法不一致による折返し乱れを防ぐ）
                sessionInfo.ResizeTerminalBuffer(cols, rows);
                newSession.Resize(cols, rows);

                _logger.LogInformation("セッション再起動成功: {SessionId}, タイプ: {Type}", sessionId, sessionInfo.TerminalType);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "セッション再起動でエラーが発生しました: {SessionId}", sessionId);
                return false;
            }
        }

        /// <summary>
        /// セッションが存在するかどうか
        /// </summary>
        public bool HasSessions()
        {
            return !_sessionInfos.IsEmpty;
        }

        /// <summary>
        /// アーカイブセッションを追加（LocalStorageからの復元用）
        /// </summary>
        public void AddArchivedSession(SessionInfo sessionInfo)
        {
            if (sessionInfo == null) return;

            // アーカイブフラグを確認
            if (!sessionInfo.IsArchived)
            {
                sessionInfo.IsArchived = true;
                sessionInfo.ArchivedAt = DateTime.Now;
            }

            _sessionInfos[sessionInfo.SessionId] = sessionInfo;
            NotifySessionsChanged();
        }

        /// <summary>
        /// セッションをアーカイブ
        /// </summary>
        public void ArchiveSession(Guid sessionId)
        {
            ConPtySession? conPtySessionToDispose = null;
            ConPtySession? sessionInfoConPtyToDispose = null;
            SemaphoreSlim? initLockToDispose = null;
            bool shouldNotify = false;

            lock (_lockObject)
            {
                if (!_sessionInfos.TryGetValue(sessionId, out var sessionInfo))
                {
                    _logger.LogWarning("アーカイブするセッションが見つかりません: {SessionId}", sessionId);
                    return;
                }

                // 二重アーカイブ防止
                if (sessionInfo.IsArchived)
                {
                    _logger.LogWarning("セッションは既にアーカイブされています: {SessionId}", sessionId);
                    return;
                }

                // ConPtyセッションを取得（Disposeはロック外で実行）
                if (_sessions.TryRemove(sessionId, out var conPtySession))
                {
                    conPtySessionToDispose = conPtySession;
                }

                if (sessionInfo.ConPtySession != null)
                {
                    // 二重Dispose防止
                    if (!ReferenceEquals(sessionInfo.ConPtySession, conPtySessionToDispose))
                    {
                        sessionInfoConPtyToDispose = sessionInfo.ConPtySession;
                    }
                    sessionInfo.ConPtySession = null;
                }

                // 初期化ロックを削除
                if (_initializationLocks.TryRemove(sessionId, out var initLock))
                {
                    initLockToDispose = initLock;
                }

                // アーカイブフラグを設定
                sessionInfo.IsArchived = true;
                sessionInfo.ArchivedAt = DateTime.Now;
                shouldNotify = true;

                _logger.LogInformation("セッションをアーカイブしました: {SessionId}", sessionId);
            }

            // ロック外でDispose（デッドロック防止）
            conPtySessionToDispose?.Dispose();
            sessionInfoConPtyToDispose?.Dispose();
            initLockToDispose?.Dispose();

            if (shouldNotify)
            {
                NotifySessionsChanged();
            }
        }

        /// <summary>
        /// アーカイブからセッションを復元
        /// </summary>
        public async Task<SessionInfo?> RestoreArchivedSessionAsync(Guid sessionId)
        {
            if (!_sessionInfos.TryGetValue(sessionId, out var archivedSession))
            {
                _logger.LogWarning("復元するセッションが見つかりません: {SessionId}", sessionId);
                return null;
            }

            if (!archivedSession.IsArchived)
            {
                _logger.LogWarning("セッションはアーカイブされていません: {SessionId}", sessionId);
                return archivedSession;
            }

            // フォルダが存在するか確認
            if (!Directory.Exists(archivedSession.FolderPath))
            {
                _logger.LogWarning("セッションのフォルダが存在しません: {FolderPath}", archivedSession.FolderPath);
                return null;
            }

            // アーカイブフラグをクリア
            archivedSession.IsArchived = false;
            archivedSession.ArchivedAt = null;

            // 初期化ロックを作成
            _initializationLocks[sessionId] = new SemaphoreSlim(1, 1);

            // Git情報を更新
            await PopulateGitInfoForSessionAsync(sessionId);

            _logger.LogInformation("セッションを復元しました: {SessionId}", sessionId);

            NotifySessionsChanged();

            return archivedSession;
        }

        /// <summary>
        /// セッションを完全削除（アーカイブ含む）
        /// </summary>
        public void DeleteSession(Guid sessionId)
        {
            ConPtySession? sessionToDispose = null;
            ConPtySession? sessionInfoConPtyToDispose = null;
            SemaphoreSlim? initLockToDispose = null;
            bool deleted = false;

            lock (_lockObject)
            {
                // ConPtyセッションが存在する場合は取得
                if (_sessions.TryRemove(sessionId, out var session))
                {
                    sessionToDispose = session;
                    deleted = true;
                }

                // SessionInfoを削除
                if (_sessionInfos.TryRemove(sessionId, out var sessionInfo))
                {
                    // 二重Dispose防止
                    if (sessionInfo.ConPtySession != null &&
                        !ReferenceEquals(sessionInfo.ConPtySession, sessionToDispose))
                    {
                        sessionInfoConPtyToDispose = sessionInfo.ConPtySession;
                    }
                    sessionInfo.ConPtySession = null;
                    deleted = true;
                }

                // 初期化ロックを削除
                if (_initializationLocks.TryRemove(sessionId, out var initLock))
                {
                    initLockToDispose = initLock;
                }

                // アクティブセッションの更新
                if (deleted && _activeSessionId == sessionId)
                {
                    _activeSessionId = _sessionInfos.Keys.FirstOrDefault();
                }
            }

            // ロック外でDispose（デッドロック防止）
            sessionToDispose?.Dispose();
            sessionInfoConPtyToDispose?.Dispose();
            initLockToDispose?.Dispose();

            if (deleted)
            {
                _logger.LogInformation("セッションを完全削除しました: {SessionId}", sessionId);
                NotifySessionsChanged();
            }
        }

        /// <summary>
        /// セッション変更イベントを発火
        /// </summary>
        private void NotifySessionsChanged()
        {
            try
            {
                OnSessionsChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "セッション変更イベントの発火中にエラーが発生しました");
            }
        }

        public void Dispose()
        {
            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }
            _sessions.Clear();

            // ConPtySessionを破棄
            foreach (var sessionInfo in _sessionInfos.Values)
            {
                sessionInfo.ConPtySession?.Dispose();
            }
            _sessionInfos.Clear();

            foreach (var initLock in _initializationLocks.Values)
            {
                initLock.Dispose();
            }
            _initializationLocks.Clear();
        }
        
    }
}
