using System.Diagnostics;
using System.Text.Json;
using ByteScape.Treemap.Layout;
using Xunit;
using Xunit.Abstractions;

namespace ByteScape.Tests;

public class SquarifyTests
{
    private readonly ITestOutputHelper _out;
    public SquarifyTests(ITestOutputHelper output) => _out = output;

    private static RectangleD[] Run(double[] values, RectangleD bounds, double ratio = 1, List<LayoutRow>? rows = null)
    {
        var rects = new RectangleD[values.Length];
        Squarify.Layout(values, bounds, ratio, rects, rows);
        return rects;
    }

    private static double[] Desc(IEnumerable<double> v) => v.OrderByDescending(x => x).ToArray();

    private static double[] Random(int n, int seed, double min, double max, bool log = false)
    {
        var r = new Random(seed);
        var v = new double[n];
        for (int i = 0; i < n; i++)
            v[i] = log ? Math.Pow(10, Math.Log10(min) + r.NextDouble() * (Math.Log10(max) - Math.Log10(min))) : min + r.NextDouble() * (max - min);
        return Desc(v);
    }

    // ------------------------------------------------------------------ 1..12 required cases

    [Fact]
    public void SingleNode_FillsBounds()
    {
        var b = new RectangleD(10, 20, 300, 200);
        var v = new double[] { 42 };
        var r = Run(v, b);
        LayoutAssert.FlatLayoutIsValid(v, r, b, "single");
        Assert.Equal(b, r[0]);
    }

    [Fact]
    public void TwoNodes_SplitAlongShortSide()
    {
        var b = new RectangleD(0, 0, 300, 200);
        var v = new double[] { 70, 30 };
        var r = Run(v, b);
        LayoutAssert.FlatLayoutIsValid(v, r, b, "two");
        // width > height: rows are sliced (vertical strips), 70% and 30% of the width
        Assert.Equal(210, r[0].Width, 9); Assert.Equal(200, r[0].Height, 9);
        Assert.Equal(90, r[1].Width, 9); Assert.Equal(210, r[1].X, 9);
    }

    [Fact]
    public void TenNodes_Valid()
    {
        var b = new RectangleD(0, 0, 640, 480);
        var v = Random(10, 1, 1, 1000);
        var r = Run(v, b);
        LayoutAssert.FlatLayoutIsValid(v, r, b, "ten");
        Assert.True(Squarify.WorstAspect(r) < 4, "aspect " + Squarify.WorstAspect(r));
    }

    [Fact]
    public void HundredNodes_Valid()
    {
        var b = new RectangleD(0, 0, 1024, 768);
        var v = Random(100, 2, 1, 1e6);
        var r = Run(v, b);
        LayoutAssert.FlatLayoutIsValid(v, r, b, "hundred");
        Assert.True(Squarify.WorstAspect(r) < 6, "aspect " + Squarify.WorstAspect(r));
    }

    [Fact]
    public void OneBigManySmall_BigTakesItsShare()
    {
        var b = new RectangleD(0, 0, 500, 300);
        var v = new double[201];
        v[0] = 1_000_000;
        for (int i = 1; i < v.Length; i++) v[i] = 1;
        var r = Run(v, b);
        LayoutAssert.FlatLayoutIsValid(v, r, b, "big+small");
        double share = r[0].Area / b.Area;
        Assert.InRange(share, 0.9997, 1.0);
    }

    [Fact]
    public void WildlyDifferentValues_Valid()
    {
        var b = new RectangleD(0, 0, 1920, 1080);
        var v = Random(500, 3, 1, 1e12, log: true);
        var r = Run(v, b);
        LayoutAssert.FlatLayoutIsValid(v, r, b, "wild");
        // 1 GB must get ~10x the area of 100 MB
        var pair = new double[] { 1e9, 1e8 };
        var pr = Run(pair, b);
        Assert.Equal(10.0, pr[0].Area / pr[1].Area, 6);
    }

