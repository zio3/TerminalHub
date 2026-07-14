# Codex 権限プリセット設計方針

## 文書の目的

TerminalHub から Codex CLI を起動する際の権限設定を、一般利用者にも理解・選択しやすいプリセットとして提供するための設計方針をまとめる。

Codex には、ファイルアクセス範囲、承認方法、Windows サンドボックスの実装方式、コマンドのネットワークアクセス、Web 検索など、互いに独立した設定がある。これらを利用者へそのまま提示すると複雑すぎるため、通常利用では目的別プリセットを選び、必要な場合だけ詳細設定を開く構成を採用する。

## 検討の経緯

### 当初の要望

目指していた利用体験は次のとおり。

- ワークスペース内の通常作業は、ほぼ自動で進めたい
- ファイル編集、ビルド、テスト、GitHub 操作などで逐一確認されたくない
- 危険性の高い操作だけは止めてほしい
- 安全機構をすべて解除する YOLO は、常用したくない

この目的に近い構成として、当初は次を使用していた。

```text
--sandbox workspace-write
--ask-for-approval on-request
```

しかし、Windows 環境ではワークスペース内の通常のファイル編集でも `apply_patch` が失敗し、サンドボックス外での再実行や承認を求める場合があった。編集内容そのものは安全でも、パッチ適用のたびに確認が発生し、利用体験が大きく損なわれていた。

### Auto-review の導入

次の設定を追加すると、境界を越える操作を人間ではなく審査エージェントが確認できるようになり、操作は大幅に滑らかになった。

```text
-c approvals_reviewer="auto_review"
```

Auto-review は権限を拡張する機能ではない。従来なら人間へ表示される承認要求を別の審査エージェントへ送り、低リスク・中リスクの操作を自動判定する機能である。

ただし、ワークスペース内の通常編集まで審査へ流れる状態は、本来の使い方ではない。Auto-review は症状を緩和するが、不要な審査、待ち時間、ログ出力、モデル使用量が発生する。

### Windows サンドボックス方式の調査

Windows 版 Codex には、サンドボックスの許可範囲とは別に、Windows 上で制約を強制する方式がある。

```toml
[windows]
sandbox = "unelevated" # または "elevated"
```

調査時の環境では `[windows]` が未指定であり、実際には `unelevated` が選択されていた。ログには次のエラーが記録されていた。

```text
windows unelevated restricted-token sandbox cannot enforce split writable root sets directly
```

このエラーは、対象リポジトリがワークスペース外だったという意味ではない。対象の TerminalHub リポジトリ自体には書き込み権限があったが、`unelevated` の制限付きトークン方式では複数の書き込みルートを正確に強制できず、通常編集が失敗していた。

### Elevated への切り替え

次を明示的に設定して Codex を再起動した。

```toml
[windows]
sandbox = "elevated"
```

初回は Windows のユーザーアカウント制御（UAC）が表示された。セットアップ完了後、サンドボックス内のコマンドは次の専用低権限ユーザーで動作することを確認した。

```text
MAIN\codexsandboxoffline
```

`elevated` という名前は、Codex のコマンドを管理者として実行するという意味ではない。管理者権限が必要なのは、専用ユーザー、ACL、ファイアウォール規則などを準備する初回セットアップである。通常のコマンドは専用の低権限ユーザーで実行される。

この方式では、許可されたワークスペース内の書き込み範囲を Windows の ACL で明確に強制できるため、通常編集が余計な承認へ流れにくくなることが期待できる。

### ネットワークと Web 検索の整理

Codex には、異なる2種類のネットワーク利用がある。

1. `gh`、`git fetch`、`npm install` など、サンドボックス内コマンドのネットワークアクセス
2. Codex が提供する Web 検索機能

コマンドのネットワークアクセスは次で制御する。

```text
-c sandbox_workspace_write.network_access=true
```

Web 検索は次で有効にする。

```text
--search
```

`CodexSandboxOffline` と `CodexSandboxOnline` は、ネットワーク設定に応じて使い分けられる専用ユーザーである。`elevated` 自体がネットワーク接続不能という意味ではない。

開発支援ツールでは GitHub、パッケージマネージャー、公式ドキュメントなどの利用頻度が高く、ネットワークを禁止した状態を推奨値にすると承認疲れにつながる。そのため TerminalHub の推奨プリセットでは、コマンドのネットワークアクセスと Web 検索を有効にする。

### 安全側へ倒しすぎる問題

安全設定が厳しすぎると、通常作業でも確認や失敗が頻発する。その結果、利用者が安全機構を段階的に調整するのではなく、面倒を避けるために YOLO を選ぶ可能性が高くなる。

