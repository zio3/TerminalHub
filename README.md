# TerminalHub

TerminalHubは、Webブラウザから複数のターミナルセッションを管理できるBlazor Serverアプリケーションです。Windows ConPTY APIを使用して、本格的なターミナル体験を提供します。

## 主な機能

### 🖥️ マルチセッション管理
- 複数のターミナルセッションを同時に管理
- セッションごとに独立したConPTYインスタンス
- タブ切り替えで簡単にセッション間を移動
- セッション状態の自動保存と復元

### 🤖 AI CLIツール対応
- **Claude Code CLI**: トークン使用量と処理時間をリアルタイム表示
- **Gemini CLI**: 出力解析と処理状態の可視化
- 処理完了時の通知機能
- タイムアウト検出（5秒間更新なし）

### 📦 タスクランナー
- package.jsonからnpmスクリプトを自動読み込み
- タスクの実行・停止をUIから制御
- 複数タスクの並列実行サポート
- タスク選択状態の永続化

### 🌳 Git統合
- Gitリポジトリの自動検出
- ブランチ情報の表示
- Git Worktree作成機能
- ファイル変更状態のインジケーター

### 💾 その他の機能
- コマンド履歴（Ctrl+↑/↓でナビゲーション）
- ターミナル内URLの自動検出とクリック対応
- セッション展開状態の永続化
- マルチブラウザ対応（同一セッションを複数ブラウザから操作可能）

## システム要件

- Windows 10/11（ConPTY API使用のため）
- .NET 9.0 SDK
- Node.js（タスクランナー機能使用時）

## インストールと起動

### 基本的な起動方法

```powershell
# リポジトリをクローン
git clone https://github.com/zio3/TerminalHub.git
cd TerminalHub

# ビルド
dotnet build

# 起動（バックグラウンド、ブラウザ自動起動）
./start.ps1

# または、フォアグラウンドで起動
./start.ps1 -Foreground
```

### NPMスクリプトを使用した起動

```bash
# 依存関係のインストール（初回のみ）
npm install

# 起動
npm run start

# バックグラウンドで起動
npm run start:background

# 停止
npm run stop
```

### 複数インスタンスの起動

異なるポートで複数のインスタンスを起動できます：

```batch
# start-dev.bat を使用
start-dev.bat 5090 7190

# または start-multi.bat でメニューから選択
start-multi.bat
```

## 使い方

### セッションの作成
1. 左側の「+」ボタンをクリック
2. フォルダを選択
3. セッションタイプを選択（通常/Claude Code/Gemini/DOS/タスクランナー）
4. 必要に応じてオプションを設定

### キーボードショートカット
- `Ctrl + ↑/↓`: コマンド履歴のナビゲーション
- `Ctrl + C`: 選択テキストのコピー（選択なしの場合は中断）
- `Ctrl + V`: ペースト

### タスクランナーの使用
1. package.jsonがあるフォルダでセッションを作成
2. 下部パネルの「タスクランナー」タブを選択
3. 実行したいnpmスクリプトを選択
4. 実行ボタンをクリック

## プロジェクト構造

```
TerminalHub/
├── TerminalHub/              # メインプロジェクト
│   ├── Components/          # Blazorコンポーネント
│   │   ├── Pages/          # ページコンポーネント
│   │   └── Shared/         # 共有コンポーネント
│   ├── Services/           # ビジネスロジック
│   ├── Models/             # データモデル
│   ├── Helpers/            # ヘルパークラス
│   └── wwwroot/           # 静的ファイル
│       └── js/            # JavaScriptファイル
├── start.ps1              # 起動スクリプト
├── package.json           # npm設定
└── CLAUDE.md             # 開発者向けドキュメント
```

## 技術スタック

- **フロントエンド**: Blazor Server, XTerm.js
- **バックエンド**: ASP.NET Core 9.0
- **ターミナル**: Windows ConPTY API
- **JavaScript**: XTerm.js, WebLinksAddon
- **スタイリング**: Bootstrap 5

## 開発

開発に関する詳細情報は[CLAUDE.md](CLAUDE.md)を参照してください。

## トラブルシューティング

### ターミナルが表示されない
- Windows 10/11を使用しているか確認
- ブラウザのコンソールでエラーを確認
- `dotnet clean` → `dotnet build` を実行

### 長い文字列が切れる
- 最新バージョンでは265文字単位でチャンク処理により修正済み
- 問題が続く場合はConPtyService.csのWriteAsyncメソッドを確認

### セッションが保存されない
- ブラウザのローカルストレージが有効か確認
- プライベートブラウジングモードでは保存されません

## ライセンス

ISC License

## 作者

akihiro taguchi (info@zio3.net)

## 貢献

プルリクエストを歓迎します。大きな変更の場合は、まずissueを開いて変更内容を議論してください。