    [Fact]
    public void EqualValues_NearSquares()
    {
        var b = new RectangleD(0, 0, 400, 400);
        var v = Enumerable.Repeat(10.0, 16).ToArray();
        var r = Run(v, b);
        LayoutAssert.FlatLayoutIsValid(v, r, b, "equal");
        // 16 equal items on a square: 4x4 grid of 100x100 squares
        foreach (var rect in r) { Assert.Equal(100, rect.Width, 6); Assert.Equal(100, rect.Height, 6); }
        Assert.Equal(1.0, Squarify.WorstAspect(r), 9);
    }

    [Fact]
    public void ZeroAndInvalidValues_GetEmptyRectangles()
    {
        var b = new RectangleD(0, 0, 200, 100);
        var v = new double[] { 5, 4, 3, 0, 0, double.NaN, -7, double.PositiveInfinity };
        var r = Run(v, b);
        LayoutAssert.FlatLayoutIsValid(v, r, b, "zero");
        for (int i = 3; i < v.Length; i++) Assert.Equal(0, r[i].Area);
        // all zero
        var z = new double[] { 0, 0, 0 };
        var zr = Run(z, b);
        foreach (var rect in zr) { Assert.True(rect.IsFinite); Assert.Equal(0, rect.Area); }
        // empty bounds
        var er = Run(new double[] { 1, 2 }, new RectangleD(0, 0, 0, 100));
        foreach (var rect in er) { Assert.True(rect.IsFinite); Assert.Equal(0, rect.Area); }
    }

    [Fact]
    public void VeryNarrowParent_Valid()
    {
        var b = new RectangleD(0, 0, 3, 1000);
        var v = Random(50, 4, 1, 100);
        var r = Run(v, b);
        LayoutAssert.FlatLayoutIsValid(v, r, b, "narrow");
        // every row must span the (tiny) width: items are stacked vertically
        foreach (var rect in r) Assert.True(rect.Width <= 3 + LayoutAssert.Eps);
    }

    [Fact]
    public void VeryWideParent_Valid()
    {
        var b = new RectangleD(0, 0, 4000, 2);
        var v = Random(50, 5, 1, 100);
        var r = Run(v, b);
        LayoutAssert.FlatLayoutIsValid(v, r, b, "wide");
        foreach (var rect in r) Assert.True(rect.Height <= 2 + LayoutAssert.Eps);
    }

    [Fact]
    public void NestedFolders_RecursiveLayoutValid()
    {
        var root = BuildDiskTree(seed: 6, folders: 12, filesPerFolder: 15, depth: 4);
        root.SumValues();
        var o = new LayoutOptions { Ratio = 1, PaddingOuter = 2, PaddingTop = 14, PaddingInner = 0 };
        var engine = new TreemapLayoutEngine();
        var bounds = new RectangleD(0, 0, 1280, 800);
        engine.Layout(root, bounds, o);
        Assert.Equal(bounds, root.Bounds);
        LayoutAssert.TreeIsValid(root, o, "nested");
        // folder value = sum of children, file value = its own size
        foreach (var n in root.Descendants().Where(n => n.Children.Count > 0))
            Assert.Equal(n.Children.Sum(c => c.Value), n.Value, 6);
    }

    [Fact]
    public void StressTest_TenThousandNodes()
    {
        var b = new RectangleD(0, 0, 4096, 4096);
        var v = Random(10_000, 7, 1, 1e9, log: true);
        var r = new RectangleD[v.Length];
        var sw = Stopwatch.StartNew();
        Squarify.Layout(v, b, 1, r);
        sw.Stop();
        _out.WriteLine($"10k flat layout: {sw.Elapsed.TotalMilliseconds:0.00} ms, worst aspect {Squarify.WorstAspect(r):0.00}");
        LayoutAssert.FlatLayoutIsValid(v, r, b, "10k");
        Assert.True(sw.ElapsedMilliseconds < 2000);

        // 100k as well, invariants only spot-checked (pairwise overlap is O(n²))
        var v2 = Random(100_000, 8, 1, 1e9, log: true);
        var r2 = new RectangleD[v2.Length];
        sw.Restart();
        Squarify.Layout(v2, b, 1, r2);
        sw.Stop();
        _out.WriteLine($"100k flat layout: {sw.Elapsed.TotalMilliseconds:0.00} ms");
        double sum = 0;
        foreach (var rect in r2) { LayoutAssert.RectangleIsSane(rect, "100k"); LayoutAssert.Inside(rect, b, "100k"); sum += rect.Area; }
        Assert.Equal(b.Area, sum, 3);
        Assert.True(sw.ElapsedMilliseconds < 5000);
    }

