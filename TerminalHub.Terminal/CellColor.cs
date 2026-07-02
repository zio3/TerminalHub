namespace TerminalHub.Terminal;

/// <summary>セルの前景/背景色。既定色・256色インデックス・truecolor の3種を表す。</summary>
public readonly struct CellColor : IEquatable<CellColor>
{
    public enum ColorKind : byte
    {
        Default = 0,
        Indexed = 1,
        Rgb = 2,
    }

    public ColorKind Kind { get; }
    public byte Index { get; }
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    private CellColor(ColorKind kind, byte index, byte r, byte g, byte b)
    {
        Kind = kind;
        Index = index;
        R = r;
        G = g;
        B = b;
    }

    public static CellColor Default => default;

    public static CellColor FromIndex(int index)
        => new(ColorKind.Indexed, (byte)Math.Clamp(index, 0, 255), 0, 0, 0);

    public static CellColor FromRgb(int r, int g, int b)
        => new(ColorKind.Rgb, 0, (byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));

    public bool Equals(CellColor other)
        => Kind == other.Kind && Index == other.Index && R == other.R && G == other.G && B == other.B;

    public override bool Equals(object? obj) => obj is CellColor c && Equals(c);

    public override int GetHashCode() => HashCode.Combine(Kind, Index, R, G, B);

    public static bool operator ==(CellColor left, CellColor right) => left.Equals(right);
    public static bool operator !=(CellColor left, CellColor right) => !left.Equals(right);
}
