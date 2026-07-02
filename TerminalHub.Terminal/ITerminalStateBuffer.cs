namespace TerminalHub.Terminal;

/// <summary>
/// セッションのターミナル状態を保持し、復元用の出力（xterm へ流し込む文字列）を生成する抽象。
/// </summary>
/// <remarks>
/// 現状の実装は生ストリームをそのまま貯める <see cref="RawStreamStateBuffer"/>（既存挙動）。
/// 将来はここに VTエミュレータ実装（グリッド保持＋ANSIシリアライズ）を差し込み、
/// リサイズ/再読込時の repaint 追記によるスクロールバック二重化を根治する。
/// フィーチャーフラグで両実装を並存させ、問題があれば即座に生ストリーム方式へ戻せるようにする。
/// </remarks>
public interface ITerminalStateBuffer
{
    /// <summary>ConPTY から届いた（UTF-8 デコード済みの）出力チャンクを取り込む。</summary>
    void Append(string data);

    /// <summary>
    /// セッション切替/リサイズ/再接続時に、新しい xterm へ書き戻すための出力を生成する。
    /// 生ストリーム方式では貯めた生データそのまま。エミュレータ方式では確定状態を最小 ANSI にシリアライズしたもの。
    /// </summary>
    string SerializeForReplay();

    /// <summary>保持状態を破棄する。</summary>
    void Clear();

    /// <summary>
    /// 端末サイズの変更を通知する。エミュレータ方式はグリッドを追随させる（直後に ConPTY が repaint を送る前提）。
    /// 生ストリーム方式では何もしない。
    /// </summary>
    void Resize(int cols, int rows);

    /// <summary>現在保持しているデータ量の目安（文字数）。診断・UI 表示用。</summary>
    int Size { get; }
}
