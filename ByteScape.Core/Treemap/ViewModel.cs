using System.Drawing;
using ByteScape.Core.Config;
using ByteScape.Core.Filtering;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using ByteScape.Core.Util;
using ByteScape.Treemap.Layout;
using System.Buffers;

namespace ByteScape.Treemap;

/// <summary>
/// Per-view state: which tree, where we are zoomed, the filter and its results, the
/// selection, the navigation history and the treemap item tree built for painting.
/// </summary>
public sealed class ViewModel : IDisposable
{
    private readonly FreeSpaceNode _free = new();
    private readonly UnknownSpaceNode _unknown = new();
    private readonly MissingDataNode _missing = new();

    public ViewModel(ScanTree tree, ContainerNode viewRoot)
    {
        Tree = tree;
        ViewRoot = viewRoot;
        ZoomRoot = viewRoot;
        var cfg = AppConfig.Current;
        DetailLevel = cfg.InitialDetailLevel;
        Style = cfg.InitialStyle;
        ShowFreeSpace = cfg.ShowFreeSpace;
        ShowUnknownSpace = cfg.ShowUnknownSpace;
    }

    public ScanTree Tree { get; }
    public ContainerNode ViewRoot { get; }
    public ContainerNode ZoomRoot { get; private set; }
    public Filter Filter { get; private set; } = Filter.Empty;
    public int DetailLevel { get; set; }
    public ViewStyle Style { get; set; }
    public bool ShowFreeSpace { get; set; }
    public bool ShowUnknownSpace { get; set; }
    public FsNode? Selected { get; set; }
    public ScanJob? SubScan { get; private set; }

    public event Action? Changed;

    private readonly List<ContainerNode> _back = new();
    private readonly List<ContainerNode> _forward = new();
    private readonly List<ContainerNode> _zoomChain = new();   // ancestors of ZoomRoot (view root .. parent), for ValidateZoomRoot
    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public bool CanGoUp => !ReferenceEquals(ZoomRoot, ViewRoot);
    public ContainerNode? PeekBack() => _back.Count > 0 ? _back[^1] : null;
    public ContainerNode? PeekForward() => _forward.Count > 0 ? _forward[^1] : null;

    // ------------------------------------------------------------------ navigation

    public void ZoomTo(ContainerNode target, bool record = true)
    {
        if (ReferenceEquals(target, ZoomRoot)) return;
        if (record) { _back.Add(ZoomRoot); _forward.Clear(); }
        SetZoomRoot(target);
        Changed?.Invoke();
    }

    private void SetZoomRoot(ContainerNode target)
    {
        ZoomRoot = target;
        _zoomChain.Clear();
        for (var p = target.Parent; p != null; p = p.Parent) { _zoomChain.Insert(0, p); if (ReferenceEquals(p, ViewRoot)) break; }
    }

    public void GoBack()
    {
        if (_back.Count == 0) return;
        var t = _back[^1]; _back.RemoveAt(_back.Count - 1);
        _forward.Add(ZoomRoot);
        SetZoomRoot(t);
        Changed?.Invoke();
    }

    public void GoForward()
    {
        if (_forward.Count == 0) return;
        var t = _forward[^1]; _forward.RemoveAt(_forward.Count - 1);
        _back.Add(ZoomRoot);
        SetZoomRoot(t);
        Changed?.Invoke();
    }

    public void GoUp()
    {
        if (ZoomRoot.Parent is ContainerNode p && !ReferenceEquals(ZoomRoot, ViewRoot)) ZoomTo(p);
    }

    public void GoHome() => ZoomTo(ViewRoot);

    /// <summary>The zoom root may have been deleted externally: climb to the nearest surviving ancestor.</summary>
    public void ValidateZoomRoot()
    {
        lock (Tree.Sync)
        {
            if (!IsAttached(ZoomRoot))
            {
                // RemoveChild clears Parent, so walk the ancestor chain remembered at zoom time
                ContainerNode z = ViewRoot;
                for (int i = _zoomChain.Count - 1; i >= 0; i--)
                    if (IsAttached(_zoomChain[i])) { z = _zoomChain[i]; break; }
                ZoomTo(z, record: false);
            }
            if (Selected != null && Selected.Parent == null && !ReferenceEquals(Selected, ViewRoot)) Selected = null;
        }
    }

