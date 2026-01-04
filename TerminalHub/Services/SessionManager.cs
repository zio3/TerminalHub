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
        Task<SessionInfo> CreateSessionAsync(string folderPath, string sessionName, TerminalType terminalType, Dictionary<string, string> options);
        Task<SessionInfo> CreateSessionAsync(Guid sessionGuid, string folderPath, string sessionName, TerminalType terminalType, Dictionary<string, string> options, bool skipGitInfo = false);

        /// <summary>
        /// セッションのGit情報を非同期で取得する
        /// </summary>
        Task PopulateGitInfoForSessionAsync(Guid sessionId);
        Task<bool> RemoveSessionAsync(Guid sessionId);
        Task<ConPtySession?> GetSessionAsync(Guid sessionId);
        IEnumerable<SessionInfo> GetAllSessions();
        SessionInfo? GetSessionInfo(Guid sessionId);
        Task<bool> SetActiveSessionAsync(Guid sessionId);
        Guid? GetActiveSessionId();
        Task SaveSessionInfoAsync(SessionInfo sessionInfo);
        Task<SessionInfo?> CreateWorktreeSessionAsync(Guid parentSessionId, string branchName);
        Task<SessionInfo?> CreateWorktreeSessionAsync(Guid parentSessionId, string branchName, TerminalType terminalType, Dictionary<string, string>? options);
        Task<SessionInfo?> CreateWorktreeSessionWithExistingBranchAsync(Guid parentSessionId, string branchName, TerminalType terminalType, Dictionary<string, string>? options);
        Task<SessionInfo?> CreateSamePathSessionAsync(Guid parentSessionId, string folderPath, TerminalType terminalType, Dictionary<string, string>? options);
        Task<ConPtySession?> RecreateSessionAsync(Guid sessionId, bool removeContinueOption = false);
        Task<bool> RestartSessionAsync(Guid sessionId);
        // event EventHandler<string>? ActiveSessionChanged;
        
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
        private readonly IServer? _server;
        private Guid? _activeSessionId;
        private readonly object _lockObject = new();
        private readonly int _maxSessions;

        // public event EventHandler<string>? ActiveSessionChanged;

        public SessionManager(IConPtyService conPtyService, ILogger<SessionManager> logger, IConfiguration configuration, IGitService gitService, IClaudeHookService? claudeHookService = null, IServer? server = null)
        {
            _conPtyService = conPtyService;
            _logger = logger;
            _configuration = configuration;
            _gitService = gitService;
            _claudeHookService = claudeHookService;
            _server = server;
            _maxSessions = _configuration.GetValue<int>("SessionSettings:MaxSessions", TerminalConstants.DefaultMaxSessions);
        }

        /// <summary>
        /// サーバーのポート番号を取得する
        /// </summary>
        private int GetServerPort()
        {
            const int defaultPort = 5081;

            if (_server == null)
            {
                _logger.LogWarning("IServer が利用できません。デフォルトポート {Port} を使用します", defaultPort);
                return defaultPort;
            }

            var addressesFeature = _server.Features.Get<IServerAddressesFeature>();
            if (addressesFeature == null || !addressesFeature.Addresses.Any())
            {
                _logger.LogWarning("サーバーアドレスが取得できません。デフォルトポート {Port} を使用します", defaultPort);
                return defaultPort;
            }

            // 最初のアドレスからポートを取得（http://localhost:5081 形式）
            var address = addressesFeature.Addresses.First();
            try
            {
                var uri = new Uri(address);
                _logger.LogDebug("サーバーポートを取得: {Port} (from {Address})", uri.Port, address);
                return uri.Port;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "アドレスからポートを解析できません: {Address}。デフォルトポート {Port} を使用します", address, defaultPort);
                return defaultPort;
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
                    // /c オプションを使用（コマンド実行後にプロセスを終了）
                    var args = string.IsNullOrWhiteSpace(claudeArgs)
                        ? $"/c \"{claudeCmdPath}\""
                        : $"/c \"{claudeCmdPath}\" {claudeArgs}";
                    return ("cmd.exe", args);

                case TerminalType.GeminiCLI:
                    var geminiArgs = TerminalConstants.BuildGeminiArgs(options);
                    var geminiArgsString = string.IsNullOrWhiteSpace(geminiArgs)
                        ? "/k gemini"
                        : $"/k gemini {geminiArgs}";
                    return ("cmd.exe", geminiArgsString);

                case TerminalType.CodexCLI:
                    var codexArgs = TerminalConstants.BuildCodexArgs(options);
                    var codexArgsString = string.IsNullOrWhiteSpace(codexArgs)
                        ? "/k codex"
                        : $"/k codex {codexArgs}";
                    return ("cmd.exe", codexArgsString);

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
                // セッション数の上限チェック
                if (_sessions.Count >= _maxSessions)
                {
                    _logger.LogWarning($"Maximum session count reached: {_maxSessions}");
                    throw new InvalidOperationException($"最大セッション数（{_maxSessions}）に達しています。");
                }
            
            // フォルダパスが指定されていない場合はデフォルトを使用
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                folderPath = _configuration.GetValue<string>("SessionSettings:BasePath") 
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            
                // フォルダが存在することを確認
                if (!Directory.Exists(folderPath))
                {
                    _logger.LogError($"Directory not found: {folderPath}");
                    throw new DirectoryNotFoundException($"指定されたフォルダが見つかりません: {folderPath}");
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

                _logger.LogInformation($"Session info created successfully: {sessionInfo.SessionId} ({terminalType})");
                return sessionInfo;
            }
            catch (DirectoryNotFoundException)
            {
                // フォルダが存在しないエラーはそのまま再スロー
                throw;
            }
            catch (InvalidOperationException)
            {
                // セッション数上限エラーはそのまま再スロー
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create session for {FolderPath} with type {TerminalType}", folderPath, terminalType);
                
                // エラーメッセージをより具体的に
                string errorMessage = terminalType switch
                {
                    TerminalType.ClaudeCode => $"Claude Codeの起動に失敗しました。Claude Codeがインストールされているか確認してください。",
                    TerminalType.GeminiCLI => $"Gemini CLIの起動に失敗しました。Gemini CLIがインストールされているか確認してください。",
                    TerminalType.CodexCLI => $"Codex CLIの起動に失敗しました。Codex CLIがインストールされているか確認してください。",
                    _ => $"ターミナルの起動に失敗しました。"
                };
                
                if (ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
                {
                    errorMessage += " アクセスが拒否されました。管理者権限が必要かもしれません。";
                }
                
                throw new InvalidOperationException(errorMessage, ex);
            }
        }


        private string? FindExecutable(string exeName)
        {
            // 1. 環境変数PATHから検索
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                var paths = pathEnv.Split(Path.PathSeparator);
                foreach (var path in paths)
                {
                    var fullPath = Path.Combine(path, exeName);
                    if (File.Exists(fullPath))
                    {
                        _logger.LogInformation($"Found {exeName} at: {fullPath}");
                        return fullPath;
                    }
                }
            }

            // 2. よくあるインストール場所を確認
            var commonPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Claude"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Gemini"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Claude"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Gemini"),
                @"C:\claude",
                @"C:\gemini"
            };

            foreach (var basePath in commonPaths)
            {
                var fullPath = Path.Combine(basePath, exeName);
                if (File.Exists(fullPath))
                {
                    _logger.LogInformation($"Found {exeName} at: {fullPath}");
                    return fullPath;
                }
            }

            _logger.LogWarning($"Could not find {exeName} in PATH or common locations");
            return null;
        }

        public Task<bool> RemoveSessionAsync(Guid sessionId)
        {
            bool removed = false;
            
            // ConPtyセッションが存在する場合は削除
            if (_sessions.TryRemove(sessionId, out var session))
            {
                session.Dispose();
                removed = true;
            }
            
            // SessionInfoを削除（ConPtySessionも破棄）
            if (_sessionInfos.TryRemove(sessionId, out var sessionInfo))
            {
                // ConPtySessionを破棄
                sessionInfo.ConPtySession?.Dispose();
                removed = true;
            }
            
            // 初期化ロックを削除
            if (_initializationLocks.TryRemove(sessionId, out var initLock))
            {
                initLock.Dispose();
            }

            if (removed && _activeSessionId == sessionId)
            {
                _activeSessionId = _sessionInfos.Keys.FirstOrDefault();
            }

            return Task.FromResult(removed);
        }

        public async Task<ConPtySession?> GetSessionAsync(Guid sessionId)
        {
            // まずSessionInfoの存在を確認
            if (!_sessionInfos.TryGetValue(sessionId, out var sessionInfo))
            {
                return null;
            }

            // 既にConPtyセッションが存在する場合はそれを返す
            if (_sessions.TryGetValue(sessionId, out var existingSession))
            {
                sessionInfo.LastAccessedAt = DateTime.Now;
                _logger.LogDebug("既存セッションを再利用: SessionId={SessionId}", sessionId);
                return existingSession;
            }

            // ConPtyセッションがまだ初期化されていない場合は遅延初期化を実行
            var initLock = _initializationLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
            
            try
            {
                await initLock.WaitAsync();
                
                // ダブルチェックロッキング
                if (_sessions.TryGetValue(sessionId, out existingSession))
                {
                    sessionInfo.LastAccessedAt = DateTime.Now;
                    _logger.LogDebug("既存セッションを再利用 (ダブルチェック): SessionId={SessionId}", sessionId);
                    return existingSession;
                }

                // ClaudeCode セッションの場合、ConPty 起動前に hook 設定をセットアップ
                if (sessionInfo.TerminalType == TerminalType.ClaudeCode &&
                    !sessionInfo.HookConfigured &&
                    _claudeHookService != null)
                {
                    try
                    {
                        var port = GetServerPort();
                        await _claudeHookService.SetupHooksAsync(sessionInfo.SessionId, sessionInfo.FolderPath, port);
                        sessionInfo.HookConfigured = true;
                        _logger.LogInformation("Hook 設定をセットアップ: SessionId={SessionId}, Port={Port}", sessionInfo.SessionId, port);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Hook 設定のセットアップに失敗しましたが、セッション起動は続行します: SessionId={SessionId}", sessionInfo.SessionId);
                    }
                }

                // ConPtyセッションを初期化
                var cols = _configuration.GetValue<int>("SessionSettings:DefaultCols", TerminalConstants.DefaultCols);
                var rows = _configuration.GetValue<int>("SessionSettings:DefaultRows", TerminalConstants.DefaultRows);

                var (command, args) = BuildTerminalCommand(sessionInfo.TerminalType, sessionInfo.Options);

                _logger.LogInformation("新規セッション接続開始: SessionId={SessionId}", sessionId);
                var newSession = await _conPtyService.CreateSessionAsync(command, args, sessionInfo.FolderPath, cols, rows);
                _sessions[sessionId] = newSession;
                
                // ConPtySessionをSessionInfoに設定
                sessionInfo.ConPtySession = newSession;
                // Startメソッドを呼ぶ
                newSession.Start();
                // 初期サイズを設定
                newSession.Resize(cols, rows);
                
                sessionInfo.LastAccessedAt = DateTime.Now;
                
                
                _logger.LogInformation("新規セッション接続完了: SessionId={SessionId}", sessionId);
                _logger.LogInformation($"ConPty session with buffer initialized on-demand: {sessionId}");
                
                return newSession;
            }
            catch (Exception ex)
            {
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

        public SessionInfo? GetSessionInfo(Guid sessionId)
        {
            return _sessionInfos.TryGetValue(sessionId, out var info) ? info : null;
        }

        public Task<bool> SetActiveSessionAsync(Guid sessionId)
        {
            // Console.WriteLine($"[SessionManager] SetActiveSessionAsync開始: sessionId={sessionId}");
            lock (_lockObject)
            {
                // Console.WriteLine($"[SessionManager] ロック取得");
                if (_sessionInfos.ContainsKey(sessionId))
                {
                    // Console.WriteLine($"[SessionManager] セッション存在確認OK");
                    if (_activeSessionId.HasValue && _sessionInfos.TryGetValue(_activeSessionId.Value, out var oldActive))
                    {
                        oldActive.IsActive = false;
                        // Console.WriteLine($"[SessionManager] 前のアクティブセッション({_activeSessionId})を非アクティブ化");
                    }

                    _activeSessionId = sessionId;
                    if (_sessionInfos.TryGetValue(sessionId, out var newActive))
                    {
                        newActive.IsActive = true;
                        newActive.LastAccessedAt = DateTime.Now;
                        // Console.WriteLine($"[SessionManager] 新しいセッション({sessionId})をアクティブ化");
                    }

                    // イベントを削除してシンプルにする
                    // Console.WriteLine($"[SessionManager] ActiveSessionChangedイベント発火");
                    // ActiveSessionChanged?.Invoke(this, sessionId);
                    // Console.WriteLine($"[SessionManager] SetActiveSessionAsync完了: true");
                    return Task.FromResult(true);
                }
                // Console.WriteLine($"[SessionManager] セッションが見つかりません: {sessionId}");
                return Task.FromResult(false);
            }
        }

        public Guid? GetActiveSessionId()
        {
            return _activeSessionId;
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
            return await CreateWorktreeSessionAsync(parentSessionId, branchName, TerminalType.Terminal, null);
        }

        public async Task<SessionInfo?> CreateWorktreeSessionAsync(Guid parentSessionId, string branchName, TerminalType terminalType = TerminalType.Terminal, Dictionary<string, string>? options = null)
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

                // 既存のworktreeリストを取得
                var existingWorktrees = await _gitService.GetWorktreeListAsync(parentSession.FolderPath);
                
                // 指定したブランチのworktreeが既に存在するかチェック
                var existingWorktree = existingWorktrees.FirstOrDefault(w => w.BranchName == branchName);
                if (existingWorktree != null)
                {
                    _logger.LogInformation("既存のWorktreeを使用します: ブランチ={Branch}, パス={Path}", branchName, existingWorktree.Path);
                    
                    // 既存のworktreeパスでセッションを作成
                    var existingWorktreeSessionInfo = new SessionInfo
                    {
                        SessionId = Guid.NewGuid(),
                        FolderPath = existingWorktree.Path,
                        FolderName = Path.GetFileName(existingWorktree.Path),
                        DisplayName = $"{parentSession.DisplayName} ({branchName})",
                        TerminalType = terminalType,
                        Options = options ?? new Dictionary<string, string>(),
                        ParentSessionId = parentSessionId
                    };

                    // Git情報を設定
                    await PopulateGitInfoAsync(existingWorktreeSessionInfo);

                    // SessionInfoのみを登録（ConPtyセッションは遅延初期化）
                    _sessionInfos.TryAdd(existingWorktreeSessionInfo.SessionId, existingWorktreeSessionInfo);
                    _initializationLocks[existingWorktreeSessionInfo.SessionId] = new SemaphoreSlim(1, 1);

                    _logger.LogInformation("既存Worktreeセッション情報作成成功: ブランチ={Branch}, パス={Path}, セッションID={SessionId}", 
                        branchName, existingWorktree.Path, existingWorktreeSessionInfo.SessionId);

                    return existingWorktreeSessionInfo;
                }

                // Worktreeの作成先パスを決定（常に親と同じ階層に作成）
                var parentPath = parentSession.FolderPath;
                // 末尾のディレクトリ区切り文字を削除
                if (parentPath.EndsWith(Path.DirectorySeparatorChar) || parentPath.EndsWith(Path.AltDirectorySeparatorChar))
                {
                    parentPath = parentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                var worktreeName = $"{Path.GetFileName(parentPath)}-{branchName}";
                // 親ディレクトリと同じ階層に作成（親ディレクトリが取得できない場合は、親の親を使用）
                var parentDir = Path.GetDirectoryName(parentPath);
                if (string.IsNullOrEmpty(parentDir))
                {
                    // ルートディレクトリの場合は、一つ上の階層を作成
                    parentDir = Path.GetDirectoryName(Path.GetFullPath(parentPath)) ?? parentPath;
                }
                var worktreePath = Path.Combine(parentDir, worktreeName);

                // 既に存在する場合は別の名前を試す
                int counter = 1;
                var originalWorktreePath = worktreePath;
                while (Directory.Exists(worktreePath))
                {
                    worktreePath = $"{originalWorktreePath}-{counter}";
                    counter++;
                }

                // Worktreeを作成
                var success = await _gitService.CreateWorktreeAsync(parentPath, branchName, worktreePath);
                if (!success)
                {
                    _logger.LogWarning("Worktree作成に失敗しました: ブランチ={Branch}, パス={Path}", branchName, worktreePath);
                    return null;
                }

                // 新しいセッションを作成
                var worktreeSessionInfo = new SessionInfo
                {
                    SessionId = Guid.NewGuid(),
                    FolderPath = worktreePath,
                    FolderName = Path.GetFileName(worktreePath),
                    DisplayName = $"{parentSession.DisplayName} ({branchName})",
                    TerminalType = terminalType,
                    Options = options ?? new Dictionary<string, string>(),
                    ParentSessionId = parentSessionId
                };

                // Git情報を設定
                await PopulateGitInfoAsync(worktreeSessionInfo);

                // SessionInfoのみを登録（ConPtyセッションは遅延初期化）
                _sessionInfos.TryAdd(worktreeSessionInfo.SessionId, worktreeSessionInfo);
                _initializationLocks[worktreeSessionInfo.SessionId] = new SemaphoreSlim(1, 1);

                _logger.LogInformation("Worktreeセッション情報作成成功: ブランチ={Branch}, パス={Path}, セッションID={SessionId}", 
                    branchName, worktreePath, worktreeSessionInfo.SessionId);

                return worktreeSessionInfo;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worktreeセッション作成でエラーが発生しました: 親セッション={ParentSessionId}, ブランチ={Branch}", 
                    parentSessionId, branchName);
                return null;
            }
        }

        public async Task<SessionInfo?> CreateWorktreeSessionWithExistingBranchAsync(Guid parentSessionId, string branchName, TerminalType terminalType = TerminalType.Terminal, Dictionary<string, string>? options = null)
        {
            // 既存ブランチでWorktreeを作成する場合も、基本的に同じ処理
            // git worktree add コマンドは既存ブランチでも動作する
            return await CreateWorktreeSessionAsync(parentSessionId, branchName, terminalType, options);
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
                    TerminalType.GeminiCLI => $"{sessionInfo.FolderName} (Gemini)",
                    TerminalType.CodexCLI => $"{sessionInfo.FolderName} (Codex)",
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

        public async Task<ConPtySession?> RecreateSessionAsync(Guid sessionId, bool removeContinueOption = false)
        {
            try
            {
                // セッション情報を取得
                var sessionInfo = GetSessionInfo(sessionId);
                if (sessionInfo == null)
                {
                    _logger.LogWarning("再作成するセッションが見つかりません: {SessionId}", sessionId);
                    return null;
                }

                // 現在のセッションを取得して終了
                if (_sessions.TryGetValue(sessionId, out var currentSession))
                {
                    currentSession.Dispose();
                    _sessions.TryRemove(sessionId, out _);
                    
                    // ConPtySessionも破棄
                    if (sessionInfo.ConPtySession != null)
                    {
                        sessionInfo.ConPtySession.Dispose();
                        sessionInfo.ConPtySession = null;
                        _logger.LogInformation("既存のConPtySessionを破棄しました: {SessionId}", sessionId);
                    }
                    
                    // リソースのクリーンアップを待つ
                    await Task.Delay(100);
                }

                // セッションの起動設定を準備
                var cols = _configuration.GetValue<int>("SessionSettings:DefaultCols", TerminalConstants.DefaultCols);
                var rows = _configuration.GetValue<int>("SessionSettings:DefaultRows", TerminalConstants.DefaultRows);

                // removeContinueOptionがtrueまたはHasContinueErrorOccurredフラグがtrueの場合、--continueオプションを除外
                var options = sessionInfo.Options;
                if ((removeContinueOption || sessionInfo.HasContinueErrorOccurred) && options.ContainsKey("continue"))
                {
                    options = new Dictionary<string, string>(options);
                    options.Remove("continue");
                    _logger.LogInformation("--continueオプションを除外しました: {SessionId}", sessionId);
                }

                var (command, args) = BuildTerminalCommand(sessionInfo.TerminalType, options);

                // 新しいセッションを作成（Startは呼ばない）
                ConPtySession newSession = await _conPtyService.CreateSessionAsync(command, args, sessionInfo.FolderPath, cols, rows);
                _sessions[sessionId] = newSession;
                
                // ConPtySessionをSessionInfoに設定
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
                // セッション情報を取得
                var sessionInfo = GetSessionInfo(sessionId);
                if (sessionInfo == null)
                {
                    _logger.LogWarning("再起動するセッションが見つかりません: {SessionId}", sessionId);
                    return false;
                }

                // 現在のセッションを取得して終了
                if (_sessions.TryGetValue(sessionId, out var currentSession))
                {
                    currentSession.Dispose();
                    _sessions.TryRemove(sessionId, out _);
                    
                    // ConPtySessionも破棄
                    if (sessionInfo.ConPtySession != null)
                    {
                        sessionInfo.ConPtySession.Dispose();
                        sessionInfo.ConPtySession = null;
                        _logger.LogInformation("ConPtySessionを破棄しました: {SessionId}", sessionId);
                    }
                    
                    // リソースのクリーンアップを待つ
                    await Task.Delay(100);
                }

                // セッションの起動設定を準備
                var cols = _configuration.GetValue<int>("SessionSettings:DefaultCols", TerminalConstants.DefaultCols);
                var rows = _configuration.GetValue<int>("SessionSettings:DefaultRows", TerminalConstants.DefaultRows);

                // HasContinueErrorOccurredフラグがtrueの場合、--continueオプションを除外
                var options = sessionInfo.Options;
                if (sessionInfo.HasContinueErrorOccurred && options.ContainsKey("continue"))
                {
                    options = new Dictionary<string, string>(options);
                    options.Remove("continue");
                    _logger.LogInformation("HasContinueErrorOccurredフラグにより--continueオプションを除外しました: {SessionId}", sessionId);
                }

                var (command, args) = BuildTerminalCommand(sessionInfo.TerminalType, options);

                
                // 新しいセッションを作成
                ConPtySession newSession = await _conPtyService.CreateSessionAsync(command, args, sessionInfo.FolderPath, cols, rows);
                _sessions[sessionId] = newSession;
                
                // ConPtySessionをSessionInfoに設定
                sessionInfo.ConPtySession = newSession;
                // Startメソッドを呼ぶ
                newSession.Start();
                newSession.Resize(cols, rows);
                _logger.LogInformation("新しいConPtySessionを作成しました: {SessionId}", sessionId);

                _logger.LogInformation("セッション再起動成功: {SessionId}, タイプ: {Type}", sessionId, sessionInfo.TerminalType);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "セッション再起動でエラーが発生しました: {SessionId}", sessionId);
                return false;
            }
        }

        public void Dispose()
        {
            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }
            _sessions.Clear();
            
            // ConPtySessionも破棄
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
