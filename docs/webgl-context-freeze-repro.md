# WebGL コンテキスト枯渇によるターミナル凍結 — 再現手順メモ

## 症状
特定セッションのターミナルだけ、稼働中インジケーター等のアニメーションが数秒ピタッと止まり、
数秒後に別フレームへジャンプする。UI 全体やフォーカス中のターミナルは無事。

## 原因（確定済み）
xterm の `WebglAddon` を「開いたターミナル」ごとに1個ずつ保持し、`hideAllTerminals` は
`display:none` にするだけで WebGL コンテキストを破棄しない。開いたターミナルが増えて
ブラウザの同時 WebGL コンテキスト上限（ソフト上限。単体再現では ~20 本あたり）を超えると、
ブラウザが古いコンテキストから `webglcontextlost` で破棄 → そのターミナルが描画停止 →
復帰(`webglcontextrestored`)で再描画、を数秒周期で繰り返す。

単体再現(`scratchpad/webgl-context-limit.html`, Claude in Chrome)での実測:
- 現状挙動(hideで残す): 50本作成 → 生存WebGL 46 / `contextlost` 4回、初回は20本目あたり。
- 修正案(hideでdispose): 50本作成でも生存WebGL 常に1 / `contextlost` 0回。term本体は全数生存。

## 計測ログ（このブランチで追加）
- 出力先: `%LOCALAPPDATA%\TerminalHub\logs\terminalhub-YYYYMMDD.log`
- grep: `[WebGL]`
- 形式: `[WebGL] {eventType} session={guid} 生存WebGL={n} 総ターミナル={m}`
  - `eventType`: `load` / `contextlost`(-1) / `contextlost-raw`(生canvasイベント) /
    `contextrestored-raw` / `dispose`
- 期待: `load` で生存WebGLが増えていき、上限付近で `contextlost` が出始める。

## 実機での発現手順（2026-07-26 Claude in Chrome で確定）
環境: localhost:5081（Development / sessions-dev7.db）。ログ出力先は `logs-dev/terminalhub-*.log`（dev は logs-dev、prod は logs）。

1. TerminalHub を起動（このブランチ。JS が更新されていれば `[WebGL]` 行が出る）。
2. 一覧のセッションを1本ずつ開いて生存WebGLを積み上げる（開く＝そのターミナル生成。閉じずに次へ）。
   このデバッグ機はセッション13本だったので、続けて「新しいセッションを作成」→ターミナル種別（cmd.exe）を
   `作成`連打で追加し、総ターミナルを17本超へ押し上げた。
3. 観測された閾値: **総ターミナル17本で最初の `contextlost-raw`、18本で生存WebGL=16（2本 eviction）**。
   - 最初に喪失したのは `6f2ab2d4`（最初にアクティブだった TerminalHub）と `3f0e1e6e`（最初に開いた detached）
     ＝**開いた順に古いものから奪われる（LRU）**。
   - この閾値は GPU/ブラウザ/1コンテキストの canvas サイズに依存する。TerminalHub のターミナルは大きい
     （例 201x98 セル）ので、単体再現HTMLの小さいタイル(~20本)より少ない本数で発現しうる。
4. ログ確認: `grep "[WebGL]" logs-dev/terminalhub-YYYYMMDD.log`。
   `load` で生存WebGLが増え、17〜18本で `contextlost-raw`（生canvas）→ `contextlost`（addon onContextLoss, -1）が出る。

## 視覚的な最終確認の注意（musical chairs）
奪われるのは古い（＝今表示していない背景の）ターミナル。**背景ターミナルを開き直すと cleanup→再生成で
新しいコンテキスト（MRU）になり、その場では直る＝別の古いターミナルへ問題が移る**（椅子取り）。
そのため「凍結中の背景ターミラルをそのまま覗く」ことはできない。視覚的な凍結→ジャンプは、
**多数のターミナルを開いた圧力下で、いま表示している稼働中セッションのコンテキストが一時的に
lost/restored した瞬間**に現れる（実機コンソールに出ていた webglcontextlost/restored の周期がそれ）。
最終確認は「17本超を開いた状態のまま、稼働中（スピナーや連続出力）のセッションを表示して、
数秒の凍結→ジャンプが出るか」を人手で観察する。

## 備考
- 単体再現HTMLは file:// では navigate 不可。`python -m http.server` で配信して開く。
- Claude が ConPTY を起動すると壊れるため、TerminalHub 本体の起動はユーザーが行う。
  Claude はブラウザ操作(Claude in Chrome)とログ観察のみ担当する。
