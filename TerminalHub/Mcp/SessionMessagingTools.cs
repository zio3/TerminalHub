using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using TerminalHub.Constants;
using TerminalHub.Models;
using TerminalHub.Services;

namespace TerminalHub.Mcp
{
    /// <summary>
    /// セッション間メッセージング用の MCP ツール群。
    /// TerminalHub が管理する「既存」セッションに対して、一覧取得(list_sessions)・
    /// メッセージ送信(send_to_session)・メモ/カード設定(set_memo/set_card/get_card)・
    /// 依頼の状況札(get_context/update_context)・配送記録の検証(get_delivery)・
    /// セッション専用コマンド(list_commands/add_command/remove_command)を提供する最小構成。
    ///
    /// 設計方針（壁打ちで確定）:
    /// - spawn なし: 子セッションは作らない。宛先は既存セッションのみ（暴走ガード不要）。
    /// - 集約なし: 複数の結果を待ち合わせて集める(wait/join)ことはしない。
    ///   ただし**1件の依頼の結末を依頼元へ返すことはする**（本人確定済みの接続から送った依頼は、
    ///   札が終端 status になった時点で SessionDeliveryService が依頼元セッションへ1通配る）。
    ///   無記名＝外部クライアントの依頼は従来どおり get_context のポーリングで受け取る。
    /// - 自己識別は**接続（トランスポート層）で確定**: TerminalHub がセッション起動時に配る
    ///   MCP 設定に接続キー(X-TerminalHub-Session-Key ヘッダ)が入っており、サーバーは
    ///   ヘッダで「どのセッションの呼び出しか」を確定する。モデルは秘密を運ばない
    ///   （旧方式＝proof 引数を環境変数から読ませる方式は、シェル出力が安全分類器に弾かれて
    ///   無言で無記名に劣化する実害があり、互換も残さず全面撤去した。2026-08-02）。
    ///   ヘッダ無し接続（外部クライアント・旧 .mcp.json 残骸経由）は無記名として扱う。
    ///   書き込み系(set_memo/set_card/add_command/remove_command)はこの本人確定で
    ///   本人のみに機構的に制限する。
    /// - セッション専用コマンドだけは向きが逆で、セッションが人間のために UI(クイック送信バーの
    ///   ボタン)を生やす。グローバル設定のコマンドは対象外＝共有物には触らせない。
    /// - サーバーは会話の状態を持たない（メッセージの内容を解釈しない・返答を待ち合わせない）。
    ///   ただし**配送はサーバーの責務**で、宛先が入力待ちなら DeliveryQueue に積んで
    ///   解消後に自動配送する（呼び出し側に状態確認と再送をさせない。呼び出し元が
    ///   セッションの場合、送信直後にターンが終わるので再試行する契機が存在しないため）。
    ///   状態として持つのは本人検証用の McpConnectionKey、依頼IDで引く状況札=ContextSummary、
    ///   揮発の配送キュー、および配送記録=Deliveries（エンベロープ #ID の検証台帳。
    ///   Pending で作成→配送結果で Committed へ確定 or Rejected なら削除。
    ///   上限掃除は Committed だけを数える）の4つ。
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
        /// send_to_session の結果。宛先なし/未起動は例外にせず success=false で返す。
        /// contextId は contextId="new" で送信したときだけ発行された ID が入る。
        /// delivery は "delivered"（ConPTY へ書けた）/ "queued"（宛先が入力待ちなので受理して待機中）。
        /// success=true の意味が「届いた」ではなく「受理した」に変わったため、両者を区別して返す。
        /// deliveryId は配送記録の照会キー（受理された送信で入る）。エンベロープにも埋まるが、
        /// スラッシュコマンド送信はエンベロープが付かないため、**送信者がこの値を持つことが
        /// 監査（get_delivery）の唯一の入口**になる。
        /// </summary>
        public record SendResult(
            bool success, string message, string? contextId = null, string? delivery = null, string? deliveryId = null);

