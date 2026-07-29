# MCP セッション間メッセージング

TerminalHub 本体プロセスに HTTP MCP サーバーを同居させ、**別のエージェント（Claude Code / Codex 等）から、TerminalHub が管理中の既存セッションへメッセージを送れる**ようにする機能です。

主なユースケース: **Claude で仕様を書いてファイル化し、その絶対パスを Codex のセッションへ送って実装させる** といったエージェント間の受け渡し（オーケストレーション）。

---

## 設計方針（最小構成）

| 方針 | 内容 |
|---|---|
| spawn なし | 子セッションは作らない。宛先は **既存セッションのみ**（暴走ガード不要） |
| 集約なし | 結果待ち(wait)・読み取り(read)はしない。投げっぱなし。完了は TerminalHub 本体の LED/通知で人間が気づく |
| エンベロープなし | 本文だけ送る。送信元明示や応答要否は将来「呼び出し元フラグ」で足す（自己識別・本人証明は導入済み → 後述の proof） |
| サーバーは会話状態を持たない | 渡されたフラグ（`submit` 等）に素直に従うだけ。メッセージの追跡・キューは持たない（本人検証用の proof、および依頼IDで引く**状況札 = ContextSummary** は例外として持つ → 後述） |

長文は本文に直接流さず、**ファイルに書いて絶対パスだけ送る**運用を推奨（ターミナル入力の化け・切り捨てを避けるため）。

---

## サーバー構成

- ASP.NET Core（Blazor Server）本体に `AddMcpServer().WithHttpTransport()` で同居。
- エンドポイント: **`/mcp`**（`app.MapMcp("/mcp")`）
- トランスポートは **HTTP 一択**。SessionManager（Singleton）の共有状態へ直結する必要があるため、stdio（別プロセス）では届かない。
- 実装: `TerminalHub/Mcp/SessionMessagingTools.cs`、登録は `TerminalHub/Program.cs`。

### ポート運用

| ポート | 用途 |
|---|---|
| 5080 | 常用環境（インストール版） |
| 5081 | Visual Studio 実行（launchSettings 既定） |
| 5082 | **開発版（MCP 検証用）** ※ 本ドキュメントの想定 |

開発版の起動（PowerShell、5082・launchSettings を無視して起動）:

```powershell
cd C:\Users\info\source\repos\TerminalHub-worktree-1
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project TerminalHub/TerminalHub.csproj --no-launch-profile --urls http://localhost:5082
```

---

## クライアント登録

### `.mcp.json`（プロジェクトスコープ）

MCP クライアント（別の Claude Code）を起動するフォルダに配置:

```json
{
  "mcpServers": {
    "terminalhub": {
      "type": "http",
      "url": "http://localhost:5082/mcp"
    }
  }
}
```

### CLI で登録する場合

```powershell
claude mcp add --transport http terminalhub http://localhost:5082/mcp
```

### 起動オプションで一時的に繋ぐ場合（設定ファイルを汚さない）

`--mcp-config` は既存の MCP 設定にマージされる（`--strict-mcp-config` を付けなければ他のサーバーも生きる）。
TerminalHub の「TerminalHub MCP」設定はこの方式で CLI へ繋いでいる（後述）。

```powershell
claude --mcp-config "C:\path\to\terminalhub-mcp.json"
```

---

## 提供ツール

### `list_sessions`

TerminalHub が管理中の（アーカイブでない）セッション一覧を返す。`send_to_session` の宛先を選ぶために使う。

**引数**（いずれも任意・部分一致・大文字小文字無視）

| 引数 | 型 | 説明 |
|---|---|---|
| `terminalType` | string? | 種別で絞り込み（`ClaudeCode` / `CodexCLI` / `GeminiCLI` / `Terminal` / `Antigravity` / `Grok`）。未指定なら全種別 |
| `nameContains` | string? | 表示名に含む文字列で絞り込み |
| `folderContains` | string? | 作業フォルダパスに含む文字列で絞り込み |

**返り値**: `SessionSummary` の配列

| フィールド | 型 | 説明 |
|---|---|---|
| `sessionId` | string | セッション GUID |
| `name` | string | 表示名 |
| `terminalType` | string | 種別 |
| `folderPath` | string | 作業フォルダ |
| `status` | string | 送信可否を表す。`ready`（受付中=送信可。作業中でも相手CLIのキューに積まれる） / `waiting_user_input`（ユーザーの許可/選択待ち=送信不可） / `not_ready`（ConPTY未接続=起動が必要・送信不可） |
| `hasCard` | bool | 自己紹介カードの有無（本文は含めない。`get_card` で取得）。カード持ちだけ読みに行くための当たり付け用 |
| `memo` | string | セッションのメモ（UI の一覧に出るのと同じ短い札）。worktree レーン運用ではディスパッチャが「タスク無し」＝空きレーンを判別するのに使う（`docs/worktree-lane-operation.md` 参照） |

