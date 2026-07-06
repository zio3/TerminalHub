using System.ComponentModel;
using ModelContextProtocol.Server;
using TerminalHub.Models;
using TerminalHub.Services;

namespace TerminalHub.Mcp
{
    /// <summary>
    /// セッション間メッセージング用の MCP ツール群。
    /// TerminalHub が管理する「既存」セッションに対して、一覧取得(list_sessions)・
    /// メッセージ送信(send_to_session)・メモ設定(set_memo)を提供する最小構成。
    ///
    /// 設計方針（壁打ちで確定）:
    /// - spawn なし: 子セッションは作らない。宛先は既存セッションのみ（暴走ガード不要）。
    /// - 集約なし: 結果待ち(wait)/読み取り(read)はしない。完了は TerminalHub 本体の LED/通知で人間が気づく。
    /// - 自己識別は環境変数経由: ConPTY 起動時に TERMINALHUB_SESSION_ID を注入しており、CLI/エージェントは
    ///   それを読めば自分のセッション GUID が分かる。set_memo で自分自身を対象にできる。
    /// - サーバーは状態を持たず、渡されたフラグ(submit 等)に素直に従うだけ。
    /// メインユースケース: Claude で仕様を書きファイル化 → その絶対パスを Codex セッションへ送って実装させる。
    /// 自分の作業状況を set_memo で一覧に書いておけば、TerminalHub から進捗が一目で分かる。
    /// </summary>
    [McpServerToolType]
    public class SessionMessagingTools
    {
        /// <summary>list_sessions の返却項目。</summary>
        public record SessionSummary(
            string sessionId,
            string name,
            string terminalType,
            string folderPath,
            string status);

        /// <summary>
        /// send_to_session の結果。宛先なし/未起動/処理中は例外にせず success=false で返し、
        /// 呼び出し側（エージェント）にリトライ判断を委ねる。
        /// </summary>
        public record SendResult(bool success, string message);