        [McpServerTool(Name = "list_sessions")]
        [Description(
            "TerminalHub が管理中のセッション一覧を返す。send_to_session の宛先を選ぶために使う。" +
            "任意のフィルタ引数で絞り込める。各項目の status は ready(受付中。作業中でも相手CLIのキューに積まれる) / " +
            "waiting_user_input(ユーザーの許可/選択待ち。**送信は可能**で、待ちが解消してから自動配送される) / " +
            "not_ready(ConPTY未接続=起動が必要・送信不可)。" +
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

                // 宛先の状態（呼び出し側が「今どうなっているか」を把握するため）。
                //   not_ready          = ConPTY 未接続。まず起動が必要（送信不可）。
                //   waiting_user_input = ユーザーの許可/選択待ち。送信自体は可能で、配送キューが
                //                        待ち解消後に送る（呼び出し側で確認・再送する必要はない）。
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
            "指定した既存セッションのターミナルにメッセージを1件送る(末尾でEnterを送り即実行させる。応答は待たない)。" +
            "target はセッションGUIDか表示名(完全一致)。" +
            "**届く本文の末尾には、TerminalHub 経由の自動メッセージであることを示す角括弧がサーバーによって必ず付く**" +
            "(TerminalHub 起動のセッションからの呼び出しは接続キーで本人が自動確定し、検証済みのあなたの名前とGUID入りになる。" +
            "無記名になるのはセッションを持たない外部クライアント等だけ。外すことはできない)。" +
            "唯一の例外は `/` で始まる本文(スラッシュコマンド送信)で、コマンド引数を汚さないためエンベロープを付けない" +
            "(そのため contextId とは併用不可=拒否される)。" +
            "相手がユーザーの許可/選択待ちでも送ってよい。**待ちが解消してから自動で配送される**" +
            "(あなたが状態を確認したり再送したりする必要はない。ただし待ちのまま溜まった分が上限に達すると受理されなくなる)。" +
            "結果の delivery が \"delivered\"=書き込み済み / \"queued\"=受理して配送待ち。どちらも success=true。" +
            "success=false になるのは、宛先が見つからない / 宛先が未起動(自動起動しない・ユーザーに起動を依頼する) / " +
            "配送待ちが上限 / 指定した既存 contextId が見つからない / 本文に改行・制御文字が含まれる / **書き込みが途中で失敗**、のいずれか。" +
            "最後のケースだけは本文が相手の入力欄へ届いている可能性があるため、" +
            "**同じ内容をそのまま再送してはいけない**(二重に連結される)。メッセージ本文に区別が書かれているので必ず読むこと。" +
            "長文はそのまま流さず、ファイルに書いて絶対パスだけ送る運用を推奨。" +
            "contextId=\"new\" を渡すと ContextSummary(依頼の状況札)を発行し、結果の contextId で返す。" +
            "呼び出し元が確定しているセッション(通常はそう)なら、札が完了/失敗/中止になった時点で" +
            "あなたのセッションへ自動で通知が届く(通常はポーリング不要)。" +
            "ただし通知が届いた時点であなた自身が長く入力待ちだと通知は破棄される(失敗通知の連鎖を避けるため再通知しない)。" +
            "依頼を出したあと長く放置される場合は、念のため get_context で確認すること。" +
            "無記名の外部クライアントは get_context をポーリングして結果を受け取る。既存の contextId を渡すと同じ札への続報になる。")]
        public static async Task<SendResult> SendToSession(
            ISessionManager sessionManager,
            IContextRepository contextRepository,
            IDeliveryRepository deliveryRepository,
            ISessionDeliveryService deliveryService,
            IHttpContextAccessor httpContextAccessor,
            [Description("宛先。セッションGUID、または表示名(完全一致・大文字小文字無視)。")]
            string target,
            [Description("送る本文(1行のみ)。改行・制御文字は拒否される(受け手のターミナルで送信確定等として解釈され、意図しない実行につながるため)。複数行の内容はファイルに書いて絶対パスだけを送る。")]
            string message,
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

            // 送信元の確定。接続キー（ヘッダ）のみで行う（GUID の自己申告は受け付けない）。
            // requester=null は無記名＝外部クライアント・残骸経由等。
            var requester = ResolveCaller(sessionManager, httpContextAccessor);

            // 未起動だけは積んでも意味がない（起動は人間の操作が要る）ので、ここで断る。
            // 入力待ちは断らない: 配送キューが待ち解消後に送る（呼び出し元がセッションの場合、
            // 送信直後にターンが終わるため「呼び出し側でリトライ」は履行不能な契約だった）。
            if (info.ConPtySession == null)
                return new SendResult(false,
                    $"宛先セッションが未起動です(status=not_ready / ConPTY 未接続)。これは自動起動できません。" +
                    $"ユーザーに「TerminalHub で『{info.GetDisplayName()}』を開いて起動してください」と依頼し、" +
                    $"status=ready になったのを確認してから再送してください。自動でリトライしないこと。");

            // ContextSummary の紐づけ。作成は全チェック通過後（＝実際に送るときだけ）に行い、
            // 送信失敗時に孤児レコードを残さない。
            string? issuedContextId = null;
            string? effectiveContextId = null;
            // **本文に改行・制御文字を許さない**。本文中の \r は TUI に送信確定として解釈されるため、
            // "指示\r" のような本文だと**指示だけが先に無印で実行され、エンベロープは次の入力欄に
            // 残骸として残る**＝「マーカー無し＝人間」の境界をまるごとすり抜ける穴になる
            // （スラッシュ例外と組めば "/context\r指示" でも同じ）。ESC・Ctrl+C 等の他の C0 制御文字も
            // キー操作の注入に使えるため一括で拒否する（タブも TUI の補完等を起こすので含める）。
            // 複数行の内容はファイルに書いてパスを送る、という既存の推奨運用がそのまま逃げ道になる。
            if (MessageTextGuard.TryFindControlChar(message, out var bad))
            {
                return new SendResult(false,
                    $"本文に制御文字(U+{(int)bad:X4})が含まれているため送信できません。" +
                    "改行を含む内容は本文で送らず、ファイルに書いて絶対パスだけを送ってください" +
                    "(本文中の改行は受け手のターミナルで送信確定として解釈され、意図しない実行につながります)。");
            }

            // `/` で始まる本文（スラッシュコマンド送信）にはエンベロープを付けない（後述）。
            // その場合 contextId を受け手へ伝える手段が無くなる＝受け手は update_context を
            // 呼べず、依頼元は完了通知を永遠に待つ「静かな機能停止」になる。併用は組み立ての
            // 時点で拒否する（黙って通すより、送る前に気づかせる）。
            var isSlashCommand = (message ?? string.Empty).StartsWith("/", StringComparison.Ordinal);
            if (isSlashCommand && !string.IsNullOrWhiteSpace(contextId))
                return new SendResult(false,
                    "スラッシュコマンド（`/` で始まる本文）には contextId を紐づけられません。" +
                    "コマンド引数を汚さないためエンベロープを付けない仕様で、受け手に contextId を伝える手段が無いためです。" +
                    "結果が要る依頼は通常の文章で送ってください。");

            if (!string.IsNullOrWhiteSpace(contextId))
            {
                if (string.Equals(contextId, "new", StringComparison.OrdinalIgnoreCase))
                {
                    effectiveContextId = Guid.NewGuid().ToString("N");
                    await contextRepository.CreateAsync(
                        effectiveContextId, requester?.SessionId.ToString(), requester?.GetDisplayName());
                    issuedContextId = effectiveContextId;
                }
                else
                {
                    // 既存 ID の続報。タイポで紐づけが静かに失われるのを防ぐため存在確認する。
                    var existing = await contextRepository.GetAsync(contextId);
                    if (existing == null)
                        return new SendResult(false,
                            $"contextId が見つかりません: {contextId}。新規発行なら contextId=\"new\" を指定してください。");
                    effectiveContextId = contextId;
                }
            }

            // 送信元・contextId の伝達はサーバーが定型文で担う（送信者の手書きに任せると書き忘れ事故が
            // 起きるため）。改行ではなくスペース連結にしているのは、TUI CLI が本文中の改行を
            // 送信確定と誤解釈する事故を避けるため。
            //
            // deliveryId は配送の照会キー。エンベロープを受け取った者が get_delivery で
            // 「本当に TerminalHub を通ったか・どこからどこへか」を検証できる。
            // 12文字あれば偶然の衝突も総当たりの当てずっぽうも実用上無視できる。
            var deliveryId = Guid.NewGuid().ToString("N")[..12];

            // **`/` で始まる本文（スラッシュコマンド送信）にはエンベロープを付けない**。
            // 末尾の角括弧がコマンドの引数として解釈され、引数を取るコマンド（/model 等）を
            // 壊すため。判定は行頭の `/` のみ（Trim しない。先頭に空白があれば TUI 側も
            // コマンドとして扱わないので、通常本文と同じにエンベロープを付ける）。
            // 「マーカー無し＝人間」の保証にはこの分だけ穴が開くが、`/` で始まる入力は
            // CLI にコマンドとして解釈され通常プロンプトにならないため、指示のなりすましには
            // 実質使えない。ローカル利用前提で許容する（2026-08-01 判断）。
            // 配送記録はスラッシュ送信でも作る（誰がどのセッションへコマンドを送ったかの監査を保つ）。
            // ただし Rejected で受理されなかった場合は通常送信と同じく削除される（下の Rejected 分岐）。
            // contextId との併用は上（組み立て前）で拒否済み。
            var deliveredMessage = isSlashCommand
                ? message!
                : message + BuildEnvelope(deliveryId, requester, effectiveContextId);

            DeliveryOutcome outcome;
            try
            {
                // 配送記録は送信前に書く。受け手がエンベロープを見た瞬間から照会できる必要があり、
                // 「届いたのに記録がまだ無い」瞬間を作らないため（逆の「記録はあるが届かなかった」は
                // 誰も ID を知らないので無害）。
                // **try の中に置く**: この INSERT が例外を投げた場合（ロック競合等）も、
                // 下の catch が発行済みの札を片付ける安全網の対象に含める（外に置くと
                // submitted の孤児行が残る＝#187 で塞いだのと同じ穴が別の呼び出しで再発する）。
                await deliveryRepository.CreateAsync(new DeliveryRecord(
                    deliveryId,
                    requester?.SessionId.ToString(),
                    requester?.GetDisplayName(),
                    info.SessionId.ToString(),
                    info.GetDisplayName(),
                    effectiveContextId,
                    DateTime.UtcNow));

                outcome = await deliveryService.SendAsync(
                    info, deliveredMessage, effectiveContextId, requester?.SessionId);
            }
            catch
            {
                // 想定外の例外（終了時の Dispose と競合した SemaphoreSlim 等）でも、発行したばかりの
                // 札を必ず片付ける。submitted は終端状態でなく TTL 掃除の対象外なので、
                // 残すと永久に消えない行になる。DeliveryOutcome での分岐だけに頼ると
                // この経路が抜ける（旧実装が持っていた安全網の復元）。
                // 配送記録は消さない（Pending のまま残っても上限の数えに入らず TTL で消える）。
                if (issuedContextId != null)
                    await contextRepository.DeleteAsync(issuedContextId);
                throw;
            }

            if (outcome == DeliveryOutcome.Rejected)
            {
                // 一度も書いていないので、発行したばかりの札を片付ける。submitted は終端状態でなく
                // TTL 掃除の対象外のため、残すと永続の孤児になる。
                if (issuedContextId != null)
                    await contextRepository.DeleteAsync(issuedContextId);

                // 配送記録も片付ける。Rejected は「一度も書いていない」ことが確定しており
                // （Failed と違い部分配送の可能性がない）、この ID は結果にもエンベロープにも
                // 出ないので誰も参照できない。残すと、Rejected を量産して総数上限の掃除を
                // 走らせ、**真正な記録を押し出す**攻撃に使える（押し出された配送は
                // get_delivery で「偽装か期限切れ」と誤判定される）。
                await deliveryRepository.DeleteAsync(deliveryId);

                // ここに来る原因は待ち行列の上限だけ（未起動は手前で別メッセージにして弾いている）。
                return new SendResult(false,
                    $"『{info.GetDisplayName()}』の配送待ちが上限に達しているため受理できません。" +
                    $"相手が入力待ちのまま溜まっている状態なので、ユーザーに宛先セッションの" +
                    $"許可/選択待ちを解消してもらってから送り直してください。");
            }

            // ここから先は記録を残すことが確定（Delivered / Queued / Failed=部分配送の可能性あり）。
            // Pending → Committed へ確定してから上限掃除。掃除は Committed だけを数えるので、
            // 並行中の他送信の未確定行が判断に混ざらない（配送時間に上限が無いため、
            // 時刻ベースの猶予では防ぎきれない＝レビュー指摘）。
            await deliveryRepository.CommitAsync(deliveryId);
            await deliveryRepository.PruneToCapAsync();

            if (outcome == DeliveryOutcome.Failed)
            {
                // **札は消さない**。本文は contextId 入りのエンベロープごと相手の入力欄へ
                // 届いている可能性があり、人間がそれを送信したら受け手はこの ID へ書きに来る。
                // 消してしまうと「存在しない contextId」を渡したことになる。
                // 代わりに failed を記録して、届いたか確認できない旨を残す
                // （終端状態なので TTL 掃除の対象にもなり、孤児にはならない）。
                if (issuedContextId != null)
                {
                    await contextRepository.UpdateAsync(
                        issuedContextId,
                        $"宛先「{info.GetDisplayName()}」への書き込みが途中で失敗しました。届いたかどうか確認できません" +
                        "（本文だけが相手の入力欄に残っている可能性があります）。" +
                        "人間が入力欄を確認して送信した場合、受け手はこの札へ結果を書きます。",
                        "failed", null, SessionDeliveryService.SystemWriterName);
                }

                // 「次に取るべき行動」が Rejected と正反対なので、文面を分ける。
                // ここで同じ内容を再送させると本文が二重に連結される。
                return new SendResult(false,
                    $"宛先への書き込みが途中で失敗しました: {info.GetDisplayName()}。" +
                    $"**同じ内容をそのまま再送しないでください**（本文だけが相手の入力欄に " +
                    $"届いている可能性があり、再送すると二重に連結されます）。" +
                    $"ユーザーに宛先セッションの状態を確認してもらい、必要なら人間の目で入力欄を" +
                    $"片付けてから送り直してください。" +
                    (issuedContextId == null
                        ? string.Empty
                        : $" contextId={issuedContextId} は発行済みで、本文にも載っています。" +
                          "人間が送信した場合は受け手がこの札へ結果を書くので、get_context で確認できます。"),
                    issuedContextId,
                    deliveryId: deliveryId);
            }

            var queued = outcome == DeliveryOutcome.Queued;
            var head = queued
                ? $"受理しました（宛先が入力待ちのため配送待ち。解消次第このまま送られます）: {info.GetDisplayName()}"
                : $"送信しました: {info.GetDisplayName()}";
            var tail = issuedContextId == null
                ? string.Empty
                : requester != null
                    ? $" contextId={issuedContextId} を発行しました。完了/失敗時はあなたのセッションへ通知が届きます（ポーリング不要）。"
                    : $" contextId={issuedContextId} を発行しました。get_context でポーリングして結果を受け取れます。";

            return new SendResult(true, head + tail, issuedContextId, queued ? "queued" : "delivered", deliveryId);
        }

