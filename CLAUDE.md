# CLAUDE.md

このファイルは、Claude Code (claude.ai/code) がこのリポジトリで作業する際のガイダンスを提供します。

## 重要な指示
- このプロジェクトの開発者は日本語を使用します
- 応答は日本語で行ってください
- コメントやコミットメッセージも日本語で記述してください

## ビルドと開発コマンド

### ビルド
```bash
dotnet build
dotnet build TerminalHub/TerminalHub.csproj
```

### 実行
```powershell
# 直接実行
dotnet run --project TerminalHub/TerminalHub.csproj

# npm経由で実行
npm start
```

### NPMスクリプト (package.json経由)
```bash
npm start             # プロジェクトを起動
npm run build         # プロジェクトをビルド
npm run publish       # 自己完結型でpublish
npm run clean         # ビルド成果物をクリーン
```

### インストーラーのビルド
```bash
./build-installer.bat   # Inno Setup 6 が必要
```

### リリース手順
`/release` スキルを使用する。バージョン更新、コミット、タグ付け、リリースノート作成、Discordお知らせ投稿までを一括実行する。

### 機能紹介記事（Tips）の執筆
`/write-tips` スキルを使用する。Notion への記事作成（結論先行・AI伴走プロンプト型）から Discord #tips への紹介投稿までを一括実行する。ネタ元は `docs/tips-draft.md`。

### クリーン
```bash
dotnet clean
```

### テスト
VTエミュレータ（`TerminalHub.Terminal`）のヘッドレステスト（xUnit）が `TerminalHub.Terminal.Tests` にある。WinForms/ConPTY/Web に依存しないので高速に回せる。
```bash
# 全テスト実行
dotnet test TerminalHub.Terminal.Tests/TerminalHub.Terminal.Tests.csproj

# 単一テストクラス/メソッドを実行（フィルタ）
dotnet test TerminalHub.Terminal.Tests/TerminalHub.Terminal.Tests.csproj --filter "FullyQualifiedName~EmulatedStateBufferTests"
dotnet test TerminalHub.Terminal.Tests/TerminalHub.Terminal.Tests.csproj --filter "DisplayName~メソッド名の一部"
```
実 TUI 出力のキャプチャ（`Fixtures/*.raw` = `PreserveNewest` でテスト出力へコピー）を流し込んでパリティ検証する方式。`Xunit.SkippableFact` を使うテストはフィクスチャ不在時にスキップされる。

## ソリューション構成

`TerminalHub.sln` は3プロジェクト構成（TargetFramework は `net10.0` / メインのみ `net10.0-windows`）:

1. **TerminalHub** (`net10.0-windows`, WinForms 有効): Blazor Server 本体。ConPTY・UI・各サービス・MCP サーバー・CLI(`--notify`)モードを含む。
2. **TerminalHub.Terminal** (`net10.0`): 自作 VT エミュレータ。ConPTY 出力を解釈してターミナル状態バッファ（グリッド）を持つ。`VtParser` / `EmulatedStateBuffer` / `TerminalGrid` / `AnsiSerializer` / `ReplaySnapshot` 等。UI 非依存で単体テスト可能。**バッファ二重化・切替時表示崩れ対策の根幹**（PR #91 で全面採用）。
3. **TerminalHub.Terminal.Tests** (`net10.0`): 上記のヘッドレステスト。

### 開発時のプロセス管理（重要）
- TerminalHubは通常利用しているインスタンスが別に動作している場合がある
- 開発で起動したプロセスのみを停止すること
- **プロセス停止時は、自分が起動したプロセスIDを記録しておき、そのIDのみを停止する**
- 他のTerminalHubプロセスを無差別に停止しないこと

## アーキテクチャ概要

TerminalHubは、Windows ConPTY統合により複数のターミナルセッションを提供するWebベースのターミナルインターフェースを実装したBlazor Serverアプリケーションです。

### コアコンポーネント

1. **ターミナル管理**
   - `ConPtyService`: 擬似コンソールセッション用のWindows ConPTY APIラッパー
   - `ConPtyConnectionService`: マルチブラウザ対応のためのCircuit毎のConPTY接続管理
   - `SessionManager`: 遅延初期化による複数ターミナルセッション管理
   - `TerminalService`: XTerm.js操作のためのJavaScript相互運用の抽象化
   - `TaskManagerService`: タスクランナーセッションとnpmスクリプト実行の管理

