using Microsoft.Extensions.Configuration;
using Microsoft.JSInterop;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerminalHub.Models;

namespace TerminalHub.Services
{
    public class NotificationService : INotificationService
    {
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IJSRuntime _jsRuntime;
        private readonly ILogger<NotificationService> _logger;
        private readonly IAppSettingsService _appSettingsService;

        public NotificationService(
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            IJSRuntime jsRuntime,
            ILogger<NotificationService> logger,
            IAppSettingsService appSettingsService)
        {
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
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
            await SendWebHookEventAsync(session, "complete", elapsedSeconds, webhookSettings);
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

            await SendWebHookEventAsync(session, "start", null, webhookSettings);
        }

        private async Task SendWebHookEventAsync(SessionInfo session, string eventType, int? elapsedSeconds, WebhookSettings webhookSettings)
        {
            try
            {
                // IHttpClientFactory から取得した HttpClient は Dispose 不要（ファクトリーが管理）
                var httpClient = _httpClientFactory.CreateClient();
                httpClient.Timeout = TimeSpan.FromSeconds(10);

                // ヘッダーを設定（重複時は上書きせず警告ログ）
                if (webhookSettings.Headers != null)
                {
                    foreach (var header in webhookSettings.Headers)
                    {
                        if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        {
                            continue; // Content-Type は StringContent で設定されるためスキップ
                        }

                        if (httpClient.DefaultRequestHeaders.Contains(header.Key))
                        {
                            _logger.LogDebug("ヘッダー '{HeaderKey}' は既に存在するためスキップ", header.Key);
                            continue;
                        }

                        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

                // ペイロードを作成
                var payload = new
                {
                    eventType = eventType,  // "start" or "complete"
                    sessionId = session?.SessionId ?? Guid.Empty,
                    sessionName = session?.GetDisplayName() ?? "不明なセッション",
                    terminalType = session?.TerminalType.ToString() ?? "Unknown",
                    elapsedSeconds = elapsedSeconds,
                    elapsedMinutes = elapsedSeconds.HasValue ? Math.Round(elapsedSeconds.Value / 60.0, 2) : (double?)null,
                    timestamp = DateTime.UtcNow,
                    folderPath = session?.FolderPath ?? ""
                };

                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                _logger.LogInformation($"WebHook送信中 ({eventType}): {webhookSettings.Url}");
                _logger.LogDebug($"ペイロード: {json}");

                using var response = await httpClient.PostAsync(webhookSettings.Url, content);

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"WebHook呼び出しが失敗しました: {response.StatusCode}, Body: {responseBody}");
                }
                else
                {
                    _logger.LogInformation($"WebHook呼び出しが成功しました ({eventType}): {webhookSettings.Url}");
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, $"WebHook通知のネットワークエラー ({eventType})");
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, $"WebHook通知がタイムアウトしました ({eventType})");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"WebHook通知の送信に失敗しました ({eventType})");
            }
        }
    }
}