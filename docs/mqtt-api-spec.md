# MQTT リモート起動 API仕様

## 概要

外出先（スマホ）からTerminalHubにMQTT経由でリクエストを送り、Claude Codeセッションを`--remote-control`付きで起動し、Remote Control URLを取得する。

## MQTTトピック

```
claude/{TopicGUID}/request   — クライアント → TerminalHub
claude/{TopicGUID}/response  — TerminalHub → クライアント
```

- `TopicGUID`: TerminalHubの設定画面で有効化時に自動生成されるGUID
- ブローカー: `vps3.zio3.net:1883`（TCP）/ `:9001`（WebSocket）

## 認証

- パスワード未設定（`PasswordHash`がnull）→ 認証なし、全リクエスト通過
- パスワード設定済み → リクエストの`passwordHash`とサーバー側の`PasswordHash`をSHA256で比較
- 不一致 → `{"action":"error","message":"unauthorized"}`

パスワードハッシュの生成: `SHA256(パスワード文字列)` → 小文字16進数

## リクエスト（request）

### セッション一覧取得

```json
{ "action": "list", "passwordHash": "sha256ハッシュ" }
```

`passwordHash`はパスワード未設定時は省略可。

### セッション起動

```json
{ "action": "launch", "sessionId": "セッションGUID", "passwordHash": "sha256ハッシュ" }
```

## レスポンス（response）

### 一覧返却

```json
{
  "action": "list",
  "sessions": [
    { "id": "guid", "name": "TerminalHub", "folder": "C:\\...", "type": "ClaudeCode" }
  ]
}
```

※ ClaudeCodeセッションのみ返却

### 起動開始

```json
{ "action": "launch", "status": "started", "sessionId": "guid" }
```

### URL取得成功

```json
{ "action": "launch", "status": "ready", "sessionId": "guid", "url": "https://claude.ai/code/..." }
```

### エラー

```json
{ "action": "error", "message": "unauthorized" }
{ "action": "error", "message": "session not found" }
{ "action": "error", "message": "launch failed or timeout" }
{ "action": "error", "message": "sessionId required" }
{ "action": "error", "message": "unknown action" }
```

## タイムアウト

- URL検知: 30秒でタイムアウト → `{"action":"error","message":"launch failed or timeout"}`

## シーケンス

```
クライアント                    MQTT                    TerminalHub
    │                           │                           │
    │  {"action":"list"}        │                           │
    ├──────────────────────────►│──────────────────────────►│
    │                           │                           │ セッション一覧取得
    │                           │◄──────────────────────────┤
    │  {"action":"list",        │                           │
    │   "sessions":[...]}       │                           │
    │◄──────────────────────────┤                           │
    │                           │                           │
    │  {"action":"launch",      │                           │
    │   "sessionId":"xxx"}      │                           │
    ├──────────────────────────►│──────────────────────────►│
    │                           │                           │ --remote-control付き起動
    │                           │◄──────────────────────────┤
    │  {"status":"started"}     │                           │
    │◄──────────────────────────┤                           │
    │                           │                           │ URL検知（約2秒）
    │                           │◄──────────────────────────┤
    │  {"status":"ready",       │                           │
    │   "url":"https://..."}    │                           │
    │◄──────────────────────────┤                           │
    │                           │                           │
    │  ブラウザでURLを開く       │                           │
    └─────────────────────────────────────────────────────► Claude Code Remote Control
```
