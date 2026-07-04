using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TerminalHub.Models;

namespace TerminalHub.Services;

/// <summary>
/// 試験機能: 対応CLI(Claude Code / Codex)のフォルダへ TerminalHub のローカル MCP サーバー
/// (terminalhub) を自動登録/撤去するサービス。CodexHookService と同じく per-folder に書き、
/// 既存設定はマージ・TerminalHub は「terminalhub」エントリのみ所有する。
///
/// - Claude Code → <c>&lt;folder&gt;/.mcp.json</c> の <c>mcpServers.terminalhub</c>（type:"http"）
/// - Codex       → <c>&lt;folder&gt;/.codex/config.toml</c> の <c>[mcp_servers.terminalhub]</c>
///                 （per-folder の config.toml を Codex が MCP 用に読むかは要検証。読まなければ無害な no-op）
/// </summary>
public interface IMcpConfigService
{
    /// <summary>terminalhub MCP サーバーを CLI 設定へ追記（既存があれば最新値へ更新）。</summary>
    Task SetupAsync(string folderPath, TerminalType terminalType, string baseUrl);

    /// <summary>TerminalHub 由来（terminalhub）エントリだけを CLI 設定から除去。</summary>
    Task RemoveAsync(string folderPath, TerminalType terminalType);
}

public class McpConfigService : IMcpConfigService
{
    private readonly ILogger<McpConfigService> _logger;

    /// <summary>登録する MCP サーバー名（＝所有マーク）。撤去時はこのキー/テーブルだけ触る。</summary>
    private const string ServerName = "terminalhub";

    private const string ClaudeMcpFileName = ".mcp.json";
    private const string CodexConfigFileName = ".codex/config.toml";

    public McpConfigService(ILogger<McpConfigService> logger)
    {
        _logger = logger;
    }

    /// <summary>baseUrl(例 http://localhost:5081) から MCP エンドポイント URL を作る。</summary>
    private static string BuildMcpUrl(string baseUrl) => baseUrl.TrimEnd('/') + "/mcp";

    public async Task SetupAsync(string folderPath, TerminalType terminalType, string baseUrl)
    {
        var url = BuildMcpUrl(baseUrl);
        switch (terminalType)
        {
            case TerminalType.ClaudeCode:
                await SetupClaudeAsync(folderPath, url);
                break;
            case TerminalType.CodexCLI:
                await SetupCodexAsync(folderPath, url);
                break;
            default:
                // Claude Code / Codex のみサポート。それ以外は何もしない。
                break;
        }
    }

    public async Task RemoveAsync(string folderPath, TerminalType terminalType)
    {
        switch (terminalType)
        {
            case TerminalType.ClaudeCode:
                await RemoveClaudeAsync(folderPath);
                break;
            case TerminalType.CodexCLI:
                await RemoveCodexAsync(folderPath);
                break;
        }
    }

    // ---- Claude Code (.mcp.json / JSON) ----

    private async Task SetupClaudeAsync(string folderPath, string url)
    {
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

    private async Task RemoveClaudeAsync(string folderPath)
    {
        var path = Path.Combine(folderPath, ClaudeMcpFileName);
        if (!File.Exists(path)) return;

        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))?.AsObject();
        if (root?["mcpServers"] is not JsonObject servers) return;
        if (!servers.ContainsKey(ServerName)) return;

        servers.Remove(ServerName);
        if (servers.Count == 0) root.Remove("mcpServers");

        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(path, root.ToJsonString(options));
        _logger.LogInformation("MCP設定を除去(Claude): {Path}", path);
    }

    // ---- Codex (.codex/config.toml / TOML) ----
    // TOML ライブラリを持たないため、TerminalHub が所有する [mcp_servers.terminalhub] テーブルの
    // ブロックだけを行スキャンで置換/除去する。他のTOMLには手を触れない。

    private async Task SetupCodexAsync(string folderPath, string url)
    {
        var path = Path.Combine(folderPath, CodexConfigFileName);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var lines = File.Exists(path)
            ? (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n").Split('\n').ToList()
            : new List<string>();

        // 既存の terminalhub ブロックを除去してから、新しい値で追記（＝更新に対応）。
        RemoveTerminalHubTomlBlock(lines);
        TrimTrailingBlankLines(lines);

        if (lines.Count > 0) lines.Add("");
        lines.Add($"[mcp_servers.{ServerName}]");
        lines.Add($"url = \"{url}\"");
        lines.Add("");

        await File.WriteAllTextAsync(path, string.Join("\n", lines));
        _logger.LogInformation("MCP設定を追記(Codex): {Path} url={Url}", path, url);
    }

    private async Task RemoveCodexAsync(string folderPath)
    {
        var path = Path.Combine(folderPath, CodexConfigFileName);
        if (!File.Exists(path)) return;

        var lines = (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n").Split('\n').ToList();
        if (!RemoveTerminalHubTomlBlock(lines)) return;

        TrimTrailingBlankLines(lines);
        if (lines.Count > 0) lines.Add("");
        await File.WriteAllTextAsync(path, string.Join("\n", lines));
        _logger.LogInformation("MCP設定を除去(Codex): {Path}", path);
    }

    /// <summary>
    /// [mcp_servers.terminalhub] のテーブルヘッダ行から、次のテーブルヘッダ([で始まる行)または
    /// EOF までを lines から取り除く。除去したら true。
    /// </summary>
    private static bool RemoveTerminalHubTomlBlock(List<string> lines)
    {
        int start = lines.FindIndex(l => IsTerminalHubTableHeader(l));
        if (start < 0) return false;

        int end = start + 1;
        while (end < lines.Count && !lines[end].TrimStart().StartsWith("["))
        {
            end++;
        }
        lines.RemoveRange(start, end - start);
        return true;
    }

    private static bool IsTerminalHubTableHeader(string line)
    {
        var t = line.Trim();
        // [mcp_servers.terminalhub] / [mcp_servers."terminalhub"] の両表記を許容。
        return t == $"[mcp_servers.{ServerName}]" || t == $"[mcp_servers.\"{ServerName}\"]";
    }

    private static void TrimTrailingBlankLines(List<string> lines)
    {
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }
    }
}
