# プレビュー版作って

master（または指定ブランチ）の最新を GitHub Actions で先行ビルドし、成果物を取得・展開して「起動するだけ」の状態にする。正式リリース前に master の最新を即試したいときに使う。

「プレビュー版作って」「プレビューして」等で発動。

## 重要な前提
- **起動は行わない**: Claude のツール経由で TerminalHub を起動すると ConPTY が壊れるため、最後はユーザーに起動を依頼する（展開先のパスを案内するだけ）。
- 取得するのは `TerminalHub-dev-build`（zip 展開でそのまま動く publish 出力）。インストーラーが欲しいと言われたときだけ `TerminalHub-installer-preview` を取得する。

## 手順

### 1. ビルド実行（workflow_dispatch）
```bash
gh workflow run "Build and Release" --repo zio3/TerminalHub --ref master -f build_installer=true
```
- ブランチ指定があれば `--ref <branch>` を差し替える。

### 2. Run の特定
数秒待ってから直近の Run を一覧し、**今 dispatch した workflow_dispatch の in_progress な Run の ID** を特定する。
```bash
gh run list --workflow "Build and Release" --repo zio3/TerminalHub --limit 3
```

### 3. 完了まで監視
```bash
gh run watch <runId> --repo zio3/TerminalHub --exit-status
```
- 失敗したら、その旨と失敗ステップを報告して中止する。

### 4. 取得・展開
`gh run download` で dev-build を展開先に取得する（gh が zip を自動展開する）。
```bash
gh run download <runId> --repo zio3/TerminalHub -n TerminalHub-dev-build --dir "C:/Users/info/TerminalHub-preview"
```
- 展開先の既定は `C:\Users\info\TerminalHub-preview`。
- 既存のプレビューインスタンスが起動中だと `TerminalHub.exe` がロックされて展開に失敗する。その場合はユーザーに「起動中のプレビュー版を閉じてください」と依頼してから再実行する（**常用している別インスタンスは止めない**）。

### 5. 起動案内
展開先の `TerminalHub.exe` のパスを提示し、ユーザーに起動を依頼する。
- 例: 「`C:\Users\info\TerminalHub-preview\TerminalHub.exe` を起動してください」
- ConPTY の都合で Claude 側からは起動しない旨を一言添える。
- このビルドに含まれる master 上の主な変更点（直近のマージ PR）を簡潔に伝えると親切。