    // ------------------------------------------------------------------ algorithm parity with d3

    private sealed class RefCase { public double[] values { get; set; } = null!; public double w { get; set; } public double h { get; set; } public double ratio { get; set; } public double[][] rects { get; set; } = null!; public int[][] rows { get; set; } = null!; }

    [Fact]
    public void MatchesD3SquarifyRatio_Exactly()
    {
        // d3_reference.json was produced by running d3-hierarchy's squarifyRatio (verbatim) with node.js
        var path = Path.Combine(AppContext.BaseDirectory, "d3_reference.json");
        var cases = JsonSerializer.Deserialize<RefCase[]>(File.ReadAllText(path))!;
        Assert.True(cases.Length >= 9);
        foreach (var c in cases)
        {
            var rows = new List<LayoutRow>();
            var rects = Run(c.values, new RectangleD(0, 0, c.w, c.h), c.ratio, rows);
            Assert.Equal(c.rows.Length, rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                Assert.Equal(c.rows[i][0], rows[i].Count);
                Assert.Equal(c.rows[i][1] == 1, rows[i].Dice);
            }
            for (int i = 0; i < c.values.Length; i++)
            {
                var e = c.rects[i];
                // the only intended deviation from d3 is flushing the last item of a row / the last row (removes 1e-13 drift)
                Assert.True(Math.Abs(e[0] - rects[i].X) <= 1e-6 && Math.Abs(e[1] - rects[i].Y) <= 1e-6
                            && Math.Abs(e[2] - rects[i].Width) <= 1e-6 && Math.Abs(e[3] - rects[i].Height) <= 1e-6,
                    $"item {i} (value {c.values[i]}): d3 ({e[0]},{e[1]},{e[2]},{e[3]}) vs {rects[i]}");
            }
        }
    }

    [Fact]
    public void BrulsPaperExample()
    {
        // Bruls, Huizing & van Wijk, fig. 3: values 6,6,4,3,2,2,1 in a 6x4 rectangle
        var v = new double[] { 6, 6, 4, 3, 2, 2, 1 };
        var rows = new List<LayoutRow>();
        var r = Run(v, new RectangleD(0, 0, 6, 4), 1, rows);
        Assert.True(new RectangleD(0, 0, 3, 2).Equals(r[0]) || Math.Abs(r[0].Width - 3) + Math.Abs(r[0].Height - 2) < 1e-9, r[0].ToString());
        Assert.True(Math.Abs(r[1].Y - 2) + Math.Abs(r[1].Width - 3) + Math.Abs(r[1].Height - 2) < 1e-9, r[1].ToString());
        // rows: {6,6} {4,3} {2} {2} {1} — the same partition d3 produces
        Assert.Equal(5, rows.Count);
        Assert.Equal(2, rows[0].Count);
        Assert.False(rows[0].Dice); // width 6 > height 4: first row is a vertical strip (slice)
        Assert.True(Squarify.WorstAspect(r) <= 3, "worst aspect " + Squarify.WorstAspect(r)); // paper: the 1-unit element ends up 0.6 x 1.67
    }

    // ------------------------------------------------------------------ determinism / sorting

    [Fact]
    public void Order_IsDeterministicAndStableForTies()
    {
        var values = new double[] { 5, 9, 5, 1, 9, 5 };
        var keys = new[] { "b", "y", "a", "z", "x", "c" };
        var order = new int[values.Length];
        LayoutOrder.SortDescending(values, keys, order);
        Assert.Equal(new[] { 4, 1, 2, 0, 5, 3 }, order); // 9x, 9y, 5a, 5b, 5c, 1z
        var big = Random(500, 9, 1, 10).Select(x => Math.Round(x)).ToArray(); // many ties
        var bigKeys = Enumerable.Range(0, 500).Select(i => "k" + i.ToString("D4")).ToArray();
        var o1 = new int[500]; var o2 = new int[500];
        LayoutOrder.SortDescending(big, bigKeys, o1);
        LayoutOrder.SortDescending(big, bigKeys, o2);
        Assert.Equal(o1, o2);
        for (int i = 1; i < 500; i++) Assert.True(big[o1[i - 1]] >= big[o1[i]]);
    }

