# Fixtures — 実ターミナル出力のキャプチャ置き場

VTエミュレータのパリティ検証に使う「ConPTY からの生出力（UTF-8 デコード済みの文字ストリーム）」を置く。

## 形式
- `*.raw` … ConPTY 出力チャンクを連結したもの（UTF-8, エスケープシーケンス込みのそのまま）。
  エミュレータの `Append(string)` にそのまま流し込める。

## 取得方法（実環境でのキャプチャ）
1. TerminalHub の設定 → 開発ツール → 「生ストリームをキャプチャ」を ON。
2. 再現させたいシナリオを実操作（例: Claude Code を起動 → TUI 表示 → セッション切替/リサイズで二重化を誘発）。
3. キャプチャ OFF に戻す。
4. `%LOCALAPPDATA%\TerminalHub\captures\` に出力された `{sessionId}-{timestamp}.raw` を、
   意味の分かる名前（例: `claude-tui-resize-dup.raw`）でこのフォルダへコピー。

> ConPTY は Claude Code ツール経由の起動だと壊れるため、キャプチャは常用/デバッグ実行のユーザー操作で行う。

## 命名の目安
- `claude-tui-*` … Claude Code の alt-screen TUI
- `plain-*` … 通常ターミナル出力（スクロールのみ）
- `cjk-*` … 全角/絵文字を含む出力（EAW 幅の検証用）
- `*-resize-dup.*` … リサイズ/再読込で二重化が起きるシナリオ

## local/ フォルダ（git 管理外）
実環境キャプチャは**セッションの表示テキスト（作業内容）をそのまま含む**ため、リポジトリにはコミットしない。
`local/` に置いたファイルは .gitignore で除外され、存在する環境でのみ `LocalCaptureParityTests` が実行される
（無ければ Skip）。現在使っているローカルフィクスチャ:
- `claude-tui-repaint-dup.raw` … Claude Code のメイン画面 repaint 型（ESC[H＋ESC[K フレーム×10、
  CJK 6千字・truecolor 多数）。「recap:」が生ストリームで 20 回残る二重化サンプル。
