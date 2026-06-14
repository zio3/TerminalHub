# MQTT リモート起動 API仕様

## 概要

外出先（スマホ）からTerminalHubにMQTT経由でリクエストを送り、Claude Codeセッションから Remote Control URL を取得する。

URL 取得方式は Claude Code v2.1.162 の仕様変更に伴い、以下に変更されている:

- **旧方式** (`--remote-control` フラグ起動): URL は起動時の startup message として stdout に出力されていた。
- **新方式** (`/remote-control` スラッシュコマンド送信): 起動済みの ClaudeCode セッション (未起動なら遅延起動) に `/remote-control` を送り、応答 1 秒待機後の文字列で状態分岐:
    - **直接 URL が出る** = 既に Remote Control 接続済みだったので status panel が即時オープン → URL 取得
    - **`connecting…` が出る** = 新規接続中。接続完了を待ってもう一度 `/remote-control` を送り、status panel から URL 取得
- URL は TerminalHub 側にキャッシュせず、要求の都度セッションへ問い合わせる。

通信はRSA鍵交換で確立したセッション鍵によるAES-256-GCMで暗号化され、ワンタイムnonceによるリプレイ攻撃防止を併用する。セキュリティ設計の詳細は [mqtt-security-design.md](mqtt-security-design.md) を参照。

## MQTTトピック

```
claude/{TopicGUID}/request   — クライアント → TerminalHub
claude/{TopicGUID}/response  — TerminalHub → クライアント
```

- `TopicGUID`: TerminalHubの設定画面で有効化時に自動生成されるGUID
- ブローカー: `vps3.zio3.net:1883`（TCP）
- ACL: `ClaudeLauncher` ユーザーは `claude/#` 配下のみアクセス可能

## アクセスURL

```
https://claude-launcher.azurewebsites.net/{TopicGUID}
```

## 暗号化

### セッション鍵交換（RSA）

handshakeアクションでTerminalHubがセッション鍵を生成し、Webサーバーの公開鍵でRSA暗号化して返却する。Webサーバーが秘密鍵で復号してセッション鍵を取得。

### 通信暗号化（AES-GCM）

鍵交換後は両方向ともセッション鍵（32byte）でAES-256-GCM暗号化。

### メッセージフォーマット

暗号化メッセージ:
```json
{ "encrypted": "Base64(AES-256-GCM([12byte IV][16byte Tag][暗号文]))" }
```

平文メッセージ（ping / handshake / nonce のみ）:
```json
{ "action": "ping" }
{ "action": "handshake" }
{ "action": "nonce" }
```

## リクエスト

### 平文リクエスト（認証不要）

#### 疎通確認

```json
{ "action": "ping" }
```

TerminalHubがMQTT接続中であればバージョン情報を返す。

#### セッション鍵交換

```json
{ "action": "handshake", "requestId": "クライアント生成のランダムID" }
```

TerminalHubがセッション鍵を生成し、RSA暗号化して返却する。`requestId` はレスポンスにそのまま返され、クライアントは自分のリクエストへの応答か判別できる。以降の暗号化通信にはこのセッション鍵と返却された `handshakeId` を使用する。セッション鍵の有効期限は5分（TerminalHub側で生成時刻からの経過で判定）。期限切れ後はhandshakeを再送して鍵を再取得する。

#### nonce発行

```json
{ "action": "nonce" }
```

暗号化リクエストに含めるワンタイムnonceを発行する。nonceは1回使用で無効化され、未使用でも30秒で自動失効する。

### 暗号化リクエスト

以下はすべて暗号化して `{"encrypted": "..."}` として送信する。復号後のペイロード:

#### セッション一覧取得

```json
{ "action": "list", "handshakeId": "...", "nonce": "発行されたnonce" }
```

パスワード設定時:
```json
{ "action": "list", "handshakeId": "...", "nonce": "...", "passwordHash": "SHA256(パスワード)" }
```

#### セッション起動

```json
{ "action": "launch", "sessionId": "セッションGUID", "handshakeId": "...", "nonce": "..." }
```

パスワード設定時:
```json
{ "action": "launch", "sessionId": "...", "handshakeId": "...", "nonce": "...", "passwordHash": "SHA256(パスワード)" }
```

対象セッションが未起動なら遅延初期化される。既に起動済みの場合はそのセッションに `/remote-control` を送って URL を取得する。

**Busy 判定**: 対象セッションの ClaudeCode が処理中 (LLM 思考中・ツール実行中など) の場合は `/remote-control` を送らず、エラーで返す（誤って処理割り込みを起こさないため）。

