using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TerminalHub.Constants;

namespace TerminalHub.Services;

/// <summary>
/// 試験機能: 対応CLI(Claude Code / Codex)へ TerminalHub のローカル MCP サーバー
/// (terminalhub) を繋ぐサービス。ONにすると起動しただけで list_sessions / send_to_session が使える。
///
/// どちらの CLI も<b>起動オプションで渡す</b>方式で、ユーザーの設定ファイルは一切書き換えない。
/// MCP の URL は実行中のポートに依存するため、プロジェクトの設定ファイルへ永続化すると
/// 古いポートが残る・Git を汚す・マシンごとに競合する、といった問題が起きるため。
///
/// 設定には URL に加えて <b>MCP 接続キー</b>(<see cref="TerminalConstants.McpSessionKeyHeader"/> ヘッダ)を
/// 載せる。サーバーはこのヘッダで「どのセッションからの接続か」を確定するため、モデルは
/// 秘密を知らずに済む(旧 proof 引数方式＝環境変数から読ませて運ばせる形は、秘密のシェル出力が
/// 安全分類器に弾かれて無言で無記名に劣化する実害があり、互換を残さず撤去した)。
/// キーがセッション固有なので、設定ファイルは旧「ポート毎の共有」ではなく
/// <b>ポート＋セッション毎</b>(hook 設定と同じライフサイクル)。
///
/// - Claude Code → <c>--mcp-config &lt;JSONパス&gt;</c>。JSON は TerminalHub 自身のデータ領域
///                 (<see cref="AppDataPaths.GetMcpConfigFilePath"/>) に置き、パスだけをコマンドラインに乗せる。
///                 生成はこのサービス (<see cref="EnsureClaudeMcpConfigFile"/>)、オプション付与は
///                 SessionManager/TerminalConstants 側。
/// - Codex       → <c>-c mcp_servers.terminalhub.url=&lt;URL&gt;</c> と
///                 <c>-c mcp_servers.terminalhub.http_headers.…=&lt;キー&gt;</c>。ファイルを用意する
///                 必要がないのでこのサービスは <see cref="BuildMcpUrl"/> で URL を組むだけ。
///                 呼び出しは SessionManager、オプション付与は TerminalConstants.BuildCodexArgs が行う。
///
/// どちらも起動オプションなので、この機能は新しいプロジェクト設定を残さない（OFF に戻せば次の起動から繋がらない）。
///
/// ただし旧バージョン（〜v1.0.70）は設定ファイルへ書き込む方式だったため、その残骸は残る
/// （Claude は <c>&lt;folder&gt;/.mcp.json</c>、Codex は <c>&lt;folder&gt;/.codex/config.toml</c>）。
/// TerminalHub からは消さないので、不要なら利用者が消す。どちらも当時のポートを指しているため、
/// <b>OFF にしている場合は残骸経由で古いポートへ繋ぎに行くことがある</b>。ON の間は起動オプションが
/// 優先される（Codex: Session flags > User config / Claude: --mcp-config > .mcp.json）ので無害。
/// 残骸経由の接続には接続キーが付かない＝無記名扱いになる(書き込み系は使えない。
/// 本機能を ON にしてセッションを起動し直せばキー付きで接続される)。
/// 詳細は docs/mcp-session-messaging.md を参照。
/// </summary>
public interface IMcpConfigService
{
    /// <summary>
    /// Claude Code に <c>--mcp-config</c> で渡すセッション専用 JSON（接続キー入り）を用意し、
    /// そのフルパスを返す。失敗したら null（呼び出し側はオプション無しで起動する）。
    /// </summary>
    string? EnsureClaudeMcpConfigFile(string baseUrl, Guid sessionId, string connectionKey);

    /// <summary>baseUrl(例 http://localhost:5081) から MCP エンドポイント URL を作る。</summary>
    string BuildMcpUrl(string baseUrl);

