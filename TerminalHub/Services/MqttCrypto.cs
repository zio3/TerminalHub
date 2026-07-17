using System.Security.Cryptography;
using System.Text;

namespace TerminalHub.Services;

/// <summary>
/// MQTT リモート起動で使う暗号プリミティブ（RSA-OAEP / AES-256-GCM）。
/// MqttService から純ロジックとして切り出し、ラウンドトリップ・改ざん検知を
/// ヘッドレステストで固定できるようにする（セキュリティ境界のため）。
/// </summary>
public static class MqttCrypto
{
    /// <summary>
    /// RSA-OAEP-SHA256でデータを暗号化し、Base64文字列を返す。
    /// PKCS#1（RSA PUBLIC KEY）とPKCS#8/X.509（PUBLIC KEY）の両形式に対応。
    /// </summary>
    public static string RsaEncrypt(byte[] data, string publicKeyBase64)
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
    /// AES-256-GCMで復号する。IV/Tag/暗号文のいずれかが改ざんされていると
    /// AesGcm.Decrypt が CryptographicException を投げる（GCM の認証）。
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
}
