using TerminalHub.Models;

namespace TerminalHub.Services;

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
        new("/sessions", "List active sessions"),
        new("/branch", "Create a branching conversation fork"),
        new("/fork", "Spawn a background subagent on a directive"),
        new("/cd", "Move the session to a new working directory"),
        new("/add-dir", "Add directory access without moving the session"),
        new("/rename", "Rename the current session"),
        new("/exit", "Exit the CLI or detach a background session"),
        // --- モデル/設定 ---
        new("/model", "Switch the AI model"),
        new("/effort", "Set the reasoning effort level"),
        new("/fast", "Toggle fast mode"),
        new("/config", "Open settings or set configuration"),
        new("/permissions", "Manage tool permission rules"),
        new("/fewer-permission-prompts", "Reduce permission prompts"),
        new("/mcp", "Manage MCP server connections"),
        new("/hooks", "View hook configurations"),
        new("/keybindings", "Open the keyboard shortcuts file"),
        new("/theme", "Switch between light and dark theme"),
        new("/color", "Set the prompt bar color"),
        new("/statusline", "Configure the status line UI"),
        new("/advisor", "Enable or disable the advisor tool"),
        new("/voices", "Configure voice settings"),
        // --- コンテキスト/履歴 ---
        new("/context", "Visualize context usage"),
        new("/compact", "Summarize the conversation to free context"),
        new("/rewind", "Rewind to a previous point"),
        new("/export", "Export the conversation as text"),
        new("/copy", "Copy the last response to the clipboard"),
        new("/recap", "Show a recap of recent session activity"),
        new("/prompt", "View or edit the current prompt"),
        new("/btw", "Ask a side question outside the conversation history"),
        new("/goal", "Set or clear a goal for the session"),
        // --- 診断/ヘルプ/アカウント ---
        new("/doctor", "Run a setup checkup and diagnostics"),
        new("/debug", "Enable debug logging"),
        new("/heapdump", "Write a heap snapshot for memory diagnostics"),
        new("/status", "Show version, model, and account info"),
        new("/usage", "View usage and costs"),
        new("/cost", "View usage and costs (alias of /usage)"),
        new("/insights", "Generate a session analytics report"),
        new("/feedback", "Report a bug or share the conversation"),
        new("/release-notes", "View the changelog"),
        new("/update", "Update Claude Code"),
        new("/tips", "Show usage tips"),
        new("/skills", "List available skills"),
        new("/reload-skills", "Reload skill definitions"),
        new("/plugin", "Manage plugins"),
        new("/help", "Show help and available commands"),
        new("/login", "Sign in to your account"),
        new("/logout", "Sign out"),
        new("/passes", "Share a free week with friends"),
        // --- 連携/リモート ---
        new("/remote-control", "Control this session from your phone or claude.ai/code"),
        new("/desktop", "Continue this session in the Desktop app"),
        new("/mobile", "Show a QR code for the mobile app"),
        new("/ide", "Manage IDE integrations"),
        new("/chrome", "Configure Claude in Chrome"),
        new("/slack", "Manage the Slack integration"),
        new("/install-github-app", "Install the Claude GitHub app"),
        new("/install-slack-app", "Install the Claude Slack app"),
        new("/twilio", "Configure phone/SMS integration"),
        new("/sync", "Sync files with remote storage"),
        // --- バンドルスキル/拡張コマンド ---
        new("/code-review", "Review the diff for bugs and cleanups"),
        new("/simplify", "Review changed code for cleanup opportunities"),
        new("/security-review", "Check the diff for vulnerabilities"),
        new("/review", "Fast single-pass PR review"),
        new("/quick-check", "Run a quick sanity check"),
        new("/verify", "Verify code changes end-to-end"),
        new("/plan", "Enter plan mode for large changes"),
        new("/batch", "Orchestrate large-scale parallel changes"),
        new("/loop", "Run a prompt repeatedly on a schedule"),
        new("/schedule", "Create or manage scheduled routines"),
        new("/autofix-pr", "Auto-fix CI failures in a cloud session"),
        new("/diff", "View uncommitted changes interactively"),
        new("/run", "Launch and drive your project's app"),
        new("/search", "Search code in the repository"),
        new("/deep-research", "Fan out web searches and synthesize a report"),
        new("/dataviz", "Design charts, graphs, and dashboards"),
        new("/claude-api", "Load Claude API reference docs"),
        new("/run-skill-generator", "Create or improve run-<unit> skills"),
        new("/team-onboarding", "Create an onboarding guide for teammates"),
        new("/agents", "Manage subagents"),
        new("/tasks", "List background subagent tasks"),
        new("/background", "Detach the session as a background agent"),
        new("/focus", "Toggle focus view"),
    };

    // Codex CLI の組み込みコマンド。
    // 出典: Codex 本人(codex-cli 0.144.1)の自己申告。Codex は headless から一覧を
    // トークン消費なしで取る手段が無い（exec --json は init にコマンドを含まず即ターン開始）ため、
    // バージョン別の静的辞書として持つ方針。自作プロンプト(~/.codex/prompts/*.md → /prompts:<name>)は別途走査で足す想定。
    // 除外: /debug-m-drop・/debug-m-update（DO NOT USE）、/rollout・/test-approval（デバッグビルド限定）、
    //       /clean（/stop の非公開エイリアス）。
    private static readonly SlashCommandItem[] CodexCommands =
    {
        // --- 権限/環境 ---
        new("/permissions", "Codexが確認なしで実行できる範囲を設定"),
        new("/ide", "IDEの選択範囲や開いているファイルをコンテキストへ追加"),
        new("/keymap", "TUIのキーボードショートカットを設定"),
        new("/vim", "コンポーザーのVimモードを切り替え"),
        new("/setup-default-sandbox", "Windowsの昇格サンドボックスをセットアップ"),
        new("/sandbox-add-read-dir", "Windowsサンドボックスへ追加の読み取りディレクトリを許可"),
        // --- モデル/スタイル ---
        new("/model", "モデルと推論レベルを選択"),
        new("/fast", "Fastサービス層を切り替え（対応モデルでのみ動的に出現）"),
        new("/personality", "応答スタイルを選択"),
        // --- 実行制御 ---
        new("/approve", "自動レビューで拒否された直近の操作を1回再試行"),
        new("/experimental", "実験的機能を切り替え"),
        new("/memories", "メモリの利用・生成を設定"),
        new("/plan", "Planモードへ切り替え"),
        new("/goal", "長時間タスクのGoalを設定・表示・編集"),
        new("/review", "作業ツリーのコードレビューを開始"),
        // --- セッション ---
        new("/new", "同じCLI内で新しいタスクを開始"),
        new("/clear", "端末表示とタスクコンテキストをクリア"),
        new("/resume", "保存済みセッションを再開"),
        new("/fork", "現在のセッションを分岐"),
        new("/rename", "現在のセッション名を変更"),
        new("/archive", "現在のセッションをアーカイブして終了"),
        new("/delete", "現在のセッションを完全削除して終了"),
        // --- コンテキスト/履歴 ---
        new("/compact", "会話を要約してコンテキストを圧縮"),
        new("/copy", "直近のCodex応答をMarkdownとしてコピー"),
        new("/raw", "コピーしやすいrawスクロールバック表示を切り替え"),
        new("/diff", "未追跡ファイルを含むGit差分を表示"),
        new("/mention", "ファイルやフォルダを会話へ添付"),
        // --- 拡張/連携 ---
        new("/init", "AGENTS.mdのひな型を生成"),
        new("/import", "Claude Codeの設定・プロジェクト・最近の会話をインポート"),
        new("/skills", "スキルを参照・選択"),
        new("/hooks", "ライフサイクルフックを参照・管理"),
        new("/plugins", "プラグインを参照・管理"),
        new("/apps", "Apps（コネクター）を管理"),
        new("/mcp", "設定済みMCPツールを表示"),
        // --- エージェント/サイド会話 ---
        new("/agent", "アクティブなエージェントスレッドを切り替え"),
        new("/subagents", "エージェント選択（/agent と同系統）"),
        new("/side", "一時的なサイド会話を開始"),
        new("/btw", "サイド会話を開始（/side の別名）"),
        // --- バックグラウンド端末 ---
        new("/ps", "バックグラウンド端末を一覧表示"),
        new("/stop", "すべてのバックグラウンド端末を停止"),
        // --- 診断/表示 ---
        new("/status", "セッション設定、トークン使用量などを表示"),
        new("/usage", "アカウント使用量やリセット状況を表示"),
        new("/debug-config", "設定レイヤーと制約の診断情報を表示"),
        new("/title", "ターミナルタイトルの表示項目を設定"),
        new("/statusline", "ステータスラインの表示項目を設定"),
        new("/theme", "シンタックスハイライトテーマを選択"),
        new("/pets", "ターミナルペットを選択・非表示"),
        new("/pet", "ターミナルペット（/pets の別名）"),
        // --- アプリ/アカウント/終了 ---
        new("/app", "現在のセッションをCodex Desktopで継続"),
        new("/feedback", "ログを添えてフィードバックを送信"),
        new("/logout", "Codexからログアウト"),
        new("/quit", "Codex CLIを終了"),
        new("/exit", "Codex CLIを終了（/quit と同じ）"),
    };

    // Gemini CLI の組み込みコマンドは、各CLIに自前で辞書を作らせてここへはめ込む予定。
    // それまでは未対応（空＝補完オフ）とする。

    private static readonly SlashCommandItem[] Empty = System.Array.Empty<SlashCommandItem>();

    /// <summary>CLI種別ごとの候補。辞書が無い種別は空を返す（＝補完無効）。</summary>
    public static IReadOnlyList<SlashCommandItem> ForTerminalType(TerminalType type) => type switch
    {
        TerminalType.ClaudeCode => ClaudeCommands,
        TerminalType.CodexCLI => CodexCommands,
        // TODO: Gemini は各CLI提供の辞書を追加したら分岐を復活させる。
        _ => Empty,
    };
}