2. **セッションタイプ**（`terminalType`: `ClaudeCode` / `GeminiCLI` / `CodexCLI` / `Antigravity` / `Grok` / `Terminal`）
   - 通常のターミナルセッション
   - Claude Code CLIセッション（hook 連携 + 出力解析）
   - Codex CLIセッション（hook 連携。type:command ブリッジ経由）
   - Gemini CLIセッション（**廃止**。起動経路・出力解析器とも撤去済みで、既存セッションは通常ターミナルとして起動する。`GeminiCLI` enum 値のみ永続化互換のため残置）
   - Antigravity / Grok セッション
   - DOSコマンドセッション
   - タスクランナーセッション（npmスクリプト）
   - `Analyzers/`: CLI 別の出力解析器（`ClaudeCodeAnalyzer` / `CodexCliAnalyzer`）を `OutputAnalyzerFactory` で振り分け。他の種別には解析器を登録しない（＝出力解析は走らない）

3. **UIアーキテクチャ**
   - `Root.razor`: UIを統括するメインコンポーネント（大幅にリファクタリングされ縮小）
   - 左側にセッションリスト、右側にターミナル表示
   - 異なるセッションタイプ用のタブ付き下部パネル
   - XTerm.jsによるリアルタイムターミナル出力
   - ターミナル内のクリック可能なURL用WebLinksAddon

4. **主要サービス**
   - `OutputAnalyzerService`: Claude Code/Codex用CLI出力解析、処理状態追跡
   - `InputHistoryService`: 永続化機能付きコマンド履歴管理
   - `GitService`: worktree管理を含むGit操作
   - `PackageJsonService`: package.jsonファイルからnpmスクリプトを読み取り
   - `LocalStorageService`: エラーハンドリング付きブラウザローカルストレージ永続化
   - `NotificationService`: クロスセッション通知
   - `ConPtyConnectionService`: Circuit毎のイベント購読管理

