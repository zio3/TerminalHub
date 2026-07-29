using System.ComponentModel;
using ModelContextProtocol.Server;
using TerminalHub.Models;
using TerminalHub.Services;

namespace TerminalHub.Mcp
{
    /// <summary>
    /// セッション間メッセージング用の MCP ツール群。
    /// TerminalHub が管理する「既存」セッションに対して、一覧取得(list_sessions)・
    /// メッセージ送信(send_to_session)・メモ/カード設定(set_memo/set_card/get_card)・
    /// 依頼の状況札(get_context/update_context)・セッション専用コマンド
    /// (list_commands/add_command/remove_command)を提供する最小構成。
    ///
    /// 設計方針（壁打ちで確定）:
    /// - spawn なし: 子セッションは作らない。宛先は既存セッションのみ（暴走ガード不要）。
    /// - 集約なし: 結果待ち(wait)/読み取り(read)はしない。完了は TerminalHub 本体の LED/通知で人間が気づく。
    /// - 自己識別は環境変数経由: ConPTY 起動時に TERMINALHUB_SESSION_ID(自分が誰か) と
    ///   TERMINALHUB_SESSION_PROOF(本人証明・起動ごとに変わるランダム値) を注入している。
    ///   書き込み系(set_memo/set_card/add_command/remove_command)は proof の検証で
    ///   本人のみに機構的に制限する。
    /// - セッション専用コマンドだけは向きが逆で、セッションが人間のために UI(クイック送信バーの
    ///   ボタン)を生やす。グローバル設定のコマンドは対象外＝共有物には触らせない。
    /// - サーバーは会話状態を持たず、渡されたフラグ(submit 等)に素直に従うだけ
    ///   （メッセージの追跡・待ち合わせ・キューを持たないという意味。本人検証用の
    ///   SessionProof、および依頼IDで引く状況札=ContextSummary は例外として持つ）。
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
            string status,
            bool hasCard,
            string memo);

        /// <summary>
        /// send_to_session の結果。宛先なし/未起動/処理中は例外にせず success=false で返し、
        /// 呼び出し側（エージェント）にリトライ判断を委ねる。
        /// contextId は contextId="new" で送信したときだけ発行された ID が入る。
        /// </summary>
        public record SendResult(bool success, string message, string? contextId = null);

        [McpServerTool(Name = "list_sessions")]
        [Description(
            "TerminalHub が管理中のセッション一覧を返す。send_to_session の宛先を選ぶために使う。" +
            "任意のフィルタ引数で絞り込める。各項目の status は ready(受付中=送信可。作業中でも相手CLIのキューに積まれる) / " +
            "waiting_user_input(ユーザーの許可/選択待ち=送信不可) / not_ready(ConPTY未接続=起動が必要・送信不可)。" +
            "hasCard=true のセッションは自己紹介カードを持っている(本文は get_card で取得)。" +
            "memo はセッションの短い注釈(「今なにをしているか」やレーン運用の空き/予約札)。")]
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
                    status,
                    // カード本文は一覧に含めない(GUID 指定の get_card で読む)。有無だけ知らせて
                    // 「カード持ちのセッションだけ get_card する」を可能にし、N 回の空振りを省く。
                    !string.IsNullOrEmpty(s.Card),
                    // メモは短い札(UI の一覧に出るのと同じもの)なので本文ごと返す。
                    // ディスパッチャが worktree レーンの空き(「タスク無し」等)を判別する用途。
                    s.Memo ?? ""));
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
            "長文はそのまま流さず、ファイルに書いて絶対パスだけ送る運用を推奨。" +
            "contextId=\"new\" を渡すと ContextSummary(依頼の状況札)を発行し、結果の contextId で返す。" +
            "受け手には本文末尾に contextId が自動付与され、あなたは get_context をポーリングして結果を受け取れる" +
            "(返信を受け取るセッションを持たない外部クライアント向けの依頼経路)。既存の contextId を渡すと同じ札への続報になる。")]
        public static async Task<SendResult> SendToSession(
            ISessionManager sessionManager,
            IContextRepository contextRepository,
            [Description("宛先。セッションGUID、または表示名(完全一致・大文字小文字無視)。")]
            string target,
            [Description("送る本文。改行を含む長文は避け、短い指示＋ファイルの絶対パスを推奨。")]
            string message,
            [Description("末尾にEnterを送って実行を確定するか(既定 true)。false なら入力欄に流し込むだけ。")]
            bool submit = true,
            [Description("ContextSummary(依頼の状況札)の紐づけ。\"new\"=発行して紐づけ(結果で ID が返る) / 既存ID=続報として紐づけ / 未指定=紐づけなし(従来どおり)。")]
            string? contextId = null)
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

            // ContextSummary の紐づけ。作成は全チェック通過後（＝実際に送るときだけ）に行い、
            // 送信失敗時に孤児レコードを残さない。受け手への ID 伝達はサーバーが定型文で担う
            // （送信者の手書きに任せると書き忘れ事故が起きるため）。改行ではなくスペース連結に
            // しているのは、TUI CLI が本文中の改行を送信確定と誤解釈する事故を避けるため。
            string? issuedContextId = null;
            var deliveredMessage = message;
            if (!string.IsNullOrWhiteSpace(contextId))
            {
                string effectiveId;
                if (string.Equals(contextId, "new", StringComparison.OrdinalIgnoreCase))
                {
                    effectiveId = Guid.NewGuid().ToString("N");
                    await contextRepository.CreateAsync(effectiveId);
                    issuedContextId = effectiveId;
                }
                else
                {
                    // 既存 ID の続報。タイポで紐づけが静かに失われるのを防ぐため存在確認する。
                    var existing = await contextRepository.GetAsync(contextId);
                    if (existing == null)
                        return new SendResult(false,
                            $"contextId が見つかりません: {contextId}。新規発行なら contextId=\"new\" を指定してください。");
                    effectiveId = contextId;
                }
                deliveredMessage = $"{message} [contextId: {effectiveId} — 状況・結果は update_context で共有]";
            }

            // 送信本体。submit=true なら Enter(\r) を続けて送り実行を確定する。
            // WriteAsync 側で256文字チャンク＋Flush 済みなので長文でも切り捨てられない。
            try
            {
                await conpty.WriteAsync(deliveredMessage);
                if (submit)
                {
                    // テキスト送信後、Enter 送信前に待機する。
                    // Codex 等の TUI CLI は本文取り込み前に \r が来ると送信確定されず入力欄で止まるため、
                    // UI の SendInput と同じく 0.2 秒挟んでから Enter を送る。
                    await Task.Delay(200);
                    await conpty.WriteAsync("\r");
                }
            }
            catch
            {
                // 書き込み自体の失敗（ConPTY切断等）では、発行したばかりの札を片付ける。
                // submitted は終端状態でなく TTL 掃除の対象外のため、残すと永続の孤児になる。
                // DeleteAsync は例外を投げない（送信エラー本体を握り潰さない）。
                if (issuedContextId != null)
                {
                    await contextRepository.DeleteAsync(issuedContextId);
                }
                throw;
            }

            return new SendResult(true,
                issuedContextId != null
                    ? $"送信しました: {info.GetDisplayName()} (submit={submit})。contextId={issuedContextId} を発行しました。get_context でポーリングして結果を受け取れます。"
                    : $"送信しました: {info.GetDisplayName()} (submit={submit})",
                issuedContextId);
        }

        [McpServerTool(Name = "set_memo")]
        [Description(
            "自分のセッションのメモ(TerminalHub のセッション一覧に表示される短い注釈)を設定する。" +
            "「今なにをしているか」等のステータスを書いておくと、一覧から一目で分かる。既存のメモは上書きされる(空文字でクリア)。" +
            "proof には環境変数 TERMINALHUB_SESSION_PROOF の値を渡す(あなたが本人であることの証明。本文やメッセージには書かないこと)。" +
            "その環境変数が無いなら、あなたはセッションを持たない外部クライアントなのでこのツールは使えない(メモは TerminalHub の UI からも編集できる)。")]
        public static async Task<SendResult> SetMemo(
            ISessionManager sessionManager,
            ISessionRepository sessionRepository,
            [Description("本人証明。環境変数 TERMINALHUB_SESSION_PROOF の値をそのまま渡す。")]
            string proof,
            [Description("設定するメモ本文。空文字にするとメモをクリアする。")]
            string memo)
        {
            // proof が本人証明と宛先特定を兼ねる。GUID/表示名の自己申告は受け付けない
            // （誤認・詐称による他セッションの書き換えを機構的に不可能にする）。
            var info = sessionManager.ResolveBySessionProof(proof);
            if (info == null)
                return new SendResult(false, ProofRejectedMessage);

            var text = memo ?? string.Empty;

            // 永続化(SQLite)。MCP は非Circuitコンテキストなので、Circuit 内の StorageService 経由ではなく
            // Singleton の ISessionRepository で直接 SQLite を更新する。
            // 注意: ストレージが LocalStorage モード(真実の保存先がブラウザ側)のときは、この SQLite への
            // UPDATE が対象行なしで空振りし反映されないため、インメモリ更新＋UI反映のみ効き、
            // リロード後の保持は効かないことがある(既定は SQLite)。
            await sessionRepository.UpdateMemoAsync(info.SessionId, text);

            // インメモリの SessionInfo.Memo を更新し、開いている一覧を再描画させる。
            sessionManager.UpdateMemo(info.SessionId, text);

            return new SendResult(true, $"メモを設定しました: {info.GetDisplayName()}");
        }

        /// <summary>
        /// proof 検証に失敗したときの共通メッセージ。proof の値そのものはエコーしない
        /// （エラーメッセージ経由で会話ログへ漏れるのを防ぐ）。
        /// </summary>
        private const string ProofRejectedMessage =
            "本人証明(proof)が一致しません。環境変数 TERMINALHUB_SESSION_PROOF の値をそのまま渡してください。" +
            "この環境変数が存在しないなら、あなたはセッションを持たない外部クライアントであり、このツールは使えません。" +
            "値はセッション再起動のたびに変わるため、古い値を記憶から使い回している場合は環境変数を読み直してください。";

        // ---- 自己紹介カード（「何ができるか」の自己申告・A2A Agent Card のローカル版） ----
        //
        // 設計（壁打ちで確定）:
        // - memo の姉妹機能。memo=「今なにをしているか」(動的) / card=「何ができるか」(静的・長命)。
        // - 用語: A2A の Agent Card の description/skills に相当するものを card と呼ぶ。
        //   A2A の capabilities フィールドはプロトコル機能宣言(streaming 等)で別物のため、
        //   用語衝突を避けて capabilities という名前は使わない。
        // - set は「自分のみ」。当初は GUID 自己申告＋説明文の「仕様上の契約」だったが、
        //   ConPTY 起動時に注入する本人証明(TERMINALHUB_SESSION_PROOF)の検証に格上げした。
        //   proof はそのセッションの子プロセスだけが知る値なので、誤認・詐称による
        //   他セッションの書き換えは機構的に不可能。
        //   カードは自己申告＝「本人がそう名乗っている」以上の保証はしない、という A2A と同じ信頼モデル。
        // - get は誰のカードでも読める（宛先選びの当たりを付ける用途）。

        /// <summary>get_card の結果。</summary>
        public record CardResult(bool success, string? name, string? card, string message);

        [McpServerTool(Name = "set_card")]
        [Description(
            "自分のセッションの自己紹介カード(「何ができるか」の短い自己申告。他エージェントが宛先選びに使う)を設定する。" +
            "proof には環境変数 TERMINALHUB_SESSION_PROOF の値を渡す(あなたが本人であることの証明。本文やメッセージには書かないこと)。" +
            "その環境変数が無いなら、あなたはセッションを持たない外部クライアントなのでこのツールは使えない。" +
            "他セッションのカードは書き換えられない(proof は自分のセッションの分しか知り得ない=自己申告制の機構的担保)。" +
            "全体書き換え(部分更新なし)・空文字でクリア。数行の短文を想定(宛先選びの広告であって詳細ドキュメントではない)。")]
        public static async Task<SendResult> SetCard(
            ISessionManager sessionManager,
            ISessionRepository sessionRepository,
            [Description("本人証明。環境変数 TERMINALHUB_SESSION_PROOF の値をそのまま渡す。")]
            string proof,
            [Description("設定するカード本文(「何ができるか」の数行の短文)。空文字でクリア。既存の内容は全体上書きされる。")]
            string card)
        {
            // proof が本人証明と宛先特定を兼ねる。GUID の自己申告は受け付けない。
            var info = sessionManager.ResolveBySessionProof(proof);
            if (info == null)
                return new SendResult(false, ProofRejectedMessage);

            var text = card ?? string.Empty;

            // 永続化(SQLite)。set_memo と同じく Singleton の ISessionRepository で直接更新する
            // (LocalStorage モード時の制約も set_memo と同じ)。
            await sessionRepository.UpdateCardAsync(info.SessionId, text);

            // インメモリの SessionInfo.Card を更新する。
            sessionManager.UpdateCard(info.SessionId, text);

            return new SendResult(true, $"自己紹介カードを設定しました: {info.GetDisplayName()}");
        }

        [McpServerTool(Name = "get_card")]
        [Description(
            "指定セッションの自己紹介カード(そのセッションが自己申告した「何ができるか」)を取得する。誰のカードでも読める。" +
            "カードは自己申告であり古い可能性がある前提で、宛先の当たりを付ける用途に限定すること(書いてある≠今も動く)。" +
            "sessionId は list_sessions で得た GUID(自分のカードは TERMINALHUB_SESSION_ID)。")]
        public static CardResult GetCard(
            ISessionManager sessionManager,
            [Description("対象セッションのGUID。")]
            string sessionId)
        {
            if (!Guid.TryParse(sessionId, out var guid))
                return new CardResult(false, null, null,
                    $"sessionId が GUID ではありません: {sessionId}。list_sessions で GUID を確認してください。");

            var info = sessionManager.GetSessionInfo(guid);
            if (info == null)
                return new CardResult(false, null, null, $"対象セッションが見つかりません: {sessionId}");

            var card = info.Card ?? string.Empty;
            return new CardResult(true, info.GetDisplayName(), card,
                card.Length == 0
                    ? "自己紹介カードは未設定です(このセッションはまだ自己申告していない)。"
                    : "取得しました(自己申告・古い可能性あり)。");
        }

        // ---- ContextSummary（依頼の状況札・A2A の contextId に対応） ----
        //
        // 設計（壁打ちで確定。docs/mcp-session-messaging.md 参照）:
        // - send_to_session（push・受信箱がある相手用）の欠けていた片割れ。受信箱を持たない
        //   外部クライアント（Claude Desktop 等）が依頼の結果を pull で受け取るための仕組み。
        // - サーバーが持つのは「contextId → status＋要約1枚」だけ。追記ログ・claim・担当割当・
        //   完了通知・一覧（ID なし列挙）は作らない（調整ロジックはクライアント側）。
        // - contextId は capability 兼用（知っている=読み書きできる）。A2A の contextId と同じく
        //   最初の送信時にサーバーが発行して返す。status の語彙は A2A TaskState をそのまま使う。

        private static readonly string[] AllowedContextStatuses =
            { "submitted", "working", "completed", "failed", "canceled" };

        /// <summary>
        /// get_context の結果。updatedBy は最終書き込み者の検証済みセッション名
        /// （proof 付きで書かれた場合のみ。null なら無記名＝外部クライアント等の書き込み）。
        /// </summary>
        public record ContextSummaryResult(
            bool success,
            string? contextId,
            string? status,
            string? summary,
            string? updatedAt,
            string? updatedBy,
            string message);

        [McpServerTool(Name = "get_context")]
        [Description(
            "ContextSummary(依頼の状況札)を取得する。contextId は A2A の contextId に対応する依頼単位の ID" +
            "(モデルのコンテキストウィンドウとは無関係)。send_to_session の contextId=\"new\" で発行される。" +
            "依頼側はこれをポーリングして進捗・結果を受け取る(サーバーからの通知は無い)。" +
            "ID を知っていれば誰でも読める(ID が読み書きの資格を兼ねる)。" +
            "updatedBy は最終書き込み者の検証済みセッション名(null なら無記名の書き込み)。")]
        public static async Task<ContextSummaryResult> GetContext(
            IContextRepository contextRepository,
            [Description("対象の contextId。")]
            string contextId)
        {
            var record = await contextRepository.GetAsync(contextId ?? "");
            if (record == null)
                return new ContextSummaryResult(false, null, null, null, null, null,
                    $"contextId が見つかりません: {contextId}。終端状態(completed等)から一定期間で自動削除されます。");

            return new ContextSummaryResult(true, record.ContextId, record.Status, record.Summary,
                record.UpdatedAt.ToString("o"),
                record.UpdatedByName,
                record.Summary.Length == 0 ? "取得しました(要約はまだ書かれていません)。" : "取得しました。");
        }

        [McpServerTool(Name = "update_context")]
        [Description(
            "ContextSummary(依頼の状況札)の要約を全体上書きし、任意で status を更新する。" +
            "依頼を受けた側が「今どうなっているか・結果」を書く用途(依頼側は get_context で読む)。" +
            "summary は要約1枚(履歴は積まれない)。長い成果物はファイルに書いてパスを載せる。" +
            "status は submitted / working / completed / failed / canceled (A2A TaskState 準拠)。" +
            "完了時は status=completed と結果の要約をセットで書くこと。" +
            "セッション内から書くときは proof(環境変数 TERMINALHUB_SESSION_PROOF)を必ず渡すこと" +
            "(「どのセッションが書いたか」が検証済みで記録され、依頼側が信頼できる)。" +
            "proof 無しでも書けるが無記名になる(外部クライアント用)。")]
        public static async Task<SendResult> UpdateContext(
            ISessionManager sessionManager,
            IContextRepository contextRepository,
            [Description("対象の contextId(受け取ったメッセージ末尾に付与されている)。")]
            string contextId,
            [Description("状況・結果の要約(全体上書き)。長いものはファイルパスを書く。")]
            string summary,
            [Description("状態。submitted / working / completed / failed / canceled のいずれか。省略時は状態を変えず要約だけ更新。")]
            string? status = null,
            [Description("本人証明(環境変数 TERMINALHUB_SESSION_PROOF の値)。セッション内から書くなら必ず渡す。外部クライアント(環境変数なし)は省略可=無記名。")]
            string? proof = null)
        {
            if (!string.IsNullOrEmpty(status) &&
                !AllowedContextStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
                return new SendResult(false,
                    $"status が不正です: {status}。使えるのは {string.Join(" / ", AllowedContextStatuses)} のみ。");

            // 書き込み元の検証(任意)。proof が正しければ「どのセッションが書いたか」を検証済みで記録する。
            // proof が渡されたのに一致しない場合は、無記名として黙って通さずエラーにする
            // (古い proof の使い回し等に気づかせる。無記名で書きたいなら proof を渡さなければよい)。
            SessionInfo? writer = null;
            if (!string.IsNullOrWhiteSpace(proof))
            {
                writer = sessionManager.ResolveBySessionProof(proof);
                if (writer == null)
                    return new SendResult(false, ProofRejectedMessage);
            }

            var updated = await contextRepository.UpdateAsync(
                contextId ?? "",
                summary ?? string.Empty,
                status?.ToLowerInvariant(),
                writer?.SessionId.ToString(),
                writer?.GetDisplayName());
            if (!updated)
                return new SendResult(false,
                    $"contextId が見つかりません: {contextId}。終端状態(completed等)から一定期間で自動削除されます。");

            return new SendResult(true,
                $"ContextSummary を更新しました(status={status ?? "変更なし"}, " +
                $"記名={(writer != null ? writer.GetDisplayName() : "無記名")})。");
        }

        // ---- セッション専用コマンド（クイック送信バーのボタン） ----
        //
        // 設計（壁打ちで確定）:
        // - **セッション専用のみ**。グローバル(設定のコマンド)は読み書きとも対象外。
        //   全セッション・全CLIに出る共有物なので、人間がローカルで試してから手動で持っていく運用に任せる。
        // - **全体上書きにしない**（card とはここが違う）。人間も UI から編集する共有リストなので、
        //   上書きだと人間の編集を踏み潰す。add / remove / list に分ける。
        // - **remove は AI が登録したものだけ**。人間が作ったコマンドは失うと復旧が難しいのに対し、
        //   AI が作ったものは同じ手順で作り直せる、という非対称性に基づく安全弁。
        // - Title はセッション専用リスト内で一意（remove が Title 指定のため）。
        //   グローバルや親からの伝搬と同名になるのは許す（既存の表示仕様が重複を許している）。

        /// <summary>list_commands の返却項目。</summary>
        public record CommandSummary(
            string title,
            string type,
            string body,
            string? groupName,
            bool propagateToChildren,
            bool createdByAgent);

        [McpServerTool(Name = "list_commands"), Description(
            "自分のセッション専用コマンド(クイック送信バーのボタン)の一覧を取得する。" +
            "createdByAgent=true は自分(AI)が登録したもので、remove_command で消せる。" +
            "false は人間が作った/編集したもので、消せない。" +
            "グローバル設定のコマンドはここには含まれない(MCP の対象外)。")]
        public static IEnumerable<CommandSummary> ListCommands(
            ISessionManager sessionManager,
            [Description("本人証明。環境変数 TERMINALHUB_SESSION_PROOF の値をそのまま渡す。")]
            string proof)
        {
            var info = sessionManager.ResolveBySessionProof(proof);
            if (info == null)
                return Array.Empty<CommandSummary>();

            return info.SessionCommands.Select(c => new CommandSummary(
                title: c.Title ?? string.Empty,
                type: c.Type == CustomCommandType.KeySequence ? "key" : "text",
                body: c.Type == CustomCommandType.KeySequence ? (c.KeyName ?? string.Empty) : c.CommandText,
                groupName: string.IsNullOrWhiteSpace(c.GroupName) ? null : c.GroupName,
                propagateToChildren: c.PropagateToChildren,
                createdByAgent: c.CreatedByAgent)).ToList();
        }

        [McpServerTool(Name = "add_command"), Description(
            "自分のセッション専用コマンド(クイック送信バーのボタン)を追加する。登録すると即座に UI に現れる。" +
            "繰り返す操作を人間がワンクリックで撃てるようにしておく用途。" +
            "type=\"text\" ならテキスト送信、type=\"key\" ならキー送信(keyName にプリセット名)。" +
            "追加したコマンドは remove_command で消せる(人間が編集すると消せなくなる)。")]
        public static async Task<SendResult> AddCommand(
            ISessionManager sessionManager,
            ISessionRepository sessionRepository,
            [Description("本人証明。環境変数 TERMINALHUB_SESSION_PROOF の値をそのまま渡す。")]
            string proof,
            [Description("ボタンに出す名前。セッション専用コマンドの中で一意にすること(remove_command の指定に使う)。")]
            string title,
            [Description("種別。\"text\"=テキスト送信 / \"key\"=キー送信。")]
            string type,
            [Description("type=\"text\" のとき送る本文。type=\"key\" では無視される。")]
            string? commandText = null,
            [Description("type=\"key\" のときのプリセット名(CtrlC / Escape / ArrowUp / ShiftTab 等)。type=\"text\" では無視される。")]
            string? keyName = null,
            [Description("同名を指定すると1つのドロップダウンにまとめられる。単独ボタンにするなら省略。")]
            string? groupName = null,
            [Description("type=\"text\" のとき、送信せず入力欄へ流し込むだけにするなら true(人間が内容を確認してから送れる)。")]
            bool insertToInputOnly = false,
            [Description("サブセッションにも同じボタンを出すなら true。親セッションでのみ意味を持つ。")]
            bool propagateToChildren = false)
        {
            var info = sessionManager.ResolveBySessionProof(proof);
            if (info == null)
                return new SendResult(false, ProofRejectedMessage);

            if (string.IsNullOrWhiteSpace(title))
                return new SendResult(false, "title は必須です（remove_command の指定に使うため）。");
            // 前後の空白は落とす。残すと "Foo" と "Foo " が別物になり、重複判定も
            // remove_command の完全一致も人間の目には説明できない挙動になる。
            title = title.Trim();

            var isKey = string.Equals(type, "key", StringComparison.OrdinalIgnoreCase);
            if (!isKey && !string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                return new SendResult(false, $"type が不正です: {type}。\"text\" か \"key\" のいずれかを指定してください。");

            if (isKey && !KeySequencePresets.Contains(keyName))
                return new SendResult(false,
                    $"keyName が不正です: {keyName ?? "(未指定)"}。使えるのは " +
                    string.Join(" / ", KeySequencePresets.All.Select(kv => kv.Key)) + " です。");

            if (!isKey && string.IsNullOrWhiteSpace(commandText))
                return new SendResult(false, "type=\"text\" では commandText が必須です（空白だけも不可）。");

            var commands = new List<CustomCommand>(info.SessionCommands);
            if (commands.Any(c => string.Equals(c.Title, title, StringComparison.Ordinal)))
                return new SendResult(false,
                    $"同じ title のコマンドが既にあります: {title}。別の名前にするか、先に remove_command してください。");

            commands.Add(new CustomCommand
            {
                Title = title,
                CommandText = isKey ? string.Empty : (commandText ?? string.Empty),
                Type = isKey ? CustomCommandType.KeySequence : CustomCommandType.Text,
                KeyName = isKey ? keyName : null,
                GroupName = string.IsNullOrWhiteSpace(groupName) ? null : groupName,
                SendMode = (!isKey && insertToInputOnly)
                    ? CustomCommandSendMode.InsertToInput
                    : CustomCommandSendMode.DirectSend,
                PropagateToChildren = propagateToChildren,
                CreatedByAgent = true,
            });

            // 永続化(SQLite)。set_memo と同様、MCP は非Circuitコンテキストなので Singleton の
            // ISessionRepository で直接更新する。
            // 注意: ストレージが LocalStorage モード(真実の保存先がブラウザ側)のときは、この SQLite への
            // UPDATE が対象行なしで空振りし、インメモリ更新＋UI反映のみ効いてリロード後に消えることがある(既定は SQLite)。
            await sessionRepository.UpdateSessionCommandsAsync(info.SessionId, commands);
            sessionManager.UpdateSessionCommands(info.SessionId, commands);

            // 子セッションで伝搬を立てても効かない。黙って無視すると「登録できたのに効かない」に
            // 気づけないので、成功メッセージに但し書きを添える。
            var note = (propagateToChildren && info.ParentSessionId.HasValue)
                ? " ただしこのセッションはサブセッションなので、伝搬フラグは効きません（親セッションで登録してください）。"
                : string.Empty;

            return new SendResult(true, $"コマンドを追加しました: {title}。{note}");
        }

        [McpServerTool(Name = "remove_command"), Description(
            "自分のセッション専用コマンドを title 指定で削除する。" +
            "**自分(AI)が add_command で登録したものだけ消せる**。人間が作ったコマンドや、" +
            "人間が UI で編集したコマンドは消せない(失うと復旧が難しいため)。")]
        public static async Task<SendResult> RemoveCommand(
            ISessionManager sessionManager,
            ISessionRepository sessionRepository,
            [Description("本人証明。環境変数 TERMINALHUB_SESSION_PROOF の値をそのまま渡す。")]
            string proof,
            [Description("削除するコマンドの title。")]
            string title)
        {
            var info = sessionManager.ResolveBySessionProof(proof);
            if (info == null)
                return new SendResult(false, ProofRejectedMessage);

            var key = (title ?? string.Empty).Trim();

            // リストは1回だけ読んで、判定も除去も同じスナップショットに対して行う。
            // 2回読むと、その間に別の書き込みで差し替わったとき参照が一致せず、
            // 何も消していないのに成功を返す（＝静かに効かない）ことがある。
            var snapshot = info.SessionCommands.ToList();
            var target = snapshot.FirstOrDefault(c => string.Equals(c.Title, key, StringComparison.Ordinal));
            if (target == null)
                return new SendResult(false, $"そのコマンドが見つかりません: {key}。list_commands で確認してください。");

            if (!target.CreatedByAgent)
                return new SendResult(false,
                    $"「{key}」は人間が作った(または編集した)コマンドなので削除できません。" +
                    "消す必要があるなら、人間に UI から操作してもらってください。");

            var commands = snapshot
                .Where(c => !string.Equals(c.Title, key, StringComparison.Ordinal)).ToList();

            // LocalStorage モードでの空振り注意は add_command 側のコメント参照。
            await sessionRepository.UpdateSessionCommandsAsync(info.SessionId, commands);
            sessionManager.UpdateSessionCommands(info.SessionId, commands);

            return new SendResult(true, $"コマンドを削除しました: {key}");
        }
    }
}