        [McpServerTool(Name = "list_sessions")]
        [Description(
            "TerminalHub が管理中の(アーカイブでない)セッション一覧を返す。send_to_session の宛先を選ぶために使う。" +
            "任意のフィルタ引数で絞り込める。各項目の status は ready(受付中=送信可。作業中でも相手CLIのキューに積まれる) / " +
            "waiting_user_input(ユーザーの許可/選択待ち=送信不可) / not_ready(ConPTY未接続=起動が必要・送信不可)。")]
        public static IEnumerable<SessionSummary> ListSessions(
            ISessionManager sessionManager,
            [Description("種別で絞り込み(ClaudeCode / CodexCLI / GeminiCLI / Terminal / Antigravity / Grok)。未指定なら全種別。")]
            string? terminalType = null,
            [Description("表示名に含む文字列で絞り込み(部分一致・大文字小文字無視)。")]
            string? nameContains = null,
            [Description("作業フォルダパスに含む文字列で絞り込み(部分一致・大文字小文字無視)。")]
            string? folderContains = null)
        {
            var result = new List<SessionSummary>();
            foreach (var s in sessionManager.GetActiveSessions())
            {
                if (!string.IsNullOrEmpty(terminalType) &&
                    !string.Equals(s.TerminalType.ToString(), terminalType, StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = s.GetDisplayName();
                if (!string.IsNullOrEmpty(nameContains) &&
                    name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var folder = s.FolderPath ?? "";
                if (!string.IsNullOrEmpty(folderContains) &&
                    folder.IndexOf(folderContains, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                // 送信可否で状態を導出（呼び出し側が「送れるか」を一目で判断できるように）。
                //   not_ready          = ConPTY 未接続。まず起動が必要（送信不可）。
                //   waiting_user_input = ユーザーの許可/選択待ち（送信不可・待ち解消後に再試行）。
                //   ready              = 受付中。idle でも busy でも送れる（busy は相手CLIのキューに積まれる）。
                var status = s.ConPtySession == null ? "not_ready"
                    : s.IsWaitingForUserInput ? "waiting_user_input"
                    : "ready";

                result.Add(new SessionSummary(
                    s.SessionId.ToString(),
                    name,
                    s.TerminalType.ToString(),
                    folder,
                    status));
            }
            return result;
        }

        [McpServerTool(Name = "send_to_session")]
        [Description(
            "指定した既存セッションのターミナルにメッセージを1件送る(投げっぱなし・応答は待たない)。" +
            "target はセッションGUIDか表示名(完全一致)。submit=true なら末尾でEnterを送り即実行させる。" +
            "相手がユーザーの許可/選択待ち(waiting)のときは送らず success=false を返す(承認プロンプトを誤確定させないため。待ち解消後に再試行)。" +
            "単なる作業中(busy)は送信可(AI CLI がプロンプトをキューに積む)。" +
            "宛先が未起動のときも success=false(自動起動しない・ユーザーに起動を依頼し、自動リトライはしない)。" +
            "長文はそのまま流さず、ファイルに書いて絶対パスだけ送る運用を推奨。")]
        public static async Task<SendResult> SendToSession(
            ISessionManager sessionManager,
            [Description("宛先。セッションGUID、または表示名(完全一致・大文字小文字無視)。")]
            string target,
            [Description("送る本文。改行を含む長文は避け、短い指示＋ファイルの絶対パスを推奨。")]
            string message,
            [Description("末尾にEnterを送って実行を確定するか(既定 true)。false なら入力欄に流し込むだけ。")]
            bool submit = true)
        {
            // 宛先解決: GUID を優先し、ダメなら表示名の完全一致で探す。
            SessionInfo? info = null;
            if (Guid.TryParse(target, out var guid))
            {
                info = sessionManager.GetSessionInfo(guid);
            }
            info ??= sessionManager.GetActiveSessions()
                .FirstOrDefault(s => string.Equals(s.GetDisplayName(), target, StringComparison.OrdinalIgnoreCase));

            if (info == null)
                return new SendResult(false, $"宛先セッションが見つかりません: {target}");

            // 入力待ち(許可/選択待ち)なら送らない。ここで送ると submit の Enter が
            // 許可プロンプトの確定に化けて意図しない承認をしてしまうため。呼び出し側でリトライ判断させる。
            // 単なる作業中(busy)は送信を許可する(AI CLI がプロンプトをキューに積むため)。
            if (info.IsWaitingForUserInput)
                return new SendResult(false,
                    $"宛先がユーザーの許可/選択待ち(status=waiting_user_input)のため送信しませんでした。" +
                    $"ここで送ると承認プロンプトを誤って確定させる恐れがあります。" +
                    $"待ちが解消(status=ready)してから再試行してください: {info.GetDisplayName()}");

            var conpty = info.ConPtySession;
            if (conpty == null)
                return new SendResult(false,
                    $"宛先セッションが未起動です(status=not_ready / ConPTY 未接続)。これは自動起動できません。" +
                    $"ユーザーに「TerminalHub で『{info.GetDisplayName()}』を開いて起動してください」と依頼し、" +
                    $"status=ready になったのを確認してから再送してください。自動でリトライしないこと。");

            // 送信本体。submit=true なら Enter(\r) を続けて送り実行を確定する。
            // WriteAsync 側で256文字チャンク＋Flush 済みなので長文でも切り捨てられない。
            await conpty.WriteAsync(message);
            if (submit)
            {
                // テキスト送信後、Enter 送信前に待機する。
                // Codex 等の TUI CLI は本文取り込み前に \r が来ると送信確定されず入力欄で止まるため、
                // UI の SendInput と同じく 0.2 秒挟んでから Enter を送る。
                await Task.Delay(200);
                await conpty.WriteAsync("\r");
            }

            return new SendResult(true, $"送信しました: {info.GetDisplayName()} (submit={submit})");
        }

        [McpServerTool(Name = "set_memo")]
        [Description(
            "指定した既存セッションのメモ(TerminalHub のセッション一覧に表示される短い注釈)を設定する。" +
            "「今なにをしているか」等のステータスを書いておくと、一覧から一目で分かる。既存のメモは上書きされる(空文字でクリア)。" +
            "自分自身のメモを更新するには、環境変数 TERMINALHUB_SESSION_ID の値を target に渡す。" +
            "target はセッションGUIDか表示名(完全一致)。")]
        public static async Task<SendResult> SetMemo(
            ISessionManager sessionManager,
            ISessionRepository sessionRepository,
            [Description("対象。セッションGUID(自分自身なら環境変数 TERMINALHUB_SESSION_ID の値)、または表示名(完全一致・大文字小文字無視)。")]
            string target,
            [Description("設定するメモ本文。空文字にするとメモをクリアする。")]
            string memo)
        {
            // 宛先解決: send_to_session と同じく GUID 優先 → 表示名の完全一致。
            SessionInfo? info = null;
            if (Guid.TryParse(target, out var guid))
            {
                info = sessionManager.GetSessionInfo(guid);
            }
            info ??= sessionManager.GetActiveSessions()
                .FirstOrDefault(s => string.Equals(s.GetDisplayName(), target, StringComparison.OrdinalIgnoreCase));

            if (info == null)
                return new SendResult(false, $"対象セッションが見つかりません: {target}");

            var text = memo ?? string.Empty;

            // 永続化(SQLite)。MCP は非Circuitコンテキストなので、Circuit 内の StorageService 経由ではなく
            // Singleton の ISessionRepository で直接 SQLite を更新する。
            // 注意: ストレージが LocalStorage モードのときはサーバー側から書き込めないため、
            // その場合はインメモリ更新＋UI反映のみ効き、リロード後の保持は効かないことがある(既定は SQLite)。
            await sessionRepository.UpdateMemoAsync(info.SessionId, text);

            // インメモリの SessionInfo.Memo を更新し、開いている一覧を再描画させる。
            sessionManager.UpdateMemo(info.SessionId, text);

            return new SendResult(true, $"メモを設定しました: {info.GetDisplayName()}");
        }
    }
}
