# MCP セッション間メッセージング

TerminalHub 本体プロセスに HTTP MCP サーバーを同居させ、**別のエージェント（Claude Code / Codex 等）から、TerminalHub が管理中の既存セッションへメッセージを送れる**ようにする機能です。

主なユースケース: **Claude で仕様を書いてファイル化し、その絶対パスを Codex のセッションへ送って実装させる** といったエージェント間の受け渡し（オーケストレーション）。

---

## 設計方針（最小構成）

| 方針 | 内容 |
|---|---|
| spawn なし | 子セッションは作らない。宛先は **既存セッションのみ**（暴走ガード不要） |
| 集約なし | 結果待ち(wait)・読み取り(read)はしない。投げっぱなし。完了は TerminalHub 本体の LED/通知で人間が気づく |
| エンベロープ/自己識別なし | 本文だけ送る。送信元明示や応答要否は将来「呼び出し元フラグ」で足す |
| サーバーは状態を持たない | 渡されたフラグ（`submit` 等）に素直に従うだけ |

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
TerminalHub の「MCP自動登録」はこの方式を使っている（後述）。

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

### `send_to_session`

指定した既存セッションのターミナルへメッセージを1件送る（投げっぱなし・応答は待たない）。

**引数**

| 引数 | 型 | 既定 | 説明 |
|---|---|---|---|
| `target` | string | （必須） | 宛先。セッション GUID、または表示名（完全一致・大文字小文字無視） |
| `message` | string | （必須） | 送る本文。改行を含む長文は避け、短い指示＋ファイルの絶対パスを推奨 |
| `submit` | bool | `true` | 末尾に Enter(`\r`) を送って実行を確定するか。`false` なら入力欄に流し込むだけ |

**返り値**: `SendResult`

| フィールド | 型 | 説明 |
|---|---|---|
| `success` | bool | 送信できたか |
| `message` | string | 結果メッセージ |

**`success=false` になるケース**（いずれも例外にせず結果で返し、呼び出し側にリトライ判断を委ねる）

- 宛先セッションが見つからない
- 宛先が **ユーザーの許可/選択待ち（`waiting_user_input`）** → submit の Enter が承認プロンプトを誤確定させる恐れがあるため送信しない。`ready` になってから再試行（単なる作業中は `ready` 扱いで送信可＝相手CLIのキューに積まれる）
- 宛先が **未起動（`not_ready` / ConPTY 未接続）** → 自動起動はしない。ユーザーに起動を依頼し、`ready` を確認してから再送

---

## 典型フロー（Claude → Codex）

1. Claude 側で仕様を書き、ファイル（例: `C:\work\spec.md`）に保存する。
2. `list_sessions` で Codex セッションを探す（例: `terminalType="CodexCLI"`）。
3. 対象が `ready` なら `send_to_session` で
   `target=<GUID or 表示名>`, `message="C:\work\spec.md の内容で実装して"`, `submit=true` を送る。
4. Codex 側で処理が走る。完了は TerminalHub 本体の LED / 通知で人間が確認する。

---

## 注意点

- **ConPTY 制約**: 実際の送信テスト（ターミナルへの書き込み）は実機で行うこと。
- **antiforgery**: 既存の `/api/hook`（JSON POST）は `UseAntiforgery` 下でも通っている実績があり、MCP の POST も通る見込み。もし `/mcp` への POST が 400 になる場合は `app.MapMcp("/mcp").DisableAntiforgery()` にする。
- **セキュリティ**: ローカル利用前提。無認証で `/mcp` を公開するため、localhost 以外へバインドを広げる際は再評価すること。

---

## 接続設定の自動配置（試験機能・既定OFF）

セッション起動時に、対応CLIへ `terminalhub` MCP サーバーを自動で繋ぐ試験機能（設定「特殊」タブ）。CLI ごとに手段が異なる。

- **Claude Code → 起動オプション `--mcp-config "<JSONパス>"`**。ユーザーの設定ファイル（`.mcp.json` / `~/.claude.json`）は**一切書き換えない**。
  JSON は TerminalHub 自身のデータ領域 `%LOCALAPPDATA%\TerminalHub\mcp-config-<ポート>.json` に置き、コマンドラインにはパスだけを乗せる。
  OFF に戻せば次の起動から繋がらなくなり、残骸も残らない。
- **Codex → 起動オプション `-c mcp_servers.terminalhub.url=<URL>`**。設定ファイルへの書き込みは不要で、既存 MCP とマージされる。
  値は TOML としてパースされ、失敗すればリテラル文字列として扱われるため URL は引用符なしでそのまま渡せる。
  ユーザーが `extra-args` / `custom-args` に手書きで `-c mcp_servers.terminalhub.url=...` を入れている場合はそちらを優先する。
- ポートは実行中の値を反映。
- 実装: `TerminalHub/Services/McpConfigService.cs`（Claude 用 JSON 生成と URL 組み立て）、
  `TerminalHub/Constants/TerminalConstants.cs`（`BuildClaudeCodeArgs` / `BuildCodexArgs`）、
  `TerminalHub/Services/SessionManager.cs`（`ResolveClaudeMcpConfigPath` / `GetCodexMcpUrl`）。設定は `AppSettings.Experimental.AutoRegisterMcp`。

> 旧バージョン（〜v1.0.70）は Codex について `<folder>/.codex/config.toml` の `[mcp_servers.terminalhub]` へ書き込んでいた。
> その残骸は TerminalHub からは削除しないので、不要なら利用者が消す。Codex はプロジェクト階層の `config.toml` も読む
> （`config_loader` の project layer。信頼済みプロジェクトのみ）が、優先順位は `Session flags (-c)` > `User config` なので
> 本機能が ON の間は起動オプションが勝ち、無害。

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
  そのため、**旧バージョンが作業フォルダに書き残した `.mcp.json` が残っていても無害**で、
  常に起動中の正しいポートへ繋がる。移行のために古い `.mcp.json` を消して回る必要はない
  （不要なら利用者が消せばよいだけ）。

回帰テスト: `TerminalHub.Terminal.Tests/ClaudeArgumentsTests.cs`（引用符・並び順・退行）。

## 今後の拡張候補（未実装）

- 送信元を包むエンベロープ／自己識別、応答要否フラグ
- 結果の集約（wait / read）
- 宛先セッションの状態変化（`ready` 化）を待つオプション
