namespace BiliIndexTranslate.Core.Geometry;

/// <summary>
/// Rectangle in physical screen pixels (device coordinates). All geometry that crosses the
/// UI Automation / Win32 boundary is kept in physical pixels; conversion to WPF's logical units
/// happens only at the window edge using the target monitor's scale.
/// </summary>
public readonly record struct PxRect(int Left, int Top, int Right, int Bottom)
{
    public static readonly PxRect Empty = new(0, 0, 0, 0);

    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public int CenterX => Left + Width / 2;
    public int CenterY => Top + Height / 2;

    public static PxRect FromSize(int left, int top, int width, int height) => new(left, top, left + width, top + height);

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public bool Contains(PxPoint p) => Contains(p.X, p.Y);

    public PxRect Intersect(PxRect other)
    {
        var l = Math.Max(Left, other.Left);
        var t = Math.Max(Top, other.Top);
        var r = Math.Min(Right, other.Right);
        var b = Math.Min(Bottom, other.Bottom);
        return r <= l || b <= t ? Empty : new PxRect(l, t, r, b);
    }

    public PxRect Union(PxRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return new PxRect(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    public PxRect Inflate(int dx, int dy) => new(Left - dx, Top - dy, Right + dx, Bottom + dy);

    public PxRect Offset(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);

    public override string ToString() => $"[{Left},{Top} {Width}x{Height}]";
}

public readonly record struct PxPoint(int X, int Y)
{
    public override string ToString() => $"({X},{Y})";
}

/// <summary>Physical-pixel insets (e.g. the shadow margin around the popup card).</summary>
public readonly record struct PxInsets(int Left, int Top, int Right, int Bottom)
{
    public static PxInsets Uniform(int v) => new(v, v, v, v);
    public int Horizontal => Left + Right;
    public int Vertical => Top + Bottom;
}
