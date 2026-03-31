using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;
using TerminalHub.Constants;
using TerminalHub.Models;

namespace TerminalHub.Services;

public class MqttService : IHostedService, IDisposable
{
    private readonly IAppSettingsService _appSettingsService;
    private readonly ISessionManager _sessionManager;
    private readonly IRemoteLaunchService _remoteLaunchService;
    private readonly ILogger<MqttService> _logger;
    private IMqttClient? _mqttClient;
    private string? _currentTopicGuid;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MqttService(
        IAppSettingsService appSettingsService,
        ISessionManager sessionManager,
        IRemoteLaunchService remoteLaunchService,
        ILogger<MqttService> logger)
    {
        _appSettingsService = appSettingsService;
        _sessionManager = sessionManager;
        _remoteLaunchService = remoteLaunchService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = _appSettingsService.GetSettings().RemoteLaunch;
        if (!settings.Enabled || string.IsNullOrEmpty(settings.TopicGuid))
        {
            _logger.LogInformation("[MQTT] リモート起動は無効です");
            return;
        }

        await ConnectAsync(settings.TopicGuid, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await DisconnectAsync();
    }

    public async Task ConnectAsync(string topicGuid, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync();

        _currentTopicGuid = topicGuid;

        var factory = new MqttClientFactory();
        _mqttClient = factory.CreateMqttClient();

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(MqttConstants.BrokerHost, MqttConstants.TcpPort)
            .WithClientId($"terminalhub-{topicGuid[..8]}")
            .WithCleanSession(true)
            .Build();

        _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _mqttClient.DisconnectedAsync += OnDisconnectedAsync;

        try
        {
            await _mqttClient.ConnectAsync(options, cancellationToken);
            _logger.LogInformation("[MQTT] ブローカーに接続: {Host}:{Port}", MqttConstants.BrokerHost, MqttConstants.TcpPort);

            var requestTopic = $"{MqttConstants.TopicPrefix}/{topicGuid}/request";
            await _mqttClient.SubscribeAsync(requestTopic, MqttQualityOfServiceLevel.AtLeastOnce, cancellationToken);
            _logger.LogInformation("[MQTT] トピック購読: {Topic}", requestTopic);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MQTT] 接続失敗");
        }
    }

    public async Task DisconnectAsync()
    {
        if (_mqttClient?.IsConnected == true)
        {
            try
            {
                await _mqttClient.DisconnectAsync();
                _logger.LogInformation("[MQTT] 切断完了");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MQTT] 切断エラー");
            }
        }
        _mqttClient?.Dispose();
        _mqttClient = null;
    }

    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        if (e.ClientWasConnected)
        {
            _logger.LogWarning("[MQTT] 接続が切断されました。5秒後に再接続を試みます...");
            await Task.Delay(5000);

            var settings = _appSettingsService.GetSettings().RemoteLaunch;
            if (settings.Enabled && !string.IsNullOrEmpty(settings.TopicGuid))
            {
                try
                {
                    await ConnectAsync(settings.TopicGuid);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[MQTT] 再接続失敗");
                }
            }
        }
    }

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var payload = e.ApplicationMessage.ConvertPayloadToString();
            _logger.LogInformation("[MQTT] メッセージ受信: {Payload}", payload);

            var request = JsonSerializer.Deserialize<MqttRequest>(payload, JsonOptions);
            if (request == null) return;

            // パスワード認証チェック
            if (!ValidatePassword(request.PasswordHash))
            {
                await PublishResponseAsync(new { action = "error", message = "unauthorized" });
                return;
            }

            switch (request.Action?.ToLower())
            {
                case "list":
                    await HandleListAsync();
                    break;
                case "launch":
                    if (request.SessionId.HasValue)
                        await HandleLaunchAsync(request.SessionId.Value);
                    else
                        await PublishResponseAsync(new { action = "error", message = "sessionId required" });
                    break;
                default:
                    await PublishResponseAsync(new { action = "error", message = "unknown action" });
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MQTT] メッセージ処理エラー");
            await PublishResponseAsync(new { action = "error", message = ex.Message });
        }
    }

    private bool ValidatePassword(string? requestPasswordHash)
    {
        var settings = _appSettingsService.GetSettings().RemoteLaunch;

        // パスワード未設定なら認証不要
        if (string.IsNullOrEmpty(settings.PasswordHash))
            return true;

        // パスワード設定済みだがリクエストにハッシュがない
        if (string.IsNullOrEmpty(requestPasswordHash))
            return false;

        return string.Equals(settings.PasswordHash, requestPasswordHash, StringComparison.OrdinalIgnoreCase);
    }

    private async Task HandleListAsync()
    {
        var sessions = _sessionManager.GetActiveSessions()
            .Where(s => s.TerminalType == TerminalType.ClaudeCode)
            .Select(s => new
            {
                id = s.SessionId.ToString(),
                name = s.GetDisplayName(),
                folder = s.FolderPath,
                type = s.TerminalType.ToString()
            })
            .ToList();

        await PublishResponseAsync(new { action = "list", sessions });
        _logger.LogInformation("[MQTT] セッション一覧返却: {Count}件", sessions.Count);
    }

    private async Task HandleLaunchAsync(Guid sessionId)
    {
        await PublishResponseAsync(new { action = "launch", status = "started", sessionId = sessionId.ToString() });

        var url = await _remoteLaunchService.LaunchRemoteControlAsync(sessionId);
        if (url != null)
        {
            await PublishResponseAsync(new { action = "launch", status = "ready", sessionId = sessionId.ToString(), url });
            _logger.LogInformation("[MQTT] Remote Control URL返却: {Url}", url);
        }
        else
        {
            await PublishResponseAsync(new { action = "error", message = "launch failed or timeout" });
        }
    }

    private async Task PublishResponseAsync(object payload)
    {
        if (_mqttClient?.IsConnected != true || string.IsNullOrEmpty(_currentTopicGuid))
            return;

        var responseTopic = $"{MqttConstants.TopicPrefix}/{_currentTopicGuid}/response";
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(responseTopic)
            .WithPayload(json)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _mqttClient.PublishAsync(message);
    }

    public void Dispose()
    {
        _mqttClient?.Dispose();
    }

    /// <summary>
    /// SHA256ハッシュを生成するユーティリティ
    /// </summary>
    public static string ComputePasswordHash(string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return Convert.ToHexStringLower(bytes);
    }
}

/// <summary>
/// MQTTリクエストペイロード
/// </summary>
public class MqttRequest
{
    public string? Action { get; set; }
    public Guid? SessionId { get; set; }
    public string? PasswordHash { get; set; }
}