### `send_to_session`

指定した既存セッションのターミナルへメッセージを1件送る（投げっぱなし・応答は待たない）。

**引数**

| 引数 | 型 | 既定 | 説明 |
|---|---|---|---|
| `target` | string | （必須） | 宛先。セッション GUID、または表示名（完全一致・大文字小文字無視） |
| `message` | string | （必須） | 送る本文。改行を含む長文は避け、短い指示＋ファイルの絶対パスを推奨 |
| `submit` | bool | `true` | 末尾に Enter(`\r`) を送って実行を確定するか。`false` なら入力欄に流し込むだけ |
| `contextId` | string? | なし | ContextSummary（後述）の紐づけ。`"new"`=発行して紐づけ（結果で ID が返る） / 既存 ID=続報として紐づけ / 未指定=従来どおり |

**返り値**: `SendResult`

| フィールド | 型 | 説明 |
|---|---|---|
| `success` | bool | 送信できたか |
| `message` | string | 結果メッセージ |
| `contextId` | string? | `contextId="new"` で送信したときだけ発行された ID が入る（それ以外は null）。以降のポーリングはこの ID で `get_context` する |

**`success=false` になるケース**（いずれも例外にせず結果で返し、呼び出し側にリトライ判断を委ねる）

- 宛先セッションが見つからない
- 宛先が **ユーザーの許可/選択待ち（`waiting_user_input`）** → submit の Enter が承認プロンプトを誤確定させる恐れがあるため送信しない。`ready` になってから再試行（単なる作業中は `ready` 扱いで送信可＝相手CLIのキューに積まれる）
- 宛先が **未起動（`not_ready` / ConPTY 未接続）** → 自動起動はしない。ユーザーに起動を依頼し、`ready` を確認してから再送

### `set_card` / `get_card`

セッションの **自己紹介カード**（「何ができるか」の短い自己申告。A2A Agent Card のローカル版）。
memo=「今なにをしているか」（動的）に対し、card=「何ができるか」（静的・長命）という姉妹機能。

**`set_card`** — 自分のカードを設定する

| 引数 | 型 | 説明 |
|---|---|---|
| `proof` | string | **本人証明**（環境変数 `TERMINALHUB_SESSION_PROOF` の値）。proof が本人検証と宛先特定を兼ねる |
| `card` | string | カード本文（数行の短文想定）。空文字でクリア。全体書き換え（部分更新なし） |

- **自分のみ設定可（機構的担保）**。proof は ConPTY 起動ごとに生成されるランダム値で、
  そのセッションの子プロセスだけが環境変数として知っている。proof の提示＝本人であり、
  他セッションのカードは書き換えようがない（当初の「GUID 自己申告＋仕様上の契約」から格上げ）
- 永続化はセッションと同じライフサイクル（SQLite の `Sessions.Card`。セッションが消えればカードも消える）。
  proof 自体は永続化せず、再起動のたびに変わる
- `set_memo` も同じ proof 認証（自分のメモのみ。他セッションのメモは UI から人間が編集する）

**`get_card`** — 指定 GUID のカードを取得する（誰のものでも読める）

| 引数 | 型 | 説明 |
|---|---|---|
| `sessionId` | string | 対象セッションの GUID（`list_sessions` で確認） |

**信頼モデル（A2A Agent Card と同じ）**: カードは自己申告＝「本人がそう名乗っている」以上の保証はしない。
古い可能性がある前提で、**宛先の当たりを付ける用途に限定**する（書いてある≠今も動く）。
詳細な真実は各プロジェクトの CLAUDE.md / メモリ側が正。

**A2A との用語対応**: TerminalHub の card は A2A Agent Card の `description`/`skills` に相当する。
A2A の `capabilities` フィールドは**プロトコル機能宣言**（streaming / pushNotifications 等）で
別物のため、用語衝突を避けて capabilities という名前は使っていない。

想定運用: 各セッションが起動時や役割変更時に自分で `set_card` しておき、
他のセッションは `list_sessions` → `get_card` で相手を選んで `send_to_session` する。

