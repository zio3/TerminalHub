# TerminalHub

Windows向けのWebベースターミナルエミュレータ。AIアシスタント統合とマルチセッション対応。

**⚠️ 重要な注意事項**
- このツールは個人利用を前提として開発されています
- ターミナルを通じて実行されるコマンドには制限がなく、システムに影響を与える危険なコマンドも実行可能です
- 使用は自己責任でお願いします

## 機能

- **マルチセッションターミナル管理**: 複数のターミナルセッションを同時に作成・管理
- **AIアシスタント統合**: Claude Code (Anthropic CLI) と Gemini CLI の組み込みサポート
- **Git統合**: リアルタイムでGitリポジトリのステータス、ブランチ情報、worktreeサポート
- **Webベースインターフェース**: 任意のモダンWebブラウザからターミナルにアクセス
- **リアルタイム同期**: 複数のブラウザから同じセッションにマスター/スレーブモードで接続可能
- **セッション永続化**: ブラウザ再起動後もセッション情報を保存・復元

## 技術スタック

- **バックエンド**: .NET 9.0, ASP.NET Core, Blazor Server
- **ターミナル**: Windows ConPTY API によるネイティブターミナルエミュレーション
- **フロントエンド**: xterm.js v5.3.0, Bootstrap 5.3.3
- **リアルタイム通信**: SignalR によるWebSocket接続

## 必要条件

- Windows OS (ConPTY APIのため必須)
- .NET 9.0 SDK 以降
- PowerShell (起動スクリプト用)

### オプションツール
- [Claude CLI](https://claude.ai/tools) - Claude Code統合用
- [Gemini CLI](https://cloud.google.com/vertex-ai/docs/generative-ai/gemini-cli) - Gemini統合用
- Git - リポジトリステータス機能用

## インストール

1. リポジトリをクローン:
```bash
git clone https://github.com/YOUR_USERNAME/TerminalHub.git
cd TerminalHub
```

2. アプリケーションをビルド:
```bash
dotnet build
```

3. アプリケーションを実行:
```powershell
# バックグラウンドで起動 (デフォルト)
.\start.ps1

# フォアグラウンドで起動
.\start.ps1 -Foreground

# ブラウザを開かずに起動
.\start.ps1 -NoBrowser
```

アプリケーションはデフォルトで `http://localhost:5000` で利用可能になります。

## 設定

`appsettings.json` を編集してカスタマイズ:

```json
{
  "TerminalHub": {
    "MaxSessions": 10,
    "DefaultCols": 120,
    "DefaultRows": 30,
    "MaxBufferLines": 2000
  }
}
```

## 使い方

### セキュリティに関する注意

- このツールは完全なシステムアクセス権限を持つターミナルです
- `rm -rf`、`format`、レジストリ編集など、システムを破壊する可能性のあるコマンドも実行できます
- 公開ネットワークでの使用は推奨しません
- localhost以外からのアクセスを許可する場合は十分注意してください

### 新しいセッションの作成

1. ターミナルタブの「+」ボタンをクリック
2. 以下から選択:
   - **通常セッション**: 特定ディレクトリでの標準ターミナル
   - **Claude Codeセッション**: Claude Code統合付きターミナル
   - **Geminiセッション**: Gemini CLI統合付きターミナル

### キーボードショートカット

- `Ctrl+C`: 選択テキストをコピー / 割り込み信号を送信
- `Ctrl+V`: クリップボードから貼り付け
- `Ctrl+Shift+V`: 貼り付け (代替)
- `F11`: フルスクリーン切り替え

### セッション管理

- セッションはブラウザのlocalStorageに自動保存
- 各タブの「×」ボタンでセッションを閉じる
- ページを更新すると前回のセッションを復元

### AIアシスタント機能

Claude CodeまたはGeminiセッション使用時:
- リアルタイムトークン使用量モニタリング
- 処理時間追跡
- 入出力方向インジケーター
- AIツールコマンドの自動検出

## 開発

### プロジェクト構造

```
TerminalHub/
├── Components/
│   └── Pages/
│       └── Root.razor          # メインUIコンポーネント
├── Services/
│   ├── ConPtyService.cs        # Windows ConPTY ラッパー
│   ├── SessionManager.cs       # セッション管理
│   └── OutputAnalyzers/        # AI出力パーサー
├── Models/
│   ├── SessionInfo.cs          # セッションデータモデル
│   └── CircularLineBuffer.cs   # 効率的なバッファ管理
├── wwwroot/
│   ├── js/
│   │   └── terminal.js         # xterm.js統合
│   └── css/
└── start.ps1                   # 起動スクリプト
```

### ソースからのビルド

```bash
# デバッグビルド
dotnet build

# リリースビルド
dotnet build -c Release

# セルフコンテインド発行
dotnet publish -c Release -r win-x64 --self-contained
```

### テストの実行

```bash
dotnet test
```

## トラブルシューティング

### よくある問題

1. **ConPTY初期化失敗**
   - Windows 10 バージョン1809以降で実行していることを確認
   - Windows Terminalがインストールされているか確認

2. **ポートがすでに使用中**
   - 起動スクリプトは自動的に利用可能なポートを見つけます
   - 前のインスタンスがまだ実行中の場合は `stop.ps1` を確認

3. **セッションが永続化されない**
   - ブラウザでCookieとlocalStorageを有効にする
   - ブラウザの開発者コンソールでエラーを確認

## コントリビューション

1. リポジトリをフォーク
2. フィーチャーブランチを作成 (`git checkout -b feature/amazing-feature`)
3. 変更をコミット (`git commit -m 'Add some amazing feature'`)
4. ブランチにプッシュ (`git push origin feature/amazing-feature`)
5. プルリクエストを開く

## ライセンス

このプロジェクトはMITライセンスの下でライセンスされています - 詳細は[LICENSE](LICENSE)ファイルを参照してください。

## 謝辞

- [xterm.js](https://xtermjs.org/) - 優れたターミナルエミュレータ
- [Windows Terminal](https://github.com/microsoft/terminal)チーム - ConPTY API
- [Anthropic](https://www.anthropic.com/) - Claude Code CLI
- [Google](https://cloud.google.com/) - Gemini CLI

## サポート

問題、質問、コントリビューションについては、GitHubでイシューを開いてください。