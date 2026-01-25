using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using TerminalHub.Models;

namespace TerminalHub.Middleware;

/// <summary>
/// 外部アクセス時にBasic認証を要求するミドルウェア
/// localhost (127.0.0.1, ::1) 以外からのアクセスには認証を要求
/// </summary>
public class ExternalAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ExternalAuthSettings _settings;
    private readonly ILogger<ExternalAuthMiddleware> _logger;

    public ExternalAuthMiddleware(
        RequestDelegate next,
        IOptions<ExternalAuthSettings> settings,
        ILogger<ExternalAuthMiddleware> logger)
    {
        _next = next;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 認証設定が無効なら常にスキップ
        if (!_settings.IsEnabled)
        {
            await _next(context);
            return;
        }

        // リクエスト元IPアドレスを取得
        var remoteIp = context.Connection.RemoteIpAddress;

        // localhostからのアクセスは認証スキップ
        if (IsLocalhost(remoteIp))
        {
            await _next(context);
            return;
        }

        // 外部アクセスの場合、Basic認証をチェック
        _logger.LogDebug("外部アクセスを検出: RemoteIP={RemoteIP}", remoteIp);

        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();

        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            // 認証ヘッダーなし → 401を返す
            ReturnUnauthorized(context);
            return;
        }

        // Basic認証の検証
        try
        {
            var encodedCredentials = authHeader.Substring("Basic ".Length).Trim();
            var credentialBytes = Convert.FromBase64String(encodedCredentials);
            var credentials = Encoding.UTF8.GetString(credentialBytes);
            var parts = credentials.Split(':', 2);

            if (parts.Length == 2)
            {
                var username = parts[0];
                var password = parts[1];

                if (username == _settings.Username && password == _settings.Password)
                {
                    // 認証成功
                    _logger.LogDebug("外部アクセスの認証に成功");
                    await _next(context);
                    return;
                }
            }
        }
        catch (FormatException)
        {
            // Base64デコード失敗
            _logger.LogWarning("不正なBasic認証ヘッダー形式");
        }

        // 認証失敗
        _logger.LogWarning("外部アクセスの認証に失敗");
        ReturnUnauthorized(context);
    }

    private static void ReturnUnauthorized(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"TerminalHub\"";
    }

    /// <summary>
    /// IPアドレスがlocalhostかどうかを判定
    /// </summary>
    private static bool IsLocalhost(IPAddress? ipAddress)
    {
        if (ipAddress == null)
            return false;

        // ループバックアドレス (127.0.0.1, ::1)
        if (IPAddress.IsLoopback(ipAddress))
            return true;

        // IPv4-mapped IPv6アドレス (::ffff:127.0.0.1) の場合
        if (ipAddress.IsIPv4MappedToIPv6)
        {
            var ipv4 = ipAddress.MapToIPv4();
            return IPAddress.IsLoopback(ipv4);
        }

        return false;
    }
}

/// <summary>
/// ミドルウェア拡張メソッド
/// </summary>
public static class ExternalAuthMiddlewareExtensions
{
    public static IApplicationBuilder UseExternalAuth(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<ExternalAuthMiddleware>();
    }
}
