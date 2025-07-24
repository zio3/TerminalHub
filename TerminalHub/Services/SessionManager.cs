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
        ConPtySession? GetSession(Guid sessionId);
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
                    var args = string.IsNullOrWhiteSpace(claudeArgs)
                        ? $"/k \"{claudeCmdPath}\""
                        : $"/k \"{claudeCmdPath}\" {claudeArgs}";
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

        private async Task<ConPtySession> CreateClaudeCodeSessionWithFallbackAsync(string command, string args, string folderPath, int cols, int rows, Dictionary<string, string> options)
        {            
            try
            {
                // 最初は設定されたオプションで試行
                return await _conPtyService.CreateSessionAsync(command, args, folderPath, cols, rows);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Claude Codeセッション作成が失敗しました");
                
                // --continueオプションを除去して再試行
                if (options.ContainsKey("continue"))
                {
                    _logger.LogInformation("--continueオプションなしで再試行します");
                    var fallbackOptions = new Dictionary<string, string>(options);
                    fallbackOptions.Remove("continue");
                    var fallbackArgs = TerminalConstants.BuildClaudeCodeArgs(fallbackOptions);
                    var claudeCmdPath = GetClaudeCmdPath();
                    var fallbackArgsString = string.IsNullOrWhiteSpace(fallbackArgs)
                        ? $"/k \"{claudeCmdPath}\""
                        : $"/k \"{claudeCmdPath}\" {fallbackArgs}";
                    
                    _logger.LogInformation("--continueオプションなしで再試行: {Args}", fallbackArgsString);
                    return await _conPtyService.CreateSessionAsync(command, fallbackArgsString, folderPath, cols, rows);
                }
                
                // --continueオプションがない場合は元の例外を再スロー
                throw;
            }
        }

        public async Task<SessionInfo> CreateSessionAsync(string? folderPath = null)
        {
            // 既存のメソッドは互換性のために残す
            var basePath = _configuration.GetValue<string>("SessionSettings:BasePath") 
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "TerminalHub");
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
                MaxBufferSize = _configuration.GetValue<int>("SessionSettings:MaxBufferSize", 10000)
            };

            // Git情報を非同期で取得してセッション情報に設定
            await PopulateGitInfoAsync(sessionInfo);

                var cols = _configuration.GetValue<int>("SessionSettings:DefaultCols", TerminalConstants.DefaultCols);
                var rows = _configuration.GetValue<int>("SessionSettings:DefaultRows", TerminalConstants.DefaultRows);
                
                var (command, args) = BuildTerminalCommand(terminalType, options);
                
                ConPtySession session;
                if (terminalType == TerminalType.ClaudeCode)
                {
                    session = await CreateClaudeCodeSessionWithFallbackAsync(command, args, folderPath, cols, rows, options);
                }
                else
                {
                    session = await _conPtyService.CreateSessionAsync(command, args, folderPath, cols, rows);
                }

                _sessions[sessionInfo.SessionId] = session;
                _sessionInfos[sessionInfo.SessionId] = sessionInfo;

                _logger.LogInformation($"Session created successfully: {sessionInfo.SessionId} ({terminalType})");
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
            if (_sessions.TryRemove(sessionId, out var session))
            {
                session.Dispose();
                _sessionInfos.TryRemove(sessionId, out _);

                if (_activeSessionId == sessionId)
                {
                    _activeSessionId = _sessionInfos.Keys.FirstOrDefault();
                    // イベントを削除
                    // ActiveSessionChanged?.Invoke(this, _activeSessionId ?? string.Empty);
                }

                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public ConPtySession? GetSession(Guid sessionId)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                if (_sessionInfos.TryGetValue(sessionId, out var info))
                {
                    info.LastAccessedAt = DateTime.Now;
                }
                return session;
            }
            return null;
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
                        MaxBufferSize = parentSession.MaxBufferSize,
                        ParentSessionId = parentSessionId
                    };

                    // Git情報を設定
                    await PopulateGitInfoAsync(existingWorktreeSessionInfo);

                    // セッションを開始
                    var terminalCols = _configuration.GetValue<int>("SessionSettings:DefaultCols", TerminalConstants.DefaultCols);
                    var terminalRows = _configuration.GetValue<int>("SessionSettings:DefaultRows", TerminalConstants.DefaultRows);

                    var (terminalCommand, terminalArgs) = BuildTerminalCommand(existingWorktreeSessionInfo.TerminalType, existingWorktreeSessionInfo.Options);

                    ConPtySession newSession;
                    if (existingWorktreeSessionInfo.TerminalType == TerminalType.ClaudeCode)
                    {
                        newSession = await CreateClaudeCodeSessionWithFallbackAsync(terminalCommand, terminalArgs, existingWorktree.Path, terminalCols, terminalRows, existingWorktreeSessionInfo.Options);
                    }
                    else
                    {
                        newSession = await _conPtyService.CreateSessionAsync(terminalCommand, terminalArgs, existingWorktree.Path, terminalCols, terminalRows);
                    }
                    _sessions.TryAdd(existingWorktreeSessionInfo.SessionId, newSession);
                    _sessionInfos.TryAdd(existingWorktreeSessionInfo.SessionId, existingWorktreeSessionInfo);

                    _logger.LogInformation("既存Worktreeセッション作成成功: ブランチ={Branch}, パス={Path}, セッションID={SessionId}", 
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
                    MaxBufferSize = parentSession.MaxBufferSize,
                    ParentSessionId = parentSessionId
                };

                // Git情報を設定
                await PopulateGitInfoAsync(worktreeSessionInfo);

                // セッションを開始
                var cols = _configuration.GetValue<int>("SessionSettings:DefaultCols", TerminalConstants.DefaultCols);
                var rows = _configuration.GetValue<int>("SessionSettings:DefaultRows", TerminalConstants.DefaultRows);

                // 親セッションと同じターミナルタイプで起動
                var (command, args) = BuildTerminalCommand(worktreeSessionInfo.TerminalType, worktreeSessionInfo.Options);

                ConPtySession session;
                if (worktreeSessionInfo.TerminalType == TerminalType.ClaudeCode)
                {
                    session = await CreateClaudeCodeSessionWithFallbackAsync(command, args, worktreePath, cols, rows, worktreeSessionInfo.Options);
                }
                else
                {
                    session = await _conPtyService.CreateSessionAsync(command, args, worktreePath, cols, rows);
                }
                _sessions.TryAdd(worktreeSessionInfo.SessionId, session);
                _sessionInfos.TryAdd(worktreeSessionInfo.SessionId, worktreeSessionInfo);

                _logger.LogInformation("Worktreeセッション作成成功: ブランチ={Branch}, パス={Path}, セッションID={SessionId}", 
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
                    MaxBufferSize = parentSession.MaxBufferSize,
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

                // セッションを開始
                var cols = _configuration.GetValue<int>("SessionSettings:DefaultCols", TerminalConstants.DefaultCols);
                var rows = _configuration.GetValue<int>("SessionSettings:DefaultRows", TerminalConstants.DefaultRows);

                var (command, args) = BuildTerminalCommand(sessionInfo.TerminalType, sessionInfo.Options);

                ConPtySession session;
                if (sessionInfo.TerminalType == TerminalType.ClaudeCode)
                {
                    session = await CreateClaudeCodeSessionWithFallbackAsync(command, args, folderPath, cols, rows, sessionInfo.Options);
                }
                else
                {
                    session = await _conPtyService.CreateSessionAsync(command, args, folderPath, cols, rows);
                }
                _sessions.TryAdd(sessionInfo.SessionId, session);
                _sessionInfos.TryAdd(sessionInfo.SessionId, sessionInfo);

                _logger.LogInformation("同じパスでセッション作成成功: パス={Path}, タイプ={Type}, セッションID={SessionId}",
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
                    
                    // リソースのクリーンアップを待つ
                    await Task.Delay(100);
                }

                // セッションの起動設定を準備
                var cols = _configuration.GetValue<int>("SessionSettings:DefaultCols", TerminalConstants.DefaultCols);
                var rows = _configuration.GetValue<int>("SessionSettings:DefaultRows", TerminalConstants.DefaultRows);

                var (command, args) = BuildTerminalCommand(sessionInfo.TerminalType, sessionInfo.Options);

                // 新しいセッションを作成
                ConPtySession newSession;
                if (sessionInfo.TerminalType == TerminalType.ClaudeCode)
                {
                    newSession = await CreateClaudeCodeSessionWithFallbackAsync(command, args, sessionInfo.FolderPath, cols, rows, sessionInfo.Options);
                }
                else
                {
                    newSession = await _conPtyService.CreateSessionAsync(command, args, sessionInfo.FolderPath, cols, rows);
                }
                _sessions[sessionId] = newSession;

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
            _sessionInfos.Clear();
        }
        
    }
}
