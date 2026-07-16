namespace TerminalHub.Models;

/// <summary>
/// アプリケーション全体の設定（ファイルベースで永続化）
/// </summary>
public class AppSettings
{
    public NotificationSettings Notifications { get; set; } = new();
    public WebhookSettings Webhook { get; set; } = new();
    public ClaudeHookSettings ClaudeHook { get; set; } = new();
    public SpecialSettings Special { get; set; } = new();
    public SessionDisplaySettings Sessions { get; set; } = new();
    public DevToolsSettings DevTools { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
    public CustomCommandSettings Commands { get; set; } = new();
    public CustomCliOptionsSettings CliOptions { get; set; } = new();
    public RemoteLaunchSettings RemoteLaunch { get; set; } = new();
    public ExperimentalSettings Experimental { get; set; } = new();
    public SessionDefaultsSettings SessionDefaults { get; set; } = new();
}

/// <summary>
/// セッション作成ダイアログの「前回選んだ値」（入力補助の既定値）。
/// 従来はブラウザ localStorage に保存していたが、ブラウザごとに変えたい性質のものではない
/// （その PC のユーザーの作業習慣）ため、ファイルベースの設定へ移した。値の発生源は
/// 新規セッション作成ダイアログに一元化し、サブセッション側は読むだけ（書き戻さない）。
/// null は「未保存」を意味し、各 OptionsData の初期値を維持する。
/// Yolo（承認・サンドボックス完全バイパス）は危険なため、ここには保持しない（毎回 OFF 既定）。
/// </summary>
public class SessionDefaultsSettings
{
    /// <summary>前回選んだターミナル種別。</summary>
    public TerminalType? LastTerminalType { get; set; }
    /// <summary>Claude Code の権限モード（"bypass" | "auto" | "default"）。</summary>
    public string? LastClaudePermissionMode { get; set; }
    /// <summary>Gemini CLI の承認モード（"default" | "auto_edit" | "yolo"）。</summary>
    public string? LastGeminiApprovalMode { get; set; }
    /// <summary>Codex の権限プリセット（"ask-for-approval" | "recommended" | "custom"）。</summary>
    public string? LastCodexPermissionPreset { get; set; }
    /// <summary>Codex のサンドボックスモード（"" | "read-only" | "workspace-write" | "danger-full-access"）。</summary>
    public string? LastCodexSandboxMode { get; set; }
    /// <summary>Codex の承認ポリシー（"" | "untrusted" | "on-request" | "never"）。</summary>
    public string? LastCodexApprovalPolicy { get; set; }
    /// <summary>Codex の承認リクエスト自動レビュー。</summary>
    public bool? LastCodexAutoReviewApprovals { get; set; }
    /// <summary>Codex の承認審査者（"" | "user" | "auto_review"）。</summary>
    public string? LastCodexApprovalReviewer { get; set; }
    /// <summary>Codex の Windows サンドボックス方式（"" | "elevated" | "unelevated"）。</summary>
    public string? LastCodexWindowsSandbox { get; set; }
    /// <summary>Codex のコマンド用ネットワーク（"" | "true" | "false"）。</summary>
    public string? LastCodexNetworkAccess { get; set; }
    /// <summary>Codex の Web 検索（"" | "live" | "disabled"）。</summary>
    public string? LastCodexWebSearchMode { get; set; }
    /// <summary>Codex の resume --last。</summary>
    public bool? LastCodexResumeLast { get; set; }
    /// <summary>Codex の --no-alt-screen。未設定時は false。</summary>
    public bool? LastCodexNoAltScreen { get; set; }
    /// <summary>Codex の --search。</summary>
    public bool? LastCodexSearchEnabled { get; set; }
}

/// <summary>
/// 試験機能設定。TerminalHub と各CLIの連携などの実験的挙動を、既定OFFで束ねる。
/// </summary>
public class ExperimentalSettings
{
    /// <summary>
    /// セッション生成時に、対応CLI(Claude Code / Codex)へ TerminalHub のローカル MCP サーバー
    /// (terminalhub) を繋ぐ。ONにすると起動しただけで list_sessions / send_to_session が使える。既定OFF。
    /// どちらも起動オプションで渡し、ユーザーの設定ファイルは書き換えない。
    /// Claude Code は --mcp-config <JSONパス>、Codex は -c mcp_servers.terminalhub.url=...。
    /// </summary>
    public bool EnableLocalMcp { get; set; } = false;

    /// <summary>
    /// テキスト入力欄でスラッシュコマンドの補完（オートコンプリート）を有効にする。
    /// 入力全体が "/" 始まりのときだけ候補ポップアップを表示し、名前の部分一致で絞り込む。
    /// 現状は Claude Code のみ対応（組み込みコマンド辞書）。既定OFF。
    /// </summary>
    public bool SlashAutocompleteEnabled { get; set; } = false;

    /// <summary>
    /// セッションを一覧からクリックして開いたときに、そのセッションの LastAccessedAt を
    /// 更新して一覧の先頭へ引き上げる。既定OFF。ONにすると「最終利用順」ソート時に、
    /// 開いた（＝注目した）セッションが自然に上へ来る。順位を上げるためだけに挨拶を
    /// 打っていた運用の代替。プログラム由来の選択（起動復元・URL・再起動等）では上げない。
    /// </summary>
    public bool PromoteSessionOnOpen { get; set; } = false;

