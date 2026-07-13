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
    private static readonly SlashCommandItem[] ClaudeCommands =
    {
        new("/clear", "Start a new session with empty context"),
        new("/compact", "Compact the conversation to free up context"),
        new("/context", "Show current context usage"),
        new("/resume", "Resume a previous conversation"),
        new("/rewind", "Restore the code and/or conversation to a previous point"),
        new("/fork", "Fork the current conversation"),
        new("/model", "Switch the active model"),
        new("/effort", "Change the reasoning effort level"),
        new("/fast", "Toggle fast mode"),
        new("/config", "Open configuration"),
        new("/permissions", "Manage tool permissions"),
        new("/mcp", "Manage MCP server connections"),
        new("/memory", "Edit memory files"),
        new("/init", "Initialize a CLAUDE.md for this codebase"),
        new("/diff", "Show the current diff"),
        new("/copy", "Copy the last response"),
        new("/export", "Export the conversation"),
        new("/usage", "Show token usage"),
        new("/help", "Show help"),
        // バンドルスキル
        new("/review", "Review a GitHub pull request"),
        new("/code-review", "Review the current diff for bugs and cleanups"),
        new("/security-review", "Security review of the pending changes"),
        new("/simplify", "Clean up the changed code for reuse and simplicity"),
        new("/loop", "Run a prompt or command on a recurring interval"),
        new("/schedule", "Manage scheduled cloud agents"),
    };

    // Codex CLI の組み込みコマンド。
    private static readonly SlashCommandItem[] CodexCommands =
    {
        new("/clear", "Clear the conversation"),
        new("/compact", "Compact the conversation"),
        new("/new", "Start a new conversation"),
        new("/model", "Switch the model"),
        new("/approvals", "Change the approval policy"),
        new("/diff", "Show the working diff"),
        new("/mcp", "Manage MCP servers"),
        new("/init", "Create an AGENTS.md for this project"),
        new("/help", "Show help"),
    };

    // Gemini CLI の組み込みコマンド。
    private static readonly SlashCommandItem[] GeminiCommands =
    {
        new("/clear", "Clear the screen and context"),
        new("/compress", "Compress the context"),
        new("/chat", "Manage saved chat sessions"),
        new("/memory", "Manage memory (GEMINI.md)"),
        new("/tools", "List available tools"),
        new("/mcp", "List MCP servers and tools"),
        new("/stats", "Show session stats"),
        new("/help", "Show help"),
    };

    private static readonly SlashCommandItem[] Empty = System.Array.Empty<SlashCommandItem>();

    /// <summary>CLI種別ごとの候補。スラッシュコマンドを持たない種別は空を返す（＝補完無効）。</summary>
    public static IReadOnlyList<SlashCommandItem> ForTerminalType(TerminalType type) => type switch
    {
        TerminalType.ClaudeCode => ClaudeCommands,
        TerminalType.CodexCLI => CodexCommands,
        TerminalType.GeminiCLI => GeminiCommands,
        _ => Empty,
    };
}
