using TerminalHub.Models;

namespace TerminalHub.Services;

/// <summary>
/// スラッシュコマンド1件（補完候補）。名前は先頭の "/" を含む。
/// </summary>
public record SlashCommandItem(string Name, string? Description);

/// <summary>
/// 【スパイク用の偽辞書】スラッシュコマンド補完のUI検証のために、
/// それらしいコマンド一覧をハードコードで返すだけの暫定カタログ。
///
/// TODO(本実装): ここは将来、以下に置き換える想定。
///   - カスタム: プロジェクト/ユーザーの .claude/commands/*.md・.claude/skills/*/SKILL.md を
///     走査して name / description / argument-hint を取得（自動・鮮度あり）。
///   - 組み込み: CLI種別ごとの埋め込みJSONリソース（英語説明・人力更新、無ければ名前のみ）。
/// 説明文は表示専用で、マッチング対象にはしない（名前一致のみ）。
/// </summary>
public static class SlashCommandCatalog
{
    // Claude Code 想定の偽データ（組み込み＋よくある自作コマンドを混在）。
    private static readonly SlashCommandItem[] ClaudeCommands =
    {
        new("/clear", "Start a new session with empty context"),
        new("/compact", "Compact the conversation to free up context"),
        new("/context", "Show current context usage"),
        new("/resume", "Resume a previous conversation"),
        new("/rewind", "Restore the code and/or conversation to a previous point"),
        new("/model", "Switch the active model"),
        new("/config", "Open configuration"),
        new("/fast", "Toggle fast mode"),
        new("/init", "Initialize a CLAUDE.md for this codebase"),
        new("/memory", "Edit memory files"),
        new("/mcp", "Manage MCP server connections"),
        new("/review", "Review a GitHub pull request"),
        new("/code-review", "Review the current diff for bugs and cleanups"),
        new("/security-review", "Security review of the pending changes"),
        new("/simplify", "Review changed code for reuse and simplification"),
        new("/help", "Show help"),
        new("/usage", "Show token usage"),
        // 自作スキル（このリポジトリの実例）
        new("/release", "リリース一括実行（バージョン更新〜Discord告知）"),
        new("/write-tips", "Tips記事の執筆〜Discord投稿"),
        new("/preview", "プレビュー版を作成"),
    };

    // Codex CLI 想定の偽データ（少なめ）。
    private static readonly SlashCommandItem[] CodexCommands =
    {
        new("/clear", "Clear the conversation"),
        new("/compact", "Compact the conversation"),
        new("/model", "Switch model"),
        new("/approvals", "Change approval policy"),
        new("/help", "Show help"),
    };

    // Gemini CLI 想定の偽データ（少なめ）。
    private static readonly SlashCommandItem[] GeminiCommands =
    {
        new("/clear", "Clear the screen"),
        new("/compress", "Compress the context"),
        new("/chat", "Manage chat sessions"),
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
