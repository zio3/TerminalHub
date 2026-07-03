using System.ComponentModel;
using ModelContextProtocol.Server;
using TerminalHub.Models;
using TerminalHub.Services;

namespace TerminalHub.Mcp
{
    /// <summary>
    /// セッション間メッセージング用の MCP ツール群。
    /// TerminalHub が管理する「既存」セッションに対して、一覧取得(list_sessions)と
    /// メッセージ送信(send_to_session)だけを提供する最小構成。
    ///
    /// 設計方針（壁打ちで確定）:
    /// - spawn なし: 子セッションは作らない。宛先は既存セッションのみ（暴走ガード不要）。
    /// - 集約なし: 結果待ち(wait)/読み取り(read)はしない。完了は TerminalHub 本体の LED/通知で人間が気づく。
    /// - エンベロープ/自己識別なし: 本文だけ送る。送信元明示や応答要否は将来「呼び出し元フラグ」で足す。
    /// - サーバーは状態を持たず、渡されたフラグ(submit 等)に素直に従うだけ。
    /// メインユースケース: Claude で仕様を書きファイル化 → その絶対パスを Codex セッションへ送って実装させる。
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
            "任意のフィルタ引数で絞り込める。各項目の status は idle(受信可) か processing(処理中=送ると入力が化ける)。")]
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

                result.Add(new SessionSummary(
                    s.SessionId.ToString(),
                    name,
                    s.TerminalType.ToString(),
                    folder,
                    s.ProcessingStartTime.HasValue ? "processing" : "idle"));
            }
            return result;
        }

        [McpServerTool(Name = "send_to_session")]
        [Description(
            "指定した既存セッションのターミナルにメッセージを1件送る(投げっぱなし・応答は待たない)。" +
            "target はセッションGUIDか表示名(完全一致)。submit=true なら末尾でEnterを送り即実行させる。" +
            "相手が処理中(processing)のときは送らず success=false を返すので、呼び出し側で待って再試行すること。" +
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

            // 処理中なら送らない(送ると入力が化ける)。呼び出し側でリトライ判断させる。
            if (info.ProcessingStartTime.HasValue)
                return new SendResult(false,
                    $"宛先が処理中(processing)のため送信しませんでした。idle になってから再試行してください: {info.GetDisplayName()}");

            var conpty = info.ConPtySession;
            if (conpty == null)
                return new SendResult(false, $"宛先セッションが未起動です(ConPTY 未接続): {info.GetDisplayName()}");

            // 送信本体。submit=true なら Enter(\r) を続けて送り実行を確定する。
            // WriteAsync 側で256文字チャンク＋Flush 済みなので長文でも切り捨てられない。
            await conpty.WriteAsync(message);
            if (submit)
            {
                await conpty.WriteAsync("\r");
            }

            return new SendResult(true, $"送信しました: {info.GetDisplayName()} (submit={submit})");
        }
    }
}
