using Microsoft.Extensions.Configuration;
using Microsoft.JSInterop;
using TerminalHub.Models;

namespace TerminalHub.Services
{
    public class NotificationService : INotificationService
    {
        private readonly IConfiguration _configuration;
        private readonly IJSRuntime _jsRuntime;
        private readonly ILogger<NotificationService> _logger;
        private readonly IAppSettingsService _appSettingsService;

        public NotificationService(
            IConfiguration configuration,
            IJSRuntime jsRuntime,
            ILogger<NotificationService> logger,
            IAppSettingsService appSettingsService)
        {
            _configuration = configuration;
            _jsRuntime = jsRuntime;
            _logger = logger;
            _appSettingsService = appSettingsService;
        }

        public async Task NotifyProcessingCompleteAsync(SessionInfo session, int elapsedSeconds)
        {
            // 設定を取得
            var notificationSettings = GetNotificationSettings();
            var webhookSettings = GetWebhookSettings();

            var thresholdSeconds = notificationSettings.ProcessingTimeThresholdSeconds;

            // 閾値を超えていない場合は通知しない（0秒設定の場合は常に通知）
            if (thresholdSeconds > 0 && elapsedSeconds < thresholdSeconds)
            {
                _logger.LogDebug($"処理時間 {elapsedSeconds}秒は閾値 {thresholdSeconds}秒未満のため通知をスキップ");
                return;
            }

            // ブラウザ通知
            if (notificationSettings.EnableBrowserNotifications)
            {
                await SendBrowserNotificationAsync(session, elapsedSeconds);
            }

            // WebHook通知
            if (webhookSettings.Enabled)
            {
                await SendWebHookNotificationAsync(session, elapsedSeconds, webhookSettings);
            }
        }

        private async Task SendBrowserNotificationAsync(SessionInfo session, int elapsedSeconds)
        {
            try
            {
                var minutes = elapsedSeconds / 60;
                var seconds = elapsedSeconds % 60;
                var timeText = minutes > 0 ? $"{minutes}分{seconds}秒" : $"{seconds}秒";

                var title = "処理が完了しました";
                var displayName = session?.GetDisplayName() ?? "不明なセッション";
                var body = $"{displayName} - 処理時間: {timeText}";
                var tag = session?.SessionId.ToString() ?? Guid.NewGuid().ToString();

                _logger.LogDebug("ブラウザ通知送信: SessionId={SessionId}, Body={Body}", session?.SessionId, body);

                await _jsRuntime.InvokeVoidAsync("terminalHubHelpers.showNotification", title, body, tag);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ブラウザ通知の送信に失敗しました");
            }
        }

        private async Task SendWebHookNotificationAsync(SessionInfo session, int elapsedSeconds, WebhookSettings webhookSettings)
        {
            await SendWebHookEventAsync(session, "complete", elapsedSeconds);
        }

        public async Task<bool> RequestBrowserNotificationPermissionAsync()
        {
            try
            {
                var permission = await _jsRuntime.InvokeAsync<string>("terminalHubHelpers.requestNotificationPermission");
                return permission == "granted";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "通知権限の要求に失敗しました");
                return false;
            }
        }

        private NotificationSettings GetNotificationSettings()
        {
            return _appSettingsService.GetSettings().Notifications;
        }

        private WebhookSettings GetWebhookSettings()
        {
            return _appSettingsService.GetSettings().Webhook;
        }

        /// <summary>
        /// タスク開始時にWebhook通知を送信
        /// </summary>
        public async Task NotifyProcessingStartAsync(SessionInfo session)
        {
            var webhookSettings = GetWebhookSettings();

            if (!webhookSettings.Enabled || string.IsNullOrEmpty(webhookSettings.Url))
            {
                return;
            }

            await SendWebHookEventAsync(session, "start", null);
        }

        // 非 ClaudeCode セッション（Gemini/Codex/Terminal など）の Webhook 送信。
        // ClaudeCode は Hook 経由（HookNotificationService）で送るため、ここは出力解析ベースの
        // 疑似 start/complete を扱う。送信経路は AppSettingsService.SendWebhookAsync に統一し、
        // ペイロード形式（フィールド名・生 GUID の sessionId・elapsedMinutes 付与など）を
        // Hook 経由と揃える。eventType は従来どおり "start"/"complete" を維持し、
        // tool は付けない（null。ClaudeCode の Hook 由来とだけ区別できれば十分なため）。
        private async Task SendWebHookEventAsync(SessionInfo session, string eventType, int? elapsedSeconds)
        {
            await _appSettingsService.SendWebhookAsync(new WebhookPayload
            {
                EventType = eventType,                                    // "start" / "complete"
                SessionId = session?.SessionId ?? Guid.Empty,
                SessionName = session?.GetDisplayName() ?? "不明なセッション",
                TerminalType = session?.TerminalType.ToString() ?? "Unknown",
                ElapsedSeconds = elapsedSeconds,                          // start では null、complete で値
                FolderPath = session?.FolderPath ?? "",
                Tool = null,                                             // 非 ClaudeCode は tool を付けない
            });
        }
    }
}