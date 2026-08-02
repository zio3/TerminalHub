# ws 配送設計メモ（イベント配送への移行・段階計画）

2026-08-02 検討確定。`send_to_session`・完了通知の配送を「ConPTY へのキー入力」から
「イベント」へ移すための設計。**v1（Claude Code 向け）と v2 候補（Codex 向け）は別工事**として進める。

## 背景と確定済みの事実（実測・調査済み）

- Claude Code の組み込みツール **Monitor に ws ソース**がある。実測（2026-08-02）:
  - `ws://`（非TLS・localhost）が通る。バリデーション無し
  - **複数行フレームが1イベントのまま**届く（日本語・絵文字も素通し）
  - イベントは「ユーザー入力ではない」とハーネスが構造的に区別する
  - close コード（code + reason）が最終イベントとして表面化
  - レート制限あり（firehose は監視ごと停止される）→ 依頼粒度なら問題なし
- Monitor の ws には **カスタムヘッダを付ける口が無い**（スキーマは `url` / `protocols` のみ）
  → MCP の `X-TerminalHub-Session-Key` 方式は流用不可
- **Codex CLI（対話型）に Monitor 相当は無い**（Codex 0.146.0・本人調査 2026-08-02。
  MCP 通知・常駐 exec・notify・hooks も受信口にならないことを代替案込みで確認済み）
- ただし **`codex app-server`**（別実行形態）に正式なプッシュ経路がある:
  - `codex app-server --listen ws://127.0.0.1:<port>`（JSON-RPC。1 text frame = 1 message）
  - クライアントから `turn/steer`（進行中ターンへ追加入力）/ `turn/start`（アイドル時に新ターン）
  - 既存 TUI は `codex --remote ws://…` で同じ app-server に接続できる（人間の画面は維持）
  - 認証: `--ws-auth capability-token` 等（Bearer）。WS listen は公式に **experimental and unsupported**
  - `turn/steer` は active turn 無し／turnId 不一致で失敗 → 通知ストリームでの状態追跡が必要

## トポロジ（交換手モデル）

交換手（配送の中継・エンベロープ・記録）は今も TerminalHub。変わるのは「最後の届け方」だけ。

```
send_to_session / 完了通知
        │
        ▼
  SessionDeliveryService（交換手）
        │  宛先のイベントチャネルは生きているか？
        ├─ Claude(v1): セッションが引いてきた内線に流す（Monitor が TerminalHub へ接続）
        ├─ Codex(v2候補): TerminalHub が繋ぎに行った線に turn/steer で流す（app-server）
        └─ 無し: 従来どおり ConPTY キー入力（配送キュー込み）＝フォールバック
```

- 接続の向きは Claude と Codex で逆（Claude=セッションがクライアント / Codex=TerminalHub がクライアント）だが、
  **配送インターフェースの形は同じ**にできる。抽象は最初から両対応で切り、v1 は Claude backend のみ実装する。
- 線の維持コストは非対称: Claude 側は張る・張り直すのがセッションの責任（切れてもフォールバックが吸う）。
  Codex 側は接続確立・再接続・turn 状態追跡まで TerminalHub の責任＋起動形態の変更が要る。

## v1: Claude Code 向け（Monitor 経由）

- **エンドポイント**: `ws://localhost:{port}/mcp-events`（本体に WebSocket ミドルウェアで同居）
- **認証: サブプロトコル**（確定）。`Monitor({ws: {url, protocols: ["thub-evt.<wsトークン>"]}, persistent: true})`
  の形で `Sec-WebSocket-Protocol` ヘッダに載る。サーバーは検証して選択プロトコルをエコー、不一致は握手拒否。
  URL に秘密を載せない（ログ残留回避）
- **ws トークンは MCP 接続キーとは別の秘密**（`SessionInfo` にもう1本・非永続。用途が違う秘密を使い回さない）。
  モデルには読ませず、instructions の「この接続について」付記に**張るための Monitor 呼び出しを丸ごと**書く
  （コピペで張れる形。トークンがモデルのコンテキストに載る弱点は許容: 漏れても被害は
  「そのセッション宛イベントの盗み見」まで）
- **スコープ: 配送経路の置き換えのみ**（確定）。`send_to_session` の仕様は不変
  （**1行制限・ファイルパス運用も維持**。複数行解禁は運用を見て v2 以降で判断）
- 配送分岐: 生きた ws 接続あり → エンベロープ・#ID 込みの現行文面をテキストフレーム1発。
  送信成功を確認してから配送確定（失敗→ ConPTY 経路へフォールバック。二重配送しない）
- ws 宛先に配送キューは不要（許可待ち中でも届く。「届く」と「処理される」は別＝応答は待ち解消後）
- `send_to_session` の結果 `delivery` に `"delivered_event"` を追加（送信側が経路を知れる）
- **スラッシュコマンドは常に ConPTY**（ターミナルで実行させる性質のため）
- 張り忘れ・compaction 後の persistent 生存（未確認）・切断 → すべてフォールバックが吸収（段階導入）
- 未検証で残る点: instructions に書いた Monitor 呼び出しをモデルが安定して張ってくれるか（実機で確認）

## v2 候補: Codex 向け（app-server 経由・別 PoC）

**1セッションだけ**で spike してから判断する:

1. `codex app-server --listen ws://127.0.0.1:<port>` ＋ TUI を `codex --remote` で起動する構成に変える
2. TerminalHub が WS クライアントとして接続（capability-token・loopback 限定）
3. 通知ストリームで thread/turn 状態を追跡し、active なら `turn/steer` / idle なら `turn/start`
4. 確認事項: 複数クライアント競合の実挙動（公式説明が薄い）・experimental の安定度・
   ConPTY(表示) と app-server(制御) の二重化がセッション管理と衝突しないか

成功すれば Codex もキー入力依存を外せる。失敗してもフォールバックがあるので v1 には影響しない。

## 据え置き

- 外部クライアント: 従来どおり `get_context` ポーリング
- 通知の粒度: 依頼・完了・失敗のみ（進捗の垂れ流しはレート制限で不可）
- リアルタイム性の位置づけ: 「配送遅延ほぼゼロの**会話**基盤」であって文字ストリームではない
  （律速はモデルのターン時間）

## 経緯

- 発端: zatsudan セッションの調査提案（Monitor ws ソースの発見）→ TerminalHub セッションが実測評価
- 「まず #187/#188/#189 の実運用を見てから」の整理を経て、2026-08-02 に設計確定
- Codex 側の調査は Codex セッション本人に send_to_session + contextId で依頼（無人ラリー）した結果
