# TerminalHub

[![GitHub Release](https://img.shields.io/github/v/release/zio3/TerminalHub)](https://github.com/zio3/TerminalHub/releases/latest)
[![License](https://img.shields.io/badge/license-ISC-blue.svg)](LICENSE)

TerminalHubは、Webブラウザから複数のターミナルセッションを管理できるBlazor Serverアプリケーションです。Windows ConPTY APIを使用して、本格的なターミナル体験を提供します。

## ダウンロード

**[最新版をダウンロード](https://github.com/zio3/TerminalHub/releases/latest)**

インストーラーをダウンロードして実行するだけで使用できます。.NETのインストールは不要です。

## 主な機能

### マルチセッション管理
- 複数のターミナルセッションを同時に管理
- セッションごとに独立したConPTYインスタンス
- タブ切り替えで簡単にセッション間を移動
- セッション状態の自動保存と復元
- セッション検索フィルター機能

### AI CLIツール対応
- **Claude Code CLI**: トークン使用量と処理時間をリアルタイム表示
- **Gemini CLI**: 出力解析と処理状態の可視化
- 処理完了時の通知機能
- タイムアウト検出（5秒間更新なし）

### Git統合
- Gitリポジトリの自動検出
- ブランチ情報の表示
- Git Worktree作成機能
- ファイル変更状態のインジケーター

### その他の機能
- コマンド履歴（Ctrl+↑/↓でナビゲーション）
- ターミナル内URLの自動検出とクリック対応
- セッション展開状態の永続化
- マルチブラウザ対応（同一セッションを複数ブラウザから操作可能）

## システム要件

- Windows 10/11（ConPTY API使用のため）

### 開発者向け追加要件
- .NET 9.0 SDK
- Node.js（オプション）

## インストール

### インストーラーを使用（推奨）

1. [最新版をダウンロード](https://github.com/zio3/TerminalHub/releases/latest)
2. `TerminalHub-Setup-x.x.x.exe` を実行
3. インストール完了後、スタートメニューまたはデスクトップから起動

### 開発者向け（ソースから実行）

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

## 使い方

### セッションの作成
1. 左側の「新しいセッションを作成」ボタンをクリック
2. フォルダを選択（存在しないフォルダは自動作成可能）
3. セッションタイプを選択（通常/Claude Code/Gemini）
4. 必要に応じてオプションを設定

### セッションの検索
- セッションリスト上部の検索ボックスでセッション名やメモで絞り込み

### キーボードショートカット
- `Ctrl + ↑/↓`: コマンド履歴のナビゲーション
- `Ctrl + C`: 選択テキストのコピー（選択なしの場合は中断）
- `Ctrl + V`: ペースト

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
├── installer/             # インストーラー関連ファイル
├── .github/workflows/     # GitHub Actions（自動リリース）
├── start.ps1              # 開発用起動スクリプト
├── build-installer.bat    # インストーラービルドスクリプト
├── package.json           # npm設定
└── CLAUDE.md             # 開発者向けドキュメント
```

## 技術スタック

- **フロントエンド**: Blazor Server, XTerm.js
- **バックエンド**: ASP.NET Core 9.0
- **ターミナル**: Windows ConPTY API
- **JavaScript**: XTerm.js, WebLinksAddon
- **スタイリング**: Bootstrap 5
- **インストーラー**: Inno Setup

## 開発

開発に関する詳細情報は[CLAUDE.md](CLAUDE.md)を参照してください。

### インストーラーのビルド

```powershell
# Inno Setup 6 が必要
./build-installer.bat
```

## トラブルシューティング

### ターミナルが表示されない
- Windows 10/11を使用しているか確認
- ブラウザのコンソールでエラーを確認

### セッションが保存されない
- ブラウザのローカルストレージが有効か確認
- プライベートブラウジングモードでは保存されません

## ライセンス

ISC License

## 作者

akihiro taguchi (info@zio3.net)

## 貢献

プルリクエストを歓迎します。大きな変更の場合は、まずissueを開いて変更内容を議論してください。
