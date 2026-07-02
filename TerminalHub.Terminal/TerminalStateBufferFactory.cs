namespace TerminalHub.Terminal;

/// <summary>
/// ターミナル状態バッファのファクトリ兼フィーチャーフラグ。
/// アプリ起動時/設定保存時に <see cref="UseEmulator"/> を設定し、
/// セッション作成時に <see cref="Create"/> で実装を選ぶ。
/// フラグ変更は「その後に作られるバッファ」から効く（既存セッションは作成時の方式を維持）。
/// </summary>
public static class TerminalStateBufferFactory
{
    /// <summary>true でVTエミュレータ方式、false（既定）で従来の生ストリーム方式。</summary>
    public static bool UseEmulator { get; set; }

    /// <summary>エミュレータの初期グリッドサイズ（作成後は Resize で実端末サイズに追随する）。</summary>
    public static int DefaultCols { get; set; } = 120;
    public static int DefaultRows { get; set; } = 30;

    public static ITerminalStateBuffer Create()
        => UseEmulator
            ? new EmulatedStateBuffer(DefaultCols, DefaultRows)
            : new RawStreamStateBuffer();
}
