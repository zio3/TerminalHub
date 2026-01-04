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
    private readonly IWebhookSettingsService _webhookSettingsService;

    public event EventHandler<HookNotificationEventArgs>? OnHookNotification;

    public HookNotificationService(
        ILogger<HookNotificationService> logger,
        ISessionManager sessionManager,
        IWebhookSettingsService webhookSettingsService)
    {
        _logger = logger;
        _sessionManager = sessionManager;
        _webhookSettingsService = webhookSettingsService;
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

        // イベントを発火
        OnHookNotification?.Invoke(this, new HookNotificationEventArgs(notification));

        // イベント種類に応じた処理
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
    }

    private async Task HandleStopEventAsync(SessionInfo session, HookNotification notification)
    {
        _logger.LogInformation("Stop イベント処理: Session={SessionName}", session.GetDisplayName());

        // 処理時間を計算
        var elapsedSeconds = 0;
        if (session.ProcessingStartTime.HasValue)
        {
            elapsedSeconds = (int)(DateTime.Now - session.ProcessingStartTime.Value).TotalSeconds;
        }

        // Webhook通知を送信
        try
        {
            await _webhookSettingsService.SendWebhookAsync(
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

        // 処理状態をリセット
        session.ProcessingStartTime = null;
        session.ProcessingStatus = null;
    }

    private async Task HandleUserPromptSubmitEventAsync(SessionInfo session, HookNotification notification)
    {
        _logger.LogInformation("UserPromptSubmit イベント処理: Session={SessionName}", session.GetDisplayName());

        // 処理開始を記録
        session.ProcessingStartTime = DateTime.Now;
        session.ProcessingStatus = "処理中";

        // Webhook通知を送信
        try
        {
            await _webhookSettingsService.SendWebhookAsync(
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