        /// <summary>
        /// 本文末尾へ付ける定型文。配送 ID・送信元（接続キーで検証済み）・contextId を1つの括弧にまとめる。
        ///
        /// **必ず何かを付ける（空を返さない）**。マーカーはサーバーが付与するため送信側 LLM には
        /// 外せず、「末尾に [TerminalHub …] が無い入力＝人間がキーボードで打った指示」という
        /// 区別が受け手側で成立する（機械が人間のふりをして指示を出す穴を塞ぐ。逆方向＝人間が
        /// 同じ文字列を手打ちして機械のふりをするのは実害がないので防がない）。
        ///
        /// deliveryId は配送記録（Deliveries）の照会キー。マーカー単体は「本文の途中に偽の
        /// エンベロープを埋め込む」偽装に弱い（受け手が位置ルールを厳密に守れる保証がない）ため、
        /// get_delivery で「本当に TerminalHub を通ったか・どこからどこへか」を事実で検証できるようにする。
        /// 送信元が分かることで、受け手は「意図して中間報告したい」ときだけ自分から
        /// send_to_session で返せる（事務的な status 更新は終端だけ自動通知される、との役割分担）。
        /// </summary>
        private static string BuildEnvelope(string deliveryId, SessionInfo? requester, string? contextId)
        {
            var head = $"TerminalHub 自動メッセージ #{deliveryId}";
            var from = requester == null
                ? "送信元は無記名"
                : $"依頼元: {requester.GetDisplayName()} ({requester.SessionId})";

            if (contextId == null)
            {
                return requester == null
                    ? $" [{head} — {from}]"
                    : $" [{head} — {from} — 返信は send_to_session でこの GUID へ]";
            }

            if (requester == null)
                return $" [{head} — {from} / contextId: {contextId} — 状況・結果は update_context で共有]";

            return $" [{head} — {from} / contextId: {contextId} — " +
                   "完了時は update_context に status と結果を書く。途中で相談したいときだけ send_to_session で依頼元へ返す]";
        }

