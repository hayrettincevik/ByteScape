using System.Buffers;

namespace ByteScape.Treemap.Layout;

/// <summary>
/// Renderer independent hierarchy node for the layout engine. A folder's value is the sum of its
/// children (<see cref="SumValues"/>), a file's value is its size in bytes; the area of every
/// rectangle is proportional to the value.
/// </summary>
public sealed class TreemapNode
{
    public TreemapNode(string id, string name, double value = 0)
    {
        Id = id;
        Name = name;
        Value = value;
    }

    public string Id { get; }
    public string Name { get; set; }
    public double Value { get; set; }
    public List<TreemapNode> Children { get; } = new();
    public TreemapNode? Parent { get; private set; }
    public RectangleD Bounds { get; set; }
    public int Depth { get; private set; }
    public bool IsLeaf => Children.Count == 0;

    public TreemapNode Add(TreemapNode child)
    {
        child.Parent = this;
        child.Depth = Depth + 1;
        foreach (var d in child.Descendants()) d.Depth = d.Parent!.Depth + 1;
        Children.Add(child);
        return child;
    }

    public TreemapNode Add(string id, string name, double value = 0) => Add(new TreemapNode(id, name, value));

    /// <summary>Recomputes every container value as the sum of its children (leaves keep their own value). Returns this node's value.</summary>
    public double SumValues()
    {
        if (Children.Count == 0) return Value = Squarify.Sanitize(Value);
        double sum = 0;
        foreach (var c in Children) sum += c.SumValues();
        return Value = sum;
    }

    public IEnumerable<TreemapNode> Descendants()
    {
        var stack = new Stack<TreemapNode>();
        for (int i = Children.Count - 1; i >= 0; i--) stack.Push(Children[i]);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
        }
    }

    public override string ToString() => $"{Name} [{Value}] {Bounds}";
}

/// <summary>Layout parameters. Padding follows d3-hierarchy's model (outer, top, inner).</summary>
public sealed class LayoutOptions
{
    /// <summary>Desired aspect ratio of the tiles (long/short), &gt;= 1. 1 = squares, d3's default is the golden ratio.</summary>
    public double Ratio { get; set; } = 1;

    /// <summary>Space between a container's border and its children (left, right, bottom).</summary>
    public double PaddingOuter { get; set; } = 0;

    /// <summary>Space reserved at the top of a container (caption band). Applied in addition to <see cref="PaddingOuter"/>.</summary>
    public double PaddingTop { get; set; } = 0;

    /// <summary>Gap between sibling rectangles. Children are tiled on an area enlarged by half the gap and then each shrunk by it (d3 scheme), so their areas stay proportional.</summary>
    public double PaddingInner { get; set; } = 0;

    /// <summary>Sort children by value descending (deterministic: ties by id) before tiling. Squarify assumes this; turn off only to keep the caller's order.</summary>
    public bool SortChildren { get; set; } = true;

    /// <summary>Containers deeper than this are not subdivided (their children keep empty bounds).</summary>
    public int MaxDepth { get; set; } = int.MaxValue;

    /// <summary>Reuse the previous row partition per parent when its children are unchanged (see <see cref="StableSquarify"/>).</summary>
    public bool Stable { get; set; } = true;

    /// <summary>Containers whose inner area is smaller than this (in layout units²) are not subdivided.</summary>
    public double MinSubdivideArea { get; set; } = 0;
}

/// <summary>
/// Recursive squarified treemap over a <see cref="TreemapNode"/> hierarchy.
/// <para>
/// <b>Root layout</b>: the root receives <c>bounds</c> as is (it is never tiled against siblings).
/// <b>Child layout</b>: for every container, the area inside its padding is tiled among its children
/// with <see cref="Squarify"/> (or <see cref="StableSquarify"/>), then each child is processed the same way.
/// </para>
/// One engine instance can be reused across frames; that is what makes the stable mode work.
/// </summary>
public sealed class TreemapLayoutEngine
{
    private readonly StableSquarify _stable = new();

    public StableSquarify StableCache => _stable;

    public void Layout(TreemapNode root, in RectangleD bounds, LayoutOptions? options = null)
    {
        options ??= new LayoutOptions();
        root.Bounds = bounds;
        _stable.BeginPass();
        LayoutChildren(root, options);
        if (options.Stable) _stable.Prune();
    }

