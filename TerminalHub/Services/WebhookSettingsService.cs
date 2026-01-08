using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TerminalHub.Services;

/// <summary>
/// Webhook設定をファイルで管理するサービス
/// </summary>
public interface IWebhookSettingsService
{
    /// <summary>
    /// Webhook設定を取得
    /// </summary>
    WebhookSettings? GetSettings();

    /// <summary>
    /// Webhook設定を保存
    /// </summary>
    void SaveSettings(WebhookSettings settings);

    /// <summary>
    /// Webhookを送信（設定が有効な場合のみ）
    /// </summary>
    Task SendWebhookAsync(string eventType, Guid sessionId, string sessionName,
        string terminalType, int? elapsedSeconds, string folderPath);
}

public class WebhookSettingsService : IWebhookSettingsService
{
    private readonly ILogger<WebhookSettingsService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _settingsFilePath;
    private WebhookSettings? _cachedSettings;
    private DateTime _lastReadTime = DateTime.MinValue;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public WebhookSettingsService(
        ILogger<WebhookSettingsService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;

        // 実行ファイルと同じディレクトリに設定ファイルを保存
        var exeDir = AppContext.BaseDirectory;
        _settingsFilePath = Path.Combine(exeDir, "webhook-settings.json");
    }

    public WebhookSettings? GetSettings()
    {
        lock (_lock)
        {
            // ファイルが更新されているかチェック（キャッシュを使用）
            if (_cachedSettings != null && File.Exists(_settingsFilePath))
            {
                var lastWrite = File.GetLastWriteTime(_settingsFilePath);
                if (lastWrite <= _lastReadTime)
                {
                    return _cachedSettings;
                }
            }

            try
            {
                if (!File.Exists(_settingsFilePath))
                {
                    return null;
                }

                var json = File.ReadAllText(_settingsFilePath);
                _cachedSettings = JsonSerializer.Deserialize<WebhookSettings>(json, JsonOptions);
                _lastReadTime = DateTime.Now;
                return _cachedSettings;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Webhook設定ファイルの読み込みに失敗: {Path}", _settingsFilePath);
                return null;
            }
        }
    }

    public void SaveSettings(WebhookSettings settings)
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(_settingsFilePath, json);
                _cachedSettings = settings;
                _lastReadTime = DateTime.Now;
                _logger.LogInformation("Webhook設定を保存しました: {Path}", _settingsFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Webhook設定ファイルの保存に失敗: {Path}", _settingsFilePath);
                throw;
            }
        }
    }

    public async Task SendWebhookAsync(string eventType, Guid sessionId, string sessionName,
        string terminalType, int? elapsedSeconds, string folderPath)
    {
        var settings = GetSettings();
        if (settings?.Enabled != true || string.IsNullOrEmpty(settings.Url))
        {
            _logger.LogDebug("Webhook通知をスキップ（無効または未設定）");
            return;
        }

        try
        {
            // IHttpClientFactory から取得した HttpClient は Dispose 不要（ファクトリーが管理）
            var httpClient = _httpClientFactory.CreateClient();

            // ヘッダーを設定
            if (settings.Headers != null)
            {
                foreach (var header in settings.Headers)
                {
                    if (!header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }

            // ペイロードを作成
            var payload = new
            {
                eventType = eventType,
                sessionId = sessionId,
                sessionName = sessionName,
                terminalType = terminalType,
                elapsedSeconds = elapsedSeconds,
                elapsedMinutes = elapsedSeconds.HasValue ? Math.Round(elapsedSeconds.Value / 60.0, 2) : (double?)null,
                timestamp = DateTime.UtcNow,
                folderPath = folderPath
            };

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            _logger.LogDebug("Webhook送信: {Url}, Event={Event}", settings.Url, eventType);

            using var response = await httpClient.PostAsync(settings.Url, content);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Webhook送信成功: {Event} -> {Url}", eventType, settings.Url);
            }
            else
            {
                _logger.LogWarning("Webhook送信失敗: {StatusCode} - {Url}", response.StatusCode, settings.Url);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Webhook送信エラー: {Url}", settings.Url);
        }
    }
}