        [McpServerTool(Name = "set_memo")]
        [Description(
            "自分のセッションのメモ(TerminalHub のセッション一覧に表示される短い注釈)を設定する。" +
            "「今なにをしているか」等のステータスを書いておくと、一覧から一目で分かる。既存のメモは上書きされる(空文字でクリア)。" +
            "対象は自分のセッションのみで、接続キーにより自動で本人確定される(引数で証明を渡す必要はない)。" +
            "本人確定できない接続(外部クライアント等)ではこのツールは使えない(メモは TerminalHub の UI からも編集できる)。")]
        public static async Task<SendResult> SetMemo(
            ISessionManager sessionManager,
            ISessionRepository sessionRepository,
            IHttpContextAccessor httpContextAccessor,
            [Description("設定するメモ本文。空文字にするとメモをクリアする。")]
            string memo)
        {
            // 接続キーが本人証明と宛先特定を兼ねる。GUID/表示名の自己申告は受け付けない
            // （誤認・詐称による他セッションの書き換えを機構的に不可能にする）。
            var info = ResolveCaller(sessionManager, httpContextAccessor);
            if (info == null)
                return new SendResult(false, IdentityRequiredMessage);

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
        /// 本人確定が必須のツールで、接続キーが無かったときの共通メッセージ。
        /// </summary>
        private const string IdentityRequiredMessage =
            "呼び出し元セッションを特定できません。TerminalHub 起動のセッションなら通常は接続キー" +
            "（TerminalHub が配る MCP 設定のヘッダ）で自動的に本人確定されるため、何も渡す必要はありません。" +
            "この接続にキーが無いなら、あなたは古い設定の残骸経由か、セッションを持たない外部クライアントであり、" +
            "このツールは使えません（残骸経由の場合、TerminalHub の設定で「TerminalHub MCP」を ON にして" +
            "セッションを起動し直せばキー付きで接続される）。";

        /// <summary>
        /// 呼び出し元セッションの特定。**接続キー（HTTP ヘッダ＝トランスポート層）だけ**で行う。
        /// TerminalHub が配った MCP 設定で接続しているセッションは何も渡さなくても本人が確定し、
        /// キーの無い接続（外部クライアント・旧 .mcp.json 残骸経由）は一律で無記名（null）。
        /// 旧方式の proof 引数（環境変数 TERMINALHUB_SESSION_PROOF をモデルに運ばせる）は
        /// 互換を残さず全面撤去した（秘密のシェル出力が安全分類器に弾かれて無言で無記名に
        /// 劣化する実害があり、モデルに秘密を持たせる構造ごと消した。2026-08-02）。
        /// </summary>
        private static SessionInfo? ResolveCaller(
            ISessionManager sessionManager, IHttpContextAccessor httpContextAccessor)
        {
            var key = httpContextAccessor.HttpContext?.Request.Headers[TerminalConstants.McpSessionKeyHeader]
                .FirstOrDefault();
            return sessionManager.ResolveByMcpConnectionKey(key);
        }

        // ---- 自己紹介カード（「何ができるか」の自己申告・A2A Agent Card のローカル版） ----
        //
        // 設計（壁打ちで確定）:
        // - memo の姉妹機能。memo=「今なにをしているか」(動的) / card=「何ができるか」(静的・長命)。
        // - 用語: A2A の Agent Card の description/skills に相当するものを card と呼ぶ。
        //   A2A の capabilities フィールドはプロトコル機能宣言(streaming 等)で別物のため、
        //   用語衝突を避けて capabilities という名前は使わない。
        // - set は「自分のみ」。当初は GUID 自己申告＋説明文の「仕様上の契約」→ proof 引数の検証
        //   → 接続キー（MCP 設定のヘッダ）の検証へと段階的に格上げした。キーはそのセッションの
        //   CLI に配られた設定だけが知る値なので、誤認・詐称による他セッションの書き換えは
        //   機構的に不可能。旧 proof 引数は互換を残さず撤去済み（2026-08-02）。
        //   カードは自己申告＝「本人がそう名乗っている」以上の保証はしない、という A2A と同じ信頼モデル。
        // - get は誰のカードでも読める（宛先選びの当たりを付ける用途）。

        /// <summary>get_card の結果。</summary>
        public record CardResult(bool success, string? name, string? card, string message);

        [McpServerTool(Name = "set_card")]
        [Description(
            "自分のセッションの自己紹介カード(「何ができるか」の短い自己申告。他エージェントが宛先選びに使う)を設定する。" +
            "対象は自分のセッションのみで、接続キーにより自動で本人確定される(引数で証明を渡す必要はない)。" +
            "本人確定できない接続(外部クライアント等)ではこのツールは使えない。" +
            "他セッションのカードは書き換えられない(本人確定は接続単位=自己申告制の機構的担保)。" +
            "全体書き換え(部分更新なし)・空文字でクリア。数行の短文を想定(宛先選びの広告であって詳細ドキュメントではない)。")]
        public static async Task<SendResult> SetCard(
            ISessionManager sessionManager,
            ISessionRepository sessionRepository,
            IHttpContextAccessor httpContextAccessor,
            [Description("設定するカード本文(「何ができるか」の数行の短文)。空文字でクリア。既存の内容は全体上書きされる。")]
            string card)
        {
            // 接続キーが本人証明と宛先特定を兼ねる。GUID の自己申告は受け付けない。
            var info = ResolveCaller(sessionManager, httpContextAccessor);
            if (info == null)
                return new SendResult(false, IdentityRequiredMessage);

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
        //   一覧（ID なし列挙）は作らない（調整ロジックはクライアント側）。
        // - **終端 status の書き込み時の完了通知だけは持つ**（当初は「作らない」に入れていたが撤回。
        //   再書き込み=再完了の続報も配る。依頼元自身の書き込みは除く=自己再通知ループ防止）。
        //   依頼元がセッションのときポーリングは原理的に成立しない（送信直後にターンが終わるので
        //   回す主体がいない）ため。禁じているのは「複数の結果を待ち合わせて集める」ことで、
        //   これは「札に購読者が1人いる。終端 status が書かれたら1通配る」というルーティング1本。
        // - contextId は capability 兼用（知っている=読み書きできる）。A2A の contextId と同じく
        //   最初の送信時にサーバーが発行して返す。status の語彙は A2A TaskState をそのまま使う。

        private static readonly string[] AllowedContextStatuses =
            { "submitted", "working", "completed", "failed", "canceled" };

        // 終端 status の定義と通知要否の判定は ContextNotifyPolicy が正本
        // （書き込み成功時は毎回通知・依頼元自身は除く。テスト可能にするため分離）。

        /// <summary>
        /// get_context の結果。updatedBy は最終書き込み者の検証済みセッション名
        /// （本人確定済みの接続から書かれた場合のみ。null なら無記名＝外部クライアント等の書き込み）。
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
            "依頼側が本人確定済みのセッション(TerminalHub 起動のセッションなら通常はそう)の場合、札が終端状態" +
            "(completed / failed / canceled)になった時点で自動通知が届くのでポーリングは不要。" +
            "本人確定の無い外部クライアントは、これをポーリングして進捗・結果を受け取る。" +
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
            "セッション内からの書き込みは接続キーにより自動で記名される(「どのセッションが書いたか」が" +
            "検証済みで記録され、依頼側が信頼できる。引数で証明を渡す必要はない)。" +
            "本人確定できない接続からでも書けるが無記名になる(外部クライアント用)。")]
        public static async Task<SendResult> UpdateContext(
            ISessionManager sessionManager,
            IContextRepository contextRepository,
            ISessionDeliveryService deliveryService,
            IHttpContextAccessor httpContextAccessor,
            [Description("対象の contextId(受け取ったメッセージ末尾に付与されている)。")]
            string contextId,
            [Description("状況・結果の要約(全体上書き)。長いものはファイルパスを書く。")]
            string summary,
            [Description("状態。submitted / working / completed / failed / canceled のいずれか。省略時は状態を変えず要約だけ更新。")]
            string? status = null)
        {
            if (!string.IsNullOrEmpty(status) &&
                !AllowedContextStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
                return new SendResult(false,
                    $"status が不正です: {status}。使えるのは {string.Join(" / ", AllowedContextStatuses)} のみ。");

            // 書き込み元の検証。接続キー（ヘッダ）のみで行い、キーの無い接続は無記名で通す
            // (外部クライアント用。アクセス制御ではなく検証済み署名として機能する)。
            var writer = ResolveCaller(sessionManager, httpContextAccessor);

            var newStatus = status?.ToLowerInvariant();
            var updated = await contextRepository.UpdateAsync(
                contextId ?? "",
                summary ?? string.Empty,
                newStatus,
                writer?.SessionId.ToString(),
                writer?.GetDisplayName());
            if (!updated.Found)
                return new SendResult(false,
                    $"contextId が見つかりません: {contextId}。終端状態(completed等)から一定期間で自動削除されます。");

            // 札は存在するが他の書き込みと競合し続けた場合。「見つかりません」と混ぜると
            // 原因も対処も誤って伝わるので分けて返す（こちらは再試行すれば通る）。
            if (updated.Conflicted)
                return new SendResult(false,
                    $"他の書き込みと競合したため更新できませんでした: {contextId}。" +
                    "札は存在しているので、そのまま同じ内容で再試行してください。");

            // 黙って無視すると「書けたつもり」で先へ進んでしまうので、拒否は明示して返す。
            if (updated.RewindRejected)
                return new SendResult(false,
                    $"この札は既に終端状態(completed / failed / canceled)なので、{status} へは戻せません。" +
                    "依頼が閉じたあとに進行中へ巻き戻すと、依頼元の認識と食い違い、自動削除の対象からも外れます。" +
                    "続きの作業が要るなら、新しい依頼として contextId=\"new\" で発行し直してください。");

            // 依頼元への自動通知は**終端 status の書き込みが成功したとき**。
            // working への遷移でも撃つと、依頼元は「着手した」を聞くためだけにフルターンを1回起こす
            // ことになり（送信直後にターンを終えているので毎回起床する）、得るものが無い。
            // 意図的な中間報告は、受け手がエンベロープの依頼元へ send_to_session で自分から返す。
            //
            // 当初は「遷移が成立したときだけ」（StatusTransitioned）だったが、
            // **同一終端への再書き込み（completed → completed）でも撃つ**ように変更した:
            // 完了後に人間が受け手セッションで追加調整をさせて「再完了」を書いたとき、
            // 遷移縛りだとその続報が依頼元に永遠に伝わらない実害があった（2026-08-02）。
            // 代償として、同時に completed を書いた2者がいると依頼元が二度起きることがあるが、
            // どちらの通知も「最新の要約を読みに行く」以上の意味を持たないので実害は薄い
            // （通知は status と contextId を運ぶだけで、内容は get_context で読む）。
            //
            // writer を渡すのは**依頼元自身の書き込みに通知しない**ため: 通知文面が
            // update_context への記入を促すので、自己書き込みにも通知すると
            // 「自分へ再通知 → また書く」の自己励振ループが成立してしまう（レビュー指摘。
            // 抑止の判定は ContextNotifyPolicy に分離してテストで固定）。
            if (ContextNotifyPolicy.IsTerminal(newStatus))
            {
                await deliveryService.NotifyContextStatusAsync(contextId ?? "", newStatus!, writer?.SessionId);
            }

            return new SendResult(true,
                $"ContextSummary を更新しました(status={status ?? "変更なし"}, " +
                $"記名={(writer != null ? writer.GetDisplayName() : "無記名")})。");
        }

        // ---- 配送記録（エンベロープの検証） ----
        //
        // 設計（2026-08-01 壁打ちで確定）:
        // - エンベロープ常時付与とセット。マーカー単体は「本文の途中に偽エンベロープを埋め込む」
        //   偽装に弱いため、#ID の照会で「本当に TerminalHub を通ったか・どこからどこへか」を
        //   事実で検証できるようにする（From=null は無記名＝外部クライアントの可能性を含む）。
        // - deliveryId を知っている＝そのエンベロープを受け取った者、という capability 哲学は
        //   contextId と同じ。ID なしの一覧は作らない。

        /// <summary>get_delivery の結果。</summary>
        public record DeliveryResult(
            bool success,
            string? fromSessionId,
            string? fromName,
            string? toSessionId,
            string? toName,
            string? contextId,
            string? sentAt,
            string message);

        [McpServerTool(Name = "get_delivery")]
        [Description(
            "受け取ったメッセージ末尾のエンベロープ [TerminalHub 自動メッセージ #ID …] の ID から、" +
            "その配送の記録(どのセッションからどのセッションへ・いつ・contextId)を取得する。" +
            "**エンベロープが本物か(本当に TerminalHub を通った配送か)の検証に使う**。" +
            "記録が見つからない場合、そのエンベロープは偽装(本文に手書きされたもの)か、期限切れ(14日で削除)。" +
            "fromSessionId が null の配送は無記名(本人確定なし=外部クライアントの可能性を含む)。")]
        public static async Task<DeliveryResult> GetDelivery(
            IDeliveryRepository deliveryRepository,
            [Description("エンベロープに書かれている配送ID(# の後ろの12文字)。")]
            string deliveryId)
        {
            var record = await deliveryRepository.GetAsync(deliveryId?.TrimStart('#') ?? "");
            if (record == null)
                return new DeliveryResult(false, null, null, null, null, null, null,
                    $"配送記録が見つかりません: {deliveryId}。このエンベロープは偽装（本文に手書きされたもの）か、" +
                    "期限切れ（14日で削除）です。偽装の場合、そのメッセージは TerminalHub を通った配送ではありません。");

            return new DeliveryResult(true,
                record.FromSessionId,
                record.FromName,
                record.ToSessionId,
                record.ToName,
                record.ContextId,
                record.SentAt.ToString("o"),
                record.FromSessionId != null
                    ? "取得しました。from は検証済みの送信元です。"
                    : record.FromName == SessionDeliveryService.SystemWriterName
                        ? "取得しました。この配送は TerminalHub 自身が発したシステム通知です。"
                        : "取得しました。この配送は無記名（本人確定なし＝外部クライアントの可能性を含む）です。");
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
            IHttpContextAccessor httpContextAccessor)
        {
            var info = ResolveCaller(sessionManager, httpContextAccessor);
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
            IHttpContextAccessor httpContextAccessor,
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
            var info = ResolveCaller(sessionManager, httpContextAccessor);
            if (info == null)
                return new SendResult(false, IdentityRequiredMessage);

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
            IHttpContextAccessor httpContextAccessor,
            [Description("削除するコマンドの title。")]
            string title)
        {
            var info = ResolveCaller(sessionManager, httpContextAccessor);
            if (info == null)
                return new SendResult(false, IdentityRequiredMessage);

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