    /// <summary>True while the node still hangs below the view root.</summary>
    private bool IsAttached(FsNode n)
    {
        for (FsNode? x = n; x != null; x = x.Parent)
        {
            if (ReferenceEquals(x, ViewRoot)) return true;
            if (x.Parent == null || !x.Parent.Children.Contains(x)) return false;
        }
        return false;
    }

    // ------------------------------------------------------------------ sub scan

    public bool IsSubScanning => SubScan is { IsRunning: true };

    public void StartSubScan()
    {
        if (!Tree.IsLive || !CanGoUp || IsSubScanning) return;
        Tree.ResetScanFlags(ZoomRoot);
        var job = new ScanJob(Tree, ZoomRoot, ZoomRoot.FullPath, isMaster: false);
        SubScan = job;
        job.Finished += _ => Changed?.Invoke();
        job.Start();
        Changed?.Invoke();
    }

    public void StopSubScan() => SubScan?.Stop();

    // ------------------------------------------------------------------ filtering

    private FilterResult? _result;
    private Thread? _filterThread;
    private CancellationTokenSource? _filterCts;
    private volatile bool _refilterRequested;
    public bool IsFiltering { get; private set; }
    public double FilterProgress { get; private set; }
    public string FilterError { get; private set; } = "";

    private sealed class FilterResult
    {
        public readonly Dictionary<FsNode, long> Sizes = new(ReferenceEqualityComparer.Instance);
        public readonly HashSet<FsNode> BoldFolders = new(ReferenceEqualityComparer.Instance);
        public Filter Filter = Filter.Empty;
        public int Version;
    }

    /// <summary>Parses and applies a new filter text. Throws <see cref="FilterException"/> on syntax errors.</summary>
    public void SetFilter(string text)
    {
        var f = new Filter(text);
        Filter = f;
        FilterError = "";
        RequestRefilter(force: true);
    }

    /// <summary>Filtered size of a node under the current filter (0 = hidden).</summary>
    public long SizeOf(FsNode n)
    {
        if (n is FreeSpaceNode) return Tree.Root.FreeBytes;
        if (n is UnknownSpaceNode) return Tree.Root.UnknownBytes;
        var r = _result;
        if (Filter.IsEmpty || r == null || !ReferenceEquals(r.Filter, Filter))
            return Filter.IsEmpty ? n.DiskSize : 0;
        return r.Sizes.TryGetValue(n, out var v) ? v : 0;
    }

    /// <summary>Export inclusion: every real folder, and the files matching the current filter (size 0 included).</summary>
    public bool IsIncluded(FsNode n)
    {
        if (n.IsSynthetic) return false;
        if (n is ContainerNode || Filter.IsEmpty) return true;
        var r = _result;
        return r != null && ReferenceEquals(r.Filter, Filter) && r.Sizes.ContainsKey(n);
    }

    /// <summary>Where exports start: the zoom root, or the snapshot content when zoomed on the "[SNAPSHOT: ...]" frame.</summary>
    public ContainerNode ExportRoot => ZoomRoot is DriveNode { IsSnapshotFrame: true } f && f.ChildCount == 1 && f.Children[0] is ContainerNode c ? c : ZoomRoot;

    public bool IsBoldFolder(FsNode n)
    {
        var r = _result;
        return r != null && ReferenceEquals(r.Filter, Filter) && r.BoldFolders.Contains(n);
    }

    public bool FilterResultStale => !Filter.IsEmpty && (_result == null || !ReferenceEquals(_result.Filter, Filter) || _result.Version != ViewRoot.Version);

    /// <summary>Schedules a (re)filter pass on a background thread (coalesced).</summary>
    public void RequestRefilter(bool force = false)
    {
        if (Filter.IsEmpty)
        {
            _filterCts?.Cancel();
            _result = null;
            Changed?.Invoke();
            return;
        }
        if (!force && !FilterResultStale) return;
        _refilterRequested = true;
        if (_filterThread is { IsAlive: true })
        {
            if (force) _filterCts?.Cancel();
            return;
        }
        _filterThread = new Thread(FilterLoop) { IsBackground = true, Name = "FilterThread" };
        _filterThread.Start();
    }

