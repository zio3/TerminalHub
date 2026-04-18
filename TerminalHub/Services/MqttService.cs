using System.Reflection;
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

    /// <summary>現在有効なワンタイムnonce</summary>
    private string? _currentNonce;
    private DateTime _nonceCreatedAt;
    private readonly object _nonceLock = new();
    private static readonly TimeSpan NonceExpiry = TimeSpan.FromSeconds(30);

    /// <summary>handshake時に生成されるセッション鍵（AES-256用、32byte）</summary>
    private byte[]? _sessionKey;
    private string? _handshakeId;
    private DateTime _sessionKeyCreatedAt;
    private static readonly TimeSpan SessionKeyExpiry = TimeSpan.FromMinutes(5);

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
        _sessionKey = null;
        _handshakeId = null;

        var factory = new MqttClientFactory();
        _mqttClient = factory.CreateMqttClient();

        var mqttHost = _configuration.GetValue<string>("Mqtt:Host") ?? "vps3.zio3.net";
        var mqttPort = _configuration.GetValue<int>("Mqtt:Port", 1883);
        var mqttUsername = _configuration.GetValue<string>("Mqtt:Username");
        var mqttPassword = _configuration.GetValue<string>("Mqtt:Password");

        var clientId = $"terminalhub-{topicGuid[..8]}";
        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(mqttHost, mqttPort)
            .WithClientId(clientId)
            .WithCleanSession(true);

        if (!string.IsNullOrEmpty(mqttUsername))
        {
            optionsBuilder.WithCredentials(mqttUsername, mqttPassword);
        }

        var options = optionsBuilder.Build();

        _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _mqttClient.DisconnectedAsync += OnDisconnectedAsync;

        _logger.LogInformation("[MQTT] 接続試行: {Host}:{Port}, ClientId={ClientId}, HasCredentials={HasCredentials}",
            mqttHost, mqttPort, clientId, !string.IsNullOrEmpty(mqttUsername));

        try
        {
            var connectResult = await _mqttClient.ConnectAsync(options, cancellationToken);
            _logger.LogInformation(
                "[MQTT] ConnectAsync戻り: ResultCode={ResultCode}, ReasonString={ReasonString}, AssignedClientId={AssignedClientId}, IsSessionPresent={IsSessionPresent}, IsConnected={IsConnected}",
                connectResult?.ResultCode, connectResult?.ReasonString, connectResult?.AssignedClientIdentifier, connectResult?.IsSessionPresent, _mqttClient.IsConnected);

            if (!_mqttClient.IsConnected)
            {
                _logger.LogError("[MQTT] Connect直後にIsConnected=false。ClientId重複で別クライアントに蹴られた可能性が高い (ClientId={ClientId})", clientId);
                return;
            }

            var requestTopic = $"{MqttConstants.TopicPrefix}/{topicGuid}/request";
            var subResult = await _mqttClient.SubscribeAsync(requestTopic, MqttQualityOfServiceLevel.AtLeastOnce, cancellationToken);
            foreach (var item in subResult.Items)
            {
                _logger.LogInformation("[MQTT] SUBACK: Topic={Topic}, ResultCode={ResultCode}",
                    item.TopicFilter.Topic, item.ResultCode);
            }
            _logger.LogInformation("[MQTT] トピック購読完了: {Topic}", requestTopic);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MQTT] 接続失敗 (Host={Host}:{Port}, ClientId={ClientId}, IsConnected={IsConnected})",
                mqttHost, mqttPort, clientId, _mqttClient?.IsConnected);
        }
    }

    public async Task DisconnectAsync()
    {
        _intentionalDisconnect = true;
        _sessionKey = null;
        _handshakeId = null;
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
        _logger.LogWarning(
            "[MQTT] 切断検知: Reason={Reason}, ReasonString={ReasonString}, ClientWasConnected={ClientWasConnected}, Intentional={Intentional}, ConnectResultCode={ConnectResultCode}, Exception={ExceptionType}/{ExceptionMessage}",
            e.Reason, e.ReasonString, e.ClientWasConnected, _intentionalDisconnect,
            e.ConnectResult?.ResultCode, e.Exception?.GetType().Name, e.Exception?.Message);

        if (!e.ClientWasConnected || _intentionalDisconnect) return;

        _sessionKey = null;
        _handshakeId = null;

        var topicGuid = _currentTopicGuid;
        if (string.IsNullOrEmpty(topicGuid)) return;

        var delays = new[] { 5, 10, 20, 30, 30, 30, 30, 30, 30, 30, 30, 30 };

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
        var requestTopic = $"{MqttConstants.TopicPrefix}/{topicGuid}/request";

        for (int i = 0; i < delays.Length; i++)
        {
            _logger.LogWarning("[MQTT] 接続断。{Delay}秒後にリトライ ({Attempt}/{Max})",
                delays[i], i + 1, delays.Length);
            await Task.Delay(delays[i] * 1000);

            if (_intentionalDisconnect) return;

            try
            {
                var connectResult = await _mqttClient!.ConnectAsync(options);
                _logger.LogInformation(
                    "[MQTT] リトライConnectAsync戻り: ResultCode={ResultCode}, ReasonString={ReasonString}, IsConnected={IsConnected}",
                    connectResult?.ResultCode, connectResult?.ReasonString, _mqttClient.IsConnected);

                if (!_mqttClient.IsConnected)
                {
                    _logger.LogWarning("[MQTT] リトライ Connect直後に IsConnected=false ({Attempt}/{Max})", i + 1, delays.Length);
                    continue;
                }

                await _mqttClient.SubscribeAsync(requestTopic, MqttQualityOfServiceLevel.AtLeastOnce);
                _logger.LogInformation("[MQTT] 再接続成功 (リトライ {Attempt}回目)", i + 1);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MQTT] リトライ失敗 ({Attempt}/{Max}): {ExceptionType}/{Message}",
                    i + 1, delays.Length, ex.GetType().Name, ex.Message);
            }
        }

        _logger.LogError("[MQTT] 再接続断念（{Max}回リトライ失敗）", delays.Length);
    }

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var payload = e.ApplicationMessage.ConvertPayloadToString();
            _logger.LogDebug("[MQTT] メッセージ受信");

            var envelope = JsonSerializer.Deserialize<MqttEnvelope>(payload, JsonOptions);
            if (envelope == null) return;

            // 平文メッセージ（ping / nonce）
            if (!string.IsNullOrEmpty(envelope.Action))
            {
                await HandlePlaintextMessageAsync(envelope);
                return;
            }

            // 暗号化メッセージ
            if (!string.IsNullOrEmpty(envelope.Encrypted))
            {
                await HandleEncryptedMessageAsync(envelope.Encrypted);
                return;
            }

            await PublishResponseAsync(new { action = "error", message = "invalid message format" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MQTT] メッセージ処理エラー");
            await PublishResponseAsync(new { action = "error", message = ex.Message });
        }
    }

    /// <summary>
    /// 平文メッセージの処理（ping, nonce のみ許可）
    /// </summary>
    private async Task HandlePlaintextMessageAsync(MqttEnvelope envelope)
    {
        _logger.LogInformation("[MQTT] 平文メッセージ: action={Action}", envelope.Action);

        // ping: 疎通確認（バージョン情報のみ）
        if (string.Equals(envelope.Action, "ping", StringComparison.OrdinalIgnoreCase))
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "unknown";
            await PublishResponseAsync(new { action = "pong", version });
            return;
        }

        // handshake: セッション鍵を生成し、RSA暗号化して返却
        if (string.Equals(envelope.Action, "handshake", StringComparison.OrdinalIgnoreCase))
        {
            var publicKeyBase64 = _configuration.GetValue<string>("Mqtt:PublicKey");
            if (string.IsNullOrEmpty(publicKeyBase64))
            {
                await PublishResponseAsync(new { action = "error", message = "public key not configured" });
                return;
            }

            _sessionKey = RandomNumberGenerator.GetBytes(32);
            _handshakeId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            _sessionKeyCreatedAt = DateTime.UtcNow;

            string encryptedSessionKey;
            try
            {
                encryptedSessionKey = RsaEncrypt(_sessionKey, publicKeyBase64);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MQTT] RSA暗号化失敗");
                _sessionKey = null;
                _handshakeId = null;
                await PublishResponseAsync(new { action = "error", message = "handshake failed" });
                return;
            }

            await PublishResponseAsync(new { action = "handshake", requestId = envelope.RequestId, handshakeId = _handshakeId, sessionKey = encryptedSessionKey });
            _logger.LogInformation("[MQTT] セッション鍵を生成・配信 (handshakeId={HandshakeId})", _handshakeId);
            return;
        }

        // nonce発行は認証不要（暗号化リクエストの前提ステップ）
        if (string.Equals(envelope.Action, "nonce", StringComparison.OrdinalIgnoreCase))
        {
            var nonce = GenerateNonce();
            await PublishResponseAsync(new { action = "nonce", nonce });
            _logger.LogInformation("[MQTT] nonce発行");
            return;
        }

        // それ以外の平文アクションは拒否（暗号化必須）
        _logger.LogWarning("[MQTT] 暗号化されていないリクエストを拒否: action={Action}", envelope.Action);
        await PublishResponseAsync(new { action = "error", message = "encryption required" });
    }

    /// <summary>
    /// 暗号化メッセージの処理
    /// </summary>
    private async Task HandleEncryptedMessageAsync(string encryptedBase64)
    {
        if (_sessionKey == null)
        {
            _logger.LogWarning("[MQTT] セッション鍵が未確立");
            await PublishResponseAsync(new { action = "error", message = "session not established" });
            return;
        }

        if (DateTime.UtcNow - _sessionKeyCreatedAt > SessionKeyExpiry)
        {
            _logger.LogWarning("[MQTT] セッション鍵が期限切れ");
            _sessionKey = null;
            _handshakeId = null;
            await PublishResponseAsync(new { action = "error", message = "session expired" });
            return;
        }

        // 復号
        string plainJson;
        try
        {
            plainJson = AesGcmDecrypt(encryptedBase64, _sessionKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[MQTT] 復号失敗（認証エラー）: {Message}", ex.Message);
            await PublishResponseAsync(new { action = "error", message = "unauthorized" });
            return;
        }

        _logger.LogDebug("[MQTT] 復号後ペイロード: {PlainJson}", plainJson);

        var request = JsonSerializer.Deserialize<MqttRequest>(plainJson, JsonOptions);
        if (request == null)
        {
            await PublishResponseAsync(new { action = "error", message = "invalid decrypted payload" });
            return;
        }

        _logger.LogInformation("[MQTT] 暗号化メッセージ: action={Action}, sessionId={SessionId}", request.Action, request.SessionId);

        // handshakeId照合（セッション識別）
        if (!string.Equals(_handshakeId, request.HandshakeId, StringComparison.Ordinal))
        {
            _logger.LogWarning("[MQTT] handshakeId不一致: action={Action}", request.Action);
            await PublishEncryptedResponseAsync(new { action = "error", message = "session mismatch" }, _sessionKey);
            return;
        }

        // パスワード検証（設定済みの場合）
        if (!ValidatePassword(request.PasswordHash))
        {
            _logger.LogWarning("[MQTT] パスワード検証失敗: action={Action}", request.Action);
            await PublishEncryptedResponseAsync(new { action = "error", message = "unauthorized" }, _sessionKey);
            return;
        }

        // nonceチェック（リプレイ攻撃防止）
        if (!ValidateAndConsumeNonce(request.Nonce))
        {
            _logger.LogWarning("[MQTT] nonce検証失敗: action={Action}", request.Action);
            await PublishEncryptedResponseAsync(new { action = "error", message = "invalid or expired nonce" }, _sessionKey);
            return;
        }

        // アクション実行（レスポンスも暗号化）
        switch (request.Action?.ToLower())
        {
            case "list":
                await HandleListAsync(_sessionKey);
                break;
            case "launch":
                if (request.SessionId.HasValue)
                    await HandleLaunchAsync(request.SessionId.Value, _sessionKey);
                else
                    await PublishEncryptedResponseAsync(new { action = "error", message = "sessionId required" }, _sessionKey);
                break;
            case "disconnect":
                if (request.SessionId.HasValue)
                    await HandleDisconnectAsync(request.SessionId.Value, _sessionKey);
                else
                    await PublishEncryptedResponseAsync(new { action = "error", message = "sessionId required" }, _sessionKey);
                break;
            default:
                await PublishEncryptedResponseAsync(new { action = "error", message = "unknown action" }, _sessionKey);
                break;
        }
    }

    #region パスワード検証

    private bool ValidatePassword(string? requestPasswordHash)
    {
        var settings = _appSettingsService.GetSettings().RemoteLaunch;

        // パスワード未設定でリクエストにもハッシュがない → OK
        if (string.IsNullOrEmpty(settings.PasswordHash) && string.IsNullOrEmpty(requestPasswordHash))
            return true;

        // パスワード未設定だがリクエストにハッシュがある → 不一致
        if (string.IsNullOrEmpty(settings.PasswordHash))
        {
            _logger.LogDebug("[MQTT] パスワード検証: サーバー未設定だがリクエストにpasswordHashあり");
            return false;
        }

        // パスワード設定済みだがリクエストにハッシュがない
        if (string.IsNullOrEmpty(requestPasswordHash))
        {
            _logger.LogDebug("[MQTT] パスワード検証: リクエストにpasswordHashなし（サーバー側は設定済み）");
            return false;
        }

        var match = string.Equals(settings.PasswordHash, requestPasswordHash, StringComparison.OrdinalIgnoreCase);
        _logger.LogDebug("[MQTT] パスワード検証: 一致={Match}", match);
        return match;
    }

    #endregion

    #region Nonce管理

    private string GenerateNonce()
    {
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        lock (_nonceLock)
        {
            _currentNonce = nonce;
            _nonceCreatedAt = DateTime.UtcNow;
        }
        return nonce;
    }

    private bool ValidateAndConsumeNonce(string? nonce)
    {
        if (string.IsNullOrEmpty(nonce))
            return false;

        lock (_nonceLock)
        {
            if (_currentNonce == null || !string.Equals(_currentNonce, nonce, StringComparison.Ordinal))
                return false;

            if (DateTime.UtcNow - _nonceCreatedAt > NonceExpiry)
            {
                _currentNonce = null;
                return false;
            }

            _currentNonce = null;
            return true;
        }
    }

    #endregion

    #region RSA暗号化

    /// <summary>
    /// RSA-OAEP-SHA256でデータを暗号化し、Base64文字列を返す。
    /// PKCS#1（RSA PUBLIC KEY）とPKCS#8/X.509（PUBLIC KEY）の両形式に対応。
    /// </summary>
    private static string RsaEncrypt(byte[] data, string publicKeyBase64)
    {
        using var rsa = RSA.Create();
        var keyBytes = Convert.FromBase64String(publicKeyBase64);
        try
        {
            rsa.ImportRSAPublicKey(keyBytes, out _); // PKCS#1形式
        }
        catch
        {
            rsa.ImportSubjectPublicKeyInfo(keyBytes, out _); // PKCS#8/X.509形式
        }
        var encrypted = rsa.Encrypt(data, RSAEncryptionPadding.OaepSHA256);
        return Convert.ToBase64String(encrypted);
    }

    #endregion

    #region AES-GCM暗号化

    /// <summary>
    /// AES-256-GCMで暗号化し、Base64文字列を返す。
    /// フォーマット: [12byte IV][16byte Tag][暗号文]
    /// </summary>
    public static string AesGcmEncrypt(string plaintext, byte[] keyBytes)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var iv = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintextBytes.Length];

        using var aes = new AesGcm(keyBytes, tag.Length);
        aes.Encrypt(iv, plaintextBytes, ciphertext, tag);

        var result = new byte[iv.Length + tag.Length + ciphertext.Length];
        iv.CopyTo(result, 0);
        tag.CopyTo(result, iv.Length);
        ciphertext.CopyTo(result, iv.Length + tag.Length);

        return Convert.ToBase64String(result);
    }

    /// <summary>
    /// AES-256-GCMで復号する。
    /// </summary>
    public static string AesGcmDecrypt(string encryptedBase64, byte[] keyBytes)
    {
        var data = Convert.FromBase64String(encryptedBase64);

        var iv = data[..12];
        var tag = data[12..28];
        var ciphertext = data[28..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(keyBytes, tag.Length);
        aes.Decrypt(iv, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    #endregion

    #region ハンドラー

    private async Task HandleListAsync(byte[] sessionKey)
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

        await PublishEncryptedResponseAsync(new { action = "list", sessions }, sessionKey);
        _logger.LogInformation("[MQTT] セッション一覧返却: {Count}件", sessions.Count);
    }

    private async Task HandleLaunchAsync(Guid sessionId, byte[] sessionKey)
    {
        await PublishEncryptedResponseAsync(new { action = "launch", status = "started", sessionId = sessionId.ToString() }, sessionKey);

        var url = await _remoteLaunchService.LaunchRemoteControlAsync(sessionId);
        if (url != null)
        {
            await PublishEncryptedResponseAsync(new { action = "launch", status = "ready", sessionId = sessionId.ToString(), url }, sessionKey);
            _logger.LogInformation("[MQTT] Remote Control URL返却完了");
        }
        else
        {
            await PublishEncryptedResponseAsync(new { action = "error", message = "launch failed or timeout" }, sessionKey);
        }
    }

    private async Task HandleDisconnectAsync(Guid sessionId, byte[] sessionKey)
    {
        _remoteLaunchService.DisconnectRemoteSession(sessionId);
        await PublishEncryptedResponseAsync(new { action = "disconnect", status = "ok", sessionId = sessionId.ToString() }, sessionKey);
        _logger.LogInformation("[MQTT] リモートセッション切断: {SessionId}", sessionId);
    }

    #endregion

    #region レスポンス送信

    /// <summary>平文レスポンス（ping/pong, nonce, エラー）</summary>
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

    /// <summary>暗号化レスポンス</summary>
    private async Task PublishEncryptedResponseAsync(object payload, byte[] sessionKey)
    {
        if (_mqttClient?.IsConnected != true || string.IsNullOrEmpty(_currentTopicGuid))
            return;

        var responseTopic = $"{MqttConstants.TopicPrefix}/{_currentTopicGuid}/response";
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var encrypted = AesGcmEncrypt(json, sessionKey);
        var envelopeJson = JsonSerializer.Serialize(new { encrypted }, JsonOptions);

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(responseTopic)
            .WithPayload(envelopeJson)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _mqttClient.PublishAsync(message);
    }

    #endregion

    public void Dispose()
    {
        _mqttClient?.Dispose();
    }

    /// <summary>
    /// SHA256ハッシュを生成するユーティリティ
    /// </summary>
    public static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// パスワードハッシュ生成（設定画面から使用）
    /// </summary>
    public static string ComputePasswordHash(string password) => ComputeHash(password);
}

/// <summary>
/// MQTTメッセージのエンベロープ（平文/暗号化の両方を受け付ける）
/// </summary>
public class MqttEnvelope
{
    /// <summary>平文アクション（ping, handshake, nonce用）</summary>
    public string? Action { get; set; }
    /// <summary>handshakeリクエストの相関子（クライアント生成）</summary>
    public string? RequestId { get; set; }
    /// <summary>AES-GCM暗号化されたペイロード（Base64）</summary>
    public string? Encrypted { get; set; }
}

/// <summary>
/// 復号後のリクエストペイロード
/// </summary>
public class MqttRequest
{
    public string? Action { get; set; }
    public Guid? SessionId { get; set; }
    /// <summary>handshakeで取得したセッション識別子</summary>
    public string? HandshakeId { get; set; }
    /// <summary>ワンタイムnonce（リプレイ攻撃防止）</summary>
    public string? Nonce { get; set; }
    /// <summary>パスワードハッシュ（パスワード設定時のみ）</summary>
    public string? PasswordHash { get; set; }
}