    /// <summary>Area available to the children of <paramref name="node"/> (d3 positionNode: outer/top padding, enlarged by half the inner gap).</summary>
    public static RectangleD InnerBounds(TreemapNode node, LayoutOptions o)
    {
        double half = o.PaddingInner / 2;
        return node.Bounds.Deflate(o.PaddingOuter - half, o.PaddingOuter + o.PaddingTop - half, o.PaddingOuter - half, o.PaddingOuter - half);
    }

    private void LayoutChildren(TreemapNode node, LayoutOptions o)
    {
        int n = node.Children.Count;
        if (n == 0) return;
        if (node.Depth >= o.MaxDepth)
        {
            foreach (var c in node.Children) ClearBounds(c);
            return;
        }
        var inner = InnerBounds(node, o);
        if (inner.IsEmpty || inner.Area < o.MinSubdivideArea)
        {
            foreach (var c in node.Children) ClearBounds(c);
            return;
        }

        var dpool = ArrayPool<double>.Shared;
        var rpool = ArrayPool<RectangleD>.Shared;
        var ipool = ArrayPool<int>.Shared;
        var opool = ArrayPool<object>.Shared;
        double[] values = dpool.Rent(n);
        RectangleD[] rects = rpool.Rent(n);
        int[] order = ipool.Rent(n);
        object[] keys = opool.Rent(n);
        try
        {
            if (o.SortChildren)
            {
                var ids = ArrayPool<string>.Shared.Rent(n);
                try
                {
                    var raw = dpool.Rent(n);
                    try
                    {
                        for (int i = 0; i < n; i++) { raw[i] = node.Children[i].Value; ids[i] = node.Children[i].Id; }
                        LayoutOrder.SortDescending(raw.AsSpan(0, n), ids.AsSpan(0, n), order.AsSpan(0, n));
                    }
                    finally { dpool.Return(raw); }
                }
                finally { ArrayPool<string>.Shared.Return(ids, clearArray: true); }
            }
            else
            {
                for (int i = 0; i < n; i++) order[i] = i;
            }
            for (int i = 0; i < n; i++)
            {
                var c = node.Children[order[i]];
                values[i] = c.Value;
                keys[i] = c;
            }
            var v = values.AsSpan(0, n);
            var r = rects.AsSpan(0, n);
            if (o.Stable) _stable.Layout(node, keys.AsSpan(0, n), v, inner, o.Ratio, r);
            else Squarify.Layout(v, inner, o.Ratio, r);

            double half = o.PaddingInner / 2;
            for (int i = 0; i < n; i++)
            {
                var c = node.Children[order[i]];
                c.Bounds = half > 0 ? r[i].Deflate(half) : r[i];
            }
        }
        finally
        {
            dpool.Return(values);
            rpool.Return(rects);
            ipool.Return(order);
            opool.Return(keys, clearArray: true);
        }

        foreach (var c in node.Children) LayoutChildren(c, o);
    }

    private static void ClearBounds(TreemapNode node)
    {
        node.Bounds = RectangleD.Empty;
        foreach (var c in node.Children) ClearBounds(c);
    }

    /// <summary>Stand-alone slice-and-dice layout of the same hierarchy (alternating direction per level), for debugging comparisons.</summary>
    public static void LayoutSliceDice(TreemapNode root, in RectangleD bounds, LayoutOptions? options = null)
    {
        options ??= new LayoutOptions();
        root.Bounds = bounds;
        var stack = new Stack<TreemapNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            int n = node.Children.Count;
            if (n == 0) continue;
            var inner = InnerBounds(node, options);
            if (node.Depth >= options.MaxDepth || inner.IsEmpty) { foreach (var c in node.Children) ClearBounds(c); continue; }
            var values = new double[n];
            var rects = new RectangleD[n];
            for (int i = 0; i < n; i++) values[i] = node.Children[i].Value;
            SliceDice.Layout(values, inner, horizontal: node.Depth % 2 == 0, rects);
            double half = options.PaddingInner / 2;
            for (int i = 0; i < n; i++)
            {
                node.Children[i].Bounds = half > 0 ? rects[i].Deflate(half) : rects[i];
                stack.Push(node.Children[i]);
            }
        }
    }
}