5. **永続化とデータ層**
   - `SessionDbContext` + `SqliteStorageService`: セッション状態を SQLite に永続化（`Microsoft.Data.Sqlite`）。従来の `LocalStorageService`（ブラウザ）と併用する二層構成
   - `ISessionRepository` / `ISessionMemoRepository` / `ISessionMemoSnapshotRepository`: セッション本体・メモ・メモ編集履歴のリポジトリ
   - `MemoSnapshotService`: メモ編集履歴を10分毎に自動スナップショット（HostedService）
   - `AppSettingsService`: ファイルベースのアプリ設定（LocalStorage とは別系統）
   - `AppDataPaths`: DB / app-settings / logs の保存先を `%LOCALAPPDATA%\TerminalHub\` 配下で `IsDevelopment` により切替（dev=`sessions-dev.db` / prod=`sessions.db`）。詳細はメモリの「dev/prodデータ保存先」を参照

6. **リモート起動・通知連携**
   - `MqttService` (HostedService) + `RemoteLaunchService`: MQTT 経由のリモート起動（`MQTTnet`）
   - `HookNotificationService`: hook 通知の一元ハンドラ。lifecycle hook の登録は `ClaudeHookService`（起動オプション `--settings` でセッション毎の JSON を渡す。作業フォルダのファイルには書き込みも削除もしない。旧方式が `.claude/settings.local.json` に残した残骸は hook が加算式ゆえ二重発火するが、消すのは利用者＝MCP 残骸と同じ方針）/ `CodexHookService`（起動引数 `-c hooks.*=...` で注入。ファイルには書き込まない。旧方式が `.codex/hooks.json` に残した残骸の扱いも Claude 側と同じ方針）
   - Webhook 通知の詳細ペイロード仕様は本ファイル末尾の「Webhook通知」セクション参照

7. **MCP サーバー（セッション間メッセージング）**
   - `SessionMessagingTools`: `list_sessions` / `send_to_session` / `set_memo` を公開。`SessionManager`(Singleton) に直結するため **HTTP トランスポート一択**（stdio だと別プロセスで共有状態に届かない）。エンドポイントは `/mcp`
   - instructions は接続セッションごとに設定から動的に読み込む（TerminalHub 再起動不要。CLI 側の `/clear` 等で再接続時に反映）

8. **CLI モード（`--notify`）**: `TerminalHub.exe --notify` で hook 通知を HTTP/HTTPS で本体へ送信。`--source codex` で Codex ネイティブ JSON を stdin 経由で `/api/hook/codex/{sessionId}` へ転送するブリッジになる（Program.cs の `RunNotifyModeAsync` / `RunCodexBridgeAsync`）

9. **ログ**: Serilog。ログは `%LOCALAPPDATA%\TerminalHub\` 配下（Program Files インストール時の書き込み権限エラー回避）、日次ローテーション・7日保持・10MB上限

### 重要な実装詳細

1. **遅延セッション初期化**: ConPTYセッションは最初のアクセス時にのみ作成
2. **マルチブラウザ対応**: ConPtyConnectionServiceにより同一セッションへの複数ブラウザ接続が可能
3. **XTerm.js統合**: Windows固有設定とWebLinksAddonによるターミナルレンダリング
4. **Git Worktree対応**: セッションは親ディレクトリの兄弟としてgit worktreeを作成可能
5. **出力解析**: Claude Code/Codex CLI出力のトークン使用量と処理時間のリアルタイム解析
6. **データチャンキング**: ConPTY WriteAsyncは切り捨て防止のため256文字でチャンクしてフラッシュし、チャンク間に20msの間隔を空ける（受け手の取りこぼし対策。最後のチャンクの後は待たない）

### JavaScriptファイル
- `wwwroot/js/terminal.js`: XTerm.js初期化、ターミナル管理、リサイズ処理、WebLinksAddon
- `wwwroot/js/helpers.js`: DOM操作、ローカルストレージ用ユーティリティ関数

### よくある問題と解決策

1. **ターミナル表示の問題**: terminal.jsの`term.onData()`ハンドラーを確認 - 文字の二重表示を引き起こす可能性
2. **長い文字列の切り捨て**: ConPTY WriteAsyncは256文字でチャンクし明示的にフラッシュするよう修正済み。チャンク間隔(20ms)は実測で調整された値で、詰めると切れが再発した経緯があるため安易に縮めないこと
3. **OutputAnalyzerServiceのビルドエラー**: `AnalyzeOutput`メソッドに`activeSessionId`パラメータが渡されているか確認
4. **Worktreeパスの問題**: SessionManagerはworktree作成前に末尾のディレクトリ区切り文字を削除
5. **UTF-8デコードエラー**: ConPtySession.ReadAsyncで適切なバッファサイズ計算により修正済み
6. **JSDisconnectedException**: すべてのLocalStorageServiceメソッドにJavaScript相互運用エラー用のtry-catchを実装

### セッション通知システム
- セッションは経過時間とトークン数で処理完了を追跡
- 非アクティブセッションは処理完了時に通知ベルを表示
- 通知ロジックは`activeSessionId`でセッションがアクティブか判定
- ハングしたプロセス用のタイマーベースのタイムアウト検出（5秒）

### タスクランナー統合
- TaskManagerServiceがタスクセッションを独立して管理
- タスク選択時の自動ターミナル接続
- LocalStorage経由の永続的なタスク選択状態
- package.jsonファイルからのnpmスクリプトサポート

### Webhook通知

設定画面（通知タブ）からWebhook通知を有効にできます。設定はLocalStorageに保存されます。

#### 設定方法
1. 設定ダイアログを開く
2. 「通知」タブを選択
3. 「WebHook通知を有効にする」をオン
4. URLを入力して保存

#### Webhookペイロード仕様

Claude Code / Codex CLI の hook 発火時に、設定されたURLにPOSTリクエストが送信されます。
`eventType` には **start/complete のような加工をせず、本来の hook イベント名をそのまま** 入れます。
「どのイベントを開始（LED 点灯）/終了（LED 消灯）とみなすか」は受信側で判断します。

> **Codex CLI も lifecycle hook 対応済み**。Codex は `type:"http"` 非対応のため、`type:"command"` で
> `TerminalHub.exe --notify --source codex` をブリッジ起動し、stdin の JSON を `/api/hook/codex/{sessionId}` へ
> 転送する（`CodexHookService` が起動引数 `-c hooks.*=...` で注入。ファイルには書き込まない）。受信後は Claude と同じ `HookNotificationService`
> に流れ、`eventType` は本来の hook 名そのまま・`tool="CodexCLI"` で送る。登録イベントは Stop / UserPromptSubmit /
> SubagentStart / SubagentStop / PreCompact / PostCompact / PermissionRequest（許可待ち）/
> PreToolUse（matcher=`request_user_input`＝選択待ち）。いずれも `toolName` 等は payload で送る。
> （PostToolUse / SessionStart は当面未登録）
>
> Hook を持たない CLI は `OutputAnalyzerService` の出力解析ベースで
> `eventType` は **`"start"` / `"complete"`**（このとき `tool` は `null`）を送る。
> `tool` で「hook 由来（`"ClaudeCode"` / `"CodexCLI"`）」か「出力解析由来（`null`）」かを区別できる。
>
> **現状この経路の送出元は存在しない**（受信側の契約としては維持）。出力解析器を持つのは
> hook 駆動の ClaudeCode / CodexCLI だけで、両者は `IsHookDriven` で出力解析由来の通知を
> 抑止するため。唯一の該当 CLI だった Gemini が廃止され、Antigravity / Grok / Terminal は
> 解析器を持たない。hook 非対応 CLI に解析器を追加すれば再びこの経路が生きる。

送信される hook イベント: `UserPromptSubmit` / `Stop` / `SubagentStart` / `SubagentStop` / `PreCompact` / `PostCompact` / `Notification`(Claude) / `PreToolUse` / `PermissionRequest`(Codex)

> ただし `SubagentStart` / `SubagentStop` は `agent_type` を持つ本物のサブエージェントのみ送信され、`agent_type` 空の「意図しない発火」は転送されません（後述の「受信側での解釈の目安」を参照）。

```json
{
  "eventType": "UserPromptSubmit | Stop | SubagentStart | SubagentStop | PreCompact | PostCompact | Notification | PreToolUse | PermissionRequest",
  "tool": null | "ClaudeCode" | "CodexCLI",
  "message": null | "Claude needs your permission to use Bash",
  "toolName": null | "AskUserQuestion",
  "sessionId": "{guid}",
  "agentId": null | "{agent_id}",
  "sessionName": "セッション名",
  "terminalType": "ClaudeCode" | "GeminiCLI" | "CodexCLI" | "Antigravity" | "Grok" | "Terminal",
  "elapsedSeconds": null | 123,
  "elapsedMinutes": null | 2.05,
  "timestamp": "2025-01-01T00:00:00Z",
  "folderPath": "C:\\path\\to\\folder"
}
```

| フィールド | 型 | 説明 |
|-----------|------|------|
| `eventType` | string | hook 由来（ClaudeCode / CodexCLI）は発火した hook イベント名そのまま（`UserPromptSubmit` / `Stop` / `SubagentStart` / `SubagentStop` / `PreCompact` / `PostCompact` / `Notification`(Claude) / `PreToolUse` / `PermissionRequest`(Codex)）。**hook を持たない CLI のみ**出力解析ベースで `"start"` / `"complete"`（このとき `tool=null`）。現状この経路の送出元は存在しない（上記注記参照） |
| `tool` | string? | 送信元 CLI 名（スペース無し）。hook 由来は `"ClaudeCode"` / `"CodexCLI"`。出力解析由来（hook 非対応 CLI）は `null` |
| `message` | string? | `Notification` の本文（許可待ち/idle の判別用）。例: `"Claude needs your permission to use Bash"`。それ以外は `null` |
| `toolName` | string? | 対象ツール名。`PreToolUse`（Claude=`AskUserQuestion` / Codex=`request_user_input`）や `PermissionRequest`（Codex、承認対象ツール）で値が入る。それ以外は `null` |
| `sessionId` | string | 常に生のセッション GUID（プレフィックス無し） |
| `agentId` | string? | サブエージェント由来イベント（`SubagentStart`/`SubagentStop`）のときのみ `agent_id` が入る。それ以外は `null`。キーの振り分け（個別 LED 等）は受信側で行う |
| `sessionName` | string | セッションの表示名 |
| `terminalType` | string | ターミナルの種類 |
| `elapsedSeconds` | int? | 処理時間（秒）。完了系イベント（`Stop` / 非ClaudeCode の `complete`）で値が入り、それ以外は `null` |
| `elapsedMinutes` | float? | 処理時間（分）。完了系イベント（`Stop` / 非ClaudeCode の `complete`）で値が入り、それ以外は `null` |
| `timestamp` | string | イベント発生時刻（UTC） |
| `folderPath` | string | セッションの作業ディレクトリ |

#### 受信側での解釈の目安（LED 等）
- **開始（点灯）扱い**: `UserPromptSubmit` / `PreCompact` / `SubagentStart`
- **終了（消灯）扱い**: `Stop` / `PostCompact` / `SubagentStop`
- **入力待ち（別色でアテンション等）**: `Notification`（`message` に `permission` を含めば許可待ち、それ以外は idle）/ `PreToolUse`（`toolName` の質問待ち）
- 起動時に選択肢から compaction が走る等、`UserPromptSubmit` を伴わないケースでも `PreCompact`/`PostCompact` は飛ぶので、これらを開始/終了として扱えば取りこぼさない。
- サブエージェントの開始/終了は `agentId` に `agent_id` が入る（`sessionId` は親セッションの GUID）。`agentId` の有無でセッション本体と区別し、個別 LED キーとして扱える。
- **`agent_type` を持たない Subagent 系イベントは Webhook 転送しない**: Claude Code は recap 生成等の内部処理でも `SubagentStop`（agent_type 空・SubagentStart を伴わない単発）を発火することがある。これは「意図しない発火」として TerminalHub 側で破棄し、Webhook を送らない（個別 LED の空振りを防ぐため）。hook 自体は受信し診断用の Hook イベントログには記録されるが、Webhook 転送のみ抑止する。本物のユーザー Task サブエージェントは `agent_type`（`Explore` 等）が必ず入る。

#### カスタムヘッダー
現在はContent-Type: application/jsonが固定で送信されます。追加のカスタムヘッダーはLocalStorageから設定可能です（UI未実装）。