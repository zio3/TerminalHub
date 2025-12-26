# TerminalHub Webhook通知仕様

## 概要

TerminalHubは、CLI（Claude Code / Gemini CLI / Codex CLI）の処理開始・完了時にWebhook通知を送信する機能を提供します。

## 設定方法

1. 画面左下の「設定」ボタンをクリック
2. 「通知」タブを選択
3. 「WebHook通知を有効にする」をオン
4. URLを入力して保存

設定はブラウザのLocalStorageに保存されます。

## Webhook仕様

### エンドポイント

設定したURLに対してHTTP POSTリクエストが送信されます。

### リクエストヘッダー

| ヘッダー | 値 |
|---------|-----|
| Content-Type | application/json |

カスタムヘッダーはLocalStorageから設定可能です（UI未実装）。

### ペイロード

```json
{
  "eventType": "start",
  "sessionId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
  "sessionName": "セッション名",
  "terminalType": "ClaudeCode",
  "elapsedSeconds": null,
  "elapsedMinutes": null,
  "timestamp": "2025-01-01T00:00:00.000Z",
  "folderPath": "C:\\path\\to\\folder"
}
```

### フィールド説明

| フィールド | 型 | 説明 |
|-----------|------|------|
| `eventType` | string | イベントタイプ: `"start"` (処理開始) または `"complete"` (処理完了) |
| `sessionId` | string (GUID) | セッションの一意識別子 |
| `sessionName` | string | セッションの表示名（カスタム名またはフォルダ名） |
| `terminalType` | string | ターミナルの種類（下記参照） |
| `elapsedSeconds` | int? | 処理時間（秒）。`start`イベントでは`null` |
| `elapsedMinutes` | float? | 処理時間（分、小数点2桁）。`start`イベントでは`null` |
| `timestamp` | string (ISO 8601) | イベント発生時刻（UTC） |
| `folderPath` | string | セッションの作業ディレクトリパス |

### terminalType の値

| 値 | 説明 |
|----|------|
| `Terminal` | 通常のターミナル |
| `ClaudeCode` | Claude Code CLI |
| `GeminiCLI` | Gemini CLI |
| `CodexCLI` | Codex CLI |

## イベントタイミング

### start イベント

CLI（Claude Code / Gemini / Codex）が処理を開始したときに送信されます。

**トリガー条件:**
- OutputAnalyzerServiceが処理開始を検出したとき

### complete イベント

処理が完了したときに送信されます。

**トリガー条件:**
- 処理完了が検出された
- 処理時間が閾値（デフォルト: 5秒）を超えた場合のみ

**注意:** 閾値未満の短い処理では`complete`イベントは送信されません。

## サンプルペイロード

### start イベント

```json
{
  "eventType": "start",
  "sessionId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "sessionName": "my-project (Claude)",
  "terminalType": "ClaudeCode",
  "elapsedSeconds": null,
  "elapsedMinutes": null,
  "timestamp": "2025-01-15T10:30:00.123Z",
  "folderPath": "C:\\Users\\user\\projects\\my-project"
}
```

### complete イベント

```json
{
  "eventType": "complete",
  "sessionId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "sessionName": "my-project (Claude)",
  "terminalType": "ClaudeCode",
  "elapsedSeconds": 145,
  "elapsedMinutes": 2.42,
  "timestamp": "2025-01-15T10:32:25.456Z",
  "folderPath": "C:\\Users\\user\\projects\\my-project"
}
```

## 活用例

### Discord Webhook

Discord Webhookに送信する場合は、中継サーバーでペイロードを変換してください。

### Slack Webhook

Slack Incoming Webhookに送信する場合も同様に変換が必要です。

### 自作サーバー

```python
# Python Flask サンプル
from flask import Flask, request, jsonify

app = Flask(__name__)

@app.route('/webhook', methods=['POST'])
def webhook():
    data = request.json
    event_type = data.get('eventType')
    session_name = data.get('sessionName')

    if event_type == 'start':
        print(f"処理開始: {session_name}")
    elif event_type == 'complete':
        elapsed = data.get('elapsedMinutes')
        print(f"処理完了: {session_name} ({elapsed}分)")

    return jsonify({'status': 'ok'})

if __name__ == '__main__':
    app.run(port=5000)
```

## エラーハンドリング

- Webhookの送信に失敗してもアプリケーションの動作には影響しません
- エラーはサーバーログに記録されます
- タイムアウトやネットワークエラーは自動的にスキップされます

## 制限事項

- リトライ機能はありません
- バッチ送信には対応していません（イベントごとに個別送信）
- 認証ヘッダーのUI設定は未実装（LocalStorage直接編集で対応可能）
