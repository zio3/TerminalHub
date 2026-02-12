# TerminalHub

[![GitHub Release](https://img.shields.io/github/v/release/zio3/TerminalHub)](https://github.com/zio3/TerminalHub/releases/latest)
[![License](https://img.shields.io/badge/license-ISC-blue.svg)](LICENSE)

**Windows ネイティブで動く、AI CLI セッション管理 GUI ツール**

TerminalHub は、Claude Code / Gemini CLI / Codex CLI の複数セッションを GUI で一元管理できる Windows デスクトップアプリケーションです。Windows ConPTY API を使用し、tmux なしでセッション管理・状態監視・通知を実現します。

**[最新版をダウンロード](https://github.com/zio3/TerminalHub/releases/latest)** | インストーラーを実行するだけで使用できます（.NET のインストール不要）

---

## なぜ TerminalHub？

macOS / Linux では tmux + claude-tmux 等で複数セッションを管理できますが、Windows ネイティブ環境では tmux が使えず、セッション管理の選択肢が限られています。

| 課題 | TerminalHub の解決策 |
|------|---------------------|
| Windows Terminal のタブではセッション名・状態が区別できない | セッション名・処理状態・経過時間を一覧表示 |
| tmux が使えないのでセッション管理ツールがない | GUI でセッション作成・切替・アーカイブ |
| 長時間タスクの完了を画面に張り付いて確認している | 処理完了の通知 + Webhook 連携 |
| git worktree の管理が全て手動 | GUI から worktree セッションをワンクリック作成 |
| AI CLI ごとにオプション指定が面倒 | チェックボックスでオプション設定（承認モード、resume 等） |

---

## 主な機能

### マルチセッション管理

- 複数のターミナルセッションを同時管理（セッション数の制限なし）
- セッション名・メモの設定、検索フィルター
- セッション状態の自動保存と復元
- セッションのアーカイブ / 復元 / 一括削除
- マルチブラウザ対応（同一セッションを複数ブラウザから操作可能）

### AI CLI 統合

| 機能 | Claude Code | Gemini CLI | Codex CLI |
|------|:-----------:|:----------:|:---------:|
| セッション管理 | o | o | o |
| 処理状態のリアルタイム検出 | o | o | - |
| トークン使用量 / 処理時間の表示 | o | o | - |
| 処理完了通知 | o | o | o |
| オプション GUI 設定 | o | o | o |

- 処理中 / 待機中 / 入力待ちをリアルタイムで検出・表示
- 非アクティブセッションの処理完了を通知ベルで表示
- Webhook 通知でスマートフォンや外部サービスへ連携可能

### Git 統合

- Git リポジトリの自動検出とブランチ表示
- 未コミット変更のインジケーター
- GUI から Git Worktree セッションを作成
- 親セッションと worktree セッションの親子関係表示

### Webhook 通知

セッションの処理開始・完了を外部に通知できます。

```json
{
  "eventType": "complete",
  "sessionName": "セッション名",
  "terminalType": "ClaudeCode",
  "elapsedSeconds": 123,
  "timestamp": "2025-01-01T00:00:00Z",
  "folderPath": "C:\\path\\to\\folder"
}
```

### その他

- コマンド履歴（Ctrl+Up/Down でナビゲーション）
- ターミナル内 URL の自動検出とクリック対応
- 存在しないディレクトリのセッションに警告表示
- セッション初期化エラーのトースト通知

---

## システム要件

- Windows 10 / 11

### 開発者向け追加要件
- .NET 10.0 SDK
- Node.js（オプション）

## インストール

### インストーラーを使用（推奨）

1. [最新版をダウンロード](https://github.com/zio3/TerminalHub/releases/latest)
2. `TerminalHub-Setup-x.x.x.exe` を実行
3. インストール完了後、スタートメニューまたはデスクトップから起動

### 開発者向け（ソースから実行）

```powershell
git clone https://github.com/zio3/TerminalHub.git
cd TerminalHub
dotnet run --project TerminalHub/TerminalHub.csproj

# または npm を使用
npm start
```

## 使い方

### セッションの作成
1. 「新しいセッションを作成」ボタンをクリック
2. 作業フォルダを選択
3. セッションタイプを選択（Terminal / Claude Code / Gemini CLI / Codex CLI）
4. 必要に応じてオプションを設定

### セッションの管理
- 左サイドバーでセッション一覧を確認・切替
- 検索ボックスでセッション名やメモで絞り込み
- 歯車アイコンからメモ設定・アーカイブ
- 右クリックメニューから Worktree セッション作成

### キーボードショートカット
| キー | 動作 |
|------|------|
| `Ctrl + Up/Down` | コマンド履歴のナビゲーション |
| `Ctrl + C` | 選択テキストのコピー（選択なしの場合は中断） |
| `Ctrl + V` | ペースト |

## 技術スタック

- **フロントエンド**: Blazor Server, XTerm.js
- **バックエンド**: ASP.NET Core (.NET 10.0)
- **ターミナル**: Windows ConPTY API
- **JavaScript**: XTerm.js, WebLinksAddon
- **スタイリング**: Bootstrap 5
- **インストーラー**: Inno Setup

## 開発

開発に関する詳細情報は [CLAUDE.md](CLAUDE.md) を参照してください。

## トラブルシューティング

### ターミナルが表示されない
- Windows 10/11 を使用しているか確認
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