### `get_context` / `update_context` — ContextSummary（依頼の状況札）

**受信箱を持たない相手のための pull 経路**。`send_to_session` は push 型で、返信を受け取るには
自分がセッション（＝ターミナルという受信箱）を持っている必要がある。TerminalHub のセッションを
持たない外部 MCP クライアント（Claude Desktop、TerminalHub 外の CLI 等）が依頼の結果を
受け取れるように、依頼単位の「状況札」を ID で引けるようにする。

- `send_to_session` に `contextId="new"` を渡すと、サーバーが**推測不能な ID** を発行して
  ContextSummary を作成し、ツール結果で返す。届く本文の末尾には
  `[contextId: xxx — 状況・結果は update_context で共有]` が**サーバーによって自動付与**される
  （送信者の手書きに任せると書き忘れ事故が起きるため。改行ではなくスペース連結なのは
  TUI が改行を送信確定と誤解釈する事故を避けるため）
- **`update_context(contextId, summary, status?, proof?)`**: 受け手が状況・結果を書く。summary は
  **要約1枚の全体上書き**（履歴は積まれない）。長い成果物はファイルに書いてパスを載せる。
  status は `submitted / working / completed / failed / canceled`（**A2A TaskState 準拠**）。
  **セッション内から書くときは proof（`TERMINALHUB_SESSION_PROOF`）を渡す**と、
  「どのセッションが書いたか」が検証済みで記録される（`updatedBy`）。proof 無しでも書けるが
  **無記名**になる（外部クライアント用。アクセス制御ではなく検証済み署名として機能する。
  proof が不一致の場合は無記名として通さずエラー＝古い値の使い回しに気づかせる）
- **`get_context(contextId)`**: 依頼側がポーリングして読む。サーバーからの通知は無い。
  `updatedBy` で「本当に依頼先セッションが書いた結果か」を確認できる（null は無記名）
- **contextId は capability 兼用**（知っている＝読み書きできる。proof と同じ哲学で認証を別途作らない）
- 永続化: SQLite の `Contexts` テーブル（スキーマ v10）。終端状態（completed/failed/canceled）から
  **14日で自動削除**＋総数上限500（超過分は終端状態の古い順に削除。進行中は消さない）

**A2A との対応**: この contextId は A2A の `contextId`（最初のメッセージ送信時にサーバーが生成して
返す会話束の ID）に対応し、status は TaskState に対応する（1 context = 1 タスクの簡約。
Task オブジェクトは作らない）。モデルのコンテキストウィンドウとは無関係。

**作らないもの**（設計判断・滑り込み防止）: ID なしの一覧（capability モデルを崩す）、
claim / 担当割当（1対1依頼専用。「誰かやって」型はディスパッチャ側ルールの仕事）、
サーバー発の完了通知（集約ロジックの復活）、リトライ・期限監視（クライアント側の責務）。

**典型フロー（外部クライアント）**:
1. Desktop が `send_to_session(target=<レーン>, message="仕様は C:\work\spec.md", contextId="new")` → ID が返る
2. 受け手は本文末尾の contextId を見て作業し、`update_context` で status/summary を更新
3. Desktop は `get_context(id)` をポーリングして完了・結果を取得（受信箱ゼロで往復が完結）

---

### `list_commands` / `add_command` / `remove_command` — セッション専用コマンド

クイック送信バーのボタンを、セッション自身が登録できる。
これまでのツールが「セッションが自分の状態を申告する」方向（memo / card / context）だったのに対し、
これは**セッションが人間のために UI を生やす**方向のツール。
繰り返す操作に気づいたら自分でボタンを置いておき、次から人間がワンクリックで撃てるようにする。

| 引数（`add_command`） | 意味 |
|---|---|
| `proof` | 本人証明（必須） |
| `title` | ボタン名。**セッション専用コマンドの中で一意**（`remove_command` の指定に使う） |
| `type` | `"text"`（テキスト送信）/ `"key"`（キー送信） |
| `commandText` | `type="text"` の本文 |
| `keyName` | `type="key"` のプリセット名（`CtrlC` / `Escape` / `ArrowUp` / `ShiftTab` 等） |
| `groupName` | 同名を指定すると1つのドロップダウンにまとまる |
| `insertToInputOnly` | true なら送信せず入力欄へ流し込むだけ（人間が内容を確認してから送れる） |
| `propagateToChildren` | サブセッションにも出す。**親セッションでのみ有効**（子で立てても効かない旨を戻り値で伝える） |

