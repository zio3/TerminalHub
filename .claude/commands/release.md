# リリース

TerminalHubの新バージョンをリリースする。以下の手順をすべて順番に実行すること。

## 手順

### 1. 変更内容の確認
- `git log --oneline` で前回タグからのコミットを確認
- リリースノートに含める変更内容を整理

### 2. バージョン更新
以下の2ファイルのバージョンを更新する（前回バージョン+1）：
- `TerminalHub/TerminalHub.csproj` の `<Version>`
- `installer/TerminalHub.iss` の `#define MyAppVersion`

### 3. コミット・プッシュ
- バージョン更新をコミット: `chore: バージョンをX.X.Xに更新`
- `git push origin master`

### 4. GitHubリリース作成
`gh release create` で `--notes` にリリースノートを記載。フォーマット：
```markdown
## 新機能
- 機能の説明

## 改善
- 改善の説明

## バグ修正
- 修正の説明
```
該当するセクションのみ記載する。

### 5. Discordお知らせ投稿
TerminalHubサーバーの「お知らせ」チャンネル（ID: `1488867349246513264`）にリリース案内を投稿する。
- `mcp__discord__discord_execute` の `messages.send` を使用
- ユーザー向けに主要なメリットを簡潔に伝える（技術的な詳細は不要）
- GitHubリリースページへのリンクを含める