TerminalHub では、次の中間地点を分かりやすく提示する。

```text
workspace-write
+ elevated
+ on-request
+ auto_review
+ network_access=true
+ Web検索
```

これは囲いをなくす設定ではない。信頼できる囲いを作り、その内側で通常作業を自動化する設定である。

## 設計方針

### 基本方針

- 一般利用者には個々の Codex オプションを直接選ばせない
- 最初に目的別プリセットを選択させる
- 上級者向けに詳細設定を用意する
- TerminalHub は利用者のグローバル `config.toml` を編集しない
- 選択内容は Codex の起動引数として毎回渡す
- TerminalHub が設定を明示する場合は、グローバル設定より TerminalHub の選択を優先する
- YOLO は通常設定と視覚的・操作的に分離する

## プリセット

### 1. Codex 標準

Codex 本体の既定値と、利用者の `config.toml` をそのまま使用する。

TerminalHub から次の設定を上書きしない。

- サンドボックスモード
- 承認方法
- Windows サンドボックス方式
- コマンドのネットワークアクセス
- Web 検索設定

表示例：

```text
Codex標準
Codex本体とconfig.tomlの設定を使用します。
```

### 2. 自動・推奨

通常の開発作業向けの既定プリセットとする。

```text
--sandbox workspace-write
--ask-for-approval on-request
-c approvals_reviewer="auto_review"
-c windows.sandbox="elevated"
-c sandbox_workspace_write.network_access=true
--search
```

期待する動作：

- ワークスペース内の編集、ビルド、テストを自動実行する
- `git`、`gh`、パッケージマネージャーなどのネットワーク利用を許可する
- Web 検索を利用可能にする
- ワークスペース外など、境界を越える操作は Auto-review で審査する
- 危険性の高い操作は拒否、またはユーザー確認へ送る
- ファイル書き込み範囲はワークスペース内に維持する

表示例：

```text
自動・推奨
ワークスペース内の開発作業とネットワーク利用を自動化します。
範囲外の操作はAIが安全性を審査します。
信頼できるリポジトリでの利用を推奨します。
```

`elevated` の初回利用時には次を案内する。

```text
初回のみ、Windowsサンドボックスの準備で
ユーザーアカウント制御（UAC）が表示される場合があります。
```

### 3. 詳細設定

個別の設定を変更したい利用者向け。いずれかを変更した場合、プリセット表示を `カスタム` に切り替える。

#### ファイルアクセス

- 読み取り専用：`read-only`
- ワークスペース：`workspace-write`
- フルアクセス：`danger-full-access`

#### Windows サンドボックス

- Elevated：`windows.sandbox="elevated"`
- Unelevated：`windows.sandbox="unelevated"`
- Codex の設定に従う：起動引数を渡さない

#### 承認方法

- ユーザーへ確認：`approvals_reviewer="user"`
- AI による自動審査：`approvals_reviewer="auto_review"`
- 確認しない：`--ask-for-approval never`

#### コマンドのネットワークアクセス

- 許可：`sandbox_workspace_write.network_access=true`
- 禁止：`sandbox_workspace_write.network_access=false`

#### Web 検索

- ライブ検索：`--search`
- Codex の設定に従う：起動引数を渡さない
- 無効：対応する設定値を明示する

### 4. 無制限・YOLO

サンドボックスと承認を無効化する。隔離されたVMや、壊してよい専用環境など、利用者が意図して境界を外す場合だけ使用する。

```text
--dangerously-bypass-approvals-and-sandbox
```

表示例：

```text
無制限（YOLO・非推奨）
サンドボックスと承認を無効化し、
コンピューター全体への操作を許可します。
```

UI 上の要件：

- 赤色など、他のプリセットと明確に異なる表示にする
- 初回選択時に警告を表示する
- 可能であれば「このセッションだけ適用」を基本にする
- 有効中は画面へ常時 YOLO バッジを表示する
- 信頼済みワークスペースかどうかを併記する

## 起動引数の組み立て

`ProcessStartInfo.ArgumentList` を使用し、設定名と値を個別の引数として追加する。

```csharp
args.Add("-c");
args.Add("windows.sandbox=\"elevated\"");

args.Add("-c");
args.Add("sandbox_workspace_write.network_access=true");
```

セッションを復帰する場合も、設定上書きは `resume` より前へ配置する。

```text
codex
  --sandbox workspace-write
  --ask-for-approval on-request
  -c approvals_reviewer="auto_review"
  -c windows.sandbox="elevated"
  -c sandbox_workspace_write.network_access=true
  --search
  resume --last
```