**作らないもの / 決めたこと**

- **グローバル（設定のコマンド）は読み書きとも対象外**。全セッション・全 CLI に出る共有物なので、
  人間がローカルで試してから必要なら手動で持っていく運用に任せる
- **全体上書きにしない**（`set_card` とはここが違う）。人間も UI から編集する共有リストなので、
  上書きだと人間の編集を踏み潰す。`add` / `remove` / `list` に分ける
- **`remove_command` は AI が登録したものだけ消せる**（`CustomCommand.CreatedByAgent`）。
  人間が UI で編集するとフラグが落ち、以降 MCP からは消せなくなる。
  根拠は非対称性で、**AI が作ったものは同じ手順で作り直せるが、人間が調整したものは失うと戻せない**
- title の一意性は**セッション専用リストの中だけ**。グローバルや親から伝搬されたコマンドと
  同名になるのは許す（表示側が元々「同名を並べたい場合がある」として重複排除していないため）

`title` は前後の空白を落として扱う（`"Foo"` と `"Foo "` が別物になると、重複判定も
`remove_command` の完全一致も説明できない挙動になるため）。

> 注意: ストレージが LocalStorage モード（真実の保存先がブラウザ側）のときは、`set_memo` / `set_card` と同様に
> SQLite への UPDATE が空振りし、インメモリ更新＋UI 反映だけが効いてリロード後に消えることがある（既定は SQLite）。

人間が設定ダイアログを開いている間に `add_command` が走った場合、ダイアログ側は開いた時点の
スナップショットを持っているため、そのまま保存すると追加を消してしまう。保存時に「開いてから増えた
AI 登録コマンド」を取り込むようにしてある（逆に `remove_command` は復活する形になるが、
消えるより安全側で、対象は作り直せるものに限られる）。

コマンドは人間が中身を読まずにワンクリックで実行するものなので、
クイック送信バーのボタンには**実行内容の tooltip** を出す（`KeySequence` はキー名が見えているので付けない）。
設定ダイアログの一覧では、AI が登録したものにロボットアイコンが付く。

---

## 典型フロー（Claude → Codex）

1. Claude 側で仕様を書き、ファイル（例: `C:\work\spec.md`）に保存する。
2. `list_sessions` で Codex セッションを探す（例: `terminalType="CodexCLI"`）。
3. 対象が `ready` なら `send_to_session` で
   `target=<GUID or 表示名>`, `message="C:\work\spec.md の内容で実装して"`, `submit=true` を送る。
4. Codex 側で処理が走る。完了は TerminalHub 本体の LED / 通知で人間が確認する。

---

## 注意点

- **Codex の tool シェルへの環境変数透過**: Codex は `shell_environment_policy` 次第（`inherit=core` 等）で
  ConPTY が注入した環境変数を tool 実行シェルへ渡さないことがある。このため Codex 起動時に
  `-c shell_environment_policy.set.TERMINALHUB_SESSION_ID=<GUID>` と
  `-c shell_environment_policy.set.TERMINALHUB_SESSION_PROOF=<値>` を注入して確実に届けている
  （`set` はフィルタ後に変数を足す仕組みでユーザーのポリシー設定と衝突しない。同キーの手書き指定があればそちらを優先）。
  hook ブリッジ（`$env:TERMINALHUB_SESSION_ID` 参照）の空振り対策も兼ねる。
  なお `TERMINALHUB_SESSION_PROOF` という変数名に KEY/SECRET/TOKEN を含めないのは、
  Codex の `ignore_default_excludes`（該当語を含む変数の自動除外）を踏まないための意図的な選択。
- **ConPTY 制約**: 実際の送信テスト（ターミナルへの書き込み）は実機で行うこと。
- **antiforgery**: 既存の `/api/hook`（JSON POST）は `UseAntiforgery` 下でも通っている実績があり、MCP の POST も通る見込み。もし `/mcp` への POST が 400 になる場合は `app.MapMcp("/mcp").DisableAntiforgery()` にする。
- **セキュリティ**: ローカル利用前提。無認証で `/mcp` を公開するため、localhost 以外へバインドを広げる際は再評価すること。

---

## TerminalHub MCP の有効化（試験機能・既定OFF）

セッション起動時に、対応CLIへ `terminalhub` MCP サーバーを繋ぐ試験機能（設定「試験機能」タブ）。
どちらの CLI も**起動オプションで渡す**ので、ユーザーの設定ファイルは書き換えない。
この機能は新しいプロジェクト設定を残さないため、OFF に戻せば次の起動から繋がらなくなる
（旧バージョンが書いた設定は別途残る。後述の注記を参照）。手段は CLI ごとに異なる。