    /// <summary>
    /// セッション完全削除時に、そのセッション用の MCP 設定 JSON を消す（ベストエフォート）。
    /// 接続キーが書かれたファイルなので、hook 設定と同じく持ち主が消えたら残さない。
    /// </summary>
    void DeleteMcpConfigFile(Guid sessionId);
}

public class McpConfigService : IMcpConfigService
{
    private readonly ILogger<McpConfigService> _logger;

    /// <summary>登録する MCP サーバー名（＝所有マーク）。</summary>
    private const string ServerName = "terminalhub";

    public McpConfigService(ILogger<McpConfigService> logger)
    {
        _logger = logger;
    }

    /// <summary>baseUrl(例 http://localhost:5081) から MCP エンドポイント URL を作る。</summary>
    public string BuildMcpUrl(string baseUrl) => baseUrl.TrimEnd('/') + "/mcp";

    public string? EnsureClaudeMcpConfigFile(string baseUrl, Guid sessionId, string connectionKey)
    {
        try
        {
            var url = BuildMcpUrl(baseUrl);
            var path = AppDataPaths.GetMcpConfigFilePath(new Uri(baseUrl).Port, sessionId);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = BuildClaudeMcpConfigJson(url, connectionKey);

            // 既に同じ内容なら触らない（起動のたびに書き換えて claude の読み取りと競合させない）。
            if (File.Exists(path) && File.ReadAllText(path) == json)
                return path;

            // 一時ファイルへ書いてから差し替える。直接上書きすると、claude が読んでいる最中に
            // 中身が空/途中の状態を晒し得る。
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);

            // 旧方式の「ポート毎共有ファイル」の残骸を掃除する（ベストエフォート）。
            // 残しても新コードは参照しないが、接続情報の古い写しを溜めない。
            DeleteLegacySharedConfigFile(baseUrl);

            // ログにパスは出すがキーは出さない（ログ経由の漏出を作らない）。
            _logger.LogInformation("MCP設定ファイルを用意(Claude/--mcp-config): {Path} url={Url}", path, url);
            return path;
        }
        catch (Exception ex)
        {
            // 失敗してもセッションは起動させる（MCP が繋がらないだけ）。
            _logger.LogWarning(ex, "MCP設定ファイルの用意に失敗(Claude): SessionId={SessionId} baseUrl={BaseUrl}", sessionId, baseUrl);
            return null;
        }
    }

    public void DeleteMcpConfigFile(Guid sessionId)
    {
        try
        {
            // ファイル名はポート＋セッションなので、過去に別ポートで起動した分も含めて全部消す。
            foreach (var path in AppDataPaths.EnumerateMcpConfigFilePaths(sessionId))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            // 後片付けの失敗で削除本流を壊さない。残っても次回起動時に上書きされるだけ。
            _logger.LogWarning(ex, "MCP設定ファイルの削除に失敗: SessionId={SessionId}", sessionId);
        }
    }

    /// <summary>
    /// --mcp-config 用 JSON の組み立て（純ロジック・テスト用に public static）。
    /// headers に MCP 接続キーを載せる。.mcp.json スキーマの http サーバー定義は
    /// headers(文字列辞書)をサポートしている。
    /// </summary>
    public static string BuildClaudeMcpConfigJson(string url, string connectionKey)
    {
        var root = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [ServerName] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = url,
                    ["headers"] = new JsonObject
                    {
                        [TerminalConstants.McpSessionKeyHeader] = connectionKey
                    }
                }
            }
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private void DeleteLegacySharedConfigFile(string baseUrl)
    {
        try
        {
            var port = new Uri(baseUrl).Port;
            var legacy = AppDataPaths.GetLegacyMcpConfigFilePath(port);
            if (File.Exists(legacy))
            {
                File.Delete(legacy);
                _logger.LogInformation("旧ポート毎MCP設定ファイルを掃除: {Path}", legacy);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "旧ポート毎MCP設定ファイルの掃除に失敗: baseUrl={BaseUrl}", baseUrl);
        }
    }
}