    private void FilterLoop()
    {
        while (_refilterRequested)
        {
            _refilterRequested = false;
            var cts = new CancellationTokenSource();
            _filterCts = cts;
            var filter = Filter;
            IsFiltering = true;
            FilterProgress = 0;
            Log.Debug("Filtering again: " + ViewRoot.FullPath);
            try
            {
                var result = ComputeFilter(filter, cts.Token);
                if (!cts.IsCancellationRequested) _result = result;
            }
            catch (OperationCanceledException) { _refilterRequested = true; }
            catch (Exception ex) { Log.Error("Filter error: " + ex.Message); }
            IsFiltering = false;
            Changed?.Invoke();
        }
    }

    private FilterResult ComputeFilter(Filter filter, CancellationToken token)
    {
        var res = new FilterResult { Filter = filter };
        var now = DateTime.UtcNow;
        int version;
        int total;
        lock (Tree.Sync) { version = ViewRoot.Version; total = Math.Max(1, ViewRoot.FileCount + ViewRoot.FolderCount); }
        res.Version = version;
        int done = 0;

        // iterative post-order walk; children lists are copied under the lock in small pieces
        long Walk(ContainerNode c, Filter.FolderMaskState state)
        {
            token.ThrowIfCancellationRequested();
            List<FsNode> kids;
            lock (Tree.Sync) kids = c.ChildrenSnapshot();
            long sum = 0;
            foreach (var k in kids)
            {
                if (k is FileNode f)
                {
                    if (filter.Matches(f, state, now)) { res.Sizes[f] = f.DiskSize; sum += f.DiskSize; }
                }
                else if (k is ContainerNode cc)
                {
                    var st = filter.FolderState(state, cc.Name);
                    if (filter.FolderMatches(cc.Name)) res.BoldFolders.Add(cc);
                    long s = Walk(cc, st);
                    if (s > 0) { res.Sizes[cc] = s; sum += s; }
                }
                done++;
            }
            if ((done & 0x3FF) == 0) FilterProgress = Math.Min(1.0, (double)done / total);
            return sum;
        }
        var rootState = Filter.FolderMaskState.Root;
        if (ViewRoot is FolderNode) rootState = filter.FolderState(rootState, ViewRoot.Name);
        long rootSum = Walk(ViewRoot, rootState);
        res.Sizes[ViewRoot] = rootSum;
        return res;
    }

    // ------------------------------------------------------------------ layout

    /// <summary>Builds the treemap item tree for the zoomed root inside <paramref name="bounds"/>.</summary>
    private readonly StableSquarify _stable = new();

    /// <summary>"Element proportions" 0..100 -> desired width/height of the tiles: 4 (very horizontal) .. 1 (squares) .. 0.25 (very vertical).</summary>
    public static double TargetAspect(int proportions)
    {
        double t = (Math.Clamp(proportions, 0, 100) - 50) / 50.0;
        return Math.Pow(4, -t);
    }

    /// <summary>
    /// Builds the visible tree. <paramref name="minLabelWidth"/> is the narrowest block that can still carry a readable
    /// (ellipsis trimmed) label; blocks narrower than that are hidden like blocks below the minimum size, so every
    /// rectangle that is drawn also gets a name.
    /// </summary>
    public TreemapItem BuildLayout(RectangleF bounds, float captionHeight, AppConfig cfg, float minLabelWidth = 0)
    {
        lock (Tree.Sync)
        {
            var root = new TreemapItem(ZoomRoot, null, Math.Max(1, SizeOf(ZoomRoot)), 0) { Rect = bounds };
            root.FolderMaskMatch = IsBoldFolder(ZoomRoot);
            var selectedChain = new HashSet<FsNode>(ReferenceEqualityComparer.Instance);
            for (FsNode? s = Selected; s != null; s = s.Parent) selectedChain.Add(s);
            _stable.BeginPass();
            _minLabelWidth = minLabelWidth;
            BuildChildren(root, captionHeight, cfg, selectedChain, TargetAspect(cfg.Proportions));
            _stable.Prune();
            return root;
        }
    }

