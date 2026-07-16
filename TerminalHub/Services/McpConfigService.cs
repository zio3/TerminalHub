using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TerminalHub.Models;

namespace TerminalHub.Services;

/// <summary>
/// 試験機能: 対応CLI(Claude Code / Codex)へ TerminalHub のローカル MCP サーバー
/// (terminalhub) を繋ぐサービス。CLI ごとに手段が異なる:
///
/// - Claude Code → <b>起動オプション <c>--mcp-config &lt;JSONパス&gt;</c> で渡す</b>。ユーザーの設定ファイルを
///                 一切書き換えない。JSON は TerminalHub 自身のデータ領域
///                 (<see cref="AppDataPaths.GetMcpConfigFilePath"/>) に置き、パスだけをコマンドラインに乗せる。
///                 生成は <see cref="EnsureClaudeMcpConfigFile"/>、オプション付与は SessionManager/TerminalConstants 側。
/// - Codex       → CodexHookService と同じく per-folder の <c>&lt;folder&gt;/.codex/config.toml</c> の
///                 <c>[mcp_servers.terminalhub]</c> へ直接書き、既存 TOML はマージで温存する。
///                 （per-folder の config.toml を Codex が MCP 用に読むかは要検証。読まなければ無害な no-op）
///                 <b>TODO: Codex も起動オプションで渡せることを実測で確認済み</b>
///                 （<c>codex -c mcp_servers.terminalhub.url=&lt;URL&gt;</c>。値は TOML パースに失敗すると
///                 リテラル文字列扱いになるため引用符不要）。Claude と同じ起動オプション方式へ移行し、
///                 この書き込み経路は廃止する予定。
///
/// Codex 側は書き込み型のため撤去はしない（書き込むところまでが責務）。自動登録を無効に戻しても、
/// 既に書いた terminalhub エントリはそのまま残るので、不要なら利用者が設定ファイルから消す。
/// Claude 側は起動オプション方式なので、OFF にすれば次回起動から即座に繋がらなくなる（残骸なし）。
/// </summary>
public interface IMcpConfigService
{
    /// <summary>terminalhub MCP サーバーを CLI 設定へ追記（既存があれば最新値へ更新）。Codex のみ対象。</summary>
    Task SetupAsync(string folderPath, TerminalType terminalType, string baseUrl);

    /// <summary>
    /// Claude Code に <c>--mcp-config</c> で渡す JSON を用意し、そのフルパスを返す。
    /// 失敗したら null（呼び出し側はオプション無しで起動する）。
    /// </summary>
    string? EnsureClaudeMcpConfigFile(string baseUrl);
}

public class McpConfigService : IMcpConfigService
{
    private readonly ILogger<McpConfigService> _logger;

    /// <summary>登録する MCP サーバー名（＝所有マーク）。撤去時はこのキー/テーブルだけ触る。</summary>
    private const string ServerName = "terminalhub";

    private const string CodexConfigFileName = ".codex/config.toml";

    /// <summary>--mcp-config 用 JSON の書き込みを直列化する（ポート毎に1ファイルを共有するため）。</summary>
    private static readonly object _claudeConfigFileLock = new();

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
            case TerminalType.CodexCLI:
                await SetupCodexAsync(folderPath, url);
                break;
            default:
                // Claude Code は起動オプション方式(EnsureClaudeMcpConfigFile)なのでここでは何もしない。
                // それ以外の CLI は未対応。
                break;
        }
    }

    // ---- Claude Code (--mcp-config へ渡す JSON の用意) ----

    /// <summary>
    /// Claude Code へ <c>--mcp-config</c> で渡す JSON を TerminalHub のデータ領域に用意し、パスを返す。
    ///
    /// ユーザーの設定ファイル（~/.claude.json や作業フォルダの .mcp.json）には一切書かない。
    /// --mcp-config は既存の MCP 設定に<b>マージ</b>されるので（--strict-mcp-config を付けない限り）、
    /// ユーザーが自分で入れた MCP サーバーはそのまま生きる。
    ///
    /// 中身はポートにしか依存しないため、セッション毎ではなくポート毎に1ファイルを共有する。
    /// 同時起動したセッションが同じファイルを書いて claude に破損した JSON を読ませないよう、
    /// ロックした上で「内容が同じなら書かない・書くときは一時ファイル＋差し替え」で更新する。
    /// </summary>
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
