namespace TerminalHub.Terminal;

/// <summary>セルの文字装飾属性（SGR）。</summary>
[Flags]
public enum CellAttributes : byte
{
    None = 0,
    Bold = 1 << 0,
    Faint = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Inverse = 1 << 4,
    Strikethrough = 1 << 5,
    Hidden = 1 << 6,
}
