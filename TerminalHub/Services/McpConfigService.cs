using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace TerminalHub.Services;

/// <summary>
/// 試験機能: Claude Code のフォルダへ TerminalHub のローカル MCP サーバー
/// (terminalhub) を登録するサービス。既存設定はマージし、TerminalHub は
/// 「terminalhub」エントリのみ所有する。
///
/// 撤去はしない（書き込むところまでが責務）。自動登録を無効に戻しても、既に書いた
/// terminalhub エントリはそのまま残るので、不要なら利用者が設定ファイルから消す。
/// </summary>
public interface IMcpConfigService
{
    /// <summary>terminalhub MCP サーバーを Claude Code 設定へ追記（既存があれば最新値へ更新）。</summary>
    Task SetupClaudeAsync(string folderPath, string baseUrl);
}

public class McpConfigService : IMcpConfigService
{
    private readonly ILogger<McpConfigService> _logger;

    /// <summary>登録する MCP サーバー名。</summary>
    private const string ServerName = "terminalhub";

    private const string ClaudeMcpFileName = ".mcp.json";

    public McpConfigService(ILogger<McpConfigService> logger)
    {
        _logger = logger;
    }

    /// <summary>baseUrl（例: http://localhost:5081）から MCP エンドポイント URL を作る。</summary>
    private static string BuildMcpUrl(string baseUrl) => baseUrl.TrimEnd('/') + "/mcp";

    public async Task SetupClaudeAsync(string folderPath, string baseUrl)
    {
        var url = BuildMcpUrl(baseUrl);
        var path = Path.Combine(folderPath, ClaudeMcpFileName);

        JsonObject root;
        if (File.Exists(path))
        {
            var existing = await File.ReadAllTextAsync(path);
            root = JsonNode.Parse(existing)?.AsObject() ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        if (root["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }

        // terminalhub エントリだけを最新値で上書き。他サーバーは温存。
        servers[ServerName] = new JsonObject
        {
            ["type"] = "http",
            ["url"] = url
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(path, root.ToJsonString(options));
        _logger.LogInformation("MCP設定を追記(Claude): {Path} url={Url}", path, url);
    }
}
