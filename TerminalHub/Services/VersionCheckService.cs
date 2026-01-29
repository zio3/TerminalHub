using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace TerminalHub.Services;

/// <summary>
/// バージョンチェック結果
/// </summary>
public class VersionCheckResult
{
    /// <summary>
    /// 最新バージョン（例: "1.0.31"）
    /// </summary>
    public string LatestVersion { get; set; } = "";

    /// <summary>
    /// GitHubリリースページのURL
    /// </summary>
    public string ReleaseUrl { get; set; } = "";

    /// <summary>
    /// 新しいバージョンが利用可能か
    /// </summary>
    public bool IsNewVersionAvailable { get; set; }
}

/// <summary>
/// GitHub Releases API レスポンス
/// </summary>
internal class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = "";

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }
}

public interface IVersionCheckService
{
    /// <summary>
    /// 現在のアプリケーションバージョン
    /// </summary>
    string CurrentVersion { get; }

    /// <summary>
    /// GitHubで新しいバージョンがあるかチェック
    /// </summary>
    Task<VersionCheckResult?> CheckForUpdatesAsync();
}

public class VersionCheckService : IVersionCheckService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<VersionCheckService> _logger;

    private const string GitHubApiUrl = "https://api.github.com/repos/zio3/TerminalHub/releases/latest";
    private const string GitHubReleasesUrl = "https://github.com/zio3/TerminalHub/releases";

    public VersionCheckService(IHttpClientFactory httpClientFactory, ILogger<VersionCheckService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string CurrentVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (string.IsNullOrEmpty(version))
            {
                return "不明";
            }

            // "+commitHash" などのサフィックスを除去
            var plusIndex = version.IndexOf('+');
            if (plusIndex > 0)
            {
                version = version.Substring(0, plusIndex);
            }

            return version;
        }
    }

    public async Task<VersionCheckResult?> CheckForUpdatesAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "TerminalHub");
            client.Timeout = TimeSpan.FromSeconds(10);

            var release = await client.GetFromJsonAsync<GitHubRelease>(GitHubApiUrl);
            if (release == null || string.IsNullOrEmpty(release.TagName))
            {
                return null;
            }

            // "v1.0.30" -> "1.0.30" の形式に正規化
            var latestVersion = release.TagName.TrimStart('v', 'V');
            var currentVersion = CurrentVersion;

            var isNewAvailable = CompareVersions(currentVersion, latestVersion) < 0;

            return new VersionCheckResult
            {
                LatestVersion = latestVersion,
                ReleaseUrl = release.HtmlUrl ?? GitHubReleasesUrl,
                IsNewVersionAvailable = isNewAvailable
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "バージョンチェックに失敗しました");
            return null;
        }
    }

    /// <summary>
    /// バージョン文字列を比較
    /// </summary>
    /// <returns>v1 < v2: -1, v1 == v2: 0, v1 > v2: 1</returns>
    private static int CompareVersions(string v1, string v2)
    {
        if (v1 == "不明") return -1;

        var parts1 = v1.Split('.');
        var parts2 = v2.Split('.');

        var maxLength = Math.Max(parts1.Length, parts2.Length);

        for (int i = 0; i < maxLength; i++)
        {
            var num1 = i < parts1.Length && int.TryParse(parts1[i], out var n1) ? n1 : 0;
            var num2 = i < parts2.Length && int.TryParse(parts2[i], out var n2) ? n2 : 0;

            if (num1 < num2) return -1;
            if (num1 > num2) return 1;
        }

        return 0;
    }
}
