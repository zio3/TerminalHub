using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace TerminalHub.Services;

/// <summary>
/// 試験機能: 対応CLI(Claude Code / Codex)へ TerminalHub のローカル MCP サーバー
/// (terminalhub) を繋ぐサービス。ONにすると起動しただけで list_sessions / send_to_session が使える。
///
/// どちらの CLI も<b>起動オプションで渡す</b>方式で、ユーザーの設定ファイルは一切書き換えない。
/// MCP の URL は実行中のポートに依存するため、プロジェクトの設定ファイルへ永続化すると
/// 古いポートが残る・Git を汚す・マシンごとに競合する、といった問題が起きるため。
///
/// - Claude Code → <c>--mcp-config &lt;JSONパス&gt;</c>。JSON は TerminalHub 自身のデータ領域
///                 (<see cref="AppDataPaths.GetMcpConfigFilePath"/>) に置き、パスだけをコマンドラインに乗せる。
///                 生成はこのサービス (<see cref="EnsureClaudeMcpConfigFile"/>)、オプション付与は
///                 SessionManager/TerminalConstants 側。
/// - Codex       → <c>-c mcp_servers.terminalhub.url=&lt;URL&gt;</c>。ファイルを用意する必要がないので
///                 このサービスは <see cref="BuildMcpUrl"/> で URL を組むだけ。呼び出しは SessionManager、
///                 オプション付与は TerminalConstants.BuildCodexArgs が行う。
///
/// どちらも起動オプションなので、この機能が書いたものは残らない。
///
/// ただし旧バージョン（〜v1.0.70）は設定ファイルへ書き込む方式だったため、その残骸は残る
/// （Claude は <c>&lt;folder&gt;/.mcp.json</c>、Codex は <c>&lt;folder&gt;/.codex/config.toml</c>）。
/// TerminalHub からは消さないので、不要なら利用者が消す。どちらも当時のポートを指しているため、
/// <b>OFF にしている場合は残骸経由で古いポートへ繋ぎに行くことがある</b>。ON の間は起動オプションが
/// 優先される（Codex: Session flags > User config / Claude: --mcp-config > .mcp.json）ので無害。
/// 詳細は docs/mcp-session-messaging.md を参照。
/// </summary>
public interface IMcpConfigService
{
    /// <summary>
    /// Claude Code に <c>--mcp-config</c> で渡す JSON を用意し、そのフルパスを返す。
    /// 失敗したら null（呼び出し側はオプション無しで起動する）。
    /// </summary>
    string? EnsureClaudeMcpConfigFile(string baseUrl);

    /// <summary>baseUrl(例 http://localhost:5081) から MCP エンドポイント URL を作る。</summary>
    string BuildMcpUrl(string baseUrl);
}

public class McpConfigService : IMcpConfigService
{
    private readonly ILogger<McpConfigService> _logger;

    /// <summary>登録する MCP サーバー名（＝所有マーク）。</summary>
    private const string ServerName = "terminalhub";

    /// <summary>--mcp-config 用 JSON の書き込みを直列化する（ポート毎に1ファイルを共有するため）。</summary>
    private static readonly object _claudeConfigFileLock = new();

    public McpConfigService(ILogger<McpConfigService> logger)
    {
        _logger = logger;
    }

    /// <summary>baseUrl(例 http://localhost:5081) から MCP エンドポイント URL を作る。</summary>
    public string BuildMcpUrl(string baseUrl) => baseUrl.TrimEnd('/') + "/mcp";

    public string? EnsureClaudeMcpConfigFile(string baseUrl)
    {
        try
        {
            var url = BuildMcpUrl(baseUrl);
            var port = new Uri(baseUrl).Port;
            var path = AppDataPaths.GetMcpConfigFilePath(port);

            var root = new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    [ServerName] = new JsonObject
                    {
                        ["type"] = "http",
                        ["url"] = url
                    }
                }
            };
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            lock (_claudeConfigFileLock)
            {
                // 既に同じ内容なら触らない（起動のたびに書き換えて claude の読み取りと競合させない）。
                if (File.Exists(path) && File.ReadAllText(path) == json)
                    return path;

                // 一時ファイルへ書いてから差し替える。直接上書きすると、claude が読んでいる最中に
                // 中身が空/途中の状態を晒し得る。
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
            }

            _logger.LogInformation("MCP設定ファイルを用意(Claude/--mcp-config): {Path} url={Url}", path, url);
            return path;
        }
        catch (Exception ex)
        {
            // 失敗してもセッションは起動させる（MCP が繋がらないだけ）。
            _logger.LogWarning(ex, "MCP設定ファイルの用意に失敗(Claude): baseUrl={BaseUrl}", baseUrl);
            return null;
        }
    }
}
