using TerminalHub.Models;

namespace TerminalHub.Services;

/// <summary>
/// スラッシュコマンド1件（補完候補）。名前は先頭の "/" を含む。
/// </summary>
public record SlashCommandItem(string Name, string? Description);

/// <summary>
/// 組み込みスラッシュコマンドの暫定辞書。
///
/// v1 は「組み込みコマンドのみ」対応。各CLIに標準で用意されているコマンドを
/// 人力メンテのリストとして持つ（英語説明・ベストエフォート。CLIのバージョンにより
/// 実際の一覧とは多少ずれ得るが、一覧に無くても打てるので致命的ではない）。
/// 説明文は表示専用で、マッチング対象にはしない（名前一致のみ）。
///
/// TODO(将来): ユーザー要望が来たら、以下の「自作コマンド走査」を追加してマージする。
///   - プロジェクト/ユーザーの .claude/commands/*.md・.claude/skills/*/SKILL.md を走査し、
///     frontmatter の name / description / argument-hint を取得（自動・鮮度あり）。
///   - このリポジトリ固有の /release・/write-tips 等はそこで拾う想定（組み込みには含めない）。
/// </summary>
public static class SlashCommandCatalog
{
    // Claude Code の組み込みコマンド（＋バンドルされたスキル）。
    // 出典: 公式ドキュメント（code.claude.com/docs/en/commands）。網羅は必須ではなく、
    // 実用的な範囲でのベストエフォート（バージョンにより多少ずれ得る）。
    private static readonly SlashCommandItem[] ClaudeCommands =
    {
        // --- コア（セッション/ナビゲーション） ---
        new("/init", "Initialize a CLAUDE.md for this codebase"),
        new("/memory", "Edit memory files"),
        new("/clear", "Start a new conversation with empty context"),
        new("/resume", "Resume a previous conversation"),
        new("/branch", "Create a branching conversation fork"),
        new("/fork", "Spawn a background subagent on a directive"),
        new("/cd", "Move the session to a new working directory"),
        new("/add-dir", "Add directory access without moving the session"),
        new("/rename", "Rename the current session"),
        new("/exit", "Exit the CLI or detach a background session"),
        // --- モデル/設定 ---
        new("/model", "Switch the AI model"),
        new("/effort", "Set the reasoning effort level"),
        new("/config", "Open settings or set configuration"),
        new("/permissions", "Manage tool permission rules"),
        new("/mcp", "Manage MCP server connections"),
        new("/hooks", "View hook configurations"),
        new("/keybindings", "Open the keyboard shortcuts file"),
        // --- コンテキスト/履歴 ---
        new("/context", "Visualize context usage"),
        new("/compact", "Summarize the conversation to free context"),
        new("/rewind", "Rewind to a previous point"),
        new("/export", "Export the conversation as text"),
        new("/copy", "Copy the last response to the clipboard"),
        // --- 診断/ヘルプ/アカウント ---
        new("/doctor", "Run a setup checkup and diagnostics"),
        new("/debug", "Enable debug logging"),
        new("/status", "Show version, model, and account info"),
        new("/usage", "View usage and costs"),
        new("/feedback", "Report a bug or share the conversation"),
        new("/release-notes", "View the changelog"),
        new("/skills", "List available skills"),
        new("/plugin", "Manage plugins"),
        new("/help", "Show help and available commands"),
        new("/login", "Sign in to your account"),
        new("/logout", "Sign out"),
        // --- バンドルスキル/拡張コマンド ---
        new("/code-review", "Review the diff for bugs and cleanups"),
        new("/simplify", "Review changed code for cleanup opportunities"),
        new("/security-review", "Check the diff for vulnerabilities"),
        new("/review", "Fast single-pass PR review"),
        new("/plan", "Enter plan mode for large changes"),
        new("/batch", "Orchestrate large-scale parallel changes"),
        new("/loop", "Run a prompt repeatedly on a schedule"),
        new("/schedule", "Create or manage scheduled routines"),
        new("/diff", "View uncommitted changes interactively"),
        new("/run", "Launch and drive your project's app"),
        new("/deep-research", "Fan out web searches and synthesize a report"),
        new("/tasks", "List background subagent tasks"),
        new("/background", "Detach the session as a background agent"),
        new("/focus", "Toggle focus view"),
        new("/ide", "Manage IDE integrations"),
        new("/chrome", "Configure Claude in Chrome"),
    };

    // Codex CLI / Gemini CLI の組み込みコマンドは、各CLIに自前で辞書を作らせて
    // ここへはめ込む予定。それまでは未対応（空＝補完オフ）とする。
    // （筆者の推測で不正確な一覧を出すより、各CLIが生成した正確な一覧を待つ方針）

    private static readonly SlashCommandItem[] Empty = System.Array.Empty<SlashCommandItem>();

    /// <summary>CLI種別ごとの候補。辞書が無い種別は空を返す（＝補完無効）。</summary>
    public static IReadOnlyList<SlashCommandItem> ForTerminalType(TerminalType type) => type switch
    {
        TerminalType.ClaudeCode => ClaudeCommands,
        // TODO: Codex / Gemini は各CLI提供の辞書を追加したら分岐を復活させる。
        _ => Empty,
    };

    /// <summary>
    /// 「名前（"/" 無し）→説明」の辞書。動的取得した名前一覧に説明を上書きする用途
    /// （<see cref="SlashCommandProvider"/>）。説明が無い名前は辞書に載らない。
    /// </summary>
    public static IReadOnlyDictionary<string, string?> BuildDescriptionMap(TerminalType type)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ForTerminalType(type))
        {
            var key = item.Name.StartsWith("/") ? item.Name.Substring(1) : item.Name;
            map[key] = item.Description;
        }
        return map;
    }
}
