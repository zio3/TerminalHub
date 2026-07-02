namespace TerminalHub.Terminal;

/// <summary>
/// ターミナル状態バッファのファクトリ。常にVTエミュレータ方式を生成する
/// （生ストリーム方式 <see cref="RawStreamStateBuffer"/> はテスト用ベースラインとしてのみ残置）。
/// </summary>
public static class TerminalStateBufferFactory
{
    /// <summary>エミュレータの初期グリッドサイズ（作成後は Resize で実端末サイズに追随する）。</summary>
    public static int DefaultCols { get; set; } = 120;
    public static int DefaultRows { get; set; } = 30;

    public static ITerminalStateBuffer Create()
        => new EmulatedStateBuffer(DefaultCols, DefaultRows);
}
