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

## 追加観測（2026-07-26）: ブラウザは表示中コンテキストを守る
24本オープンまで押し上げた時点で生存WebGL=16（ハード頭打ち＝Chrome の max_active_webgl_contexts 既定）、
退避8本はすべて背景セッション。**表示中セッションは webglAlive=true のまま**だった。
→ ブラウザは「今表示・描画中」のコンテキストを優先的に生かし、eviction は背景の古いもの(LRU)に向かう。
そのため「多数開いて稼働セッションを見る」だけでは表示中の凍結を**確実には**強制できない（案外無事に見える）。
実機で見えた「特定ターミナルだけ固まる」は、背景だった／切替直後でまだ"描画中"と認識される前の
コンテキストが犠牲になった瞬間や、スリープ復帰・他GPUアプリ等の外的圧力が重なったケースと考えられる。
→ 視覚での強制再現に固執せず、**仕込んだ [WebGL] ログで実運用中の再発を自動捕捉**するのが確実。
   再発時 `[WebGL] contextlost session=<GUID>` が出るので、固まったセッションを事後に特定できる。
   根治（WebGLを表示中1本に絞る）で eviction 自体が消えるのは単体再現HTMLで実証済み。

## 前面ストリーム停止は WebGL とは別問題（2026-07-26 実測で判明）
実機検証で「前面（表示中）セッションのストリームが止まる」症状は WebGL では説明が弱いと判明:
- 24本開いても表示中コンテキストは保護され、生の WebGL 圧迫を足しても前面は喪失しなかった。
- ping（毎秒1行の定常出力）を前面で流しても 24本ロード下で止まらなかった＝baseline デリバリ経路は健全。
- 「切替で戻ると完了していた」→ サーバ側エミュレータバッファは正しい＝**表示だけの問題・データ欠落なし**。
→ 前面ストリーム停止の主犯から WebGL を外し、デリバリ経路（xterm への live 書き込み）を疑う。

観察されている現象は少なくとも2つ（別物として扱う）:
- **①ストリーム凍結**: スピナー等が数秒止まって進む。直接起因は未確定。長時間使用で再現しやすい。
- **②全量書き直し→下端スクロール**: たまに画面が全リセットされ最下部へスクロールし直す。
  = `ApplyConPtyResize` が `rowsGrew`(行数増加)時に走らせる「エミュレータ再同期」（`\x1bc`全リセット＋
  バッファ全量書き直しを `_terminalWriteLock` 下で実行）。`snapshot.Content` は出力が溜まるほど育つ
  （実測 9KB→43KB）ので、長時間ほど1回が重くなる=freeze が伸びる。なぜ見続けているだけで行数増加が
  繰り返されるのかは未解明（`source` を要観測）。

## 追加した診断ログ（ブランチ feat/webgl-context-logging）
実運用中の自然発生を待って捕まえる。grep 目印:
- `[WriteStall] live書き込みがロック待ち {ms}ms` … ①用。前面 live 書き込みがロックで止められた時間。
- `[Resize] ConPTY適用 ... src={source}` … ②用。行数増加リサイズのトリガー（何が起こしたか）。
- `[Resize] エミュレータ再同期完了 ... 所要{ms}ms` … ②用。全リセット+全書き直しの freeze 実長。
- `[WebGL] contextlost session=...` … WebGL 由来か（前面GUIDと一致するか）。
症状が出たら該当時刻を grep し、どの目印が出ているかで①②/WebGL/ロックを切り分ける。

## 備考
- 単体再現HTMLは file:// では navigate 不可。`python -m http.server` で配信して開く。
- Claude が ConPTY を起動すると壊れるため、TerminalHub 本体の起動はユーザーが行う。
  Claude はブラウザ操作(Claude in Chrome)とログ観察のみ担当する。
