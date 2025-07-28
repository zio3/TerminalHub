using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TerminalHub.Models;
using TerminalHub.Analyzers;

namespace TerminalHub.Services
{
    public class OutputAnalyzerService : IOutputAnalyzerService, IDisposable
    {
        private readonly ILogger<OutputAnalyzerService> _logger;
        private readonly INotificationService _notificationService;
        private readonly IOutputAnalyzerFactory _analyzerFactory;
        private readonly Dictionary<Guid, Timer> _sessionProcessingTimers = new();
        private Action<Guid>? _timeoutCallback;
        private bool _disposed;

        public OutputAnalyzerService(
            ILogger<OutputAnalyzerService> logger,
            INotificationService notificationService,
            IOutputAnalyzerFactory analyzerFactory)
        {
            _logger = logger;
            _notificationService = notificationService;
            _analyzerFactory = analyzerFactory;
        }

        public void AnalyzeOutput(string data, SessionInfo sessionInfo, Guid activeSessionId, Action<Guid, string?> updateStatus)
        {
            try
            {
                // ターミナルタイプに応じた解析器を取得
                var analyzer = _analyzerFactory.GetAnalyzer(sessionInfo.TerminalType);
                if (analyzer == null)
                {
                    // 解析器がない場合は何もしない
                    return;
                }

                // 解析を実行
                if (analyzer.TryAnalyze(data, out var result))
                {

                    if (result.IsInterrupted)
                    {
                        // 処理が中断された
                        UpdateSessionProcessingStatus(sessionInfo, null, activeSessionId, updateStatus);
                    }
                    else if (result.IsProcessing)
                    {
                        // ユーザー入力待ち状態をセッションに設定
                        sessionInfo.IsWaitingForUserInput = result.IsWaitingForUser;
                        
                        // 処理中
                        if (result.ElapsedSeconds.HasValue)
                        {
                            // 秒数とトークンの変化をチェック
                            var secondsStr = result.ElapsedSeconds.Value.ToString();
                            var tokensStr = result.Tokens ?? "";
                            
                            if (sessionInfo.LastProcessingSeconds == secondsStr && 
                                sessionInfo.LastProcessingTokens == tokensStr)
                            {
                                // 秒数もトークンも変わらない場合はタイマーのリセットのみ
                                sessionInfo.LastProcessingUpdateTime = DateTime.Now;
                                ResetSessionTimer(sessionInfo.SessionId);
                                return;
                            }
                            
                            sessionInfo.LastProcessingSeconds = secondsStr;
                            sessionInfo.LastProcessingTokens = tokensStr;
                            
                            // GeminiCLIの場合はステータステキストも含める
                            if (!string.IsNullOrEmpty(result.StatusText))
                            {
                                UpdateSessionProcessingStatus(sessionInfo, result.StatusText, result.ElapsedSeconds.Value, 
                                    result.Tokens ?? "", result.Direction ?? "", activeSessionId, updateStatus);
                            }
                            else
                            {
                                UpdateSessionProcessingStatus(sessionInfo, result.ElapsedSeconds.Value, 
                                    result.Tokens ?? "", result.Direction ?? "", activeSessionId, updateStatus);
                            }
                        }
                        else if (!string.IsNullOrEmpty(result.StatusText))
                        {
                            // ステータステキストのみの更新
                            UpdateSessionProcessingStatus(sessionInfo, result.StatusText, activeSessionId, updateStatus);
                        }
                    }
                    else
                    {
                        // 処理完了
                        UpdateSessionProcessingStatus(sessionInfo, null, activeSessionId, updateStatus);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Output analysis error for session {SessionId}", sessionInfo.SessionId);
            }
        }

        private void UpdateSessionProcessingStatus(SessionInfo session, string? statusText, Guid activeSessionId, Action<Guid, string?> updateStatus)
        {
                session.ProcessingStatus = statusText;
                if (statusText != null)
                {
                    session.ProcessingStartTime = DateTime.Now;
                    session.LastProcessingUpdateTime = DateTime.Now;
                    
                    // セッションごとのタイマーをリセット
                    ResetSessionTimer(session.SessionId);
                }
                else
                {
                    _logger.LogDebug("Processing completed for session {SessionId}. ProcessingElapsedSeconds: {ElapsedSeconds}", 
                        session.SessionId, session.ProcessingElapsedSeconds);
                    
                    // 処理完了時の通知（クリア前にチェック）
                    var processingElapsedSeconds = session.ProcessingElapsedSeconds;
                    
                    // セッション情報をクリア
                    session.ProcessingStartTime = null;
                    session.ProcessingElapsedSeconds = null;
                    session.ProcessingTokens = null;
                    session.ProcessingDirection = null;
                    session.LastProcessingUpdateTime = null;
                    session.LastProcessingSeconds = null;
                    session.IsWaitingForUserInput = false;
                    
                    // セッションのタイマーを停止
                    StopSessionTimer(session.SessionId);
                    
                    // 通知処理（経過時間があった場合のみ）
                    if (processingElapsedSeconds.HasValue)
                    {
                       
                        // セッション情報をコピー（非同期処理で使用するため）
                        var sessionCopy = new SessionInfo
                        {
                            SessionId = session.SessionId,
                            DisplayName = session.DisplayName,
                            FolderPath = session.FolderPath,
                            FolderName = session.FolderName,
                            CreatedAt = session.CreatedAt,
                            LastAccessedAt = session.LastAccessedAt,
                            IsActive = session.IsActive,
                            TerminalType = session.TerminalType,
                            Options = session.Options,
                            Memo = session.Memo
                        };
                        var elapsedSecondsCopy = processingElapsedSeconds.Value;
                        
                        Task.Run(async () => 
                        {
                            try
                            {
                                await _notificationService.NotifyProcessingCompleteAsync(sessionCopy, elapsedSecondsCopy);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Notification error for session {SessionId}", session.SessionId);
                            }
                        });
                    }
                    else
                    {
                        _logger.LogDebug("ProcessingElapsedSeconds is null for session {SessionId}, notification flag NOT set", session.SessionId);
                    }
                    
                    session.ProcessingStartTime = null;
                    session.ProcessingElapsedSeconds = null;
                    session.ProcessingTokens = null;
                    session.ProcessingDirection = null;
                    session.LastProcessingUpdateTime = null;
                    session.LastProcessingSeconds = null;
                    session.LastProcessingTokens = null;
                    session.IsWaitingForUserInput = false;
                    
                    // セッションのタイマーを停止
                    StopSessionTimer(session.SessionId);
                }
                
                // UIを更新
                updateStatus(session.SessionId, statusText);
        }

        private void UpdateSessionProcessingStatus(SessionInfo session, int elapsedSeconds, string tokens, string direction, Guid activeSessionId, Action<Guid, string?> updateStatus)
        {
                session.ProcessingElapsedSeconds = elapsedSeconds;
                session.ProcessingTokens = tokens;
                session.ProcessingDirection = direction;
                
                if (session.ProcessingStartTime == null)
                {
                    session.ProcessingStartTime = DateTime.Now;
                }
                
                session.LastProcessingUpdateTime = DateTime.Now;
                
                // セッションごとのタイマーをリセット
                ResetSessionTimer(session.SessionId);
                
                // UIを更新
                updateStatus(session.SessionId, $"{elapsedSeconds}s");
        }

        private void UpdateSessionProcessingStatus(SessionInfo session, string statusText, int elapsedSeconds, string tokens, string direction, Guid activeSessionId, Action<Guid, string?> updateStatus)
        {
                session.ProcessingStatus = statusText;
                session.ProcessingElapsedSeconds = elapsedSeconds;
                session.ProcessingTokens = tokens;
                session.ProcessingDirection = direction;
                
                if (session.ProcessingStartTime == null)
                {
                    session.ProcessingStartTime = DateTime.Now;
                }
                
                session.LastProcessingUpdateTime = DateTime.Now;
                
                // セッションごとのタイマーをリセット
                ResetSessionTimer(session.SessionId);
                
                // UIを更新
                updateStatus(session.SessionId, statusText);
        }

        public void ResetSessionTimer(Guid sessionId)
        {
            // 既存のタイマーを停止
            StopSessionTimer(sessionId);
            
            // 新しいタイマーを作成
            var timer = new Timer(
                (state) => CheckSessionTimeout(sessionId),
                null,
                TimeSpan.FromSeconds(5),
                Timeout.InfiniteTimeSpan
            );
            
            _sessionProcessingTimers[sessionId] = timer;
        }

        public void StopSessionTimer(Guid sessionId)
        {
            if (_sessionProcessingTimers.TryGetValue(sessionId, out var timer))
            {
                timer?.Dispose();
                _sessionProcessingTimers.Remove(sessionId);
            }
        }

        private void CheckSessionTimeout(Guid sessionId)
        {
            _logger.LogDebug("CheckSessionTimeout called for session {SessionId}", sessionId);
            _timeoutCallback?.Invoke(sessionId);
        }

        public void SetTimeoutCallback(Action<Guid> timeoutCallback)
        {
            _timeoutCallback = timeoutCallback;
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            _disposed = true;
            
            // すべてのタイマーを停止
            foreach (var timer in _sessionProcessingTimers.Values)
            {
                timer?.Dispose();
            }
            _sessionProcessingTimers.Clear();
        }
    }
}