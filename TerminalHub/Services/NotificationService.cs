using Microsoft.Extensions.Configuration;
using Microsoft.JSInterop;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TerminalHub.Models;

namespace TerminalHub.Services
{
    public class NotificationService : INotificationService
    {
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IJSRuntime _jsRuntime;
        private readonly ILogger<NotificationService> _logger;

        public NotificationService(
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            IJSRuntime jsRuntime,
            ILogger<NotificationService> logger)
        {
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
            _jsRuntime = jsRuntime;
            _logger = logger;
        }

        public async Task NotifyProcessingCompleteAsync(SessionInfo session, int elapsedSeconds)
        {
            var thresholdSeconds = _configuration.GetValue<int>("NotificationSettings:ProcessingTimeThresholdSeconds", 60);

            // 閾値を超えていない場合は通知しない（0秒設定の場合は常に通知）
            if (thresholdSeconds > 0 && elapsedSeconds < thresholdSeconds)
            {
                _logger.LogDebug($"処理時間 {elapsedSeconds}秒は閾値 {thresholdSeconds}秒未満のため通知をスキップ");
                return;
            }

            // ブラウザ通知
            if (_configuration.GetValue<bool>("NotificationSettings:EnableBrowserNotifications", true))
            {
                await SendBrowserNotificationAsync(session, elapsedSeconds);
            }

            // WebHook通知
            var webHookEnabled = _configuration.GetValue<bool>("NotificationSettings:WebHookSettings:Enabled", false);
            if (webHookEnabled)
            {
                await SendWebHookNotificationAsync(session, elapsedSeconds);
            }
        }

        private async Task SendBrowserNotificationAsync(SessionInfo session, int elapsedSeconds)
        {
            try
            {
                _logger.LogInformation($"[Notification Debug] SendBrowserNotificationAsync開始");
                _logger.LogInformation($"[Notification Debug] SessionId: {session?.SessionId}");
                _logger.LogInformation($"[Notification Debug] DisplayName: {session?.DisplayName}");
                _logger.LogInformation($"[Notification Debug] FolderPath: {session?.FolderPath}");
                _logger.LogInformation($"[Notification Debug] CreatedAt: {session?.CreatedAt}");
                
                var minutes = elapsedSeconds / 60;
                var seconds = elapsedSeconds % 60;
                var timeText = minutes > 0 ? $"{minutes}分{seconds}秒" : $"{seconds}秒";

                var title = "処理が完了しました";
                var displayName = session?.GetDisplayName() ?? "不明なセッション";
                var body = $"{displayName} - 処理時間: {timeText}";
                var tag = session?.SessionId.ToString() ?? Guid.NewGuid().ToString();

                _logger.LogInformation($"[Notification Debug] 通知内容 - Title: {title}, Body: {body}, Tag: {tag}");
                
                await _jsRuntime.InvokeVoidAsync("terminalHubHelpers.showNotification", title, body, tag);
                
                _logger.LogInformation($"[Notification Debug] ブラウザ通知送信完了");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ブラウザ通知の送信に失敗しました");
                _logger.LogError($"[Notification Debug] エラー詳細: {ex.ToString()}");
            }
        }

        private async Task SendWebHookNotificationAsync(SessionInfo session, int elapsedSeconds)
        {
            try
            {
                var url = _configuration.GetValue<string>("NotificationSettings:WebHookSettings:Url");
                if (string.IsNullOrEmpty(url))
                {
                    _logger.LogWarning("WebHook URLが設定されていません");
                    return;
                }

                var httpClient = _httpClientFactory.CreateClient();

                // ヘッダーを設定（Content-Type以外）
                var headers = _configuration.GetSection("NotificationSettings:WebHookSettings:Headers")
                    .Get<Dictionary<string, string>>();
                if (headers != null)
                {
                    foreach (var header in headers)
                    {
                        // Content-Typeは後でStringContentで設定されるためスキップ
                        if (!header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        {
                            httpClient.DefaultRequestHeaders.Add(header.Key, header.Value);
                        }
                    }
                }

                // ペイロードを作成
                var payload = new
                {
                    sessionId = session?.SessionId ?? Guid.Empty,
                    sessionName = session?.GetDisplayName() ?? "不明なセッション",
                    terminalType = session?.TerminalType.ToString() ?? "Unknown",
                    elapsedSeconds = elapsedSeconds,
                    elapsedMinutes = Math.Round(elapsedSeconds / 60.0, 2),
                    timestamp = DateTime.UtcNow,
                    folderPath = session?.FolderPath ?? ""
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                _logger.LogInformation($"WebHook送信中: {url}");
                _logger.LogInformation($"ペイロード: {json}");
                
                var response = await httpClient.PostAsync(url, content);
                
                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"WebHook呼び出しが失敗しました: {response.StatusCode}, Body: {responseBody}");
                }
                else
                {
                    _logger.LogInformation($"WebHook呼び出しが成功しました: {url} - Status: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WebHook通知の送信に失敗しました");
            }
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
    }
}