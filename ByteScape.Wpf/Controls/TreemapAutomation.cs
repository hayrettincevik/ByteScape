using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Threading;
using ByteScape.Core.Model;
using ByteScape.Treemap;

namespace ByteScape.Wpf.Controls;

/// <summary>
/// The map as UI Automation sees it (what Narrator, NVDA and JAWS read): a tree whose items are the blocks the map draws,
/// nested like the folders, each named "name, kind, size, share of its folder". The selected block is the one with the
/// keyboard focus, so moving the selection with the arrow keys is announced like moving in a tree view.
/// </summary>
internal sealed class TreemapAutomationPeer : FrameworkElementAutomationPeer, ISelectionProvider
{
    /// <summary>Most children one block reports (the map draws a few hundred; this bounds a pathological view).</summary>
    internal const int MaxChildren = 2000;

    private Dictionary<FsNode, TreemapItemAutomationPeer> _peers = new(ReferenceEqualityComparer.Instance);

    public TreemapAutomationPeer(TreemapView owner) : base(owner) { }

    internal TreemapView View => (TreemapView)Owner;

    /// <summary>One peer per block for as long as it is drawn, so a screen reader keeps its place while the map repaints.</summary>
    internal TreemapItemAutomationPeer PeerFor(FsNode node)
    {
        if (!_peers.TryGetValue(node, out var p)) _peers[node] = p = new TreemapItemAutomationPeer(this, node);
        return p;
    }

    private bool _updatePending;
    private long _lastUpdate;

    /// <summary>
    /// The map built a new layout: drop the peers of blocks no longer drawn and let clients re-read the tree. A running
    /// scan rebuilds the layout many times a second; the tree is re-read at most every 300 ms.
    /// </summary>
    internal void LayoutChanged()
    {
        if (_updatePending) return;
        _updatePending = true;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(Math.Max(0, 300 - (Environment.TickCount64 - _lastUpdate))) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _updatePending = false;
            _lastUpdate = Environment.TickCount64;
            if (_peers.Count > 0)
            {
                var keep = new Dictionary<FsNode, TreemapItemAutomationPeer>(ReferenceEqualityComparer.Instance);
                foreach (var (node, peer) in _peers) if (View.ItemOf(node) != null) keep[node] = peer;
                _peers = keep;
            }
            InvalidatePeer();
        };
        timer.Start();
    }

    /// <summary>
    /// The peer of a drawn block, connected to the automation tree (every level above it enumerated, which gives it its
    /// parent and window), so events raised on it reach the clients; null when the block is not drawn.
    /// </summary>
    internal TreemapItemAutomationPeer? Connected(FsNode node)
    {
        var item = View.ItemOf(node);
        if (item == null) return null;
        var chain = new List<TreemapItem>();
        for (var x = item; x != null; x = x.Parent) chain.Insert(0, x);
        AutomationPeer parent = this;
        foreach (var it in chain)
        {
            var next = Find(parent.GetChildren(), it.Node);
            if (next == null) { parent.ResetChildrenCache(); next = Find(parent.GetChildren(), it.Node); }
            if (next == null) return null;
            parent = next;
        }
        return (TreemapItemAutomationPeer)parent;
    }

    private static TreemapItemAutomationPeer? Find(List<AutomationPeer>? kids, FsNode node) =>
        kids?.OfType<TreemapItemAutomationPeer>().FirstOrDefault(k => ReferenceEquals(k.Node, node));

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tree;
    protected override string GetClassNameCore() => nameof(TreemapView);
    protected override string GetNameCore() { var n = base.GetNameCore(); return string.IsNullOrEmpty(n) ? T("Disk map") : n; }
    protected override string GetHelpTextCore()
    {
        var h = base.GetHelpTextCore();
        return string.IsNullOrEmpty(h) ? T("Arrow keys move between blocks, Ctrl+Down zooms in, Ctrl+Up zooms out, Enter opens in Explorer, Shift+F10 opens the menu.") : h;
    }

    protected override List<AutomationPeer>? GetChildrenCore() =>
        View.LayoutRoot is { } root ? new List<AutomationPeer> { PeerFor(root.Node) } : null;

    protected override AutomationPeer? GetPeerFromPointCore(Point point)
    {
        try
        {
            if (View.ItemAt(View.PointFromScreen(point)) is { } it) return PeerFor(it.Node);
        }
        catch (InvalidOperationException) { }   // not on screen
        return base.GetPeerFromPointCore(point);
    }

    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Selection ? this : base.GetPattern(patternInterface);

    // ISelectionProvider: one block at most
    bool ISelectionProvider.CanSelectMultiple => false;
    bool ISelectionProvider.IsSelectionRequired => false;
    IRawElementProviderSimple[] ISelectionProvider.GetSelection() =>
        View.Model?.Selected is { } s && Connected(s) is { } p ? new[] { ProviderFromPeer(p) } : Array.Empty<IRawElementProviderSimple>();
}