    /// <summary>
    /// Tiles <paramref name="kids"/> (in the desired order) inside <paramref name="inner"/> with the squarified
    /// layout engine. The target aspect <paramref name="aspect"/> (width/height) is obtained without touching the
    /// algorithm: the x axis is scaled by 1/aspect, squares are produced there, and the result is scaled back.
    /// Row partitions are cached per container so a running scan changes rectangles smoothly.
    /// </summary>
    private void LayoutKids(ContainerNode parent, List<TreemapItem> kids, RectangleF inner, double aspect)
    {
        int n = kids.Count;
        var dpool = ArrayPool<double>.Shared;
        var opool = ArrayPool<object>.Shared;
        var rpool = ArrayPool<RectangleD>.Shared;
        double[] values = dpool.Rent(n);
        object[] keys = opool.Rent(n);
        RectangleD[] rects = rpool.Rent(n);
        try
        {
            for (int i = 0; i < n; i++) { values[i] = kids[i].Size; keys[i] = kids[i].Node; }
            var scaled = new RectangleD(inner.X / aspect, inner.Y, inner.Width / aspect, inner.Height);
            _stable.Layout(parent, keys.AsSpan(0, n), values.AsSpan(0, n), scaled, 1.0, rects.AsSpan(0, n));
            for (int i = 0; i < n; i++)
            {
                var r = rects[i];
                kids[i].Rect = new RectangleF((float)(r.X * aspect), (float)r.Y, (float)(r.Width * aspect), (float)r.Height);
            }
        }
        finally
        {
            dpool.Return(values);
            opool.Return(keys, clearArray: true);
            rpool.Return(rects);
        }
    }

    private static int CompareBySizeDesc(TreemapItem a, TreemapItem b)
    {
        int c = b.Size.CompareTo(a.Size);
        return c != 0 ? c : string.CompareOrdinal(a.Node.Name, b.Node.Name);
    }


    private float _minLabelWidth;

