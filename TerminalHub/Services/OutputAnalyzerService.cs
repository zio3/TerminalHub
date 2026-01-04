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
                        // 処理が中断された（ユーザーが止めたので通知不要）
                        UpdateSessionProcessingStatus(sessionInfo, null, activeSessionId, updateStatus, skipNotification: true);
                    }
                    else if (result.IsProcessing)
                    {
                        // ユーザー入力待ち状態をセッションに設定
                        sessionInfo.IsWaitingForUserInput = result.IsWaitingForUser;

                        // ステータステキストを決定
                        var statusText = result.ProcessingText ?? result.StatusText;

                        if (!string.IsNullOrEmpty(statusText))
                        {
                            // GeminiCLIの場合は経過秒数も設定
                            if (result.ElapsedSeconds.HasValue)
                            {
                                sessionInfo.ProcessingElapsedSeconds = result.ElapsedSeconds.Value;
                            }

                            UpdateSessionProcessingStatus(sessionInfo, statusText, activeSessionId, updateStatus);
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

        private void UpdateSessionProcessingStatus(SessionInfo session, string? statusText, Guid activeSessionId, Action<Guid, string?> updateStatus, bool skipNotification = false)
        {
                session.ProcessingStatus = statusText;
                if (statusText != null)
                {
                    // 処理開始時（初回のみ）にWebhook通知を送信
                    // ただし、セッション接続直後（1秒以内）は過去バッファの誤検出を防ぐためスキップ
                    // ClaudeCode の場合は Hook 経由で通知されるためスキップ
                    if (session.ProcessingStartTime == null && !skipNotification &&
                        session.TerminalType != TerminalType.ClaudeCode)
                    {
                        var isRecentConnection = session.LastConnectionTime.HasValue &&
                            (DateTime.Now - session.LastConnectionTime.Value).TotalSeconds < 1.0;

                        if (!isRecentConnection)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await _notificationService.NotifyProcessingStartAsync(session);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "処理開始通知でエラー: {SessionId}", session.SessionId);
                                }
                            });
                        }
                        else
                        {
                            _logger.LogDebug("接続直後のため開始通知をスキップ: {SessionId}", session.SessionId);
                        }
                    }

                    session.ProcessingStartTime = DateTime.Now;
                    session.LastProcessingUpdateTime = DateTime.Now;

                    // セッションごとのタイマーをリセット
                    ResetSessionTimer(session.SessionId);
                }
                else
                {
                    // 処理完了時の経過時間を計算（ProcessingElapsedSecondsがない場合はProcessingStartTimeから計算）
                    int? elapsedSeconds = session.ProcessingElapsedSeconds;
                    if (!elapsedSeconds.HasValue && session.ProcessingStartTime.HasValue)
                    {
                        elapsedSeconds = (int)(DateTime.Now - session.ProcessingStartTime.Value).TotalSeconds;
                    }

                    _logger.LogDebug("Processing completed for session {SessionId}. ElapsedSeconds: {ElapsedSeconds}",
                        session.SessionId, elapsedSeconds);

                    // 通知処理（経過時間があり、スキップでない場合のみ）
                    // ClaudeCode の場合は Hook 経由で通知されるためスキップ
                    if (elapsedSeconds.HasValue && !skipNotification &&
                        session.TerminalType != TerminalType.ClaudeCode)
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

                        Task.Run(async () =>
                        {
                            try
                            {
                                await _notificationService.NotifyProcessingCompleteAsync(sessionCopy, elapsedSeconds.Value);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Notification error for session {SessionId}", session.SessionId);
                            }
                        });
                    }
                    else if (skipNotification)
                    {
                        _logger.LogDebug("Notification skipped (user interrupted) for session {SessionId}", session.SessionId);
                    }

                    // セッション情報をクリア
                    session.ProcessingStartTime = null;
                    session.ProcessingElapsedSeconds = null;
                    session.LastProcessingUpdateTime = null;
                    session.IsWaitingForUserInput = false;

                    // セッションのタイマーを停止
                    StopSessionTimer(session.SessionId);
                }
                
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