/// <summary>One block of the map: a folder (with the blocks drawn inside it as children), a file, or a summary block.</summary>
internal sealed class TreemapItemAutomationPeer : AutomationPeer, ISelectionItemProvider
{
    private readonly TreemapAutomationPeer _map;

    public TreemapItemAutomationPeer(TreemapAutomationPeer map, FsNode node) { _map = map; Node = node; }

    public FsNode Node { get; }
    private TreemapView View => _map.View;
    private TreemapItem? Item => View.ItemOf(Node);

    protected override List<AutomationPeer>? GetChildrenCore()
    {
        if (Item is not { Children.Count: > 0 } it) return null;
        return TreemapView.InSpeakingOrder(it.Children).Take(TreemapAutomationPeer.MaxChildren).Select(c => (AutomationPeer)_map.PeerFor(c.Node)).ToList();
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TreeItem;
    protected override string GetNameCore() => View.AccessibleName(Node, Item);
    protected override string GetItemTypeCore() => TreemapView.KindName(Node);
    protected override string GetClassNameCore() => "TreemapBlock";
    protected override string GetAutomationIdCore() => "";
    protected override string GetHelpTextCore() => "";
    protected override string GetItemStatusCore() => "";
    protected override string GetAcceleratorKeyCore() => "";
    protected override string GetAccessKeyCore() => "";
    protected override AutomationPeer? GetLabeledByCore() => null;
    protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.None;
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;
    protected override bool IsEnabledCore() => true;
    protected override bool IsPasswordCore() => false;
    protected override bool IsRequiredForFormCore() => false;
    protected override bool IsKeyboardFocusableCore() => !Node.IsSynthetic;
    protected override bool HasKeyboardFocusCore() => View.IsKeyboardFocused && ReferenceEquals(View.Model?.Selected, Node);
    protected override void SetFocusCore() { View.SelectFromAutomation(Node); View.Focus(); }

    protected override Rect GetBoundingRectangleCore() => View.ScreenRectOf(Item);
    protected override bool IsOffscreenCore() => GetBoundingRectangleCore().IsEmpty;

    protected override Point GetClickablePointCore()
    {
        var r = GetBoundingRectangleCore();
        return r.IsEmpty ? new Point(double.NaN, double.NaN) : new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
    }

    /// <summary>Place among the blocks of the same folder, largest first (the order the arrow keys follow).</summary>
    protected override int GetPositionInSetCore()
    {
        if (Item?.Parent is not { } parent) return 1;
        int i = TreemapView.InSpeakingOrder(parent.Children).FindIndex(c => ReferenceEquals(c.Node, Node));
        return i < 0 ? -1 : i + 1;   // -1: not set
    }

    protected override int GetSizeOfSetCore() => Item?.Parent is { } parent ? parent.Children.Count : 1;

    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.SelectionItem && !Node.IsSynthetic ? this : null;

    // ISelectionItemProvider
    bool ISelectionItemProvider.IsSelected => ReferenceEquals(View.Model?.Selected, Node);
    IRawElementProviderSimple ISelectionItemProvider.SelectionContainer => ProviderFromPeer(_map);
    void ISelectionItemProvider.Select() => View.SelectFromAutomation(Node);
    void ISelectionItemProvider.AddToSelection()
    {
        if (!ReferenceEquals(View.Model?.Selected, Node)) throw new InvalidOperationException("The map selects one block at a time.");
    }
    void ISelectionItemProvider.RemoveFromSelection()
    {
        if (ReferenceEquals(View.Model?.Selected, Node)) throw new InvalidOperationException("The map keeps its selection until another block is selected.");
    }
}
