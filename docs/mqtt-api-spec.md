# MQTT リモート起動 API仕様

## 概要

外出先（スマホ）からTerminalHubにMQTT経由でリクエストを送り、Claude Codeセッションを`--remote-control`付きで起動し、Remote Control URLを取得する。

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

同じセッションに対して再度launchした場合、前回のリモートセッションは自動的に切断・解放される。

#### リモートセッション切断

```json
{ "action": "disconnect", "sessionId": "セッションGUID", "handshakeId": "...", "nonce": "..." }
```

パスワード設定時:
```json
{ "action": "disconnect", "sessionId": "...", "handshakeId": "...", "nonce": "...", "passwordHash": "SHA256(パスワード)" }
```

リモート起動したClaude Codeプロセスを終了し、リソースを解放する。

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

## タイムアウト

- URL検知: 60秒でタイムアウト → `{"action":"error","message":"launch failed or timeout"}`

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
    │  {"encrypted":"..."}      │◄──────────────────────────┤  → nonce検証 → 起動
    │  (中身: started)          │                           │
    │◄──────────────────────────┤                           │
    │                           │                           │ URL検知（約2秒）
    │  {"encrypted":"..."}      │◄──────────────────────────┤
    │  (中身: ready + URL)      │                           │
    │◄──────────────────────────┤                           │
    │                           │                           │
    │  ブラウザでURLを開く       │                           │
    └─────────────────────────────────────────────────────► Claude Code Remote Control
```
