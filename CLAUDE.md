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
# バックグラウンドで起動（ブラウザ自動起動）
./start.ps1

# フォアグラウンドモードで起動  
./start.ps1 -Foreground

# ブラウザを開かずに起動
./start.ps1 -NoBrowser

# サーバーを停止
./stop.ps1

# シングルインスタンス起動
./start-single.ps1
```

### NPMスクリプト (package.json経由)
```bash
npm run start          # フォアグラウンドで起動（ブラウザ付き）
npm run start:background  # バックグラウンドで起動
npm run stop          # サーバーを停止
npm run build         # プロジェクトをビルド
npm run clean         # ビルド成果物をクリーン
```

### クリーン
```bash
dotnet clean
```

## アーキテクチャ概要

TerminalHubは、Windows ConPTY統合により複数のターミナルセッションを提供するWebベースのターミナルインターフェースを実装したBlazor Serverアプリケーションです。

### コアコンポーネント

1. **ターミナル管理**
   - `ConPtyService`: 擬似コンソールセッション用のWindows ConPTY APIラッパー
   - `ConPtyConnectionService`: マルチブラウザ対応のためのCircuit毎のConPTY接続管理
   - `SessionManager`: 遅延初期化による複数ターミナルセッション管理
   - `TerminalService`: XTerm.js操作のためのJavaScript相互運用の抽象化
   - `TaskManagerService`: タスクランナーセッションとnpmスクリプト実行の管理

2. **セッションタイプ**
   - 通常のターミナルセッション
   - Claude Code CLIセッション（出力解析付き）
   - Gemini CLIセッション（出力解析付き）  
   - DOSコマンドセッション
   - タスクランナーセッション（npmスクリプト）

3. **UIアーキテクチャ**
   - `Root.razor`: UIを統括するメインコンポーネント（大幅にリファクタリングされ縮小）
   - 左側にセッションリスト、右側にターミナル表示
   - 異なるセッションタイプ用のタブ付き下部パネル
   - XTerm.jsによるリアルタイムターミナル出力
   - ターミナル内のクリック可能なURL用WebLinksAddon

4. **主要サービス**
   - `OutputAnalyzerService`: Claude Code/Gemini用CLI出力解析、処理状態追跡
   - `InputHistoryService`: 永続化機能付きコマンド履歴管理
   - `GitService`: worktree管理を含むGit操作
   - `PackageJsonService`: package.jsonファイルからnpmスクリプトを読み取り
   - `LocalStorageService`: エラーハンドリング付きブラウザローカルストレージ永続化
   - `NotificationService`: クロスセッション通知
   - `ConPtyConnectionService`: Circuit毎のイベント購読管理

### 重要な実装詳細

1. **遅延セッション初期化**: ConPTYセッションは最初のアクセス時にのみ作成
2. **マルチブラウザ対応**: ConPtyConnectionServiceにより同一セッションへの複数ブラウザ接続が可能
3. **XTerm.js統合**: Windows固有設定とWebLinksAddonによるターミナルレンダリング
4. **Git Worktree対応**: セッションは親ディレクトリの兄弟としてgit worktreeを作成可能
5. **出力解析**: Claude Code/Gemini CLI出力のトークン使用量と処理時間のリアルタイム解析
6. **データチャンキング**: ConPTY WriteAsyncは切り捨て防止のため265文字でチャンクしてフラッシュ

### JavaScriptファイル
- `wwwroot/js/terminal.js`: XTerm.js初期化、ターミナル管理、リサイズ処理、WebLinksAddon
- `wwwroot/js/helpers.js`: DOM操作、ローカルストレージ用ユーティリティ関数

### よくある問題と解決策

1. **ターミナル表示の問題**: terminal.jsの`term.onData()`ハンドラーを確認 - 文字の二重表示を引き起こす可能性
2. **長い文字列の切り捨て**: ConPTY WriteAsyncは265文字でチャンクし明示的にフラッシュするよう修正済み
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