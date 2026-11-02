namespace ByteScape.Treemap.Layout;

/// <summary>
/// Double precision rectangle used by the layout engine. Independent from any renderer
/// (System.Drawing, SkiaSharp, ...): the UI converts at the edge.
/// </summary>
public readonly struct RectangleD : IEquatable<RectangleD>
{
    public readonly double X;
    public readonly double Y;
    public readonly double Width;
    public readonly double Height;

    public RectangleD(double x, double y, double width, double height)
    {
        X = x; Y = y; Width = width; Height = height;
    }

    public static readonly RectangleD Empty = new(0, 0, 0, 0);

    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double Area => Width * Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>max(width/height, height/width); 1 = square, +inf for degenerate rectangles.</summary>
    public double AspectRatio
    {
        get
        {
            if (Width <= 0 || Height <= 0) return double.PositiveInfinity;
            return Width >= Height ? Width / Height : Height / Width;
        }
    }

    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height);

    public bool Contains(double x, double y) => x >= X && y >= Y && x < Right && y < Bottom;

    /// <summary>True when <paramref name="other"/> lies inside this rectangle (with a numeric tolerance).</summary>
    public bool Contains(in RectangleD other, double epsilon = 1e-6)
        => other.X >= X - epsilon && other.Y >= Y - epsilon && other.Right <= Right + epsilon && other.Bottom <= Bottom + epsilon;

    /// <summary>Area of the overlap between two rectangles (0 when they only touch).</summary>
    public double IntersectionArea(in RectangleD other)
    {
        double w = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        double h = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }

    public bool IntersectsWith(in RectangleD other, double epsilon = 1e-9) => IntersectionArea(other) > epsilon;

    /// <summary>Shrinks the rectangle by the given amounts; collapses to a zero-size rectangle at its centre when the padding exceeds the size (as d3 does).</summary>
    public RectangleD Deflate(double left, double top, double right, double bottom)
    {
        double x0 = X + left, y0 = Y + top, x1 = Right - right, y1 = Bottom - bottom;
        if (x1 < x0) x0 = x1 = (x0 + x1) / 2;
        if (y1 < y0) y0 = y1 = (y0 + y1) / 2;
        return new RectangleD(x0, y0, x1 - x0, y1 - y0);
    }

    public RectangleD Deflate(double all) => Deflate(all, all, all, all);

    public bool Equals(RectangleD o) => X == o.X && Y == o.Y && Width == o.Width && Height == o.Height;
    public override bool Equals(object? obj) => obj is RectangleD o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);
    public static bool operator ==(RectangleD a, RectangleD b) => a.Equals(b);
    public static bool operator !=(RectangleD a, RectangleD b) => !a.Equals(b);
    public override string ToString() => $"({X:0.##}, {Y:0.##}, {Width:0.##} x {Height:0.##})";
}
