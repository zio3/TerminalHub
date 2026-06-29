# プレビュー版作って

master（または指定ブランチ）の最新を GitHub Actions で先行ビルドし、**インストーラー**を取得して「あとはユーザーがインストールするだけ」の状態にする。正式リリース前に master の最新を常用環境へ入れて試したいときに使う。

「プレビュー版作って」「プレビューして」等で発動。

## 重要な前提
- **取得するのはインストーラー（`TerminalHub-installer-preview`）のみ**。zip 展開の dev-build は取得しない。
- **インストーラーの実行（インストール）は Claude 側では行わない**: 対話 UI を持つ exe であり、また ConPTY の都合もあるため、最後はユーザーにインストーラーの実行を依頼する（取得先のパスを案内するだけ）。

## 手順

### 1. ビルド実行（workflow_dispatch）
```bash
gh workflow run "Build and Release" --repo zio3/TerminalHub --ref master -f build_installer=true
```
- `build_installer=true` 必須（インストーラーを生成させる）。
- ブランチ指定があれば `--ref <branch>` を差し替える。

### 2. Run の特定
数秒待ってから、**今 dispatch した workflow_dispatch の in_progress な Run の ID** を特定する。
```bash
gh run list --workflow "Build and Release" --repo zio3/TerminalHub --event workflow_dispatch --limit 3 --json databaseId,status,createdAt
```

### 3. 完了まで監視
```bash
gh run watch <runId> --repo zio3/TerminalHub --exit-status
```
- 失敗したら、その旨と失敗ステップを報告して中止する。

### 4. 取得
`gh run download` でインストーラーを取得先に落とす（gh が zip を自動展開し、中の `.exe` が出てくる）。
```bash
gh run download <runId> --repo zio3/TerminalHub -n TerminalHub-installer-preview --dir "C:/Users/info/TerminalHub-preview"
```
- 取得先の既定は `C:\Users\info\TerminalHub-preview`。事前に中身を掃除してから取得する。
- 取得後、フォルダ内の `.exe`（Inno Setup 出力のセットアップ実行ファイル）の実パスを確認する。

### 5. インストール案内
取得した**インストーラー `.exe` のパス**を提示し、ユーザーに実行（インストール）を依頼する。
- 例: 「`C:\Users\info\TerminalHub-preview\TerminalHub-Setup-x.y.z.exe` を実行してインストールしてください」
- Claude 側からは実行しない旨（対話 UI ＋ ConPTY の都合）を一言添える。
- 常用している TerminalHub が起動中だと、インストール時に実行ファイルがロックされて失敗することがある。その場合は「常用インスタンスを閉じてからインストールしてください」と依頼する（**Claude が他インスタンスを止めない**）。
- このビルドに含まれる master 上の主な変更点（直近のマージ PR）を簡潔に伝えると親切。
