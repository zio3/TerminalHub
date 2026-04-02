# MQTT リモート起動 API仕様

## 概要

外出先（スマホ）からTerminalHubにMQTT経由でリクエストを送り、Claude Codeセッションを`--remote-control`付きで起動し、Remote Control URLを取得する。

## MQTTトピック

```
claude/{TopicGUID}/request   — クライアント → TerminalHub
claude/{TopicGUID}/response  — TerminalHub → クライアント
```

- `TopicGUID`: TerminalHubの設定画面で有効化時に自動生成されるGUID
- ブローカー: `vps3.zio3.net:1883`（TCP）

## セキュリティ

### MQTTブローカーのACL

ブローカー（`vps3.zio3.net`）では `ClaudeLauncher` ユーザーに対しトピックACLが設定されており、`claude/#` 配下のトピックのみアクセス可能。それ以外のトピックへのpub/subは拒否される。

### 認証

- パスワード未設定（`PasswordHash`がnull）→ 認証なし、全リクエスト通過
- パスワード設定済み → リクエストの`passwordHash`とサーバー側の`PasswordHash`をSHA256で比較
- 不一致 → `{"action":"error","message":"unauthorized"}`
- **`ping` アクションのみ認証不要**（疎通確認用途のため）

パスワードハッシュの生成: `SHA256(パスワード文字列)` → 小文字16進数

## リクエスト（request）

### 疎通確認（認証不要）

```json
{ "action": "ping" }
```

TerminalHubがMQTT接続中であれば `{"action":"pong"}` を返す。認証不要のため、Webアプリ側でアクセス時に最初にこれを送り、応答の有無でTerminalHubのオンライン状態を判定できる。

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

### 起動開始

```json
{ "action": "launch", "status": "started", "sessionId": "guid" }
```

### URL取得成功

```json
{ "action": "launch", "status": "ready", "sessionId": "guid", "url": "https://claude.ai/code/..." }
```

### 疎通確認応答

```json
{ "action": "pong" }
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
    │  {"action":"ping"}        │                           │
    ├──────────────────────────►│──────────────────────────►│
    │                           │◄──────────────────────────┤
    │  {"action":"pong"}        │                           │
    │◄──────────────────────────┤                           │
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
