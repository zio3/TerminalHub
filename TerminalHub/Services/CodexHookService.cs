using System.Text;

namespace TerminalHub.Services;

/// <summary>
/// Codex CLI の lifecycle hook 設定を管理するサービス。
/// Codex は <c>type:"http"</c> をサポートせず <c>type:"command"</c> のみのため、
/// 各 hook で TerminalHub.exe を「ブリッジ」として起動し、stdin の JSON を
/// TerminalHub の HTTP API (/api/hook/codex/{sessionId}) へ転送させる。
/// Hook 定義は <c>-c hooks.&lt;Event&gt;=...</c> で起動時だけ注入し、
/// プロジェクトの <c>.codex/hooks.json</c> は作成しない。
/// </summary>
public interface ICodexHookService
{
    /// <summary>Codex 起動時に渡す lifecycle hook の -c 引数列を生成する。</summary>
    string BuildHookConfigArguments(string baseUrl);
}

public class CodexHookService : ICodexHookService
{
    // ブリッジ起動コマンドに含める識別子。
    private const string BridgeMarker = "--source codex";

    // Codex に登録する hook (event, matcher)。matcher=null は全マッチ。command 型はプロセス起動コストが
    // あるため低頻度イベントに絞る。PreToolUse は matcher=request_user_input（Codex のユーザー選択ツール、
    // Claude の AskUserQuestion 相当）だけに絞り、全ツールでは発火させない。
    private static readonly (string Event, string? Matcher)[] HookRegistrations =
    {
        ("Stop", null),
        ("UserPromptSubmit", null),
        ("SubagentStart", null),
        ("SubagentStop", null),
        ("PreCompact", null),
        ("PostCompact", null),
        // 許可待ち（ツール実行の承認待ち）を検知して「確認待ち」ベル/Webhook に載せる。
        ("PermissionRequest", null),
        // ユーザーへの選択肢提示（request_user_input）＝「選択待ち」。matcher で絞る。
        ("PreToolUse", "^request_user_input$"),
    };

    public string BuildHookConfigArguments(string baseUrl)
    {
        var command = BuildBridgeCommand(baseUrl);
        var args = new List<string>();
        foreach (var (eventName, matcher) in HookRegistrations)
        {
            // 外側を cmd.exe 用の二重引用符、TOML 文字列を単一引用符にする。
            // command は -EncodedCommand を使うため単一引用符を含まず、この形で安全に渡せる。
            var matcherPart = string.IsNullOrEmpty(matcher) ? "" : $",matcher='{matcher}'";
            var value =
                $"[{{hooks=[{{type='command',command='{command}',timeout=5}}]{matcherPart}}}]";
            args.Add($"-c \"hooks.{eventName}={value}\"");
        }
        return string.Join(" ", args);
    }

    /// <summary>
    /// ブリッジ起動コマンドを生成する。実行中の TerminalHub.exe を --notify --source codex モードで起動し、
    /// stdin の Codex JSON を /api/hook/codex/{sessionId} へ転送させる。
    /// </summary>
    private static string BuildBridgeCommand(string baseUrl)
    {
        // 実行中サーバーの exe パス（= TerminalHub.exe）。ブリッジも同じ exe を別プロセスで起動する。
        var exePath = Environment.ProcessPath ?? "TerminalHub.exe";
        var port = GetPort(baseUrl);
        // Session ID は ConPtyService が子プロセスへ注入済みの TERMINALHUB_SESSION_ID を使う。
        // セッションごとの GUID を Hook 定義へ埋め込まないため、同一 exe/ポートでは Hook trust の
        // ハッシュが安定する。PowerShell スクリプトは UTF-16LE の EncodedCommand にして、
        // cmd.exe / TOML / PowerShell の三重クォート問題を避ける。
        var escapedExePath = exePath.Replace("'", "''");
        var script =
            $"& '{escapedExePath}' --notify {BridgeMarker} --session $env:TERMINALHUB_SESSION_ID --port {port}";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return $"powershell.exe -NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}";
    }

    private static int GetPort(string baseUrl)
    {
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Port > 0 ? uri.Port : 5081;
    }

}
