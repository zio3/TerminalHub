using TerminalHub.Models;
using System.Collections.Concurrent;
using TaskStatus = TerminalHub.Models.TaskStatus;

namespace TerminalHub.Services
{
    public interface ITaskManagerService : IDisposable
    {
        string WorkingDirectory { get; set; }
        List<TaskSession> Sessions { get; }
        TaskSession? ActiveSession { get; set; }
        
        event EventHandler<TaskSession>? SessionCreated;
        event EventHandler<TaskSession>? SessionUpdated;
        event EventHandler<string>? SessionDataReceived;
        
        Task LoadScriptsAsync();
        Dictionary<string, string> GetAvailableScripts();
        Task<TaskSession?> StartTaskAsync(string scriptName);
        Task StopTaskAsync(string sessionId);
        Task StopAllTasksAsync();
        TaskSession? GetSession(string sessionId);
        void SetActiveSession(string sessionId);
        Task SendInputAsync(string sessionId, string input);
        void ResizeSession(string sessionId, int cols, int rows);
    }

    public class TaskManagerService : ITaskManagerService, IDisposable
    {
        private readonly IConPtyService _conPtyService;
        private readonly IPackageJsonService _packageJsonService;
        private readonly ILogger<TaskManagerService> _logger;

        private Dictionary<string, string> _availableScripts = new();
        private readonly List<TaskSession> _sessions = new();
        private TaskSession? _activeSession;

        // イベントハンドラーを追跡（解除用）
        private readonly Dictionary<string, (EventHandler<DataReceivedEventArgs> DataReceived, EventHandler ProcessExited)> _sessionHandlers = new();
        
        public string WorkingDirectory { get; set; } = Directory.GetCurrentDirectory();
        public List<TaskSession> Sessions => _sessions;
        public TaskSession? ActiveSession 
        { 
            get => _activeSession;
            set
            {
                _activeSession = value;
                SessionUpdated?.Invoke(this, value!);
            }
        }
        
        public event EventHandler<TaskSession>? SessionCreated;
        public event EventHandler<TaskSession>? SessionUpdated;
        public event EventHandler<string>? SessionDataReceived;
        
        public TaskManagerService(
            IConPtyService conPtyService,
            IPackageJsonService packageJsonService,
            ILogger<TaskManagerService> logger)
        {
            _conPtyService = conPtyService;
            _packageJsonService = packageJsonService;
            _logger = logger;
        }
        
        public async Task LoadScriptsAsync()
        {
            _logger.LogInformation("作業ディレクトリからスクリプトを読み込み: {Dir}", WorkingDirectory);
            
            var scripts = await _packageJsonService.GetNpmScriptsAsync(WorkingDirectory);
            if (scripts != null)
            {
                _availableScripts = scripts;
                _logger.LogInformation("{Count} 個のスクリプトを発見", scripts.Count);
            }
            else
            {
                _availableScripts.Clear();
                _logger.LogWarning("package.jsonにスクリプトが見つかりません");
            }
        }
        
        public Dictionary<string, string> GetAvailableScripts() => _availableScripts;
        
        public async Task<TaskSession?> StartTaskAsync(string scriptName)
        {
            if (!_availableScripts.ContainsKey(scriptName))
            {
                _logger.LogError("スクリプト '{Script}' が見つかりません", scriptName);
                return null;
            }
            
            try
            {
                // 既存のセッションを確認（同じ作業ディレクトリで同じスクリプト名、かつ実行中またはIdle）
                var existingSession = _sessions.FirstOrDefault(s => 
                    s.ScriptName == scriptName && 
                    s.WorkingDirectory == WorkingDirectory && 
                    (s.Status == TaskStatus.Running || s.Status == TaskStatus.Idle));
                if (existingSession != null)
                {
                    _logger.LogWarning("スクリプト '{Script}' のセッションは既に存在します（状態: {Status}, ディレクトリ: {Dir}）", 
                        scriptName, existingSession.Status, WorkingDirectory);
                    ActiveSession = existingSession;
                    return existingSession;
                }
                
                // cmdを起動（npmコマンドは後で送信）
                var command = "cmd.exe";
                var arguments = "";
                
                _logger.LogInformation("タスク起動: {Command} {Args}", command, arguments);
                
                // ConPTYセッションを作成（初期サイズは一般的なターミナルサイズ）
                var conPtySession = await _conPtyService.CreateSessionAsync(
                    command, 
                    arguments, 
                    WorkingDirectory,
                    120,  // 初期列数（後でXTermからリサイズされる）
                    30    // 初期行数（後でXTermからリサイズされる）
                );
                
                // TaskSessionを作成（初期状態はIdle）
                var taskSession = new TaskSession
                {
                    ScriptName = scriptName,
                    ScriptCommand = _availableScripts[scriptName],
                    WorkingDirectory = WorkingDirectory, // 作業ディレクトリを保存
                    Status = TaskStatus.Idle, // 初期状態はIdle
                    StartTime = null, // 実際の開始はnpmコマンド送信時
                    ConPtySession = conPtySession
                };
                
                // データ受信イベントハンドラーを作成
                var dataReceivedHandler = new EventHandler<DataReceivedEventArgs>((sender, e) =>
                {
                    taskSession.OutputBuffer.Add(e.Data);

                    // バッファサイズを制限（最大10000行）
                    if (taskSession.OutputBuffer.Count > 10000)
                    {
                        taskSession.OutputBuffer.RemoveRange(0, 1000);
                    }

                    // アクティブセッションの場合、UIに通知
                    if (_activeSession?.Id == taskSession.Id)
                    {
                        SessionDataReceived?.Invoke(this, e.Data);
                    }
                });

                // プロセス終了イベントハンドラーを作成
                var processExitedHandler = new EventHandler((sender, e) =>
                {
                    taskSession.EndTime = DateTime.Now;
                    // ConPtySessionはExitCodeを提供しないため、完了として扱う
                    taskSession.Status = TaskStatus.Completed;

                    _logger.LogInformation("タスク終了: {Script} (ステータス: {Status})",
                        scriptName, taskSession.Status);
                    SessionUpdated?.Invoke(this, taskSession);
                });

                // イベントを登録
                conPtySession.DataReceived += dataReceivedHandler;
                conPtySession.ProcessExited += processExitedHandler;

                // ハンドラーを保存（後で解除するため）
                _sessionHandlers[taskSession.Id] = (dataReceivedHandler, processExitedHandler);

                // ConPtySessionを開始（ただしコマンドはまだ送信しない）
                conPtySession.Start();
                
                // リストに追加
                _sessions.Add(taskSession);
                
                // イベントを発生
                SessionCreated?.Invoke(this, taskSession);
                
                // アクティブセッションとして設定
                ActiveSession = taskSession;
                
                return taskSession;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "タスク起動エラー: {Script}", scriptName);
                return null;
            }
        }
        