    private void BuildChildren(TreemapItem item, float cap, AppConfig cfg, HashSet<FsNode> selectedChain, double aspect)
    {
        if (item.Node is not ContainerNode c) return;
        float border = 2f;
        var r = item.Rect;
        var inner = new RectangleF(r.X + border, r.Y + cap, r.Width - 2 * border, r.Height - cap - border);
        // not expanded (detail level reached, or too small to hold a caption plus readable children):
        // drawn as a plain block like a file
        bool expand = item.Depth < DetailLevel || selectedChain.Contains(c) || ReferenceEquals(c, ZoomRoot);
        bool roomy = inner.Width >= 40 && inner.Height >= 2.5f * cap;
        if (!expand || (!roomy && !ReferenceEquals(c, ZoomRoot)) || inner.Width < 6 || inner.Height < 6) { item.Inner = RectangleF.Empty; return; }
        item.Inner = inner;

        var kids = new List<TreemapItem>();
        foreach (var k in c.Children)
        {
            if (k.IsSynthetic) continue;
            long s = SizeOf(k);
            if (s <= 0 && !selectedChain.Contains(k)) continue;
            var ti = new TreemapItem(k, item, Math.Max(1, s), item.Depth + 1);
            if (k is ContainerNode kc) ti.FolderMaskMatch = IsBoldFolder(kc);
            kids.Add(ti);
        }
        if (item.Depth == 0 && ReferenceEquals(c, ViewRoot) && Tree.Root.IsDrive && ReferenceEquals(c, Tree.Root))
        {
            if (ShowFreeSpace && Tree.Root.FreeBytes > 0) kids.Add(new TreemapItem(_free, item, Tree.Root.FreeBytes, 1));
            if (ShowUnknownSpace && Tree.Root.UnknownBytes > 0)
            {
                _unknown.ScanFinished = !Tree.IsMasterScanning;
                _unknown.Filtering = !Filter.IsEmpty;
                kids.Add(new TreemapItem(_unknown, item, Tree.Root.UnknownBytes, 1));
            }
        }
        if (kids.Count == 0)
        {
            if (!c.Enumerated && Tree.IsLive && inner.Width > 8 && inner.Height > 8)
            {
                item.HasCaption = true;
                var m = new TreemapItem(_missing, item, 1, item.Depth + 1) { Rect = inner };
                item.Children.Add(m);
            }
            return;
        }
        item.HasCaption = true;

        // Elements that would end up smaller than the minimum element size do not get their own
        // rectangle: they are aggregated into one visible "rest" block. This keeps the view readable
        // (hiding them would leave voids) but without voids and without inflating the
        // areas of the elements that are shown. A hard cap bounds the per-frame work for huge folders.
        const int MaxItems = 4000;
        float min = Math.Max(1, cfg.MinElementSizePx);
        float minW = Math.Max(min, _minLabelWidth);       // wide enough for a label
        double total = 0;
        foreach (var k in kids) total += k.Size;
        double scale = total > 0 ? inner.Width * (double)inner.Height / total : 0;
        double minArea = Math.Max(4, min * minW * 0.5);
        var kept = new List<TreemapItem>(kids.Count);
        int restCount = 0;
        long restBytes = 0;
        if (kids.Count > 1)
        {
            // decide by size (always); the caller's order is kept when sorting is off
            var bySize = new List<TreemapItem>(kids);
            bySize.Sort(CompareBySizeDesc);
            var drop = new HashSet<TreemapItem>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < bySize.Count; i++)
            {
                var k = bySize[i];
                bool tooSmall = k.Size * scale < minArea || i >= MaxItems;
                if (tooSmall && !selectedChain.Contains(k.Node)) drop.Add(k);
            }
            if (drop.Count == kids.Count)
            {
                // nothing is big enough on its own: show a grid of the biggest ones (as many as fit at the minimum size)
                int keep = Math.Clamp((int)(inner.Width * inner.Height / (min * minW * 2)), 1, bySize.Count);
                for (int i = 0; i < keep; i++) drop.Remove(bySize[i]);
            }
            foreach (var k in kids)
            {
                if (drop.Contains(k)) { restCount++; restBytes += k.Size; }
                else kept.Add(k);
            }
        }
        else kept.AddRange(kids);
        if (cfg.UseSorting) kept.Sort(CompareBySizeDesc);
        TreemapItem? rest = null;
        bool aggregate = cfg.AggregateSmallElements;
        // Lay out, then take every element that came out narrower or lower than the minimum size out of
        // the picture and lay out again: what is drawn individually is always at least min x min pixels.
        // With aggregation the removed elements live in the single "rest" block, otherwise (the older
        // behaviour) they are simply not shown and the visible ones fill the area.
        for (int pass = 0; ; pass++)
        {
            if (rest != null) kept.Remove(rest);
            if (aggregate && restCount > 0)
            {
                var rn = GetRestNode(c);
                rn.Count = restCount; rn.Bytes = restBytes; rn.LogicalSize = rn.DiskSize = restBytes;
                rest = new TreemapItem(rn, item, Math.Max(1, restBytes), item.Depth + 1);
                kept.Add(rest);
            }
            LayoutKids(c, kept, inner, aspect);
            if (pass >= 5 || kept.Count <= 1) break;
            int moved = 0;
            for (int i = kept.Count - 1; i >= 0; i--)
            {
                var k = kept[i];
                if (ReferenceEquals(k, rest) || selectedChain.Contains(k.Node)) continue;
                if (k.Rect.Width < minW || k.Rect.Height < min)
                {
                    // without aggregation the biggest element always stays; with it, a lone sub-minimum element joins the rest block
                    if (rest == null && kept.Count <= 1) break;
                    kept.RemoveAt(i);
                    restCount++; restBytes += k.Size; moved++;
                }
            }
            if (moved == 0) break;
        }
        foreach (var k in kept)
        {
            item.Children.Add(k);
            if (k.Node is ContainerNode) BuildChildren(k, cap, cfg, selectedChain, aspect);
        }
        item.Truncated = rest != null;
    }

    private readonly Dictionary<ContainerNode, RestNode> _restNodes = new(ReferenceEqualityComparer.Instance);

    /// <summary>One stable rest node per container so the stable layout can recognise it across frames.</summary>
    private RestNode GetRestNode(ContainerNode c)
    {
        if (!_restNodes.TryGetValue(c, out var rn)) _restNodes[c] = rn = new RestNode();
        return rn;
    }

    /// <summary>Deepest visible item under the point.</summary>
    public static TreemapItem? HitTest(TreemapItem root, PointF p)
    {
        if (!root.Rect.Contains(p)) return null;
        TreemapItem cur = root;
        while (true)
        {
            TreemapItem? next = null;
            foreach (var ch in cur.Children)
                if (ch.Rect.Contains(p)) { next = ch; break; }
            if (next == null) return cur;
            cur = next;
        }
    }

    public static TreemapItem? Find(TreemapItem root, FsNode node)
    {
        if (ReferenceEquals(root.Node, node)) return root;
        foreach (var ch in root.Children)
        {
            var f = Find(ch, node);
            if (f != null) return f;
        }
        return null;
    }

    // ------------------------------------------------------------------ tagging

    public void ToggleTag(FsNode node, TagColor color)
    {
        lock (Tree.Sync)
        {
            node.Tag = (node.Tag & color) != 0 ? node.Tag & ~color : node.Tag | color;
            (node as ContainerNode ?? node.Parent)?.Touch();
        }
        RequestRefilter(force: !Filter.IsEmpty);
        Changed?.Invoke();
    }

    /// <summary>CTRL+0: clears tags on every element starting from the zoomed area.</summary>
    public void ClearTags()
    {
        lock (Tree.Sync)
        {
            ZoomRoot.Tag = TagColor.None;
            foreach (var n in ZoomRoot.Descendants()) n.Tag = TagColor.None;
            ZoomRoot.Touch();
        }
        RequestRefilter(force: !Filter.IsEmpty);
        Changed?.Invoke();
    }

    /// <summary>Percentage of the whole media currently shown (viewable percent bar).</summary>
    public double ViewablePercent
    {
        get
        {
            long total = Tree.Root.IsDrive ? Math.Max(1, Tree.Root.TotalBytes) : Math.Max(1, SizeOf(ViewRoot));
            return Math.Min(1.0, (double)SizeOf(ZoomRoot) / total);
        }
    }

    // ------------------------------------------------------------------ snapshot comparison overlay

    private Core.Compare.DiffResult? _diff;
    /// <summary>Result of "compare with snapshot"; while set the map tints changed elements and captions carry the delta.</summary>
    public Core.Compare.DiffResult? Diff
    {
        get => _diff;
        set { _diff = value; Changed?.Invoke(); }
    }

    // ------------------------------------------------------------------ program footprint highlight

    private Dictionary<FsNode, bool>? _spotlight;
    /// <summary>
    /// Folders of one program (true) and folders carved out of them (false), see <see cref="Core.Apps.FootprintResult.SpotlightFor"/>;
    /// while set the map fades everything outside them.
    /// </summary>
    public Dictionary<FsNode, bool>? Spotlight
    {
        get => _spotlight;
        set { _spotlight = value; Changed?.Invoke(); }
    }

    /// <summary>Spotlight state of a node: its nearest ancestor-or-self in <see cref="Spotlight"/> decides (outside when none).</summary>
    public bool InSpotlight(FsNode node)
    {
        var sp = _spotlight;
        if (sp == null) return true;
        for (FsNode? n = node; n != null; n = n.Parent)
            if (sp.TryGetValue(n, out bool inside)) return inside;
        return false;
    }

    // ------------------------------------------------------------------ reclaimable space (knowledge base)

    /// <summary>Known caches / temp areas below the view root and their total (background computed).</summary>
    public Core.Knowledge.ReclaimSummary Reclaim { get; private set; } = Core.Knowledge.ReclaimSummary.Empty;
    /// <summary>Cloud folders below the view root (OneDrive...) and their local / online-only files; computed with <see cref="Reclaim"/>.</summary>
    public Core.Cloud.CloudSummary Cloud { get; private set; } = Core.Cloud.CloudSummary.Empty;
    public bool IsSummarizing { get; private set; }
    public event Action? ReclaimChanged;
    private int _reclaimVersion = -1;
    private CancellationTokenSource? _reclaimCts;

    /// <summary>Recomputes the reclaimable summary when the tree changed since the last one (coalesced, off thread).</summary>
    public void RequestReclaimSummary(bool force = false)
    {
        int v;
        lock (Tree.Sync) v = ViewRoot.Version;
        if (!force && (v == _reclaimVersion || IsSummarizing)) return;
        if (IsSummarizing) _reclaimCts?.Cancel();
        var cts = new CancellationTokenSource();
        _reclaimCts = cts;
        IsSummarizing = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Core.Knowledge.ReclaimSummary s;
                s = Core.Knowledge.KnowledgeBase.Summarize(ViewRoot, n => n.DiskSize, cts.Token, Tree.Sync);   // real sizes, independent of the filter; locks per folder
                var cloud = Core.Cloud.CloudFiles.Summarize(ViewRoot, cts.Token, Tree.Sync);
                if (!cts.IsCancellationRequested) { Reclaim = s; Cloud = cloud; _reclaimVersion = v; }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Debug("Reclaim summary failed: " + ex.Message); }
            finally { if (ReferenceEquals(_reclaimCts, cts)) IsSummarizing = false; ReclaimChanged?.Invoke(); }
        });
    }

    public void Dispose()
    {
        _filterCts?.Cancel();
        _reclaimCts?.Cancel();
        SubScan?.Stop();
    }
}