TerminalHub の設定を確実に優先させる項目は、ON の場合だけでなく OFF の場合も値を明示する。

```text
-c sandbox_workspace_write.network_access=false
```

ただし `Codex標準` を選択した場合は、該当する上書き引数を一切渡さない。

## 設定変更時の挙動

Windows サンドボックス方式と、サンドボックス内コマンドのネットワークアクセスは、Codex プロセスの起動時に決まる。

実行中に設定が変更された場合、TerminalHub は次の操作を提供する。

```text
設定を適用してセッションを再起動
```

処理の流れ：

1. 現在の Codex プロセスを正常終了する
2. 新しいプリセットから起動引数を組み立てる
3. `resume --last` を付けて Codex を再起動する
4. 同じ会話を継続する

単発のネットワーク操作は、オフラインのセッションでも承認・Auto-review 経由で実行できる場合がある。ただし、常時オンラインへ切り替える場合は再起動が必要である。

## Elevated と Unelevated の位置づけ

### Unelevated

- 現在の Windows ユーザーから制限付きトークンを作成する
- 初期 UAC が不要で互換性を取りやすい
- Windows ユーザー固有のツールや資格情報を利用しやすい
- 複雑な書き込みルートを強制できない場合がある
- 通常操作が不要な承認へ流れる可能性がある
- 互換モード、または Elevated が利用できない場合のフォールバックとして扱う

### Elevated

- Codex 専用の低権限ユーザーでコマンドを実行する
- 初回セットアップに UAC が必要になる場合がある
- ACL とファイアウォール規則などで境界を強制する
- ワークスペース内の通常作業を安全に自動化しやすい
- Windows 資格情報マネージャー、DPAPI、App Execution Alias、ユーザー単位で導入されたツールなどで互換性問題が出る可能性がある
- 推奨プリセットで使用する

## 実装・検証上の注意点

### Git の所有者検証

Elevated では Git コマンドも専用ユーザーで動くため、リポジトリの所有者が通常ユーザーの場合、Git が次を表示する可能性がある。

```text
fatal: detected dubious ownership in repository
```

検討時の環境でも、`MAIN\info` が所有する TerminalHub リポジトリを `MAIN\CodexSandboxOffline` から参照した際に再現した。

TerminalHub が利用者のグローバル Git 設定へ無断で `safe.directory` を追加してはならない。Codex 側の対応状況を確認し、必要であればプロセス単位の Git 設定、対象リポジトリだけへの明示的な許可、または利用者への案内を検討する。

### 認証情報

`CodexSandboxOnline` は通常ユーザーとは別アカウントである。ネットワークを許可しても、Windows 資格情報マネージャーなどへ保存された `gh`、Git、SSH 等の認証情報を利用できるとは限らない。

検討時の環境では、次の結果を確認した。

```text
MAIN\CodexSandboxOffline で実行
  gh.exe の起動とバージョン表示は成功
  gh auth status は token is invalid と表示して失敗

MAIN\info で承認付きのサンドボックス外実行
  gh auth status は keyring の認証情報を使用して成功
```

通常ユーザー側の結果：

```text
Logged in to github.com account zio3 (keyring)
```

この場合、`gh auth status` が表示する次の案内は、実際の未ログインを意味しない。

```text
The token in default is invalid.
To re-authenticate, run: gh auth login
```

実際には通常ユーザーの keyring にあるトークンは有効であり、専用サンドボックスユーザーから復号・参照できないことが原因である。利用者へ再ログインを求めると、問題を解決できないだけでなく、既存の正常な認証状態を変更する可能性がある。

また、`sandbox_workspace_write.network_access=true` にして `CodexSandboxOnline` を使用しても、解決するのは通信経路だけである。別ユーザーの keyring を参照できない問題は残る。

```text
CodexSandboxOnline
  GitHubへの通信: 可能
  MAIN\infoのkeyring参照: 原則不可
```

したがって、認証済みの `gh` や Git Credential Manager 等を必要とする操作は、承認または Auto-review を経由して、サンドボックス外の通常ユーザーとして実行する運用を基本候補とする。

```text
通常コマンド
  → Elevatedサンドボックス内で実行

認証済みkeyringを必要とするgh/Git操作
  → 承認付きでMAIN\infoとして実行
```

Codex がこの環境差を知らない場合、サンドボックス内の失敗を「GitHubへログインしていない」と誤認し、`gh auth login` を案内する可能性がある。同じ会話を `resume` で継続している間は一度説明すれば判断材料として利用できるが、新規セッション、長い会話の圧縮、エラーメッセージの強い誘導などを考えると、会話上の記憶だけに依存するのは不安定である。