        public async Task StopTaskAsync(string sessionId)
        {
            var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
            if (session?.ConPtySession != null)
            {
                _logger.LogInformation("タスク停止: {Script} (状態: {Status})", session.ScriptName, session.Status);

                // Runningの場合はCtrl+Cを送信
                if (session.Status == TaskStatus.Running)
                {
                    // Ctrl+C を送信して正常終了を試みる
                    await session.ConPtySession.WriteAsync("\x03");

                    // 少し待つ
                    await Task.Delay(1000);
                }

                // イベントハンドラーを解除
                CleanupSessionHandlers(sessionId, session.ConPtySession);

                // ConPtySessionを終了
                if (!session.ConPtySession.HasExited)
                {
                    session.ConPtySession.Dispose();
                }

                session.Status = TaskStatus.Stopped;
                session.EndTime = DateTime.Now;
                session.ConPtySession = null; // 参照をクリア
                SessionUpdated?.Invoke(this, session);

                // セッションをリストから削除（再実行可能にするため）
                _sessions.Remove(session);
                _logger.LogInformation("セッションをリストから削除: {Script}", session.ScriptName);
            }
        }

        /// <summary>
        /// セッションのイベントハンドラーを解除
        /// </summary>
        private void CleanupSessionHandlers(string sessionId, ConPtySession? conPtySession)
        {
            if (_sessionHandlers.TryGetValue(sessionId, out var handlers))
            {
                if (conPtySession != null)
                {
                    conPtySession.DataReceived -= handlers.DataReceived;
                    conPtySession.ProcessExited -= handlers.ProcessExited;
                }
                _sessionHandlers.Remove(sessionId);
            }
        }
        
        public async Task StopAllTasksAsync()
        {
            var runningTasks = _sessions.Where(s => s.Status == TaskStatus.Running).ToList();
            foreach (var session in runningTasks)
            {
                await StopTaskAsync(session.Id);
            }
        }
        
        public TaskSession? GetSession(string sessionId)
        {
            return _sessions.FirstOrDefault(s => s.Id == sessionId);
        }
        
        public TaskSession? GetSessionByScriptName(string scriptName)
        {
            // 現在の作業ディレクトリとスクリプト名の両方で検索
            return _sessions.FirstOrDefault(s => 
                s.ScriptName == scriptName && 
                s.WorkingDirectory == WorkingDirectory);
        }
        
        public void SetActiveSession(string sessionId)
        {
            var session = GetSession(sessionId);
            if (session != null)
            {
                ActiveSession = session;
                _logger.LogInformation("アクティブセッション変更: {Script}", session.ScriptName);
            }
        }
        
        public async Task SendInputAsync(string sessionId, string input)
        {
            var session = GetSession(sessionId);
            if (session?.ConPtySession != null && session.Status == TaskStatus.Running)
            {
                await session.ConPtySession.WriteAsync(input);
            }
        }
        
        public void ResizeSession(string sessionId, int cols, int rows)
        {
            var session = GetSession(sessionId);
            session?.ConPtySession?.Resize(cols, rows);
        }
        
        public void Dispose()
        {
            foreach (var session in _sessions)
            {
                // イベントハンドラーを解除してから破棄
                CleanupSessionHandlers(session.Id, session.ConPtySession);
                session.ConPtySession?.Dispose();
            }
            _sessions.Clear();
            _sessionHandlers.Clear();
        }
    }
}