#### リモートセッション切断

```json
{ "action": "disconnect", "sessionId": "セッションGUID", "handshakeId": "...", "nonce": "..." }
```

パスワード設定時:
```json
{ "action": "disconnect", "sessionId": "...", "handshakeId": "...", "nonce": "...", "passwordHash": "SHA256(パスワード)" }
```

SessionInfo の `RemoteControlUrl` 表示状態をクリアする。

> **注**: 新方式では既存セッションを使い回しているため、disconnect は ConPty には触らない（旧方式のように専用プロセスを kill することはない）。リモート接続自体を切るには、TerminalHub 側のセッションで `/remote-control` を手動で再送信し、status panel で `Disconnect this session` を選ぶ必要がある。

## レスポンス

### 平文レスポンス

#### 疎通確認応答

```json
{ "action": "pong", "version": "1.0.44" }
```

| フィールド | 型 | 説明 |
|-----------|------|------|
| `version` | string | TerminalHubのバージョン |

#### セッション鍵交換応答

```json
{ "action": "handshake", "requestId": "リクエストで送ったID", "handshakeId": "サーバー生成ID", "sessionKey": "Base64(RSA-OAEP-SHA256暗号化されたセッション鍵)" }
```

| フィールド | 型 | 説明 |
|-----------|------|------|
| `requestId` | string | リクエストの相関子（クライアントが送った値をそのまま返却） |
| `handshakeId` | string | セッション識別子（以降の暗号化リクエストに含める） |
| `sessionKey` | string | RSA暗号化されたセッション鍵（Base64）。秘密鍵で復号して32byteのAES鍵として使用 |

#### nonce発行応答

```json
{ "action": "nonce", "nonce": "ランダム32文字の16進数" }
```

#### エラー

```json
{ "action": "error", "message": "public key not configured" }
{ "action": "error", "message": "handshake failed" }
{ "action": "error", "message": "session not established" }
{ "action": "error", "message": "session expired" }
{ "action": "error", "message": "session mismatch" }
{ "action": "error", "message": "unauthorized" }
{ "action": "error", "message": "encryption required" }
```

### 暗号化レスポンス

以下はすべて `{"encrypted": "..."}` として返却される。復号後のペイロード:

#### 一覧返却

```json
{
  "action": "list",
  "sessions": [
    {
      "id": "guid",
      "name": "TerminalHub",
      "memo": "メモ内容またはnull",
      "type": "ClaudeCode",
      "remoteControlUrl": "https://claude.ai/code/...またはnull"
    }
  ]
}
```

| フィールド | 型 | 説明 |
|-----------|------|------|
| `id` | string | セッションGUID |
| `name` | string | 表示名（未設定時はフォルダ名） |
| `memo` | string? | セッションのメモ（未設定時はnull） |
| `type` | string | "ClaudeCode"固定 |
| `remoteControlUrl` | string? | Remote Control URL（起動済みの場合のみ、未起動時はnull） |

※ ClaudeCodeセッションのみ返却。ソート順はユーザー設定に準拠（ピン留め優先→ソートモード）。

#### 起動開始

```json
{ "action": "launch", "status": "started", "sessionId": "guid" }
```

#### URL取得成功

```json
{ "action": "launch", "status": "ready", "sessionId": "guid", "url": "https://claude.ai/code/..." }
```

#### 切断完了

```json
{ "action": "disconnect", "status": "ok", "sessionId": "guid" }
```

#### エラー（暗号化レスポンス内）

```json
{ "action": "error", "message": "invalid or expired nonce" }
{ "action": "error", "message": "unauthorized" }
{ "action": "error", "message": "sessionId required" }
{ "action": "error", "message": "launch failed or timeout" }
{ "action": "error", "message": "unknown action" }
```

##### `launch failed or timeout` の内訳

TerminalHub 側の `RemoteLaunchService.LaunchRemoteControlAsync` が null を返したとき、MQTT 応答は上記の単一メッセージにまとめられている。内部的には以下のいずれかが発生している:

| 内部状態 | 発火条件 | 推奨対処 |
|---|---|---|
| セッション未存在 | リクエストの `sessionId` に対応する SessionInfo が無い | 一覧 (`list` アクション) を取り直す |
| ターミナルタイプ非対応 | 対象が ClaudeCode 以外 (Terminal / GeminiCLI / CodexCLI / Antigravity / Grok) | ClaudeCode セッションを選ぶ |
| セッション起動失敗 | ConPty 取得失敗 (`GetSessionAsync` が null。プロセス起動エラーなど) | TerminalHub 側のログを確認 |
| **Busy** | `SessionInfo.ProcessingStatus` が非空 (LLM 思考中・ツール実行中) | 処理完了を待って再試行 |
| **応答なし** | `/remote-control` 送信後 1 秒経過しても URL も `connecting…` も検出されない | TerminalHub 側のログを確認。Claude Code 側の `/remote-control` コマンド未サポート版の可能性 |
| **URL タイムアウト** | `connecting…` 検出 → 接続完了待ち → 2 回目の `/remote-control` 送信後、URL を `timeoutSeconds` (60秒) 以内に検出できず | 再試行。Anthropic 側の認証/接続問題の可能性 |
| 内部例外 | RemoteLaunchService 内部で予期せぬ例外 | TerminalHub 側のログを確認 |

将来的に MQTT 応答側でこれらを差し替えてより具体的なメッセージを返すことを検討中。

## タイムアウト

- 1 回目の `/remote-control` 送信後の応答待ち: 1 秒固定 (この間に URL も `connecting…` も検出されなければ「応答なし」エラー)
- `connecting…` 検出後の接続完了待ち: 3 秒固定
- 2 回目の `/remote-control` 送信後の URL 検出待ち: 60 秒 (`timeoutSeconds` パラメーター)
- いずれかでタイムアウトすると `{"action":"error","message":"launch failed or timeout"}` が返る

## シーケンス

```
クライアント                    MQTT                    TerminalHub
    │                           │                           │
    │  {"action":"ping"}        │                           │
    ├──────────────────────────►│──────────────────────────►│
    │  {"action":"pong",        │◄──────────────────────────┤
    │   "version":"x.x.x"}     │                           │
    │◄──────────────────────────┤                           │
    │                           │                           │
    │  {"action":"handshake",   │                           │
    │   "requestId":"req1"}     │                           │
    ├──────────────────────────►│──────────────────────────►│ セッション鍵生成
    │  {"action":"handshake",   │◄──────────────────────────┤ RSA暗号化
    │   "requestId":"req1",     │                           │
    │   "handshakeId":"hs1",    │                           │
    │   "sessionKey":"..."}     │                           │
    │◄──────────────────────────┤                           │
    │                           │                           │
    │  Webサーバーが秘密鍵で     │                           │
    │  sessionKeyを復号          │                           │
    │  handshakeIdを保持         │                           │
    │                           │                           │
    │  {"action":"nonce"}       │                           │
    ├──────────────────────────►│──────────────────────────►│ nonce生成
    │  {"nonce":"abc123..."}    │◄──────────────────────────┤
    │◄──────────────────────────┤                           │
    │                           │                           │
    │  {"encrypted":"..."}      │                           │
    │  (中身: list              │                           │
    │   + handshakeId + nonce)  │                           │
    ├──────────────────────────►│──────────────────────────►│ AES復号 → handshakeId照合
    │  {"encrypted":"..."}      │◄──────────────────────────┤  → nonce検証 → 実行
    │  (中身: sessions一覧)     │                           │
    │◄──────────────────────────┤                           │
    │                           │                           │
    │  {"action":"nonce"}       │                           │ 次のリクエスト用
    ├──────────────────────────►│──────────────────────────►│
    │  {"nonce":"def456..."}    │◄──────────────────────────┤
    │◄──────────────────────────┤                           │
    │                           │                           │
    │  {"encrypted":"..."}      │                           │
    │  (中身: launch            │                           │
    │   + handshakeId + nonce)  │                           │
    ├──────────────────────────►│──────────────────────────►│ AES復号 → handshakeId照合
    │  {"encrypted":"..."}      │◄──────────────────────────┤  → nonce検証 → セッション準備
    │  (中身: started)          │                           │
    │◄──────────────────────────┤                           │ 1. ConPty取得(未起動なら遅延起動)
    │                           │                           │ 2. Busy判定
    │                           │                           │ 3. "/remote-control" 送信
    │                           │                           │ 4. 1秒待機
    │                           │                           │    URL → 即終了
    │                           │                           │    connecting → 5へ
    │                           │                           │    無応答 → エラー
    │                           │                           │ 5. 3秒待ち→"/remote-control"再送
    │                           │                           │ 6. URL検出(最大60秒)
    │                           │                           │ 7. Esc送信(panel閉じる)
    │  {"encrypted":"..."}      │◄──────────────────────────┤
    │  (中身: ready + URL)      │                           │
    │◄──────────────────────────┤                           │
    │                           │                           │
    │  ブラウザでURLを開く       │                           │
    └─────────────────────────────────────────────────────► Claude Code Remote Control
```
