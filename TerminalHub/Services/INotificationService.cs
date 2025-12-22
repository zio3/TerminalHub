using TerminalHub.Models;

namespace TerminalHub.Services
{
    public interface INotificationService
    {
        /// <summary>
        /// タスク開始時にWebhook通知を送信
        /// </summary>
        Task NotifyProcessingStartAsync(SessionInfo session);

        /// <summary>
        /// タスク完了時に通知を送信（ブラウザ通知 + Webhook）
        /// </summary>
        Task NotifyProcessingCompleteAsync(SessionInfo session, int elapsedSeconds);

        Task<bool> RequestBrowserNotificationPermissionAsync();
    }
}