- **Claude Code → 起動オプション `--mcp-config "<JSONパス>"`**。ユーザーの設定ファイル（`.mcp.json` / `~/.claude.json`）は**一切書き換えない**。
  JSON は TerminalHub 自身のデータ領域 `%LOCALAPPDATA%\TerminalHub\mcp-config-<ポート>.json` に置き、コマンドラインにはパスだけを乗せる。
- **Codex → 起動オプション `-c mcp_servers.terminalhub.url=<URL>`**。設定ファイルへの書き込みは不要で、既存 MCP とマージされる。
  値は TOML としてパースされ、失敗すればリテラル文字列として扱われるため URL は引用符なしでそのまま渡せる。
  ユーザーが `extra-args` / `custom-args` に手書きで `-c mcp_servers.terminalhub.url=...` を入れている場合はそちらを優先する。
- ポートは実行中の値を反映。
- 実装: `TerminalHub/Services/McpConfigService.cs`（Claude 用 JSON 生成と URL 組み立て）、
  `TerminalHub/Constants/TerminalConstants.cs`（`BuildClaudeCodeArgs` / `BuildCodexArgs`）、
  `TerminalHub/Services/SessionManager.cs`（`ResolveClaudeMcpConfigPath` / `GetCodexMcpUrl`）。
  設定は `AppSettings.Experimental.EnableLocalMcp`。

> **旧バージョンの残骸について**（〜v1.0.70 は設定ファイルへ書き込む方式だった）
>
> - Claude Code → `<folder>/.mcp.json` の `mcpServers.terminalhub`
> - Codex → `<folder>/.codex/config.toml` の `[mcp_servers.terminalhub]`
>
> これらは TerminalHub からは削除しないので、不要なら利用者が消す。どちらも当時のポートを指しているため、
> **本機能を OFF にしている場合、残骸経由で古いポートへ接続を試みることがある**（ON の間は起動オプションが優先されるので無害）。
> Codex はプロジェクト階層の `config.toml` も読む（`config_loader` の project layer。信頼済みプロジェクトのみ）。
> 優先順位は `Session flags (-c)` > `User config`。Claude 側も `--mcp-config` が `.mcp.json`(project スコープ) より優先される。

### `--mcp-config` 方式のポイント（実測で確認）

- **マージであって置き換えではない**。`--strict-mcp-config` を付けない限り、ユーザーが自分で入れた MCP サーバーはそのまま生きる。
- **JSON をインライン文字列で渡すのは不可**。`ConPtyService` はコマンドラインを無加工で連結して `CreateProcess` へ渡すため、
  JSON 中の `"` が cmd.exe のパースで落ちて `Error: Invalid MCP configuration` になる。**必ずファイルパスで渡す**。
- **パスは引用符で囲む**。`ConPtyService` はクォートを足さないので、`%LOCALAPPDATA%` にスペースを含むユーザー名だと
  空白で分割されて `MCP config file not found` になる。Codex の `--add-dir "<dir>"` と同じ流儀。
- **JSON ファイルはポート毎に分ける**。中身は実質ポートそのものなので、5080(常用) と 5082(開発版) の同時起動で
  共有すると後勝ちで上書きし合い、セッションが意図しないインスタンスへ繋がる（過去に 5080/5081 の二重定義で実害あり）。
- **`--mcp-config` は `.mcp.json`(project スコープ) より優先される**。同名 `terminalhub` が両方にある場合、
  `--mcp-config` の値が勝つ（実測: `.mcp.json` に生きている 5080、`--mcp-config` に死んでいる 5999 を置くと
  `failed` になる＝後者が採用されている。逆向きも確認済み）。
  そのため、**本機能が ON の間は**、旧バージョンが作業フォルダに書き残した `.mcp.json` が残っていても
  無害で、常に起動中の正しいポートへ繋がる（移行のために消して回る必要はない）。
  OFF のときは `--mcp-config` が付かないため残骸だけが効く点に注意（上の注記を参照）。

回帰テスト: `TerminalHub.Terminal.Tests/ClaudeArgumentsTests.cs`（引用符・並び順・退行）。

## 今後の拡張候補（未実装）

- 送信元を包むエンベロープ／自己識別、応答要否フラグ
- 結果の集約（wait / read）
- 宛先セッションの状態変化（`ready` 化）を待つオプション
