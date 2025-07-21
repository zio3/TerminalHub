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
        Task<bool> RemoveSessionAsync(string sessionId);
        ConPtySession? GetSession(string sessionId);
        IEnumerable<SessionInfo> GetAllSessions();
        SessionInfo? GetSessionInfo(string sessionId);
        Task<bool> SetActiveSessionAsync(string sessionId);
        string? GetActiveSessionId();
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
            // セッション数の上限チェック
            if (_sessions.Count >= _maxSessions)
            {
                throw new InvalidOperationException($"最大セッション数（{_maxSessions}）に達しています。");
            }
            
            // 設定からベースパスを取得
            var basePath = _configuration.GetValue<string>("SessionSettings:BasePath") 
                ?? @"C:\Users\info\source\repos\Experimental2025\ClaoudeCodeWebUi\bin\Debug\net9.0";
            var sessionGuid = Guid.NewGuid().ToString();
            var sessionPath = Path.Combine(basePath, sessionGuid);
            
            // Console.WriteLine($"[SessionManager] CreateSessionAsync開始: {sessionPath}");
            
            // フォルダを作成
            try
            {
                Directory.CreateDirectory(sessionPath);
                // Console.WriteLine($"[SessionManager] フォルダ作成成功: {sessionPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SessionManager] フォルダ作成エラー: {ex.Message}");
                throw;
            }
            
            var sessionInfo = new SessionInfo
            {
                SessionId = sessionGuid,
                FolderPath = sessionPath,
                FolderName = sessionGuid
            };
            // Console.WriteLine($"[SessionManager] SessionInfo作成: ID={sessionInfo.SessionId}, Name={sessionInfo.FolderName}");

            try
            {
                // シンプルなcmd.exeを起動
                // Console.WriteLine($"[SessionManager] ConPtyService.CreateSessionAsync呼び出し");
                
                var cols = _configuration.GetValue<int>("SessionSettings:DefaultCols", TerminalConstants.DefaultCols);
                var rows = _configuration.GetValue<int>("SessionSettings:DefaultRows", TerminalConstants.DefaultRows);
                
                var session = await _conPtyService.CreateSessionAsync(
                    TerminalConstants.DefaultShell, 
                    "", 
                    sessionPath, 
                    cols, 
                    rows);
                
                // Console.WriteLine($"[SessionManager] ConPtyセッション作成成功");

                _sessions[sessionInfo.SessionId] = session;
                _sessionInfos[sessionInfo.SessionId] = sessionInfo;
                // Console.WriteLine($"[SessionManager] セッション登録完了: {sessionInfo.SessionId}");

                // 新しいセッションは自動的にアクティブにしない（呼び出し側で制御）

                // Console.WriteLine($"[SessionManager] CreateSessionAsync完了");
                return sessionInfo;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SessionManager] エラー発生: {ex.Message}");
                // Console.WriteLine($"[SessionManager] スタックトレース: {ex.StackTrace}");
                _logger.LogError(ex, "Failed to create Claude Code session for {FolderPath}", folderPath);
                throw new InvalidOperationException($"Failed to start Claude Code session for {folderPath}", ex);
            }
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