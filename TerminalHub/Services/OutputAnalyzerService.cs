using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TerminalHub.Models;
using TerminalHub.Analyzers;

namespace TerminalHub.Services
{
    public class OutputAnalyzerService : IOutputAnalyzerService
    {
        private readonly ILogger<OutputAnalyzerService> _logger;
        private readonly INotificationService _notificationService;
        private readonly IOutputAnalyzerFactory _analyzerFactory;
        private readonly ISessionTimerService _sessionTimerService;

        public OutputAnalyzerService(
            ILogger<OutputAnalyzerService> logger,
            INotificationService notificationService,
            IOutputAnalyzerFactory analyzerFactory,
            ISessionTimerService sessionTimerService)
        {
            _logger = logger;
            _notificationService = notificationService;
            _analyzerFactory = analyzerFactory;
            _sessionTimerService = sessionTimerService;
        }

        public void AnalyzeOutput(string data, SessionInfo sessionInfo, Guid activeSessionId, Action<Guid, string?>? updateStatus)
        {
            if (sessionInfo == null)
            {
                _logger.LogWarning("AnalyzeOutput called with null sessionInfo");
                return;
            }

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
                    _logger.LogDebug("[OutputAnalyzer] 解析成功: SessionId={SessionId}, IsProcessing={IsProcessing}, ProcessingText={ProcessingText}",
                        sessionInfo.SessionId, result.IsProcessing, result.ProcessingText ?? "(null)");

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
                // TryAnalyze が false の場合は何もしない（ステータスパターンに一致しないデータ）
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Output analysis error for session {SessionId}", sessionInfo.SessionId);
            }
        }

        // Stop イベント後、この秒数間は OutputAnalyzer からのステータス更新をスキップ
        private const double StopEventCooldownSeconds = 3.0;

        private void UpdateSessionProcessingStatus(SessionInfo session, string? statusText, Guid activeSessionId, Action<Guid, string?>? updateStatus, bool skipNotification = false)
        {
                // ClaudeCode の場合、Stop イベント直後は OutputAnalyzer からの更新をスキップ
                // 遅延した出力によるステータス再設定を防ぐ
                if (session.TerminalType == TerminalType.ClaudeCode && session.LastStopEventTime.HasValue)
                {
                    var timeSinceStop = (DateTime.Now - session.LastStopEventTime.Value).TotalSeconds;
                    if (timeSinceStop < StopEventCooldownSeconds)
                    {
                        _logger.LogDebug(
                            "Stop後 {Seconds:F1}秒のためステータス更新をスキップ: {SessionId}, statusText={StatusText}",
                            timeSinceStop, session.SessionId, statusText ?? "(null)");
                        // UIコールバックは呼ぶ（現在のステータスを表示更新）
                        updateStatus?.Invoke(session.SessionId, session.ProcessingStatus);
                        return;
                    }
                }

                session.ProcessingStatus = statusText;
                if (statusText != null)
                {
                    // 処理開始時（初回のみ）にWebhook通知を送信
                    // ただし、セッション接続直後（10秒以内）は過去バッファの誤検出を防ぐためスキップ
                    // ClaudeCode の場合は Hook 経由で通知されるためスキップ
                    if (session.ProcessingStartTime == null && !skipNotification &&
                        session.TerminalType != TerminalType.ClaudeCode)
                    {
                        if (!session.IsRecentConnection)
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

                    // セッションごとのタイマーをリセット（ISessionTimerServiceに委譲）
                    ResetSessionTimer(session.SessionId);
                }
                else
                {
                    // セッション接続直後（10秒以内）は過去バッファの誤検出を防ぐためスキップ
                    if (session.IsRecentConnection)
                    {
                        _logger.LogDebug("接続直後のため処理完了検出をスキップ: {SessionId}", session.SessionId);
                        return;
                    }

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
                        var sessionCopy = session.CloneForNotification();

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

                    // 最終利用時刻を更新（ソート用）
                    _logger.LogInformation("[LastAccessedAt更新] きっかけ: OutputAnalyzerService(処理完了検出), セッション: {SessionName}", session.GetDisplayName());
                    session.LastAccessedAt = DateTime.Now;

                    // セッション情報をクリア
                    session.ProcessingStartTime = null;
                    session.ProcessingElapsedSeconds = null;
                    session.LastProcessingUpdateTime = null;
                    session.IsWaitingForUserInput = false;

                    // セッションのタイマーを停止（ISessionTimerServiceに委譲）
                    StopSessionTimer(session.SessionId);
                }

                // UIを更新（コールバックが設定されている場合のみ）
                updateStatus?.Invoke(session.SessionId, statusText);
        }

        public void ResetSessionTimer(Guid sessionId)
        {
            _sessionTimerService.ResetSessionTimer(sessionId);
        }

        public void StopSessionTimer(Guid sessionId)
        {
            _sessionTimerService.StopSessionTimer(sessionId);
        }

        public void SetTimeoutCallback(Action<Guid> timeoutCallback)
        {
            _sessionTimerService.SetTimeoutCallback(timeoutCallback);
        }

        /// <summary>
        /// データにアニメーションパターンが含まれている場合、タイマーをリセットする
        /// </summary>
        public bool CheckAnimationPatternAndResetTimer(string data, SessionInfo sessionInfo)
        {
            if (sessionInfo == null || sessionInfo.ProcessingStartTime == null)
            {
                return false;
            }

            // ターミナルタイプに応じた解析器を取得
            var analyzer = _analyzerFactory.GetAnalyzer(sessionInfo.TerminalType);
            if (analyzer == null)
            {
                return false;
            }

            // アニメーションパターンが含まれているかチェック
            if (analyzer.ContainsAnimationPattern(data))
            {
                sessionInfo.LastProcessingUpdateTime = DateTime.Now;
                ResetSessionTimer(sessionInfo.SessionId);
                return true;
            }

            return false;
        }
    }
}
