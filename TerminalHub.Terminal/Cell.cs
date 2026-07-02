namespace TerminalHub.Terminal;

/// <summary>
/// グリッドの1セル。内容（書記素クラスタ）と描画スタイルを保持する。
/// 全角文字は先頭セルに Width=2 で格納し、直後のセルは <see cref="WideTrailer"/>（幅0の継続セル）にする。
/// </summary>
public struct Cell
{
    /// <summary>セル内容。null は空白（未描画または消去済み）。</summary>
    public string? Text;

    /// <summary>表示幅。1=半角, 2=全角（先頭セル）, 0=全角の後続セル。</summary>
    public byte Width;

    public CellColor Foreground;
    public CellColor Background;
    public CellAttributes Attributes;

    /// <summary>空白セル（幅1・無装飾）。行の初期状態。</summary>
    public static Cell Blank => new() { Text = null, Width = 1 };

    /// <summary>指定スタイルの空白セル（消去系シーケンスは現在の背景色で塗る）。</summary>
    public static Cell BlankWith(CellColor fg, CellColor bg, CellAttributes attrs) => new()
    {
        Text = null,
        Width = 1,
        Foreground = fg,
        Background = bg,
        Attributes = attrs,
    };

    /// <summary>全角セルの後続（幅0のプレースホルダ）。</summary>
    public static Cell WideTrailer(CellColor fg, CellColor bg, CellAttributes attrs) => new()
    {
        Text = null,
        Width = 0,
        Foreground = fg,
        Background = bg,
        Attributes = attrs,
    };

    /// <summary>内容を持たない空白セルか（全角の後続セルは含まない）。</summary>
    public readonly bool IsBlank => Text == null && Width != 0;

    /// <summary>全角の後続セルか。</summary>
    public readonly bool IsWideTrailer => Width == 0;

    /// <summary>スタイルが既定（無装飾・既定色）かどうか。シリアライズ時の SGR 省略判定に使う。</summary>
    public readonly bool HasDefaultStyle =>
        Foreground == CellColor.Default &&
        Background == CellColor.Default &&
        Attributes == CellAttributes.None;
}