    // ------------------------------------------------------------------ stability (resquarify)

    [Fact]
    public void Resquarify_KeepsPositionsWhenOneValueChanges()
    {
        var stable = new StableSquarify();
        var keys = new object[] { "A", "B", "C", "D" };
        var b = new RectangleD(0, 0, 400, 300);
        var v1 = new double[] { 40, 30, 20, 10 };
        var r1 = new RectangleD[4];
        stable.BeginPass();
        stable.Layout("parent", keys, v1, b, 1, r1);
        LayoutAssert.FlatLayoutIsValid(v1, r1, b, "pass1");

        // C grows past B: a fresh squarify would reorder (D A C B style); the stable one keeps the rows
        var v2 = new double[] { 40, 30, 35, 10 };
        var r2 = new RectangleD[4];
        stable.BeginPass();
        stable.Layout("parent", keys, v2, b, 1, r2);
        LayoutAssert.FlatLayoutIsValid(v2, r2, b, "pass2");
        for (int i = 0; i < 4; i++)
        {
            // every element stays on the same side of the layout it was on (centre moves less than a quarter of the view)
            double dx = (r2[i].X + r2[i].Width / 2) - (r1[i].X + r1[i].Width / 2);
            double dy = (r2[i].Y + r2[i].Height / 2) - (r1[i].Y + r1[i].Height / 2);
            Assert.True(Math.Abs(dx) < b.Width / 4 && Math.Abs(dy) < b.Height / 4, $"item {i} jumped by {dx},{dy}");
        }
        // same relative order along the layout as before
        Assert.True(r2[0].X <= r2[1].X + 1e-9 || r2[0].Y <= r2[1].Y + 1e-9);
    }

    [Fact]
    public void Resquarify_FallsBackWhenChildrenChangeOrQualityDegrades()
    {
        var stable = new StableSquarify();
        var b = new RectangleD(0, 0, 400, 300);
        var keys = new object[] { "A", "B", "C" };
        var r = new RectangleD[3];
        stable.BeginPass();
        stable.Layout("p", keys, new double[] { 3, 2, 1 }, b, 1, r);
        Assert.Equal(1, stable.CachedParents);
        // a child is added -> fresh layout, still valid
        var keys2 = new object[] { "A", "B", "C", "D" };
        var r2 = new RectangleD[4];
        var v2 = new double[] { 3, 2, 1, 5 };
        stable.BeginPass();
        stable.Layout("p", keys2, v2, b, 1, r2);
        LayoutAssert.FlatLayoutIsValid(v2, r2, b, "added");
        // quality guard: values change so much that replaying the old rows would give terrible strips
        var v3 = new double[] { 1, 1, 1, 1000 };
        var r3 = new RectangleD[4];
        stable.BeginPass();
        stable.Layout("p", keys2, v3, b, 1, r3);
        LayoutAssert.FlatLayoutIsValid(v3, r3, b, "degraded");
        var fresh = new RectangleD[4];
        Squarify.Layout(v3, b, 1, fresh);
        Assert.True(Squarify.WorstAspect(r3) <= Squarify.WorstAspect(fresh) * stable.AspectTolerance + 1e-9);
        // entries not laid out for several passes are pruned
        stable.BeginPass(); stable.BeginPass(); stable.BeginPass(); stable.Prune(2);
        Assert.Equal(0, stable.CachedParents);
    }

    // ------------------------------------------------------------------ padding