将来的には、TerminalHub が Codex の起動時またはセッション開始時に、次のような短い環境コンテキストを渡すことを検討する。

```text
このWindows環境ではElevatedサンドボックスを使用しています。
gh/Gitの認証情報は通常ユーザーのkeyringに保存されているため、
CodexSandboxOffline/Onlineからは読み取れません。

gh auth statusがinvalidを返しても再ログインを要求せず、
認証が必要なgh/Git操作は承認付きでサンドボックス外の
通常ユーザーとして再実行してください。
認証情報自体を表示・コピーしないでください。
```

ただし、起動時の環境コンテキスト注入は現状の TerminalHub の機能範囲外であり、本プリセット実装の必須要件には含めない。別機能として設計方法、既存の `AGENTS.md` や利用者設定との優先順位、会話への表示方法、再開セッションへの適用方法を検討する。

代替または補助案として、TerminalHub が次の組み合わせを検出した際に、再ログインではなくホスト実行を案内する方法も考えられる。

```text
実行ユーザーが CodexSandboxOffline / CodexSandboxOnline
かつ
gh auth status が invalid
```

この自動検出についても、現状のプリセット実装とは分離した将来検討事項とする。

推奨プリセットの受け入れ試験には、少なくとも次を含める。

- `gh auth status`
- `gh pr view`
- `git fetch`
- `git push` はテスト用リポジトリでのみ確認
- HTTPS 認証と SSH 認証の違いを確認
- サンドボックス内の認証失敗後、通常ユーザーでの承認付き再実行が成功することを確認
- 通常ユーザー側の認証が有効な場合、不要な `gh auth login` を案内しないことを確認

### PowerShell プロファイル

専用ユーザーまたは制限付き環境では、`winget.exe` などのユーザー固有コマンドが見つからない場合がある。PowerShell プロファイルでモジュールを無条件に読み込んでいると、コマンド実行のたびにエラーが表示される。

プロファイル側では、依存コマンドの存在を確認してから読み込むことが望ましい。

```powershell
if (Get-Command winget.exe -ErrorAction SilentlyContinue) {
    Import-Module -Name Microsoft.WinGet.CommandNotFound
}
```

### サイレントフォールバックを避ける

Elevated のセットアップに失敗した場合、TerminalHub が利用者へ知らせず Unelevated や YOLO へ切り替えてはならない。

- 現在の実効モードを表示する
- セットアップ失敗理由を表示する
- Unelevated で再試行する選択肢を提示する
- YOLO への自動切り替えは行わない

### 実効状態の確認

Windows サンドボックス方式は、サンドボックス内で `whoami` を実行すると確認できる。

```text
MAIN\info
  → Unelevated

MAIN\CodexSandboxOffline
MAIN\CodexSandboxOnline
  → Elevated
```

設定画面または診断画面で、設定値だけでなく実効状態も確認できるようにすることが望ましい。

## 受け入れ基準

### 自動・推奨プリセット

- 初回 UAC 後、2回目以降の通常起動では UAC を要求しない
- `whoami` が Codex 専用ユーザーを示す
- ワークスペース内でファイルの作成、更新、削除ができる
- 通常の `apply_patch` が承認なしで実行できる
- ワークスペース外への書き込みが自動では許可されない
- `dotnet build`、`npm run build` などの通常コマンドを実行できる
- `git` と `gh` が所有者検証・認証を含め正常に利用できる
- ネットワークを利用するパッケージ復元が実行できる
- Web 検索を利用できる
- 危険性の高い操作が無条件に実行されない
- Auto-review の承認・拒否結果を利用者が確認できる

### 詳細設定

- 各設定が対応する起動引数へ正しく変換される
- OFF の設定も明示的に反映される
- `Codexの設定に従う` では余計な引数を渡さない
- 設定変更後に再起動が必要であることを表示する
- 再起動後も `resume --last` で会話を継続できる

### YOLO

- 通常プリセットから明確に区別されている
- 警告を確認しないと有効化できない
- 有効中であることが常時表示される
- TerminalHub が自動的に YOLO へ切り替えない

## まとめ

TerminalHub の推奨値は、単に安全側へ最大限倒すのではなく、通常作業が止まらない範囲で境界を維持する。

```text
自動・推奨
  workspace-write
  elevated
  on-request
  auto_review
  network_access=true
  Web検索あり
```

一般利用者にはこのプリセットを提示し、ネットワークを禁止したい場合や承認方式を変更したい場合だけ詳細設定を使用する。サンドボックスと承認を完全に無効化する YOLO は、明確な警告付きの例外的な選択肢として扱う。
