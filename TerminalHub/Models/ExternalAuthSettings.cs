namespace TerminalHub.Models;

/// <summary>
/// 外部アクセス時のBasic認証設定
/// X-Forwarded-Forヘッダーが存在する場合（ngrok等のトンネル経由）のみ認証を要求
/// </summary>
public class ExternalAuthSettings
{
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>
    /// 認証が有効かどうか（UsernameとPasswordの両方が設定されている場合のみ有効）
    /// </summary>
    public bool IsEnabled => !string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password);
}
