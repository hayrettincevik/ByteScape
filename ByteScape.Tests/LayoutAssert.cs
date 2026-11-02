using ByteScape.Treemap.Layout;
using Xunit;

namespace ByteScape.Tests;

/// <summary>Invariants every layout must satisfy (checked on real rectangle geometry, overlap via intersection area).</summary>
internal static class LayoutAssert
{
    public const double Eps = 1e-6;

    public static void RectangleIsSane(in RectangleD r, string what)
    {
        Assert.True(r.IsFinite, $"{what}: NaN/Infinity in {r}");
        Assert.True(r.Width >= 0, $"{what}: negative width {r}");
        Assert.True(r.Height >= 0, $"{what}: negative height {r}");
        Assert.True(r.Area >= 0, $"{what}: negative area {r}");
    }

    public static void Inside(in RectangleD child, in RectangleD parent, string what)
    {
        Assert.True(parent.Contains(child, Eps), $"{what}: {child} is not inside {parent}");
    }

    /// <summary>Pairwise intersection test; a sweep over X keeps it fast for 10k+ rectangles while still testing every candidate pair.</summary>
    public static void NoOverlap(ReadOnlySpan<RectangleD> rects, string what)
    {
        int n = rects.Length;
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        var copy = rects.ToArray();
        Array.Sort(order, (a, b) => copy[a].X.CompareTo(copy[b].X));
        for (int oi = 0; oi < n; oi++)
        {
            int i = order[oi];
            if (copy[i].Area <= 0) continue;
            for (int oj = oi + 1; oj < n; oj++)
            {
                int j = order[oj];
                if (copy[j].X >= copy[i].Right - Eps) break; // sorted by X: no later rectangle can overlap i
                double a = copy[i].IntersectionArea(copy[j]);
                Assert.True(a <= Eps, $"{what}: rectangles {i} {copy[i]} and {j} {copy[j]} overlap by {a}");
            }
        }
    }

    /// <summary>Area of every rectangle is proportional to its value (within a relative tolerance), zero values get zero area.</summary>
    public static void AreasProportional(ReadOnlySpan<double> values, ReadOnlySpan<RectangleD> rects, in RectangleD bounds, string what, double relTolerance = 1e-6)
    {
        double total = 0;
        foreach (var v in values) total += Squarify.Sanitize(v);
        double scale = total > 0 ? bounds.Area / total : 0;
        double sumArea = 0;
        for (int i = 0; i < values.Length; i++)
        {
            double expected = Squarify.Sanitize(values[i]) * scale;
            double actual = rects[i].Area;
            sumArea += actual;
            double tol = Math.Max(expected * relTolerance, bounds.Area * 1e-9);
            Assert.True(Math.Abs(actual - expected) <= tol, $"{what}: item {i} value {values[i]} expected area {expected} got {actual}");
        }
        if (total > 0) Assert.True(Math.Abs(sumArea - bounds.Area) <= bounds.Area * 1e-9 + Eps, $"{what}: areas sum {sumArea} != bounds area {bounds.Area}");
    }

    /// <summary>All invariants for one flat layout.</summary>
    public static void FlatLayoutIsValid(ReadOnlySpan<double> values, ReadOnlySpan<RectangleD> rects, in RectangleD bounds, string what)
    {
        Assert.Equal(values.Length, rects.Length);
        for (int i = 0; i < rects.Length; i++)
        {
            RectangleIsSane(rects[i], $"{what} #{i}");
            Inside(rects[i], bounds, $"{what} #{i}");
        }
        NoOverlap(rects, what);
        AreasProportional(values, rects, bounds, what);
    }

    /// <summary>All invariants for a hierarchy laid out by <see cref="TreemapLayoutEngine"/>.</summary>
    public static void TreeIsValid(TreemapNode root, LayoutOptions o, string what)
    {
        RectangleIsSane(root.Bounds, what + " root");
        foreach (var node in root.Descendants())
        {
            RectangleIsSane(node.Bounds, $"{what} {node.Name}");
            // collapsed rectangles (not subdivided / padding larger than the element) carry no position
            if (!node.Bounds.IsEmpty) Inside(node.Bounds, node.Parent!.Bounds, $"{what} {node.Name} in {node.Parent.Name}");
        }
        foreach (var node in new[] { root }.Concat(root.Descendants()))
        {
            if (node.Children.Count == 0) continue;
            var rects = node.Children.Select(c => c.Bounds).ToArray();
            NoOverlap(rects, $"{what} children of {node.Name}");
            if (node.Depth >= o.MaxDepth || rects.All(r => r.IsEmpty)) continue;
            // with padding, child areas are proportional to values inside the tiling area (before the inner gap shrink)
            var inner = TreemapLayoutEngine.InnerBounds(node, o);
            if (o.PaddingInner == 0 && !inner.IsEmpty)
                AreasProportional(node.Children.Select(c => c.Value).ToArray(), rects, inner, $"{what} children of {node.Name}");
        }
    }
}