    [Fact]
    public void Padding_DoesNotBreakProportionsOrContainment()
    {
        var root = BuildDiskTree(seed: 11, folders: 6, filesPerFolder: 8, depth: 3);
        root.SumValues();
        foreach (var o in new[]
        {
            new LayoutOptions { PaddingOuter = 3, PaddingTop = 16, PaddingInner = 0 },
            new LayoutOptions { PaddingOuter = 1, PaddingTop = 12, PaddingInner = 2 },
            new LayoutOptions { PaddingOuter = 50, PaddingTop = 50, PaddingInner = 10 }, // padding larger than many elements
        })
        {
            var engine = new TreemapLayoutEngine();
            engine.Layout(root, new RectangleD(0, 0, 600, 400), o);
            LayoutAssert.TreeIsValid(root, o, $"padding {o.PaddingOuter}/{o.PaddingTop}/{o.PaddingInner}");
            // with an inner gap, child areas are still proportional on the enlarged tiling area (d3 scheme):
            // rect + gap on each side reconstructs the tile
            foreach (var n in new[] { root }.Concat(root.Descendants()).Where(x => x.Children.Count > 0))
            {
                var inner = TreemapLayoutEngine.InnerBounds(n, o);
                if (inner.IsEmpty) continue;
                double half = o.PaddingInner / 2;
                var tiles = n.Children.Select(c => c.Bounds.IsEmpty ? c.Bounds : new RectangleD(c.Bounds.X - half, c.Bounds.Y - half, c.Bounds.Width + 2 * half, c.Bounds.Height + 2 * half)).ToArray();
                if (tiles.Any(t => t.IsEmpty)) continue; // collapsed by padding, nothing to check
                LayoutAssert.AreasProportional(n.Children.Select(c => c.Value).ToArray(), tiles, inner, "tiles of " + n.Name, 1e-6);
            }
        }
    }

    // ------------------------------------------------------------------ engine options

    [Fact]
    public void MaxDepth_StopsSubdivision_AndSliceDiceReferenceIsValid()
    {
        var root = BuildDiskTree(seed: 12, folders: 5, filesPerFolder: 6, depth: 4);
        root.SumValues();
        var o = new LayoutOptions { MaxDepth = 1 };
        new TreemapLayoutEngine().Layout(root, new RectangleD(0, 0, 500, 500), o);
        foreach (var n in root.Descendants())
            if (n.Depth >= 2) Assert.True(n.Bounds.IsEmpty);
        LayoutAssert.TreeIsValid(root, o, "maxdepth");

        var o2 = new LayoutOptions();
        TreemapLayoutEngine.LayoutSliceDice(root, new RectangleD(0, 0, 500, 500), o2);
        LayoutAssert.TreeIsValid(root, o2, "slicedice");
    }

    [Fact]
    public void Engine_HierarchyStressAndAllocationSanity()
    {
        var root = BuildDiskTree(seed: 13, folders: 24, filesPerFolder: 40, depth: 4); // ~50k nodes
        root.SumValues();
        int count = root.Descendants().Count();
        var engine = new TreemapLayoutEngine();
        var o = new LayoutOptions { PaddingOuter = 1, PaddingTop = 10 };
        engine.Layout(root, new RectangleD(0, 0, 1920, 1080), o); // warm up pools
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        engine.Layout(root, new RectangleD(0, 0, 1920, 1080), o);
        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _out.WriteLine($"{count} nodes: {sw.Elapsed.TotalMilliseconds:0.0} ms, {allocated / 1024} KB allocated on a stable re-layout");
        LayoutAssert.TreeIsValid(root, o, "engine stress");
        Assert.True(sw.ElapsedMilliseconds < 3000);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Disk-like hierarchy: folders with files and sub folders, sizes log-uniform in bytes.</summary>
    internal static TreemapNode BuildDiskTree(int seed, int folders, int filesPerFolder, int depth)
    {
        var rnd = new Random(seed);
        int id = 0;
        var root = new TreemapNode("root", "Disk");
        void Fill(TreemapNode parent, int level)
        {
            for (int i = 0; i < filesPerFolder; i++)
                parent.Add("f" + id++, "File " + i, Math.Pow(10, 2 + rnd.NextDouble() * 7));
            if (level >= depth) return;
            int nf = Math.Max(1, folders / (level + 1));
            for (int i = 0; i < nf; i++)
                Fill(parent.Add("d" + id++, "Folder " + i), level + 1);
        }
        Fill(root, 1);
        return root;
    }
}
