using System.Security.Cryptography;
using TerminalHub.Services;
using Xunit;

namespace TerminalHub.Terminal.Tests;

/// <summary>
/// MqttCrypto（AES-256-GCM / RSA-OAEP）のラウンドトリップと改ざん検知。
/// リモート起動のセキュリティ境界のため、復号成功だけでなく
/// 改ざん時に確実に例外へ倒れることを固定する。
/// </summary>
public sealed class MqttCryptoTests
{
    private static byte[] NewAesKey() => RandomNumberGenerator.GetBytes(32); // AES-256

    [Fact]
    public void AesGcm_RoundTrip_RecoversPlaintext()
    {
        var key = NewAesKey();
        var plaintext = "{\"action\":\"launch\",\"sessionId\":\"abc\"}";

        var encrypted = MqttCrypto.AesGcmEncrypt(plaintext, key);
        var decrypted = MqttCrypto.AesGcmDecrypt(encrypted, key);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void AesGcm_RoundTrip_HandlesUnicode()
    {
        var key = NewAesKey();
        var plaintext = "日本語とemoji🚀を含むペイロード";

        var decrypted = MqttCrypto.AesGcmDecrypt(MqttCrypto.AesGcmEncrypt(plaintext, key), key);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void AesGcm_ProducesUniqueIvsPerCall()
    {
        // 同じ鍵・平文でも IV がランダムなので暗号文は毎回変わる（決定的暗号化でない）
        var key = NewAesKey();
        var a = MqttCrypto.AesGcmEncrypt("same", key);
        var b = MqttCrypto.AesGcmEncrypt("same", key);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void AesGcm_WrongKey_ThrowsCryptographicException()
    {
        var encrypted = MqttCrypto.AesGcmEncrypt("secret", NewAesKey());

        Assert.ThrowsAny<CryptographicException>(
            () => MqttCrypto.AesGcmDecrypt(encrypted, NewAesKey()));
    }

    [Fact]
    public void AesGcm_TamperedCiphertext_ThrowsCryptographicException()
    {
        var key = NewAesKey();
        var encrypted = MqttCrypto.AesGcmEncrypt("secret payload", key);

        // Base64 をバイト列へ戻し、暗号文の1バイトを反転（[12 IV][16 Tag][暗号文...]）
        var bytes = Convert.FromBase64String(encrypted);
        bytes[^1] ^= 0xFF;
        var tampered = Convert.ToBase64String(bytes);

        Assert.ThrowsAny<CryptographicException>(() => MqttCrypto.AesGcmDecrypt(tampered, key));
    }

    [Fact]
    public void AesGcm_TamperedTag_ThrowsCryptographicException()
    {
        var key = NewAesKey();
        var encrypted = MqttCrypto.AesGcmEncrypt("secret payload", key);

        var bytes = Convert.FromBase64String(encrypted);
        bytes[12] ^= 0xFF; // Tag の先頭バイトを改ざん
        var tampered = Convert.ToBase64String(bytes);

        Assert.ThrowsAny<CryptographicException>(() => MqttCrypto.AesGcmDecrypt(tampered, key));
    }

    [Fact]
    public void RsaEncrypt_ProducesCiphertextDecryptableByPrivateKey()
    {
        using var rsa = RSA.Create(2048);
        var publicKeyBase64 = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()); // PKCS#8/X.509
        var data = RandomNumberGenerator.GetBytes(32); // セッション鍵想定

        var encryptedBase64 = MqttCrypto.RsaEncrypt(data, publicKeyBase64);
        var recovered = rsa.Decrypt(Convert.FromBase64String(encryptedBase64), RSAEncryptionPadding.OaepSHA256);

        Assert.Equal(data, recovered);
    }

    [Fact]
    public void RsaEncrypt_AcceptsPkcs1PublicKeyFormat()
    {
        // PKCS#1（RSA PUBLIC KEY）形式でも読み込めることを確認
        using var rsa = RSA.Create(2048);
        var publicKeyBase64 = Convert.ToBase64String(rsa.ExportRSAPublicKey()); // PKCS#1
        var data = RandomNumberGenerator.GetBytes(16);

        var encryptedBase64 = MqttCrypto.RsaEncrypt(data, publicKeyBase64);
        var recovered = rsa.Decrypt(Convert.FromBase64String(encryptedBase64), RSAEncryptionPadding.OaepSHA256);

        Assert.Equal(data, recovered);
    }
}
