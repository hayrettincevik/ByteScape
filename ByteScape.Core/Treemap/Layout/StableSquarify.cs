using System.Buffers;

namespace ByteScape.Treemap.Layout;

/// <summary>
/// Squarify with layout stability across successive calls (d3's <c>treemapResquarify</c> idea):
/// for every parent the row partition of the previous layout is remembered. When the parent is
/// laid out again with the same set of children, the rows are replayed with the new values instead
/// of being rebuilt, so a value change moves rectangles smoothly instead of reshuffling them.
/// <para>
/// Unlike d3 (which blindly trusts the cache) this class
/// <list type="bullet">
/// <item>detects added / removed children and falls back to a fresh squarify for that parent;</item>
/// <item>compares the replayed layout with a fresh one and gives up stability when the replayed
///       rectangles became <see cref="AspectTolerance"/> times worse than a fresh layout (quality before stability).</item>
/// </list>
/// </para>
/// Keys identify children across calls (any reference: a data node, an id string, ...).
/// </summary>
public sealed class StableSquarify
{
    private sealed class Entry
    {
        public object[] Keys = Array.Empty<object>();
        public LayoutRow[] Rows = Array.Empty<LayoutRow>();
        public double Ratio;
        public int Generation;
    }

    private readonly Dictionary<object, Entry> _cache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, int> _indexScratch = new(ReferenceEqualityComparer.Instance);
    private int _generation;

    /// <summary>A replayed layout is accepted only while its worst aspect ratio is at most this factor above a fresh layout's (and above <see cref="AspectFloor"/>).</summary>
    public double AspectTolerance { get; set; } = 2.0;

    /// <summary>Replayed layouts whose worst aspect ratio is below this value are always accepted.</summary>
    public double AspectFloor { get; set; } = 3.0;

    public int CachedParents => _cache.Count;

    /// <summary>Marks the start of a new layout pass; parents not laid out during a pass can be dropped with <see cref="Prune"/>.</summary>
    public void BeginPass() => _generation++;

    /// <summary>Drops cache entries that were not used in the last <paramref name="keepPasses"/> passes.</summary>
    public void Prune(int keepPasses = 2)
    {
        if (_cache.Count == 0) return;
        var stale = new List<object>();
        foreach (var kv in _cache) if (_generation - kv.Value.Generation > keepPasses) stale.Add(kv.Key);
        foreach (var k in stale) _cache.Remove(k);
    }

    public void Forget(object parent) => _cache.Remove(parent);
    public void Clear() => _cache.Clear();

    /// <summary>
    /// Lays out the children of <paramref name="parent"/>. <paramref name="keys"/>/<paramref name="values"/> are
    /// given in the caller's preferred (fresh) order; rectangles are written back in that same order.
    /// </summary>
    public void Layout(object parent, ReadOnlySpan<object> keys, ReadOnlySpan<double> values, in RectangleD bounds, double ratio, Span<RectangleD> rects)
    {
        int n = values.Length;
        if (n == 0) { _cache.Remove(parent); return; }
        if (keys.Length != n || rects.Length < n) throw new ArgumentException("keys, values and rects must have the same length");

        if (_cache.TryGetValue(parent, out var entry) && entry.Ratio == ratio && SameKeySet(entry.Keys, keys))
        {
            entry.Generation = _generation;
            if (TryReplay(entry, keys, values, bounds, rects)) return;
        }

        // fresh layout in the caller's order
        var rows = new List<LayoutRow>(8);
        Squarify.Layout(values, bounds, ratio, rects, rows);
        entry = new Entry { Keys = keys.ToArray(), Rows = rows.ToArray(), Ratio = ratio, Generation = _generation };
        _cache[parent] = entry;
    }

    private bool SameKeySet(object[] cached, ReadOnlySpan<object> current)
    {
        if (cached.Length != current.Length) return false;
        _indexScratch.Clear();
        for (int i = 0; i < cached.Length; i++) _indexScratch[cached[i]] = i;
        for (int i = 0; i < current.Length; i++) if (!_indexScratch.ContainsKey(current[i])) return false;
        return true;
    }

    /// <summary>Replays the cached rows (cached order) and, if the result is still good, writes it back in the caller's order.</summary>
    private bool TryReplay(Entry entry, ReadOnlySpan<object> keys, ReadOnlySpan<double> values, in RectangleD bounds, Span<RectangleD> rects)
    {
        int n = values.Length;
        // _indexScratch currently maps cached key -> cached index (filled by SameKeySet)
        var pool = ArrayPool<double>.Shared;
        var rpool = ArrayPool<RectangleD>.Shared;
        var ipool = ArrayPool<int>.Shared;
        double[] cachedValues = pool.Rent(n);
        RectangleD[] cachedRects = rpool.Rent(n);
        RectangleD[] freshRects = rpool.Rent(n);
        int[] toCached = ipool.Rent(n); // caller index -> cached index
        try
        {
            for (int i = 0; i < n; i++)
            {
                int ci = _indexScratch[keys[i]];
                toCached[i] = ci;
                cachedValues[ci] = values[i];
            }
            var cv = cachedValues.AsSpan(0, n);
            var cr = cachedRects.AsSpan(0, n);
            Squarify.LayoutRows(cv, entry.Rows, bounds, cr);

            double replayed = Squarify.WorstAspect(cr);
            if (replayed > AspectFloor)
            {
                var fr = freshRects.AsSpan(0, n);
                Squarify.Layout(values, bounds, entry.Ratio, fr);
                double fresh = Squarify.WorstAspect(fr);
                if (replayed > fresh * AspectTolerance) return false; // quality before stability
            }
            for (int i = 0; i < n; i++) rects[i] = cr[toCached[i]];
            return true;
        }
        finally
        {
            pool.Return(cachedValues);
            rpool.Return(cachedRects);
            rpool.Return(freshRects);
            ipool.Return(toCached);
        }
    }
}
