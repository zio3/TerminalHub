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
    private readonly IConfiguration _configuration;
    private readonly ILogger<MqttService> _logger;
    private IMqttClient? _mqttClient;
    private string? _currentTopicGuid;
    private volatile bool _intentionalDisconnect;

    /// <summary>現在購読中のトピックGUID</summary>
    public string? CurrentTopicGuid => _currentTopicGuid;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MqttService(
        IAppSettingsService appSettingsService,
        ISessionManager sessionManager,
        IRemoteLaunchService remoteLaunchService,
        IConfiguration configuration,
        ILogger<MqttService> logger)
    {
        _appSettingsService = appSettingsService;
        _sessionManager = sessionManager;
        _remoteLaunchService = remoteLaunchService;
        _configuration = configuration;
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

        _intentionalDisconnect = false;
        _currentTopicGuid = topicGuid;

        var factory = new MqttClientFactory();
        _mqttClient = factory.CreateMqttClient();

        var mqttHost = _configuration.GetValue<string>("Mqtt:Host") ?? "vps3.zio3.net";
        var mqttPort = _configuration.GetValue<int>("Mqtt:Port", 1883);
        var mqttUsername = _configuration.GetValue<string>("Mqtt:Username");
        var mqttPassword = _configuration.GetValue<string>("Mqtt:Password");

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(mqttHost, mqttPort)
            .WithClientId($"terminalhub-{topicGuid[..8]}")
            .WithCleanSession(true);

        if (!string.IsNullOrEmpty(mqttUsername))
        {
            optionsBuilder.WithCredentials(mqttUsername, mqttPassword);
        }

        var options = optionsBuilder.Build();

        _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _mqttClient.DisconnectedAsync += OnDisconnectedAsync;

        try
        {
            await _mqttClient.ConnectAsync(options, cancellationToken);
            _logger.LogInformation("[MQTT] ブローカーに接続: {Host}:{Port}", mqttHost, mqttPort);

            var requestTopic = $"{MqttConstants.TopicPrefix}/{topicGuid}/request";
            await _mqttClient.SubscribeAsync(requestTopic, MqttQualityOfServiceLevel.AtLeastOnce, cancellationToken);
            _logger.LogInformation("[MQTT] トピック購読完了");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MQTT] 接続失敗");
        }
    }

    public async Task DisconnectAsync()
    {
        _intentionalDisconnect = true;
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
        if (e.ClientWasConnected && !_intentionalDisconnect)
        {
            _logger.LogWarning("[MQTT] 接続が切断されました。5秒後に再接続を試みます...");
            await Task.Delay(5000);

            // 意図的な切断（ConnectAsync/DisconnectAsync呼び出し）の場合は再接続しない
            if (_intentionalDisconnect) return;

            var topicGuid = _currentTopicGuid;
            if (!string.IsNullOrEmpty(topicGuid))
            {
                try
                {
                    await ConnectAsync(topicGuid);
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
            _logger.LogDebug("[MQTT] メッセージ受信: {Payload}", payload);

            var request = JsonSerializer.Deserialize<MqttRequest>(payload, JsonOptions);
            if (request == null) return;

            _logger.LogInformation("[MQTT] メッセージ受信: action={Action}, sessionId={SessionId}", request.Action, request.SessionId);

            // pingは認証不要（接続確認用）
            if (string.Equals(request.Action, "ping", StringComparison.OrdinalIgnoreCase))
            {
                await PublishResponseAsync(new { action = "pong" });
                return;
            }

            // パスワード認証チェック
            if (!ValidatePassword(request.PasswordHash))
            {
                _logger.LogWarning("[MQTT] 認証失敗: action={Action}", request.Action);
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
                case "disconnect":
                    if (request.SessionId.HasValue)
                        await HandleDisconnectAsync(request.SessionId.Value);
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
        var sortMode = _appSettingsService.GetSettings().Sessions.SortMode;
        var claudeSessions = _sessionManager.GetActiveSessions()
            .Where(s => s.TerminalType == TerminalType.ClaudeCode);

        var pinSorted = claudeSessions
            .OrderByDescending(s => s.IsPinned)
            .ThenBy(s => s.IsPinned && s.PinPriority.HasValue ? s.PinPriority.Value : int.MaxValue);

        IOrderedEnumerable<SessionInfo> sorted = sortMode switch
        {
            "name" => pinSorted.ThenBy(s => s.GetDisplayName(), StringComparer.CurrentCultureIgnoreCase),
            "createdAt" => pinSorted.ThenBy(s => s.CreatedAt),
            _ => pinSorted.ThenByDescending(s => s.LastAccessedAt)
        };

        var sessions = sorted.Select(s => new
            {
                id = s.SessionId.ToString(),
                name = s.GetDisplayName(),
                memo = string.IsNullOrEmpty(s.Memo) ? null : s.Memo,
                type = s.TerminalType.ToString(),
                remoteControlUrl = s.RemoteControlUrl
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

    private async Task HandleDisconnectAsync(Guid sessionId)
    {
        _remoteLaunchService.DisconnectRemoteSession(sessionId);
        await PublishResponseAsync(new { action = "disconnect", status = "ok", sessionId = sessionId.ToString() });
        _logger.LogInformation("[MQTT] リモートセッション切断: {SessionId}", sessionId);
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
