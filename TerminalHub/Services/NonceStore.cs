using System.Security.Cryptography;

namespace TerminalHub.Services;

/// <summary>
/// MQTT リモート起動のワンタイム nonce（リプレイ攻撃防止）を1個だけ保持するストア。
///
/// 発行した nonce は「1回のみ」「有効期限内のみ」検証を通す。検証成功で即消費（同じ nonce の
/// 二度目は失敗）。時刻依存の期限判定を単体テストできるよう、現在時刻は引数で受ける
/// （MqttService からは DateTime.UtcNow を渡す）。スレッド安全（内部ロック）。
/// </summary>
public sealed class NonceStore
{
    private readonly object _lock = new();
    private readonly TimeSpan _expiry;
    private string? _currentNonce;
    private DateTime _createdAt;

    /// <param name="expiry">nonce の有効期限。既定 30 秒。</param>
    public NonceStore(TimeSpan? expiry = null)
    {
        _expiry = expiry ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// 新しい nonce を発行して保持する（既存の未消費 nonce は上書きされる）。
    /// </summary>
    public string Generate(DateTime now)
    {
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        lock (_lock)
        {
            _currentNonce = nonce;
            _createdAt = now;
        }
        return nonce;
    }

    /// <summary>
    /// nonce を検証し、成功なら消費する（true を返し、以後同じ nonce は false）。
    /// 一致しない・期限切れ・null/空 はいずれも false。期限切れは保持中の nonce も破棄する。
    /// </summary>
    public bool ValidateAndConsume(string? nonce, DateTime now)
    {
        if (string.IsNullOrEmpty(nonce))
            return false;

        lock (_lock)
        {
            if (_currentNonce == null || !string.Equals(_currentNonce, nonce, StringComparison.Ordinal))
                return false;

            if (now - _createdAt > _expiry)
            {
                _currentNonce = null;
                return false;
            }

            _currentNonce = null;
            return true;
        }
    }
}
