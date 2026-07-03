namespace TerminalHub.Terminal;

/// <summary>
/// セッションのターミナル状態を保持し、復元用の出力（xterm へ流し込む文字列）を生成する抽象。
/// </summary>
/// <remarks>
/// 本番実装は <see cref="EmulatedStateBuffer"/>（VTエミュレータ: グリッド保持＋ANSIシリアライズ）のみ。
/// repaint を上書きとして畳むことで、リサイズ/再読込時のスクロールバック二重化を根治する。
/// 旧来の生ストリーム方式 <see cref="RawStreamStateBuffer"/> はテスト用ベースラインとしてのみ残置。
/// </remarks>
public interface ITerminalStateBuffer
{
    /// <summary>
    /// ConPTY から届いた（UTF-8 デコード済みの）出力チャンクを取り込む。
    /// 進行中のリプレイキャプチャ（<see cref="BeginReplay"/>〜<see cref="EndReplay"/>）がある場合は
    /// テールにも記録し <c>true</c> を返す。<c>true</c> のとき呼び出し側はこのチャンクを
    /// xterm へ直接書き込んではならない（<see cref="EndReplay"/> のテール書き込みで届くため二重になる）。
    /// </summary>
    bool Append(string data);

    /// <summary>
    /// セッション切替/リサイズ/再接続時に、新しい xterm へ書き戻すための出力を生成する。
    /// 生ストリーム方式では貯めた生データそのまま。エミュレータ方式では確定状態を最小 ANSI にシリアライズしたもの。
    /// </summary>
    string SerializeForReplay();

    /// <summary>
    /// リプレイをアトミックに開始する。スナップショット生成と同時に以降の <see cref="Append"/> の
    /// テール記録を開始するため、「スナップショット取得〜書き込み完了」の間に届いたライブ出力が
    /// 消失・順序逆転しない。書き込み完了後は必ず <see cref="EndReplay"/> を呼ぶこと。
    /// </summary>
    ReplaySnapshot BeginReplay();

    /// <summary>テール記録を終了し、スナップショット以降に届いた出力を順序どおり返す。</summary>
    string EndReplay(ReplaySnapshot snapshot);

    /// <summary>保持状態を破棄する。</summary>
    void Clear();

    /// <summary>
    /// 端末サイズの変更を通知する。エミュレータ方式は**現在の画面内容を破棄**して新サイズの空画面にする
    /// （スクロールバックは保持。ConPTY がリサイズ直後にビューポート全体を再送してくる前提で、
    /// 再送分による二重化を防ぐ）。生ストリーム方式では何もしない。
    /// 必ず ConPtySession.Resize の**前**に呼ぶこと（再送データより先に画面を空にするため）。
    /// </summary>
    void Resize(int cols, int rows);

    /// <summary>現在保持しているデータ量の目安（文字数）。診断・UI 表示用。</summary>
    int Size { get; }
}
