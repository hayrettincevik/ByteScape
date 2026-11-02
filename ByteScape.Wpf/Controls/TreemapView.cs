using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ByteScape.Core.Config;
using ByteScape.Core.Games;
using ByteScape.Core.Model;
using ByteScape.Core.Util;
using ByteScape.Treemap;
using D = System.Drawing;

namespace ByteScape.Wpf.Controls;

/// <summary>
/// WPF treemap surface. Draws the item tree built by <see cref="ViewModel"/> with a single
/// <see cref="DrawingContext"/> pass (fast for thousands of rectangles), handles hover, click
/// (select), double click (zoom with animation) and right click (Explorer menu via the owner).
/// The keyboard moves the selection like in a tree view, and UI Automation shows the drawn blocks
/// as a tree to screen readers (<see cref="TreemapAutomationPeer"/>).
/// </summary>
public sealed class TreemapView : FrameworkElement
{
    private ViewModel? _model;
    private TreemapItem? _layout;
    private int _layoutVersion = -1;
    private Size _layoutSize;
    private bool _dirty = true;
    private FsNode? _hover;
    private readonly List<FsNode> _trail = new();
    private readonly DispatcherTimer _refresh;
    private Point _lastMouse;
    private DateTime _lastMouseMove = DateTime.MinValue;
    private int _idleTicks;
    private double _lastFrameMs;                 // cost of the last OnRender (UI thread)
    private long _lastFrameEnd;                  // Environment.TickCount64 when it finished
    private readonly Dictionary<int, SolidColorBrush> _brushes = new();
    private readonly Dictionary<int, Pen> _pens = new();
    private readonly Dictionary<string, FormattedText> _textCache = new();
    private readonly Dictionary<FormattedText, Drawing> _labelDrawings = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<long, string> _sizeText = new();
    private Typeface _typeface = new("Segoe UI");
    private double _fontPx = 12;
    private float _minLabelWidth = 40;
    private double _pixelsPerDip = 1;
    private AppConfig? _configOverride;

    // zoom animation
    private bool _animating;
    private DateTime _animStart;
    private Rect _animFrom, _animTo;
    private bool _animZoomIn;
    private RenderTargetBitmap? _animBitmap;
    private ContainerNode? _animPrevRoot;