    /// <summary>
    /// MCP サーバーが接続時にモデルへ配布する instructions（取扱説明・運用ルール）。
    /// null または空なら組み込みの既定テンプレ(McpInstructionDefaults.Template)を使う。
    /// ユーザーが書き換えたら、その内容をそのまま配布する（＝ユーザーが著者、TerminalHub は下書きを置くだけ）。
    /// 全デバイス共通・グローバル設定。
    /// </summary>
    public string? McpInstructions { get; set; }
}

/// <summary>
/// ブラウザ通知設定
/// </summary>
public class NotificationSettings
{
    public bool EnableBrowserNotifications { get; set; } = true;
    public int ProcessingTimeThresholdSeconds { get; set; } = 5;
}

/// <summary>
/// Webhook通知設定
/// </summary>
public class WebhookSettings
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public Dictionary<string, string>? Headers { get; set; }
}

/// <summary>
/// Claude Code Hook設定
/// 個別イベントのトグルは実装していない (Stop / UserPromptSubmit / Notification は常に一括で有効化/削除する)。
/// </summary>
public class ClaudeHookSettings
{
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// 特殊設定
/// </summary>
public class SpecialSettings
{
    public string ClaudeModeSwitchKey { get; set; } = "none";
    public bool VoiceInputEnabled { get; set; } = false;
}

/// <summary>
/// セッション表示設定
/// </summary>
public class SessionDisplaySettings
{
    public string SortMode { get; set; } = "lastAccessed";
    public bool ShowTerminalType { get; set; }
    public bool ShowGitInfo { get; set; }
    /// <summary>Git バッジのクリックで変更ファイル一覧を表示する（ShowGitInfo が有効な場合のみ意味を持つ）。既定 false。</summary>
    public bool GitChangesOnBadgeClick { get; set; }
    public bool HideInputPanel { get; set; }
}

/// <summary>
/// 開発ツール設定
/// </summary>
public class DevToolsSettings
{
    public bool Enabled { get; set; }

    /// <summary>ConPTY 生出力ストリームをファイルへ記録する（VTエミュレータ検証用フィクスチャ採取）。既定 false。</summary>
    public bool CaptureRawStream { get; set; }

}

/// <summary>
/// 一般設定
/// </summary>
public class GeneralSettings
{
    public string DefaultFolderPath { get; set; } = "";
    public List<string> FavoriteFolders { get; set; } = new();
    public double SessionListScale { get; set; } = 1.0;
    public int TerminalFontSize { get; set; } = 14;
    public int TerminalHeightPercent { get; set; } = 70;
    public int SidebarWidthPercent { get; set; } = 25;
    public string Theme { get; set; } = "dark";
}

/// <summary>
/// カスタムコマンド設定
/// </summary>
public class CustomCommandSettings
{
    public Dictionary<string, List<CustomCommand>> CommandsByTerminalType { get; set; } = new();
}

/// <summary>
/// カスタムコマンドの種別
/// </summary>
public enum CustomCommandType
{
    /// <summary>テキスト送信（末尾に Enter を自動付与）</summary>
    Text = 0,
    /// <summary>プリセットキーのエスケープシーケンス送信（Enter は付与しない）</summary>
    KeySequence = 1
}

/// <summary>
/// テキスト型カスタムコマンドの送信方法。
/// </summary>
public enum CustomCommandSendMode
{
    /// <summary>従来動作：CommandText をターミナルに直接書き込み、Enter を自動付与する。</summary>
    DirectSend = 0,
    /// <summary>TextInputPanel のテキストエリアに流し込み、送信はユーザー操作に任せる。</summary>
    InsertToInput = 1
}

/// <summary>
/// カスタムコマンド
/// </summary>
public class CustomCommand
{
    public string? Title { get; set; }
    public string CommandText { get; set; } = "";
    public CustomCommandType Type { get; set; } = CustomCommandType.Text;
    /// <summary>KeySequence 時に参照するプリセットキー名（KeySequencePresets.All のキー）</summary>
    public string? KeyName { get; set; }
    /// <summary>表示側で同名のものをドロップダウンに集約するためのグループ名。null/空なら単独ボタン。</summary>
    public string? GroupName { get; set; }
    /// <summary>Text 型のときの送信方法。KeySequence 型では無視する。</summary>
    public CustomCommandSendMode SendMode { get; set; } = CustomCommandSendMode.DirectSend;
}

/// <summary>
/// CLI ごとに保存しておくユーザー定義カスタムオプションのコンテナ。
/// 起動時にチェックボックスで ON/OFF できる「お気に入りのコマンドラインオプション」を CLI 別に保持する。
/// </summary>
public class CustomCliOptionsSettings
{
    public List<UserCliOption> ClaudeCode { get; set; } = new();
    public List<UserCliOption> GeminiCLI { get; set; } = new();
    public List<UserCliOption> CodexCLI { get; set; } = new();
    public List<UserCliOption> Antigravity { get; set; } = new();
    public List<UserCliOption> Grok { get; set; } = new();


}

/// <summary>
/// ユーザーが定義した一行ぶんのコマンドラインオプション。
/// Arguments はそのまま起動コマンドラインの末尾に連結されるので、複数フラグを 1 行にまとめても良い。
/// </summary>
public class UserCliOption
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    /// <summary>UI に表示する短いラベル (空文字も許容、その場合は Arguments 自体を表示)</summary>
    public string Label { get; set; } = "";
    /// <summary>実引数文字列 (例: "--model gpt-5", "-c sandbox.foo=true")</summary>
    public string Arguments { get; set; } = "";
    /// <summary>新規セッション作成ダイアログを開いた時に初期 ON にするか</summary>
    public bool DefaultEnabled { get; set; } = false;
}

/// <summary>
/// リモート起動設定
/// </summary>
public class RemoteLaunchSettings
{
    public bool Enabled { get; set; }
    public string TopicGuid { get; set; } = "";
    public string? PasswordHash { get; set; }
}
