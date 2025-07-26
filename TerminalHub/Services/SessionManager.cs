using TerminalHub.Models;
using TerminalHub.Constants;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using TerminalHub.Components.Shared;

namespace TerminalHub.Services
{
    public interface ISessionManager
    {
        Task<SessionInfo> CreateSessionAsync(string? folderPath = null);
        Task<SessionInfo> CreateSessionAsync(string folderPath, string sessionName, TerminalType terminalType, Dictionary<string, string> options);
        Task<SessionInfo> CreateSessionAsync(Guid sessionGuid, string folderPath, string sessionName, TerminalType terminalType, Dictionary<string, string> options);
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
        private Guid? _activeSessionId;
        private readonly object _lockObject = new();
        private readonly int _maxSessions;

        // public event EventHandler<string>? ActiveSessionChanged;

        public SessionManager(IConPtyService conPtyService, ILogger<SessionManager> logger, IConfiguration configuration, IGitService gitService)
        {
            _conPtyService = conPtyService;
            _logger = logger;
            _configuration = configuration;
            _gitService = gitService;
            _maxSessions = _configuration.GetValue<int>("SessionSettings:MaxSessions", TerminalConstants.DefaultMaxSessions);
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


        public async Task<SessionInfo> CreateSessionAsync(string? folderPath = null)
        {
            // 既存のメソッドは互換性のために残す
            var basePath = _configuration.GetValue<string>("SessionSettings:BasePath") 
                ?? @"C:\Users\info\source\repos\Experimental2025\ClaoudeCodeWebUi\bin\Debug\net9.0";
            return await CreateSessionAsync(basePath, "", TerminalType.Terminal, new Dictionary<string, string>());
        }

        public async Task<SessionInfo> CreateSessionAsync(string folderPath, string sessionName, TerminalType terminalType, Dictionary<string, string> options)
        {
            return await CreateSessionAsync(Guid.NewGuid(), folderPath, sessionName, terminalType, options);
        }

        public async Task<SessionInfo> CreateSessionAsync(Guid sessionGuid, string folderPath, string sessionName, TerminalType terminalType, Dictionary<string, string> options)
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
                Options = options,
                OutputBuffer = new CircularLineBuffer(_configuration.GetValue<int>("SessionSettings:MaxBufferSize", 10000))
            };

            // Git情報を非同期で取得してセッション情報に設定
            await PopulateGitInfoAsync(sessionInfo);

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
            
            // SessionInfoを削除（ConPtyBufferも破棄）
            if (_sessionInfos.TryRemove(sessionId, out var sessionInfo))
            {
                // ConPtyBufferを破棄
                sessionInfo.ConPtyBuffer?.Dispose();
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
                Console.WriteLine($"[SessionManager] 既存セッションを再利用: {sessionId}");
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
                    Console.WriteLine($"[SessionManager] 既存セッションを再利用 (ダブルチェック): {sessionId}");
                    return existingSession;
                }

                // ConPtyセッションを初期化
                var cols = _configuration.GetValue<int>("SessionSettings:DefaultCols", TerminalConstants.DefaultCols);
                var rows = _configuration.GetValue<int>("SessionSettings:DefaultRows", TerminalConstants.DefaultRows);
                
                var (command, args) = BuildTerminalCommand(sessionInfo.TerminalType, sessionInfo.Options);
                
                // 初期化開始を設定
                sessionInfo.IsInitializing = true;
                sessionInfo.HasReceivedFirstData = false;
                
                Console.WriteLine($"[SessionManager] ★★★ 新規セッション接続開始: {sessionId} ★★★");
                var newSession = await _conPtyService.CreateSessionAsync(command, args, sessionInfo.FolderPath, cols, rows);
                _sessions[sessionId] = newSession;
                
                // ConPtyWithBufferを作成してSessionInfoに設定
                var bufferCapacity = _configuration.GetValue<int>("SessionSettings:MaxBufferSize", 10000);
                sessionInfo.ConPtyBuffer = new ConPtyWithBuffer(newSession, _logger, bufferCapacity);
                // 初期サイズを設定
                sessionInfo.ConPtyBuffer.Resize(cols, rows);
                
                sessionInfo.LastAccessedAt = DateTime.Now;
                
                // ConPty接続は完了したが、まだ初期化中（最初のデータ待ち）
                // sessionInfo.IsInitializing は true のまま維持
                
                Console.WriteLine($"[SessionManager] ★★★ 新規セッション接続完了: {sessionId} ★★★");
                _logger.LogInformation($"ConPty session with buffer initialized on-demand: {sessionId}");
                
                return newSession;
            }
            catch (Exception ex)
            {
                // 初期化失敗時もフラグをリセット
                sessionInfo.IsInitializing = false;
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
                    sessionInfo.WorktreeMainPath = gitInfo.WorktreeMainPath;

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
                        OutputBuffer = new CircularLineBuffer(parentSession.OutputBuffer.Capacity),
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

                // Worktreeの作成先パスを決定
                var parentPath = parentSession.FolderPath;
                var worktreeName = $"{Path.GetFileName(parentPath)}-{branchName}";
                var worktreePath = Path.Combine(Path.GetDirectoryName(parentPath) ?? parentPath, worktreeName);

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
                    OutputBuffer = new CircularLineBuffer(parentSession.OutputBuffer.Capacity),
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
                    OutputBuffer = new CircularLineBuffer(parentSession.OutputBuffer.Capacity),
                    ParentSessionId = parentSessionId
                };

                // セッション種類に応じた表示名を設定
                sessionInfo.DisplayName = terminalType switch
                {
                    TerminalType.ClaudeCode => $"{sessionInfo.FolderName} (Claude)",
                    TerminalType.GeminiCLI => $"{sessionInfo.FolderName} (Gemini)",
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
                    
                    // ConPtyBufferも破棄（イベントハンドラーをクリアしてから）
                    if (sessionInfo.ConPtyBuffer != null)
                    {
                        sessionInfo.ConPtyBuffer.ClearEventHandlers();
                        sessionInfo.ConPtyBuffer.Dispose();
                        sessionInfo.ConPtyBuffer = null;
                        _logger.LogInformation("ConPtyBufferを破棄しました: {SessionId}", sessionId);
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

                // 再起動時も初期化中として扱う
                sessionInfo.IsInitializing = true;
                sessionInfo.HasReceivedFirstData = false;
                
                // 新しいセッションを作成
                ConPtySession newSession = await _conPtyService.CreateSessionAsync(command, args, sessionInfo.FolderPath, cols, rows);
                _sessions[sessionId] = newSession;
                
                // 新しいConPtyBufferを作成
                var bufferCapacity = _configuration.GetValue<int>("SessionSettings:MaxBufferSize", 10000);
                sessionInfo.ConPtyBuffer = new ConPtyWithBuffer(newSession, _logger, bufferCapacity);
                sessionInfo.ConPtyBuffer.Resize(cols, rows);
                _logger.LogInformation("新しいConPtyBufferを作成しました: {SessionId}", sessionId);

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
            
            // ConPtyBufferも破棄
            foreach (var sessionInfo in _sessionInfos.Values)
            {
                sessionInfo.ConPtyBuffer?.Dispose();
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