    public TreemapView()
    {
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        _refresh = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(Math.Max(20, AppConfig.Current.RefreshDelayMs)) };
        _refresh.Tick += (_, _) => OnRefreshTick();
        Loaded += (_, _) => { _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip; ApplyConfig(); _refresh.Start(); ThemeManager.PaletteChanged += ApplyConfig; };
        Unloaded += (_, _) => { _refresh.Stop(); ThemeManager.PaletteChanged -= ApplyConfig; };
    }

    public event Action<FsNode?>? SelectionChanged;
    public event Action? NavigationChanged;
    public event Action<FsNode, Point>? ContextMenuRequested;
    public event Action<TreemapItem?, Point>? HoverChanged;

    public ViewModel? Model
    {
        get => _model;
        set
        {
            if (_model != null) _model.Changed -= OnModelChanged;
            _model = value;
            if (_model != null) _model.Changed += OnModelChanged;
            MarkDirty();
        }
    }

    /// <summary>Configuration used for painting; null = <see cref="AppConfig.Current"/> (the configuration preview passes its working copy).</summary>
    public AppConfig? ConfigOverride { get => _configOverride; set { _configOverride = value; ApplyConfig(); } }
    private AppConfig Cfg => _configOverride ?? AppConfig.Current;

    /// <summary>Elements drawn per frame in the last layout (debug/statistics).</summary>
    public int ItemCount { get; private set; }

    public void ApplyConfig()
    {
        var cfg = Cfg;
        _fontPx = Math.Max(6, cfg.FontSize) * 96.0 / 72.0;
        _typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _textCache.Clear();
        _labelDrawings.Clear();
        _brushes.Clear();
        _pens.Clear();
        // narrowest block that still carries a readable label: four average characters plus the ellipsis, plus padding
        _minLabelWidth = (float)(TextNatural("abcd…", false, D.Color.Black).WidthIncludingTrailingWhitespace + 4);
        _refresh.Interval = TimeSpan.FromMilliseconds(Math.Max(20, cfg.RefreshDelayMs));
        MarkDirty();
    }

    public void MarkDirty() { _dirty = true; InvalidateVisual(); }

    private void OnModelChanged()
    {
        _dirty = true;
        Dispatcher.BeginInvoke(() => NavigationChanged?.Invoke());
    }

    private void OnRefreshTick()
    {
        if (_model == null) return;
        var cfg = Cfg;
        bool active = (DateTime.Now - _lastMouseMove).TotalSeconds < 2 || _animating;
        if (cfg.UseDynaSmooth && !active) { if (++_idleTicks % 8 != 0) return; }
        else _idleTicks = 0;
        int v;
        lock (_model.Tree.Sync) v = _model.ViewRoot.Version;
        if (_dirty || v != _layoutVersion || _animating || HasBlinking())
        {
            // adaptive refresh: a view with thousands of labels costs hundreds of ms per frame; while a
            // scan keeps changing the tree, never spend more than a third of the UI thread on repainting
            if (!_animating && _lastFrameMs > 40 && Environment.TickCount64 - _lastFrameEnd < _lastFrameMs * 2) return;
            if (!_model.Filter.IsEmpty && v != _layoutVersion) _model.RequestRefilter();
            InvalidateVisual();
        }
    }

    private bool HasBlinking()
    {
        if (_layout == null) return false;
        long now = Environment.TickCount64, blink = Cfg.BlinkDurationMs;
        return Any(_layout, it => now - it.Node.ChangedAtTicks < blink);
    }

    private static bool Any(TreemapItem it, Func<TreemapItem, bool> p)
    {
        if (p(it)) return true;
        foreach (var c in it.Children) if (Any(c, p)) return true;
        return false;
    }

    private float CaptionHeight => (float)Math.Ceiling(_fontPx * 1.2 + 4);

    private void EnsureLayout()
    {
        if (_model == null) return;
        var size = RenderSize;
        int v;
        lock (_model.Tree.Sync) v = _model.ViewRoot.Version;
        if (_layout == null || _dirty || v != _layoutVersion || size != _layoutSize)
        {
            _model.ValidateZoomRoot();
            _layout = _model.BuildLayout(new D.RectangleF(0, 0, (float)size.Width, (float)size.Height), CaptionHeight, Cfg, _minLabelWidth);
            _layoutVersion = v;
            _layoutSize = size;
            _dirty = false;
            ItemCount = 0;
            Any(_layout, _ => { ItemCount++; return false; });
            // a screen reader that has read the map re-reads it (the peer exists only once a client asked for it)
            if (UIElementAutomationPeer.FromElement(this) is TreemapAutomationPeer peer) peer.LayoutChanged();
        }
    }

    // ------------------------------------------------------------------ painting

    protected override void OnRender(DrawingContext dc)
    {
        var bg = (Brush)(TryFindResource("MapBackground") ?? Brushes.White);
        dc.DrawRectangle(bg, null, new Rect(RenderSize));
        if (_model == null || RenderSize.Width < 2 || RenderSize.Height < 2) return;
        if (_animating) { PaintAnimation(dc); return; }
        var frame = System.Diagnostics.Stopwatch.StartNew();
        EnsureLayout();
        if (_layout != null) PaintItem(dc, _layout, Cfg);
        _lastFrameMs = frame.Elapsed.TotalMilliseconds;
        _lastFrameEnd = Environment.TickCount64;
    }

    private SolidColorBrush BrushFor(D.Color c)
    {
        int key = c.ToArgb();
        if (!_brushes.TryGetValue(key, out var b))
        {
            b = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            b.Freeze();
            _brushes[key] = b;
        }
        return b;
    }

    private Pen PenFor(D.Color c, double width = 1)
    {
        int key = c.ToArgb() ^ ((int)(width * 8) << 24);
        if (!_pens.TryGetValue(key, out var p))
        {
            p = new Pen(BrushFor(c), width);
            p.Freeze();
            _pens[key] = p;
        }
        return p;
    }

    /// <summary>
    /// Formatted text from the cache. A FormattedText re-runs line layout whenever MaxTextWidth changes,
    /// so the width is never mutated on a cached instance: the natural (untrimmed) text is cached once
    /// and trimmed variants are cached per 8 px width bucket. This keeps a frame with 1000+ labels cheap.
    /// </summary>
    private FormattedText Text(string s, double maxWidth, bool bold, D.Color color)
    {
        var natural = TextNatural(s, bold, color);
        if (natural.WidthIncludingTrailingWhitespace <= maxWidth) return natural;
        int bucket = Math.Max(8, (int)(maxWidth / 8) * 8);
        string key = s + "|" + (bold ? "b" : "n") + color.ToArgb() + "|" + bucket;   // '|' cannot occur in a file name
        if (!_textCache.TryGetValue(key, out var ft))
        {
            if (_textCache.Count > 6000) _textCache.Clear();
            ft = NewText(s, bold, color);
            ft.MaxTextWidth = bucket;
            _textCache[key] = ft;
        }
        return ft;
    }

    private FormattedText TextNatural(string s, bool bold, D.Color color)
    {
        string key = s + "|" + (bold ? "b" : "n") + color.ToArgb();
        if (!_textCache.TryGetValue(key, out var ft))
        {
            if (_textCache.Count > 6000) _textCache.Clear();
            ft = NewText(s, bold, color);
            ft.MaxTextWidth = 10000;
            _textCache[key] = ft;
        }
        return ft;
    }

    private FormattedText NewText(string s, bool bold, D.Color color)
    {
        var tf = bold ? new Typeface(_typeface.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal) : _typeface;
        return new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, _fontPx, BrushFor(color), _pixelsPerDip)
        { MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
    }

    private void PaintItem(DrawingContext dc, TreemapItem it, AppConfig cfg)
    {
        var r = it.Rect;
        if (r.Width < 1 || r.Height < 1) return;
        var node = it.Node;
        var rect = new Rect(Math.Round(r.X), Math.Round(r.Y), Math.Round(r.Width), Math.Round(r.Height));
        if (rect.Width < 1 || rect.Height < 1) return;

        // high contrast: every block on the theme's window colour with text and borders in its text colour, the
        // selected one in its highlight colours; the tints that carry meaning (comparison, program footprint) stay
        bool hc = ThemeManager.HighContrast;
        D.Color baseColor = BaseColorStyled(node, cfg);
        D.Color fill = hc ? Sys(SystemColors.WindowColor) : TreemapStyle.ShadeForLevel(baseColor, it.Depth, cfg);
        // snapshot comparison overlay: grown = red, shrunk = green, new = orange; intensity by the change's share of the element
        if (_model!.Diff is { } diff)
        {
            if (diff.AddedNodes.Contains(node)) fill = TreemapStyle.Blend(fill, D.Color.FromArgb(0xF5, 0x8A, 0x1F), 0.55f);
            else if (diff.DeltaOf.TryGetValue(node, out long delta) && delta != 0)
            {
                double share = Math.Min(1.0, Math.Abs(delta) / (double)Math.Max(1, node.DiskSize));
                float k = (float)(0.15 + 0.45 * Math.Sqrt(share));
                fill = TreemapStyle.Blend(fill, delta > 0 ? D.Color.FromArgb(0xD9, 0x3B, 0x3B) : D.Color.FromArgb(0x2E, 0xA0, 0x4F), k);
            }
            else fill = TreemapStyle.Blend(fill, D.Color.White, 0.25f);   // unchanged: fade back
        }
        // program footprint: everything outside the selected program's folders fades away
        bool spotRoot = false;
        if (_model.Spotlight is { } spot)
        {
            var probe = node.IsSynthetic && it.Parent != null ? it.Parent.Node : node;   // rest blocks are not attached to the tree
            if (!_model.InSpotlight(probe)) fill = TreemapStyle.Blend(fill, D.Color.White, 0.75f);
            spotRoot = spot.TryGetValue(node, out bool isPiece) && isPiece;
        }
        bool hovered = _hover != null && (ReferenceEquals(node, _hover) || IsHaloParent(it, cfg));
        bool selected = _model!.Selected != null && ReferenceEquals(node, _model.Selected);
        long since = Environment.TickCount64 - node.ChangedAtTicks;
        if (hc) { if (selected) fill = Sys(SystemColors.HighlightColor); }   // no blinking, hover or trail shades: they would lower the contrast
        else
        {
            if (since >= 0 && since < cfg.BlinkDurationMs && (since / 250) % 2 == 0) fill = TreemapStyle.Blend(fill, D.Color.White, 0.6f);
            if (hovered) fill = TreemapStyle.Blend(fill, D.Color.White, 0.35f);
            else if (cfg.MouseTrail && _trail.Contains(node)) fill = TreemapStyle.Blend(fill, D.Color.White, 0.15f);
        }

        if (selected && cfg.HilightShadow && !hc)
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, new Rect(rect.X + 4, rect.Y + 4, rect.Width, rect.Height));
        dc.DrawRectangle(BrushFor(fill), null, rect);
        if (node is RestNode && rect.Width > 3 && rect.Height > 3)
        {
            // dotted texture for the aggregated small elements
            var dot = BrushFor(hc ? Sys(SystemColors.GrayTextColor) : TreemapStyle.Blend(fill, D.Color.Black, 0.35f));
            for (double y = rect.Y + 3; y < rect.Bottom - 1; y += 4)
                for (double x = rect.X + 3 + ((int)(y / 4) % 2) * 2; x < rect.Right - 1; x += 4)
                    dc.DrawRectangle(dot, null, new Rect(x, y, 1, 1));
        }
        var border = hc ? Sys(selected ? SystemColors.HighlightTextColor : SystemColors.WindowTextColor) : selected ? D.Color.Black : TreemapStyle.BorderColor(fill, cfg);
        double bw = selected ? 2 : 1;
        dc.DrawRectangle(null, PenFor(border, bw), new Rect(rect.X + bw / 2, rect.Y + bw / 2, Math.Max(0, rect.Width - bw), Math.Max(0, rect.Height - bw)));
        if (!selected && !hc && rect.Width > 3 && rect.Height > 3)
        {
            var lp = PenFor(TreemapStyle.LightColor(fill, cfg));
            dc.DrawLine(lp, new Point(rect.X + 1, rect.Y + 1.5), new Point(rect.Right - 1, rect.Y + 1.5));
            dc.DrawLine(lp, new Point(rect.X + 1.5, rect.Y + 1), new Point(rect.X + 1.5, rect.Bottom - 1));
        }
        if (spotRoot && rect.Width > 4 && rect.Height > 4)
            dc.DrawRectangle(null, PenFor(hc ? Sys(SystemColors.HighlightColor) : D.Color.FromArgb(0x1F, 0x6F, 0xDB), 2), new Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2));
        // knowledge badge: this folder (not an inherited one) is a known cache / temp area / protected location
        double badgeShift = 0;
        if (cfg.ShowKnowledgeBadges && node is ContainerNode && rect.Width >= 30 && rect.Height >= 18
            && Core.Knowledge.KnowledgeBase.Classify(node) is { Inherited: false } km
            && !(node.Parent is ContainerNode pp && Core.Knowledge.KnowledgeBase.Classify(pp)?.Location == km.Location))   // one badge per rule, not per nested pattern
        {
            badgeShift = 16;
            var (glyph, back) = km.Location.Category == Core.Knowledge.LocationCategory.Cloud ? ("☁", D.Color.FromArgb(0x1F, 0x6F, 0xDB)) : km.Location.Safety switch
            {
                Core.Knowledge.Safety.Regenerable => ("♻", D.Color.FromArgb(0x2E, 0xA0, 0x4F)),
                Core.Knowledge.Safety.SafeWhenClosed => ("♻", D.Color.FromArgb(0x3D, 0x8F, 0xD1)),
                Core.Knowledge.Safety.Caution => ("!", D.Color.FromArgb(0xE0, 0x9A, 0x1A)),
                _ => ("×", D.Color.FromArgb(0xC8, 0x3A, 0x3A)),
            };
            var pill = new Rect(rect.X + 3, rect.Y + 3, 14, 12);
            dc.DrawRoundedRectangle(BrushFor(back), null, pill, 3, 3);
            var g = TextNatural(glyph, true, D.Color.White);
            DrawLabel(dc, g, pill.X + (pill.Width - g.Width) / 2, pill.Y + (pill.Height - g.Height) / 2 - 0.5);
        }

        var tag = node.Tag;
        if (tag != TagColor.None && rect.Width > 10 && rect.Height > 10)
        {
            double x = rect.Right - 4;
            foreach (var c in new[] { TagColor.Red, TagColor.Yellow, TagColor.Green, TagColor.Blue })
            {
                if ((tag & c) == 0) continue;
                x -= 9;
                dc.DrawRectangle(BrushFor(TreemapStyle.TagBrush(c, cfg)), PenFor(D.Color.Black), new Rect(x, rect.Y + 2.5, 7, 7));
            }
        }

        var textColor = !hc ? TreemapStyle.TextColor(fill)
            : selected ? Sys(SystemColors.HighlightTextColor)
            : Contrast(Sys(SystemColors.WindowTextColor), fill) >= Contrast(Sys(SystemColors.WindowColor), fill) ? Sys(SystemColors.WindowTextColor) : Sys(SystemColors.WindowColor);
        float cap = CaptionHeight;
        if (it.HasCaption)
        {
            if (rect.Height >= cap && rect.Width > 14)
            {
                var ft = Text(Caption(node, it), rect.Width - 8 - badgeShift, it.FolderMaskMatch, textColor);
                DrawLabel(dc, ft, rect.X + 4 + badgeShift, rect.Y + (cap - ft.Height) / 2);
            }
            foreach (var ch in it.Children) PaintItem(dc, ch, cfg);
        }
        else DrawBlockLabel(dc, it, rect, it.FolderMaskMatch, textColor);
    }

    private void DrawBlockLabel(DrawingContext dc, TreemapItem it, Rect rect, bool bold, D.Color textColor)
    {
        if (rect.Width < 14 || rect.Height < _fontPx) return;
        string name = it.Node switch
        {
            RestNode rn => P(rn.Count, "{0:N0} item", "{0:N0} items"),
            UnknownSpaceNode u => u.DisplayName,
            FreeSpaceNode => T("Free Space"),
            MissingDataNode => "...",
            DriveNode d => d.FullPath,
            _ => GameLabel(it.Node) ?? it.Node.Name,
        };
        double avail = rect.Width - 4;
        if (rect.Width < _minLabelWidth - 0.5) return;   // same threshold the layout hides blocks at
        var nameText = Text(name, avail, bold, textColor);
        double lineH = nameText.Height;
        double nameW = Math.Min(nameText.Width, avail);
        // texts are trimmed to the available width, so a clip is only needed when the height is short
        bool clip = rect.Height < lineH + 1;
        if (clip) dc.PushClip(new RectangleGeometry(rect));
        string size = it.Node is MissingDataNode ? "" : SizeText(it.Size);
        if (rect.Height >= 2 * lineH + 2 && size.Length > 0)
        {
            var sizeText = Text(size, avail, false, textColor);
            double top = rect.Y + (rect.Height - 2 * lineH) / 2;
            DrawLabel(dc, nameText, rect.X + (rect.Width - nameW) / 2, top);
            DrawLabel(dc, sizeText, rect.X + (rect.Width - Math.Min(sizeText.Width, avail)) / 2, top + lineH);
        }
        else
        {
            DrawLabel(dc, nameText, rect.X + (rect.Width - nameW) / 2, rect.Y + (rect.Height - lineH) / 2);
        }
        if (clip) dc.Pop();
    }

    /// <summary>
    /// Draws a cached label. DrawText builds the glyph runs of a FormattedText again on every call, which
    /// dominated frames with a thousand labels; the runs are built once into a frozen drawing and only
    /// translated afterwards.
    /// </summary>
    private void DrawLabel(DrawingContext dc, FormattedText ft, double x, double y)
    {
        if (!_labelDrawings.TryGetValue(ft, out var drawing))
        {
            if (_labelDrawings.Count > 6000) _labelDrawings.Clear();
            var group = new DrawingGroup();
            using (var c = group.Open()) c.DrawText(ft, new Point(0, 0));
            group.Freeze();
            _labelDrawings[ft] = drawing = group;
        }
        dc.PushTransform(new TranslateTransform(Math.Round(x), Math.Round(y)));
        dc.DrawDrawing(drawing);
        dc.Pop();
    }

    private string SizeText(long bytes)
    {
        if (!_sizeText.TryGetValue(bytes, out var s))
        {
            if (_sizeText.Count > 20000) _sizeText.Clear();
            _sizeText[bytes] = s = Format.Size(bytes);
        }
        return s;
    }

    /// <summary>"1245620 · ELDEN RING" for numbered Steam folders (shader cache, workshop, saves), once the game library is read.</summary>
    private static string? GameLabel(FsNode node) =>
        node is ContainerNode && node.Name.Length > 0 && char.IsAsciiDigit(node.Name[0]) ? GameLibrary.Loaded?.LabelFor(node) : null;

    private string Caption(FsNode node, TreemapItem it)
    {
        string text = node switch
        {
            UnknownSpaceNode u => u.DisplayName + " - " + SizeText(it.Size),
            FreeSpaceNode => T("Free Space") + " - " + SizeText(it.Size),
            MissingDataNode => "...",
            DriveNode d => d.FullPath + " - " + SizeText(it.Size),
            _ => (GameLabel(node) ?? node.Name) + " - " + SizeText(it.Size),
        };
        if (_model?.Diff is { } diff && !node.IsSynthetic)
        {
            if (diff.AddedNodes.Contains(node)) text += "  (" + T("new") + ")";
            else if (diff.DeltaOf.TryGetValue(node, out long d) && d != 0) text += "  (" + (d > 0 ? "+" : "−") + SizeText(Math.Abs(d)) + ")";
        }
        return text;
    }

    private D.Color BaseColorStyled(FsNode node, AppConfig cfg)
    {
        if (node is FileNode f && _model!.Style == ViewStyle.FileClasses)
        {
            var cls = cfg.ClassOf(f.Extension);
            if (cls != null) return cls.Color;
        }
        return TreemapStyle.BaseColor(node.Kind, cfg);
    }

    private static D.Color Sys(Color c) => D.Color.FromArgb(c.R, c.G, c.B);

    /// <summary>WCAG contrast ratio of two colours (1 … 21).</summary>
    internal static double Contrast(D.Color a, D.Color b)
    {
        static double L(D.Color c)
        {
            static double Ch(int v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        double la = L(a), lb = L(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private bool IsHaloParent(TreemapItem it, AppConfig cfg)
    {
        if (_hover == null || cfg.HilightHaloLevels <= 0) return false;
        int levels = cfg.HilightHaloLevels;
        for (FsNode? p = _hover.Parent; p != null && levels > 0; p = p.Parent, levels--)
            if (ReferenceEquals(p, it.Node)) return true;
        return false;
    }

    // ------------------------------------------------------------------ zoom animation

    /// <summary>Animates towards <paramref name="target"/>; <paramref name="commit"/> performs the model change.</summary>
    public void AnimateZoom(ContainerNode target, bool zoomIn, Action? commit = null)
    {
        if (_model == null) return;
        commit ??= () => _model.ZoomTo(target);
        var cfg = Cfg;
        EnsureLayout();
        var item = _layout != null ? ViewModel.Find(_layout, zoomIn ? target : _model.ZoomRoot) : null;
        if (cfg.ZoomTimeMs <= 0 || item == null || RenderSize.Width < 10 || RenderSize.Height < 10)
        {
            commit();
            MarkDirty();
            return;
        }
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            _animBitmap = new RenderTargetBitmap((int)(RenderSize.Width * dpi.DpiScaleX), (int)(RenderSize.Height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            _animBitmap.Render(this);
        }
        catch { _animBitmap = null; }
        var full = new Rect(RenderSize);
        var ir = new Rect(item.Rect.X, item.Rect.Y, item.Rect.Width, item.Rect.Height);
        _animZoomIn = zoomIn;
        _animPrevRoot = _model.ZoomRoot;
        _animFrom = zoomIn ? ir : full;
        _animTo = full;
        _animStart = DateTime.Now;
        _animating = true;
        commit();
        _dirty = true;
        InvalidateVisual();
    }

    private void PaintAnimation(DrawingContext dc)
    {
        var cfg = Cfg;
        double t = (DateTime.Now - _animStart).TotalMilliseconds / Math.Max(1, cfg.ZoomTimeMs);
        if (t >= 1 || _model == null)
        {
            _animating = false;
            _animBitmap = null;
            MarkDirty();
            EnsureLayout();
            if (_layout != null) PaintItem(dc, _layout, cfg);
            return;
        }
        t = 1 - Math.Pow(1 - t, 3);
        var full = new Rect(RenderSize);
        if (_animBitmap != null) dc.DrawImage(_animBitmap, full);
        if (_animZoomIn)
        {
            var cur = Lerp(_animFrom, full, t);
            if (cfg.UseRealZoom)
            {
                var item = _model.BuildLayout(new D.RectangleF((float)cur.X, (float)cur.Y, (float)cur.Width, (float)cur.Height), CaptionHeight, cfg, _minLabelWidth);
                PaintItem(dc, item, cfg);
            }
            else if (_animBitmap != null)
            {
                dc.PushClip(new RectangleGeometry(full));
                // scale the old region up to the whole view
                double sx = full.Width / Math.Max(1, _animFrom.Width), sy = full.Height / Math.Max(1, _animFrom.Height);
                double k = t;
                var target = new Rect(-_animFrom.X * sx * k, -_animFrom.Y * sy * k, full.Width * (1 + (sx - 1) * k), full.Height * (1 + (sy - 1) * k));
                dc.DrawImage(_animBitmap, target);
                dc.Pop();
            }
        }
        else
        {
            EnsureLayout();
            if (_layout != null) PaintItem(dc, _layout, cfg);
            var targetItem = _layout != null && _animPrevRoot != null ? ViewModel.Find(_layout, _animPrevRoot) : null;
            var target = targetItem != null ? new Rect(targetItem.Rect.X, targetItem.Rect.Y, targetItem.Rect.Width, targetItem.Rect.Height) : full;
            var cur = Lerp(full, target, t);
            if (_animBitmap != null) { dc.PushOpacity(1 - t * 0.3); dc.DrawImage(_animBitmap, cur); dc.Pop(); }
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Render, InvalidateVisual);
    }

    private static Rect Lerp(Rect a, Rect b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Width + (b.Width - a.Width) * t, a.Height + (b.Height - a.Height) * t);

    // ------------------------------------------------------------------ mouse

    public TreemapItem? ItemAt(Point p)
    {
        if (_layout == null || _animating) return null;
        return ViewModel.HitTest(_layout, new D.PointF((float)p.X, (float)p.Y));
    }

    public FsNode? HoveredNode => _hover ?? ItemAt(_lastMouse)?.Node;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _lastMouse = e.GetPosition(this);
        _lastMouseMove = DateTime.Now;
        var it = ItemAt(_lastMouse);
        var node = it?.Node;
        if (!ReferenceEquals(node, _hover))
        {
            _hover = Cfg.MouseHover ? node : null;
            if (Cfg.MouseTrail && node != null) { _trail.Add(node); if (_trail.Count > 12) _trail.RemoveAt(0); }
            InvalidateVisual();
            HoverChanged?.Invoke(it, _lastMouse);
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        _trail.Clear();
        InvalidateVisual();
        HoverChanged?.Invoke(null, _lastMouse);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var it = ItemAt(e.GetPosition(this));
        if (it == null || _model == null) return;
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 1 && !it.Node.IsSynthetic)
        {
            _model.Selected = it.Node;
            SelectionChanged?.Invoke(it.Node);
            MarkDirty();
            Announce();
        }
        else if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            if (it.Node is ContainerNode c && !ReferenceEquals(c, _model.ZoomRoot) && !c.IsSynthetic) AnimateZoom(c, zoomIn: true);
        }
        else if (e.ChangedButton == MouseButton.Right && !it.Node.IsSynthetic)
        {
            if (!ReferenceEquals(_model.Selected, it.Node)) { _model.Selected = it.Node; SelectionChanged?.Invoke(it.Node); MarkDirty(); Announce(); }
            ContextMenuRequested?.Invoke(it.Node, PointToScreen(e.GetPosition(this)));
        }
    }

    // ------------------------------------------------------------------ keyboard

    /// <summary>
    /// The map with the keyboard focus (Tab, or a click) works like a tree view: Up / Down go to the previous / next block of
    /// the same folder (largest first), Home / End to the largest / smallest, Right into the folder's largest block, Left out
    /// to the folder (at the top of the view it zooms out one level); Ctrl+Down zooms into the selected folder, Shift+F10 or
    /// the menu key opens the context menu. A block the map does not draw (inside a folder drawn as one block) is brought
    /// into view by zooming to its folder. Enter, Delete, Backspace and Ctrl+Up are the window's (open, delete, back, up).
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && HandleKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers)) e.Handled = true;
    }

    /// <summary>One key press on the map; true when the map used it.</summary>
    internal bool HandleKey(Key key, ModifierKeys mods)
    {
        if (_model == null || _animating) return false;
        if (key == Key.Apps && mods == ModifierKeys.None || key == Key.F10 && mods == ModifierKeys.Shift)
        {
            OpenMenuAtSelection();
            return true;
        }
        if (key == Key.Down && mods == ModifierKeys.Control)
        {
            if (_model.Selected is ContainerNode c && !c.IsSynthetic && !ReferenceEquals(c, _model.ZoomRoot)) { AnimateZoom(c, zoomIn: true); Announce(); }
            return true;
        }
        if (mods != ModifierKeys.None || key is not (Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End)) return false;
        // from here on the key is the map's: the arrow keys never move the focus to the next control
        var cur = _model.Selected;
        if (cur == null || !ReferenceEquals(cur, _model.ZoomRoot) && !_model.ZoomRoot.IsAncestorOf(cur))
        {
            SelectAndReveal(Kids(_model.ZoomRoot).FirstOrDefault() ?? _model.ZoomRoot);
            return true;
        }
        if (key == Key.Left)
        {
            if (!ReferenceEquals(cur, _model.ZoomRoot)) SelectAndReveal(cur.Parent!);
            else if (_model.CanGoUp && cur.Parent is ContainerNode up) { _model.ZoomTo(up); SelectAndReveal(up); }
            return true;
        }
        if (key == Key.Right)
        {
            if (cur is ContainerNode folder && Kids(folder).FirstOrDefault() is { } first) SelectAndReveal(first);
            return true;
        }
        if (ReferenceEquals(cur, _model.ZoomRoot) || cur.Parent is not ContainerNode parent) return true;   // the top block has no siblings in view
        var sibs = Kids(parent);
        int i = sibs.FindIndex(n => ReferenceEquals(n, cur));
        int to = key switch { Key.Up => i - 1, Key.Down => i + 1, Key.Home => 0, _ => sibs.Count - 1 };
        if (i >= 0 && to >= 0 && to < sibs.Count && to != i) SelectAndReveal(sibs[to]);
        return true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        // after the map's own focus event: the screen reader then says which block is selected
        Dispatcher.BeginInvoke(Announce, DispatcherPriority.Input);
    }

    /// <summary>The blocks of a folder the keyboard visits: real files and folders with a size under the filter, largest first.</summary>
    private List<FsNode> Kids(ContainerNode c)
    {
        var list = new List<(FsNode Node, long Size)>();
        lock (_model!.Tree.Sync)
            foreach (var k in c.Children)
            {
                long size = k.IsSynthetic ? 0 : _model.SizeOf(k);
                if (size > 0 || ReferenceEquals(k, _model.Selected) && !k.IsSynthetic) list.Add((k, size));
            }
        list.Sort((a, b) => b.Size != a.Size ? b.Size.CompareTo(a.Size) : string.CompareOrdinal(a.Node.Name, b.Node.Name));
        return list.ConvertAll(x => x.Node);
    }

    /// <summary>Selects a block and makes sure it is drawn: when its folder is drawn as a single block, the map zooms to that folder.</summary>
    private void SelectAndReveal(FsNode node)
    {
        if (_model == null) return;
        _model.Selected = node;
        _dirty = true;
        EnsureLayout();
        if (ItemOf(node) == null && (node as ContainerNode ?? node.Parent) is ContainerNode target && !ReferenceEquals(target, _model.ZoomRoot))
        {
            // a folder is shown inside its parent, a file inside its folder
            if (node is ContainerNode && node.Parent is ContainerNode p && !ReferenceEquals(node, _model.ViewRoot)) target = p;
            _model.ZoomTo(target);
            _dirty = true;
            EnsureLayout();
        }
        SelectionChanged?.Invoke(node);
        MarkDirty();
        Announce();
    }

    /// <summary>Selection by a UI Automation client (a screen reader's "select" command).</summary>
    internal void SelectFromAutomation(FsNode node)
    {
        if (_model == null || node.IsSynthetic) return;
        SelectAndReveal(node);
    }

    private void OpenMenuAtSelection()
    {
        if (_model?.Selected is not { IsSynthetic: false } node) return;
        EnsureLayout();
        var it = ItemOf(node);
        var at = it != null ? new Point(it.Rect.X + Math.Min(it.Rect.Width / 2, 40), it.Rect.Y + Math.Min(it.Rect.Height / 2, 20)) : new Point(8, 8);
        ContextMenuRequested?.Invoke(node, PointToScreen(at));
    }

    /// <summary>Tells a screen reader that the selected block now has the focus (nothing is done when no client listens).</summary>
    private void Announce()
    {
        if (!IsKeyboardFocused || _model?.Selected is not { } node) return;
        if (!AutomationPeer.ListenerExists(AutomationEvents.AutomationFocusChanged) && !AutomationPeer.ListenerExists(AutomationEvents.SelectionItemPatternOnElementSelected)) return;
        if (UIElementAutomationPeer.CreatePeerForElement(this) is not TreemapAutomationPeer map) return;
        EnsureLayout();
        if (map.Connected(node) is not { } peer) return;
        peer.RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
        peer.RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged);
    }

    // ------------------------------------------------------------------ UI Automation

    protected override AutomationPeer OnCreateAutomationPeer() => new TreemapAutomationPeer(this);

    /// <summary>The drawn tree (built when needed).</summary>
    internal TreemapItem? LayoutRoot { get { EnsureLayout(); return _layout; } }

    private Dictionary<FsNode, TreemapItem>? _itemIndex;
    private TreemapItem? _indexedLayout;

    /// <summary>The drawn block of a node in the current layout, or null when it is not drawn.</summary>
    internal TreemapItem? ItemOf(FsNode node)
    {
        var root = LayoutRoot;
        if (root == null) return null;
        if (!ReferenceEquals(_indexedLayout, root))
        {
            var index = new Dictionary<FsNode, TreemapItem>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<TreemapItem>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var it = stack.Pop();
                if (it.Node is not MissingDataNode) index[it.Node] = it;   // one placeholder object stands for every unread folder
                foreach (var c in it.Children) stack.Push(c);
            }
            _itemIndex = index;
            _indexedLayout = root;
        }
        return _itemIndex!.TryGetValue(node, out var found) ? found : null;
    }

    /// <summary>The order blocks are read and visited in: largest first, the "N smaller items" block last; unread placeholders are left out.</summary>
    internal static List<TreemapItem> InSpeakingOrder(IEnumerable<TreemapItem> items)
    {
        var list = items.Where(i => i.Node is not MissingDataNode).ToList();
        list.Sort((a, b) =>
        {
            bool ra = a.Node is RestNode, rb = b.Node is RestNode;
            if (ra != rb) return ra ? 1 : -1;
            return b.Size != a.Size ? b.Size.CompareTo(a.Size) : string.CompareOrdinal(a.Node.Name, b.Node.Name);
        });
        return list;
    }

    internal static string KindName(FsNode node) => node switch
    {
        DriveNode => T("drive"),
        ContainerNode => T("folder"),
        FileNode => T("file"),
        _ => "",
    };

    /// <summary>What a screen reader says for a block: "Documents, folder, 12.4 GB, 38%" (its share of the folder around it).</summary>
    internal string AccessibleName(FsNode node, TreemapItem? it)
    {
        var parts = new List<string>
        {
            node switch
            {
                RestNode rn => P(rn.Count, "{0:N0} smaller item", "{0:N0} smaller items"),
                UnknownSpaceNode u => u.DisplayName,
                FreeSpaceNode => T("Free Space"),
                DriveNode d => d.FullPath,
                _ => GameLabel(node) ?? node.Name,
            },
        };
        if (KindName(node) is { Length: > 0 } kind) parts.Add(kind);
        long size = it?.Size ?? _model?.SizeOf(node) ?? node.DiskSize;
        parts.Add(SizeText(size));
        if (it?.Parent is { Size: > 0 } parent)
        {
            string pct = (100.0 * size / parent.Size).ToString(size * 1000 < parent.Size ? "0.##" : "0.#", CultureInfo.CurrentCulture);
            parts.Add(F($"{pct}%"));
        }
        if (ReferenceEquals(_model?.Selected, node) && it == null) parts.Add(T("not drawn"));
        return string.Join(", ", parts);
    }

    /// <summary>The block's rectangle on the screen (device pixels), clipped to the map; empty when it is not visible.</summary>
    internal Rect ScreenRectOf(TreemapItem? it)
    {
        if (it == null || !IsVisible || PresentationSource.FromVisual(this) == null) return Rect.Empty;
        var r = Rect.Intersect(new Rect(it.Rect.X, it.Rect.Y, it.Rect.Width, it.Rect.Height), new Rect(RenderSize));
        if (r.IsEmpty || r.Width < 1 || r.Height < 1) return Rect.Empty;
        try { return new Rect(PointToScreen(r.TopLeft), PointToScreen(r.BottomRight)); }
        catch (InvalidOperationException) { return Rect.Empty; }
    }
}
