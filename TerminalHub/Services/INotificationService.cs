using TerminalHub.Models;

namespace TerminalHub.Services
{
    public interface INotificationService
    {
        Task NotifyProcessingCompleteAsync(SessionInfo session, int elapsedSeconds);
        Task<bool> RequestBrowserNotificationPermissionAsync();
    }
}