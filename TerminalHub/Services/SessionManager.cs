using TerminalHub.Models;
using TerminalHub.Constants;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace TerminalHub.Services
{
    public interface ISessionManager
    {
        Task<SessionInfo> CreateSessionAsync(string? folderPath = null);
        Task<SessionInfo> CreateSessionAsync(string folderPath, string sessionName, TerminalType terminalType, Dictionary<string, string> options);
        Task<bool> RemoveSessionAsync(string sessionId);
        ConPtySession? GetSession(string sessionId);
        IEnumerable<SessionInfo> GetAllSessions();
        SessionInfo? GetSessionInfo(string sessionId);
        Task<bool> SetActiveSessionAsync(string sessionId);
        string? GetActiveSessionId();
        Task SaveSessionInfoAsync(SessionInfo sessionInfo);
        event EventHandler<string>? ActiveSessionChanged;
    }

    public class SessionManager : ISessionManager, IDisposable
    {
        private readonly ConcurrentDictionary<string, ConPtySession> _sessions = new();
        private readonly ConcurrentDictionary<string, SessionInfo> _sessionInfos = new();
        private readonly IConPtyService _conPtyService;
        private readonly ILogger<SessionManager> _logger;
        private readonly IConfiguration _configuration;
        private string? _activeSessionId;
        private readonly object _lockObject = new();
        private readonly int _maxSessions;

        public event EventHandler<string>? ActiveSessionChanged;

        public SessionManager(IConPtyService conPtyService, ILogger<SessionManager> logger, IConfiguration configuration)
        {
            _conPtyService = conPtyService;
            _logger = logger;
            _configuration = configuration;
            _maxSessions = _configuration.GetValue<int>("SessionSettings:MaxSessions", TerminalConstants.DefaultMaxSessions);
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
            // セッション数の上限チェック
            if (_sessions.Count >= _maxSessions)
            {
                throw new InvalidOperationException($"最大セッション数（{_maxSessions}）に達しています。");
            }
            
            var sessionGuid = Guid.NewGuid().ToString();
            
            // フォルダパスが指定されていない場合はデフォルトを使用
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                folderPath = _configuration.GetValue<string>("SessionSettings:BasePath") 
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            
            // フォルダが存在することを確認
            if (!Directory.Exists(folderPath))
            {
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

            try
            {
                var cols = _configuration.GetValue<int>("SessionSettings:DefaultCols", TerminalConstants.DefaultCols);
                var rows = _configuration.GetValue<int>("SessionSettings:DefaultRows", TerminalConstants.DefaultRows);
                
                string command;
                string args;
                
                switch (terminalType)
                {
                    case TerminalType.ClaudeCode:
                        // claude.cmdをcmd.exe経由で実行
                        command = "cmd.exe";
                        var claudeArgs = BuildClaudeCodeArgs(options);
                        args = $"/k \"C:\\Users\\info\\AppData\\Roaming\\npm\\claude.cmd\" {claudeArgs}";
                        break;
                        
                    case TerminalType.GeminiCLI:
                        // geminiをcmd.exe経由で実行
                        command = "cmd.exe";
                        var geminiArgs = BuildGeminiArgs(options);
                        args = $"/k gemini {geminiArgs}";
                        break;
                        
                    default:
                        command = options.ContainsKey("command") ? options["command"] : TerminalConstants.DefaultShell;
                        args = "";
                        break;
                }
                
                var session = await _conPtyService.CreateSessionAsync(
                    command, 
                    args, 
                    folderPath, 
                    cols, 
                    rows);

                _sessions[sessionInfo.SessionId] = session;
                _sessionInfos[sessionInfo.SessionId] = sessionInfo;

                return sessionInfo;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create session for {FolderPath} with type {TerminalType}", folderPath, terminalType);
                throw new InvalidOperationException($"Failed to start {terminalType} session for {folderPath}", ex);
            }
        }

        private string BuildClaudeCodeArgs(Dictionary<string, string> options)
        {
            var args = new List<string>();
            
            if (options.ContainsKey("model"))
            {
                args.Add($"--model {options["model"]}");
            }
            
            if (options.ContainsKey("max-tokens"))
            {
                args.Add($"--max-tokens {options["max-tokens"]}");
            }
            
            if (options.ContainsKey("bypass-mode") && options["bypass-mode"] == "true")
            {
                args.Add("--dangerously-skip-permissions");
            }
            
            if (options.ContainsKey("continue") && options["continue"] == "true")
            {
                args.Add("--continue");
            }
            
            return string.Join(" ", args);
        }

        private string BuildGeminiArgs(Dictionary<string, string> options)
        {
            var args = new List<string>();
            
            if (options.ContainsKey("model"))
            {
                args.Add($"--model {options["model"]}");
            }
            
            return string.Join(" ", args);
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

        public Task<bool> RemoveSessionAsync(string sessionId)
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

        public ConPtySession? GetSession(string sessionId)
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

        public SessionInfo? GetSessionInfo(string sessionId)
        {
            return _sessionInfos.TryGetValue(sessionId, out var info) ? info : null;
        }

        public Task<bool> SetActiveSessionAsync(string sessionId)
        {
            // Console.WriteLine($"[SessionManager] SetActiveSessionAsync開始: sessionId={sessionId}");
            lock (_lockObject)
            {
                // Console.WriteLine($"[SessionManager] ロック取得");
                if (_sessionInfos.ContainsKey(sessionId))
                {
                    // Console.WriteLine($"[SessionManager] セッション存在確認OK");
                    if (_activeSessionId != null && _sessionInfos.TryGetValue(_activeSessionId, out var oldActive))
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

        public string? GetActiveSessionId()
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