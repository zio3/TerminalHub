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
/// プレビュー版（CI の workflow_dispatch）ビルドに埋め込まれる追加情報。
/// 正式リリース版・ローカル dotnet run では null。
/// </summary>
public class PreviewBuildInfo
{
    /// <summary>適用済み最新PR番号（例: "88"）。取得できない場合は null</summary>
    public string? PrNumber { get; set; }

    /// <summary>ビルド元コミットの短縮SHA（例: "e83b47f"）</summary>
    public string? Commit { get; set; }

    /// <summary>ビルド日時（UTC, "yyyy-MM-dd"）</summary>
    public string? Date { get; set; }

    /// <summary>コミットSHAへのGitHubリンク（SHAが無い場合はリポジトリトップ）</summary>
    public string CommitUrl => string.IsNullOrWhiteSpace(Commit)
        ? "https://github.com/zio3/TerminalHub"
        : $"https://github.com/zio3/TerminalHub/commit/{Commit}";

    /// <summary>バッジ等に表示する整形済みテキスト（例: "PR #88 / e83b47f (2026-07-02)"）</summary>
    public string DisplayText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(PrNumber)) parts.Add($"PR #{PrNumber}");
            if (!string.IsNullOrWhiteSpace(Commit)) parts.Add(Commit!);
            var main = string.Join(" / ", parts);
            if (!string.IsNullOrWhiteSpace(Date))
            {
                main = string.IsNullOrEmpty(main) ? Date! : $"{main} ({Date})";
            }
            return main;
        }
    }
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
    /// プレビュー版ビルドの追加情報（PR番号 / コミットSHA / ビルド日時）。
    /// 正式リリース版・ローカルビルドでは null。
    /// </summary>
    PreviewBuildInfo? PreviewBuild { get; }

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

    public PreviewBuildInfo? PreviewBuild
    {
        get
        {
            var metadata = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Where(a => !string.IsNullOrEmpty(a.Key))
                .GroupBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);

            // "preview" チャンネルのビルドのときだけ表示（正式版・ローカルビルドは null）
            if (!metadata.TryGetValue("BuildChannel", out var channel) ||
                !string.Equals(channel, "preview", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            metadata.TryGetValue("BuildPr", out var pr);
            metadata.TryGetValue("BuildCommit", out var commit);
            metadata.TryGetValue("BuildDate", out var date);

            var info = new PreviewBuildInfo
            {
                PrNumber = string.IsNullOrWhiteSpace(pr) ? null : pr,
                Commit = string.IsNullOrWhiteSpace(commit) ? null : commit,
                Date = string.IsNullOrWhiteSpace(date) ? null : date,
            };

            // 表示できる中身が何もなければ null（チャンネルだけ preview で他が空のケース）
            return string.IsNullOrEmpty(info.DisplayText) ? null : info;
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
