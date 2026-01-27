using Microsoft.Extensions.Logging;
using TerminalHub.Models;

namespace TerminalHub.Services;

/// <summary>
/// Claude Code hook からの通知を処理するサービス
/// </summary>
public interface IHookNotificationService
{
    /// <summary>
    /// Hook 通知を処理する
    /// </summary>
    Task HandleHookNotificationAsync(HookNotification notification);

    /// <summary>
    /// 通知イベントを登録する
    /// </summary>
    event EventHandler<HookNotificationEventArgs>? OnHookNotification;
}

/// <summary>
/// Hook 通知イベント引数
/// </summary>
public class HookNotificationEventArgs : EventArgs
{
    public HookNotification Notification { get; }

    public HookNotificationEventArgs(HookNotification notification)
    {
        Notification = notification;
    }
}

/// <summary>
/// HookNotificationService の実装
/// </summary>
public class HookNotificationService : IHookNotificationService
{
    private readonly ILogger<HookNotificationService> _logger;
    private readonly ISessionManager _sessionManager;
    private readonly IAppSettingsService _appSettingsService;
    private readonly ISessionTimerService _sessionTimerService;

    public event EventHandler<HookNotificationEventArgs>? OnHookNotification;

    public HookNotificationService(
        ILogger<HookNotificationService> logger,
        ISessionManager sessionManager,
        IAppSettingsService appSettingsService,
        ISessionTimerService sessionTimerService)
    {
        _logger = logger;
        _sessionManager = sessionManager;
        _appSettingsService = appSettingsService;
        _sessionTimerService = sessionTimerService;
    }

    public async Task HandleHookNotificationAsync(HookNotification notification)
    {
        var eventType = notification.GetEventType();
        if (eventType == null)
        {
            _logger.LogWarning("不明なイベント種類: {Event}", notification.Event);
            return;
        }

        _logger.LogInformation(
            "Hook通知を受信: Event={Event}, SessionId={SessionId}, Timestamp={Timestamp}",
            notification.Event,
            notification.SessionId,
            notification.Timestamp);

        // セッション情報を取得
        var session = _sessionManager.GetSessionInfo(notification.SessionId);
        if (session == null)
        {
            _logger.LogWarning("セッションが見つかりません: {SessionId}", notification.SessionId);
            return;
        }

        // イベント種類に応じた処理（ステータス更新を先に実行）
        switch (eventType)
        {
            case HookEventType.Stop:
                await HandleStopEventAsync(session, notification);
                break;

            case HookEventType.UserPromptSubmit:
                await HandleUserPromptSubmitEventAsync(session, notification);
                break;

            case HookEventType.Notification:
                await HandleNotificationEventAsync(session, notification);
                break;
        }

        // イベントを発火（ステータス更新後にUIを更新させる）
        OnHookNotification?.Invoke(this, new HookNotificationEventArgs(notification));
    }

    private async Task HandleStopEventAsync(SessionInfo session, HookNotification notification)
    {
        _logger.LogInformation("Stop イベント処理: Session={SessionName}", session.GetDisplayName());

        // タイムアウトタイマーを停止（SessionTimeoutを防ぐ）
        _sessionTimerService.StopSessionTimer(session.SessionId);

        // 処理時間を計算
        var elapsedSeconds = 0;
        if (session.ProcessingStartTime.HasValue)
        {
            elapsedSeconds = (int)(DateTime.Now - session.ProcessingStartTime.Value).TotalSeconds;
        }

        // Webhook通知を送信
        try
        {
            await _appSettingsService.SendWebhookAsync(
                "complete",
                session.SessionId,
                session.GetDisplayName(),
                session.TerminalType.ToString(),
                elapsedSeconds,
                session.FolderPath);
            _logger.LogInformation("処理完了通知を送信: Session={SessionName}, ElapsedSeconds={ElapsedSeconds}",
                session.GetDisplayName(), elapsedSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "処理完了通知の送信に失敗: Session={SessionName}", session.GetDisplayName());
        }

        // 最終利用時刻を更新（ソート用）
        _logger.LogInformation("[LastAccessedAt更新] きっかけ: HookNotificationService(Stop イベント), セッション: {SessionName}", session.GetDisplayName());
        session.LastAccessedAt = DateTime.Now;

        // 処理状態を完全にリセット（SessionTimeoutと同じ項目をクリア）
        _logger.LogInformation(
            "[ステータスクリア] きっかけ: HookNotificationService(Stop イベント), セッション: {SessionName}, 旧ステータス: {OldStatus}",
            session.GetDisplayName(),
            session.ProcessingStatus ?? "(なし)");

        // Stop イベント時刻を記録（OutputAnalyzerからの更新を一時的にスキップするため）
        session.LastStopEventTime = DateTime.Now;

        session.ProcessingStartTime = null;
        session.ProcessingStatus = null;
        session.ProcessingElapsedSeconds = null;
        session.LastProcessingUpdateTime = null;
        session.IsWaitingForUserInput = false;
    }

    private async Task HandleUserPromptSubmitEventAsync(SessionInfo session, HookNotification notification)
    {
        _logger.LogInformation("UserPromptSubmit イベント処理: Session={SessionName}", session.GetDisplayName());

        // 処理開始を記録
        _logger.LogInformation(
            "[処理開始] きっかけ: HookNotificationService(UserPromptSubmit イベント), セッション: {SessionName}",
            session.GetDisplayName());

        // Stop イベントのクールダウンをリセット（新しい処理開始のため）
        session.LastStopEventTime = null;

        session.ProcessingStartTime = DateTime.Now;
        // ProcessingStatus は OutputAnalyzer が実際のステータステキストを設定するため、ここでは設定しない

        // Webhook通知を送信
        try
        {
            await _appSettingsService.SendWebhookAsync(
                "start",
                session.SessionId,
                session.GetDisplayName(),
                session.TerminalType.ToString(),
                null,
                session.FolderPath);
            _logger.LogInformation("処理開始通知を送信: Session={SessionName}", session.GetDisplayName());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "処理開始通知の送信に失敗: Session={SessionName}", session.GetDisplayName());
        }
    }

    private Task HandleNotificationEventAsync(SessionInfo session, HookNotification notification)
    {
        _logger.LogInformation("Notification イベント処理: Session={SessionName}", session.GetDisplayName());
        // Notification イベントは Claude Code が通知を表示した時に発火
        // 必要に応じて追加の処理を実装
        return Task.CompletedTask;
    }
}
