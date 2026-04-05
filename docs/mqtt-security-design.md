# MQTT リモート起動 セキュリティ設計

## 概要

TerminalHubのリモート起動機能は、MQTTブローカーを経由してクライアント（Web UI）とTerminalHub間で通信する。MQTTブローカーは暗号化されていないため、RSA鍵交換 + AES-GCM暗号化をアプリケーション層で行う。

## 脅威モデル

| 脅威 | 対策 |
|------|------|
| MQTTブローカーの傍受（トピック監視） | セッション鍵でAES-GCM暗号化。セッション鍵はRSA暗号化で配信（秘密鍵なしでは復号不可） |
| リプレイ攻撃（傍受した暗号文の再送） | ワンタイムnonce: 暗号文を再送してもnonce消費済みで拒否 |
| アクセスURL漏洩 | パスワード設定時は暗号化リクエスト内にPasswordHashが必要。URL単体では操作不可 |
| MQTTトピックの推測 | TopicGUIDはランダム生成、ブローカーACLで `claude/#` のみ許可 |

## 暗号化方式

### 鍵交換（RSA）

1. クライアントが `{"action":"handshake"}` を送信
2. TerminalHub がセッション鍵（ランダム32byte）を生成
3. Webサーバーの RSA公開鍵（appsettings.json に配置）でセッション鍵を暗号化
4. handshake レスポンスに暗号化されたセッション鍵を含めて返却
5. Webサーバーが秘密鍵で復号 → セッション鍵を取得

- **アルゴリズム**: RSA-OAEP-SHA256（鍵長2048bit）
- セッション鍵はMQTT上ではRSA暗号文としてのみ流れる
- TerminalHub には秘密鍵がないため、自身が暗号化したセッション鍵を復号することはできない（設計上正しい）

### 通信暗号化（AES-GCM）

鍵交換後の通信は両方向ともセッション鍵でAES-256-GCM暗号化。

- **アルゴリズム**: AES-256-GCM（認証付き暗号）
- **鍵**: セッション鍵（32byte = 256bit）をそのままAES鍵として使用
- **IV**: リクエスト毎にランダム12byte生成
- **認証タグ**: 16byte
- 改ざん検知が組み込まれており、不正な鍵での復号は必ず失敗する

### リプレイ攻撃防止（ワンタイムnonce）

1. クライアントが `{"action":"nonce"}` を送信（平文）→ サーバーがnonce返却（平文）
2. クライアントが暗号化リクエストの中にnonceを含める
3. サーバーが復号後、nonceが有効か検証し、使用済みにする（ワンタイム）
4. nonceは発行から30秒で自動失効

攻撃者がnonceを見ても、セッション鍵を持たないため有効なリクエストを作成できない。

## 鍵管理

### Webサーバー側
- RSA秘密鍵を保持（環境変数 or Azure Key Vault等）
- 公開鍵をTerminalHubに配布

### TerminalHub側
- Webサーバーの**RSA公開鍵のみ**を保持（`appsettings.json` の `Mqtt:PublicKey`）
- 秘密情報は一切持たない（セッション鍵はメモリ上のみ）

### セッション識別（handshakeId）

handshake成功時にサーバーが `handshakeId` を生成して返却する。クライアントは以降の暗号化リクエストにこの `handshakeId` を含める。サーバーは `handshakeId` が現在のセッションと一致するか検証し、不一致なら `"session mismatch"` で拒否する。

これにより、第三者が handshake を送ってもセッション鍵が上書きされるだけで、正規クライアントの通信は `handshakeId` 不一致で拒否される（可用性攻撃の軽減）。

また、handshakeリクエストには `requestId`（クライアント生成）を含め、レスポンスにそのまま返すことで、クライアントは自分のリクエストへの応答か判別できる。

### セッション鍵のライフサイクル
- handshake受信時に毎回新規生成（前のは破棄）
- **有効期限: 5分**（TerminalHub側で生成時刻からの経過で判定）
- 期限切れ時は `"session expired"` エラーを返却。クライアントはhandshakeを再送して鍵を再取得する
- MQTT再接続時にリセット
- TopicGUID再生成時にリセット

## アクセスURLの構成

```
https://claude-launcher.azurewebsites.net/{TopicGUID}
```

URLにはTopicGUIDのみ。SecretKeyは不要。

## セキュリティレベルの比較

| 漏洩シナリオ | パスワードなし | パスワードあり |
|---|---|---|
| MQTT傍受のみ | セッション鍵はRSA暗号文、通信はAES暗号文 → 解読不可 | 同左 |
| URL漏洩のみ | TopicGUIDだけではセッション鍵を作れない → 安全 | 同左 + パスワード必要 |
| URL + MQTT傍受 | RSA秘密鍵がないとセッション鍵を復号できない → 安全 | 同左 |

## パスワードの役割

パスワードは暗号化の鍵ではなく、暗号化リクエスト内の認証情報として機能する:

```json
// 復号後のリクエスト
{ "action": "list", "nonce": "...", "passwordHash": "SHA256(パスワード)" }
```

- パスワード未設定: passwordHash フィールド省略可
- パスワード設定済み: passwordHash が一致しないと拒否

## 通信フォーマット

### ping/pong（平文・疎通確認のみ）

```json
{ "action": "ping" }   → { "action": "pong", "version": "x.x.x" }
```

### handshake（平文・セッション鍵交換）

```json
{ "action": "handshake" }   → { "action": "handshake", "sessionKey": "Base64(RSA暗号化されたセッション鍵)" }
```

公開鍵未設定時はエラー: `{"action":"error","message":"public key not configured"}`

### nonce発行（平文）

```json
{ "action": "nonce" }   → { "action": "nonce", "nonce": "ランダム32文字の16進数" }
```

### 暗号化リクエスト/レスポンス

```json
{ "encrypted": "Base64(AES-256-GCM([12byte IV][16byte Tag][暗号文]))" }
```

## Web UI側の動作

- pingで疎通確認・バージョン確認
- handshakeでセッション鍵を受け取り、秘密鍵で復号
- 以降の通信はセッション鍵でAES-GCM
- handshakeがエラーの場合は「公開鍵を設定してください」と警告

## 実装ファイル

| ファイル | 役割 |
|----------|------|
| `TerminalHub/Services/MqttService.cs` | MQTT通信、RSA/AES暗号化、セッション鍵管理 |
| `TerminalHub/Models/AppSettings.cs` | PasswordHashの保存 |
| `TerminalHub/Components/Shared/Dialogs/SettingsDialog.razor` | リモート起動設定UI |
| `TerminalHub/appsettings.json` | RSA公開鍵の配置（`Mqtt:PublicKey`） |
| `docs/mqtt-api-spec.md` | API仕様 |
