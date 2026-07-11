# UI・送信が突然遅くなる問題の調査メモ

作成: 2026-07-11 / 状態: **原因切り分け中（計測ログ仕込み済み・再現待ち）**

## 症状（ユーザー報告）

- きっかけ不明だが、**UIの更新も送信も含めてすべての操作が突然とても遅くなる**ことがある（通信が詰まっている印象）。
- **ローカル接続がメイン**なので、回線帯域が原因とは考えにくい。
- **使い続けているとその状態になる**。**放っておくと回復することもある**。
- 「裏で通信しているやつがいっぱいいるのでは」という体感。

## 前提アーキテクチャ

Blazor Server + ConPTY + xterm.js。1ブラウザ = 1 Circuit = 1本のWebSocket。ターミナル出力もキー入力もボタン操作も**すべて同じ1本のSignalR接続**を通る。

## 調査でわかったこと

### 帯域飽和説は後退

当初は「アクティブセッションの大量出力（64KB即時フラッシュ連発、`ConPtyService.cs:435-441`）が単一SignalR接続を飽和させる」を疑ったが、**ローカル接続では物理帯域は詰まらない**ため主因からは後退。ただしサーバ側の処理コストとしては残る。

関連: SignalRの送信方向（サーバ→クライアント）にはサイズ制限もバックプレッシャも未設定（`Program.cs:64-67` は受信1MB上限のみ）。`MaximumParallelInvocationsPerClient` は既定=1（クライアント→サーバ呼び出しは直列処理）。

### 単純な購読リークは見つからなかった（作りは良い）

「`+=` しっぱなしで `-=` 忘れ」型の明白なリークは**なし**。
- `Root.razor` のSingletonイベント購読（`SessionManager.OnSessionsChanged`・`HookNotificationService.OnHookNotification`）は「先に `-=` してから `+=`」＋`DisposeAsync`で解除、の二重ガード。
- `DotNetObjectReference`（`dotNetRef`・`terminalDotNetRef`）は両方Dispose済み。
- タイマー（ConPTYフラッシュ16ms、ハングタイムアウト検出、デバウンスCTS）はセッション/再起動単位でStop+Dispose対称。
- 診断コレクション（statusChangeHistory=500、bellChangeHistory=200、hookEventLog=300）は上限付きキューで頭打ち。

## 有力仮説：「ゾンビCircuit」の累積

症状3点（**帯域無関係・使うと悪化・放置で回復**）をすべて説明できる唯一の仮説。

Blazor Serverは**WebSocketが切れて再接続するたびに新しいCircuitを作る**。古いCircuitは即死せず、**既定で3分間（`DisconnectedCircuitRetentionPeriod`）サーバに保持**される。

- `SessionManager.OnSessionsChanged`（Singleton）と `HookNotificationService.OnHookNotification`（Singleton）に、**各Circuitがハンドラを登録**している。
- `OnSessionsChanged` は超高頻度で発火（**セッション初回データ受信ごと** `SessionManager.cs:552`、メモ更新、作成/削除など）。
- そのため**生きているCircuitの数だけ、1回のイベントで再描画がディスパッチ**される。ゾンビCircuitが3体積めば再描画が3倍。
- `Root.razor:457-461` は死んだCircuitにもfire-and-forget（`_ = InvokeWithCircuitCultureAsync(...)`）で投げ続け、CPUを空費する。

### 症状との対応

| ユーザーの体感 | ゾンビCircuit説での説明 |
|---|---|
| ローカルでも遅い（帯域無関係） | 帯域ではなくディスパッチャのCPU/キュー輻輳だから |
| 使い続けると悪化 | ネット瞬断・PCスリープ・タブ放置などで再接続が起きるたびゾンビCircuitが積む |
| 放っておくと回復する | **3分経つとゾンビが破棄され購読が減る**（← 決め手）。逆に言えば、再接続直後に生存数が一時的に増えるのは正常。**減らずに積み上がり続ける**のが異常 |
| 裏で通信してるやつが多い印象 | 死んだCircuitへの多重発火そのもの |

## 仕込んだ計測ログ（診断用・未コミット）

`ConPtyConnectionService`（Scoped＝Circuitごとに1個）に**生存インスタンス数カウンタ**を追加。1インスタンス = 1 Circuit。

```
[CircuitLife] Circuit created (tag=a1b2c3d4). 生存Circuit数=1
[CircuitLife] Circuit disposed (tag=a1b2c3d4). 生存Circuit数=0
```

`tag` はCircuitごとの短縮ID（複数タブ・再接続の区別用）。

### 変更ファイル
- `TerminalHub/Services/ConPtyConnectionService.cs`
  - 静的 `_liveInstanceCount`（Interlockedで増減）と `_circuitTag` を追加。
  - コンストラクタでIncrement＋ログ、DisposeでDecrement＋ログ。
- ビルド: 0警告0エラー確認済み。
- **診断用の一時変更。コミットしていない**（原因確定後に対策込みでブランチ/PR化する方針）。

## 確認手順（運用フロー）

1. ローカルビルド → インストールして、運用インスタンスに反映。
2. 普段どおり運用して再現を待つ。
3. 遅くなったら `[CircuitLife]` ログを確認:
   ```powershell
   Get-Content "$env:LOCALAPPDATA\TerminalHub\logs\*.log" | Select-String "CircuitLife" | Select-Object -Last 50
   ```
   （開発起動時は `logs-dev` 等になる場合あり）

### 読み方

| 観測 | 意味 |
|---|---|
| 生存Circuit数がおおむねタブ数ぶん。再接続直後だけ最大3分間 +1〜 の一時増加 | **正常**。切断Circuitは既定3分保持されるので、更新・瞬断・スマホ復帰の直後に一時的に増えるのは過渡状態。3分以内に減れば問題なし |
| 閉じた／放置したのに減らず、**積み上がり続ける**（タブを閉じて3分以上経っても減らない） | **ビンゴ**。破棄されない古いCircuitが積んでいる＝多重発火が原因確定 |
| いったん増えたが 数分後に数が減った | 3分保持→破棄で回復しているのが `tag` 突き合わせで見える（＝過渡増加。これ自体は正常挙動） |

**判定の勘所**: 「一時的に2になったか」ではなく「**減らずに積み上がり続けるか／タブを閉じても3分超えて減らないか**」で見る。再接続直後の短時間の増加は良性なので、それをビンゴと誤判定しないこと。

ログを持ってくる時は、遅いと感じた**時刻のメモ**と、`created`/`disposed` **両方**が入る範囲で。

## 原因が確定した場合の対策候補（未着手）

- `DisconnectedCircuitRetentionPeriod` 短縮 / `DisconnectedCircuitMaxRetained` を絞る。
- Singletonイベントのハンドラ側で「アクティブなCircuitか」ガードして、死んだCircuitへのディスパッチを止める。
- `OnSessionsChanged` / hook通知の再描画をデバウンス経由に統一する（現状デバウンスは出力解析ステータスのみ）。

## 未精査で残っている領域（別筋の候補）

- `TerminalHub.Terminal/EmulatedStateBuffer.cs` / `TerminalGrid.cs` のスクリーンバッファ上限（無制限成長の可能性を完全には否定できていない）。
- xterm `scrollback:10000` × セッション数 のJSヒープ。
- `NotificationService`（Scoped）内部のタイマー/購読、`MqttService`（Singleton）の内部購読。
