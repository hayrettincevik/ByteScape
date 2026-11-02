using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ByteScape.Core.Config;
using ByteScape.Core.Model;
using ByteScape.Wpf.Controls;
using ByteScape.Wpf.Views;
using Xunit;
using Xunit.Abstractions;

namespace ByteScape.Tests;

/// <summary>
/// What a screen reader and a keyboard user get: every window seen through UI Automation (the interface Narrator, NVDA
/// and JAWS read), the map's keyboard navigation and automation tree, and the high contrast colours.
/// </summary>
[Collection("language")]
public class AccessibilityTests
{
    private readonly ITestOutputHelper _out;
    public AccessibilityTests(ITestOutputHelper o) => _out = o;

    /// <summary>
    /// Every window with data, as UI Automation shows it: each control a user can operate has a name (not empty and not
    /// an icon character), no icon character is read as text, list rows are not read as type names, and nothing that
    /// shows a hand cursor is reachable by the mouse only.
    /// </summary>
    [Fact]
    public void EveryWindow_ThroughUiAutomation_NamesEveryControl() => WpfHost.Run(() =>
    {
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        var named = new SortedSet<string>(StringComparer.Ordinal);   // what the inputs are called, for review in the output
        int controls = 0;
        LanguageSwitchTests.OpenEveryWindow((root, where) =>
        {
            foreach (var p in A11yAudit.Peers(root))
            {
                controls++;
                if (A11yAudit.Problem(p) is { } why) problems.Add($"{where}: {why}");
                if (p.IsControlElement() && p.GetAutomationControlType() is AutomationControlType.Edit or AutomationControlType.Slider or AutomationControlType.ComboBox
                    or AutomationControlType.RadioButton or AutomationControlType.Button or AutomationControlType.TabItem or AutomationControlType.DataItem or AutomationControlType.CheckBox)
                    named.Add($"{where}: {p.GetAutomationControlType()} \"{p.GetName()}\"");
            }
            foreach (var why in A11yAudit.MouseOnly(root)) problems.Add($"{where}: {why}");
        });
        foreach (var p in problems) _out.WriteLine(p);
        _out.WriteLine("");
        foreach (var n in named) _out.WriteLine(n);
        Assert.True(controls > 1000, "only " + controls + " automation elements seen");
        Assert.True(problems.Count == 0, problems.Count + " accessibility problems:\n" + string.Join("\n", problems));
    });

    /// <summary>
    /// Every text of every window against what is behind it: WCAG 2.1 AA asks 4.5:1 (3:1 for large text). Disabled texts,
    /// icons and texts faded on purpose (a transparent ancestor) are left out, as WCAG does.
    /// </summary>
    [Fact]
    public void EveryWindow_TextContrast_MeetsWcagAA() => WpfHost.Run(() =>
    {
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        int texts = 0;
        LanguageSwitchTests.OpenEveryWindow((root, where) =>
        {
            foreach (var (tb, why) in A11yAudit.LowContrast(root, highContrast: false)) problems.Add($"{where}: {why}");
            texts += A11yAudit.Texts(root).Count();
        });
        foreach (var p in problems) _out.WriteLine(p);
        Assert.True(texts > 1000, "only " + texts + " texts seen");
        Assert.True(problems.Count == 0, problems.Count + " texts with too little contrast:\n" + string.Join("\n", problems));
    });

    /// <summary>
    /// With a Windows contrast theme on, every text is drawn in the theme's own colours (window text, highlight text, gray
    /// text, highlight) on its window or highlight colour, so it reads in black, white and every custom contrast theme.
    /// BYTESCAPE_A11Y_SHOTS=1 saves a picture of every window (in %TEMP%\bytescape-tests\contrast).
    /// </summary>
    [Fact]
    public void EveryWindow_InHighContrast_UsesTheSystemColours() => WpfHost.Run(() =>
    {
        var shots = Environment.GetEnvironmentVariable("BYTESCAPE_A11Y_SHOTS") == "1" ? Path.Combine(Path.GetTempPath(), "bytescape-tests", "contrast") : null;
        if (shots != null) Directory.CreateDirectory(shots);
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        Wpf.ThemeManager.ForceHighContrast(true);
        try
        {
            LanguageSwitchTests.OpenEveryWindow((root, where) =>
            {
                foreach (var (tb, why) in A11yAudit.LowContrast(root, highContrast: true)) problems.Add($"{where}: {why}");
                if (shots != null && root is Window w) WpfHost.Snapshot(w, Path.Combine(shots, where + ".png"));
            });
        }
        finally { Wpf.ThemeManager.ForceHighContrast(null); }
        foreach (var p in problems) _out.WriteLine(p);
        Assert.True(problems.Count == 0, problems.Count + " texts not in the contrast theme's colours:\n" + string.Join("\n", problems));
    });

    /// <summary>
    /// The map in high contrast: the blocks on the theme's window colour with its text colour for borders and names, the
    /// selected block in the highlight colour; none of the palette's block colours is left.
    /// </summary>
    [Fact]
    public void Map_InHighContrast_PaintsWithTheSystemColours() => WpfHost.Run(() =>
    {
        Wpf.ThemeManager.ForceHighContrast(true);
        var (host, view, map) = SampleView();
        try
        {
            var movie = view.Tree.Root.FindChild("movie.mkv")!;
            view.Model.Selected = movie;
            map.MarkDirty();
            WpfHost.Pump();
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)map.ActualWidth, (int)map.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(map);
            if (Environment.GetEnvironmentVariable("BYTESCAPE_A11Y_SHOTS") == "1")
            {
                var dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "contrast");
                Directory.CreateDirectory(dir);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using var fs = File.Create(Path.Combine(dir, "map.png"));
                enc.Save(fs);
            }
            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            var px = new byte[w * h * 4];
            bmp.CopyPixels(px, w * 4, 0);
            Color At(int x, int y) { int i = (y * w + x) * 4; return Color.FromRgb(px[i + 2], px[i + 1], px[i]); }
            var counts = new Dictionary<Color, int>();
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { var c = At(x, y); counts[c] = counts.GetValueOrDefault(c) + 1; }
            double Share(Color c) => counts.GetValueOrDefault(c) / (double)(w * h);
            Assert.True(Share(SystemColors.WindowColor) > 0.5, "window colour covers only " + Share(SystemColors.WindowColor).ToString("P0"));
            Assert.True(Share(SystemColors.WindowTextColor) > 0.005, "no borders or names in the window text colour");
            var folder = ByteScape.Treemap.TreemapStyle.BaseColor(NodeKind.Folder, AppConfig.Current);
            Assert.Equal(0, counts.GetValueOrDefault(Color.FromRgb(folder.R, folder.G, folder.B)));
            // the selected block: highlight colour inside its frame
            var r = map.ItemOf(movie)!.Rect;
            Assert.Equal(SystemColors.HighlightColor, At((int)Math.Round(r.X) + 3, (int)Math.Round(r.Y) + 3));
        }
        finally { host.Close(); Wpf.ThemeManager.ForceHighContrast(null); AppConfig.Replace(new AppConfig()); }
    });

    /// <summary>The sample drive in a view shown off screen; the caller closes the window.</summary>
    private static (Window Host, ScanView View, TreemapView Map) SampleView()
    {
        AppConfig.Replace(new AppConfig { WatchDog = false, ZoomTimeMs = 0 });
        var tree = SampleTree.Create();
        var view = new ScanView(tree, tree.Root, "sample");
        var host = new Window { Content = view, Width = 1000, Height = 700, Title = "host", Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        host.Show();
        WpfHost.Pump();
        return (host, view, view.Map);
    }

    /// <summary>
    /// The arrow keys walk the map like a tree: Down / Up through the blocks of a folder largest first, Home / End, Right into
    /// a folder, Left out of it, Ctrl+Down zooms in. Along a long random walk the selected block is always one the map draws
    /// (the view zooms to show it) and never a summary block.
    /// </summary>
    [Fact]
    public void Map_Keyboard_WalksTheBlocksLikeATree() => WpfHost.Run(() =>
    {
        var (host, view, map) = SampleView();
        try
        {
            var m = view.Model;
            var root = (ContainerNode)m.ZoomRoot;
            List<FsNode> Kids(ContainerNode c) => c.Children.Where(k => !k.IsSynthetic && k.DiskSize > 0).OrderByDescending(k => k.DiskSize).ThenBy(k => k.Name, StringComparer.Ordinal).ToList();

            Assert.True(map.HandleKey(Key.Down, ModifierKeys.None));
            Assert.Same(Kids(root)[0], m.Selected);                         // nothing selected: the largest block of the view
            Assert.True(map.HandleKey(Key.Down, ModifierKeys.None));
            Assert.Same(Kids(root)[1], m.Selected);
            map.HandleKey(Key.End, ModifierKeys.None);
            Assert.Same(Kids(root)[^1], m.Selected);
            map.HandleKey(Key.Home, ModifierKeys.None);
            Assert.Same(Kids(root)[0], m.Selected);
            map.HandleKey(Key.Up, ModifierKeys.None);
            Assert.Same(Kids(root)[0], m.Selected);                         // stays at the first

            var users = (ContainerNode)root.FindChild("Users")!;
            view.Model.Selected = users;
            map.HandleKey(Key.Right, ModifierKeys.None);
            Assert.Same(Kids(users)[0], m.Selected);                        // into the folder: its largest block
            map.HandleKey(Key.Left, ModifierKeys.None);
            Assert.Same(users, m.Selected);                                 // and out again
            map.HandleKey(Key.Left, ModifierKeys.None);
            Assert.Same(root, m.Selected);                                  // the top of the view
            Assert.True(map.HandleKey(Key.Left, ModifierKeys.None));        // nothing above the drive: stays
            Assert.Same(root, m.Selected);

            view.Model.Selected = users;
            Assert.True(map.HandleKey(Key.Down, ModifierKeys.Control));     // Ctrl+Down zooms into the selected folder
            Assert.Same(users, m.ZoomRoot);
            Assert.False(map.HandleKey(Key.A, ModifierKeys.None));          // other keys are the window's
            Assert.False(map.HandleKey(Key.Enter, ModifierKeys.None));

            // a long walk: the selection is always drawn, real, and inside the zoomed folder
            var rnd = new Random(11);
            var keys = new[] { Key.Up, Key.Down, Key.Left, Key.Right, Key.Home, Key.End };
            for (int i = 0; i < 400; i++)
            {
                map.HandleKey(keys[rnd.Next(keys.Length)], ModifierKeys.None);
                if (i % 50 == 25) m.GoHome();
                var sel = m.Selected!;
                Assert.False(sel.IsSynthetic);
                Assert.True(ReferenceEquals(sel, m.ZoomRoot) || m.ZoomRoot.IsAncestorOf(sel), "selection outside the view");
                Assert.True(map.ItemOf(sel) != null, sel.FullPath + " is selected but not drawn");
            }
        }
        finally { host.Close(); AppConfig.Replace(new AppConfig()); }
    });

    /// <summary>The map as a screen reader gets it through its automation peer: a tree of the drawn blocks, named, placed and selectable.</summary>
    [Fact]
    public void Map_AutomationPeer_IsATreeOfTheDrawnBlocks() => WpfHost.Run(() =>
    {
        var (host, view, map) = SampleView();
        try
        {
            var peer = Assert.IsType<TreemapAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(map));
            Assert.Equal(AutomationControlType.Tree, peer.GetAutomationControlType());
            Assert.Equal("Disk map", peer.GetName());
            Assert.Contains("Arrow keys", peer.GetHelpText());
            var top = Assert.Single(peer.GetChildren());
            Assert.Equal(AutomationControlType.TreeItem, top.GetAutomationControlType());
            Assert.StartsWith("C:\\, drive, ", top.GetName());

            var blocks = top.GetChildren().Cast<TreemapItemAutomationPeer>().ToList();
            Assert.True(blocks.Count >= 4, "only " + blocks.Count + " blocks");
            var layout = map.ItemOf(view.Model.ZoomRoot)!;
            long total = layout.Size;
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                var item = map.ItemOf(b.Node)!;
                Assert.Equal(i + 1, b.GetPositionInSet());
                Assert.Equal(layout.Children.Count, b.GetSizeOfSet());
                Assert.Contains(ByteScape.Core.Util.Format.Size(item.Size), b.GetName());
                Assert.EndsWith("%", b.GetName());
                Assert.False(b.IsOffscreen());
                var r = b.GetBoundingRectangle();
                Assert.True(r.Width >= 1 && r.Height >= 1);
                if (i > 0) Assert.True(item.Size <= map.ItemOf(blocks[i - 1].Node)!.Size || b.Node is RestNode);   // largest first
            }
            var movie = blocks.First(b => b.Node.Name == "movie.mkv");
            Assert.Equal("movie.mkv, file, 640.0MB, " + (100.0 * (640L << 20) / total).ToString("0.#") + "%", movie.GetName());

            // selecting through the automation pattern selects on the map
            var select = Assert.IsAssignableFrom<System.Windows.Automation.Provider.ISelectionItemProvider>(movie.GetPattern(PatternInterface.SelectionItem));
            select.Select();
            Assert.Same(movie.Node, view.Model.Selected);
            Assert.True(select.IsSelected);
            var selection = Assert.IsAssignableFrom<System.Windows.Automation.Provider.ISelectionProvider>(peer.GetPattern(PatternInterface.Selection));
            Assert.Single(selection.GetSelection());

            // a folder's blocks are its children in the automation tree
            var users = blocks.First(b => b.Node.Name == "Users");
            Assert.Contains(users.GetChildren()!.Cast<TreemapItemAutomationPeer>(), k => k.Node.Name == "Pictures");
        }
        finally { host.Close(); AppConfig.Replace(new AppConfig()); }
    });

    /// <summary>
    /// A real UI Automation client (the interface screen readers use, from another thread, through the window handle) finds
    /// the map, reads its blocks by name and selects one.
    /// </summary>
    [Fact]
    public void Map_ThroughAUiAutomationClient_ReadsAndSelectsBlocks()
    {
        Window? host = null;
        ScanView? view = null;
        IntPtr hwnd = IntPtr.Zero;
        WpfHost.Run(() => { (host, view, _) = SampleView(); hwnd = new WindowInteropHelper(host).Handle; });
        try
        {
            var window = AutomationElement.FromHandle(hwnd);
            var tree = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tree));
            Assert.NotNull(tree);
            Assert.Equal("Disk map", tree.Current.Name);
            var items = tree.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem)).Cast<AutomationElement>().ToList();
            Assert.True(items.Count >= 8, "only " + items.Count + " blocks");
            var movie = items.Single(e => e.Current.Name.StartsWith("movie.mkv, file, 640.0MB", StringComparison.Ordinal));
            Assert.False(movie.Current.BoundingRectangle.IsEmpty);
            var pictures = items.Single(e => e.Current.Name.StartsWith("Pictures, folder", StringComparison.Ordinal));
            Assert.Equal("Users", TreeWalker.ControlViewWalker.GetParent(pictures).Current.Name.Split(',')[0]);
            ((SelectionItemPattern)movie.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            string? selected = null;
            WpfHost.Run(() => selected = view!.Model.Selected?.Name);
            Assert.Equal("movie.mkv", selected);
        }
        finally { WpfHost.Run(() => { host?.Close(); AppConfig.Replace(new AppConfig()); }); }
    }

    /// <summary>Enter on a focused button presses the button, and lists keep their keys, instead of acting on the map's selection.</summary>
    [Fact]
    public void WindowShortcuts_LeaveTheFocusedControlsKeysAlone() => WpfHost.Run(() =>
    {
        Assert.True(Wpf.MainWindow.FocusedControlUses(new Button(), Key.Enter));
        Assert.False(Wpf.MainWindow.FocusedControlUses(new Button(), Key.Delete));
        Assert.True(Wpf.MainWindow.FocusedControlUses(new ListViewItem(), Key.Delete));
        Assert.True(Wpf.MainWindow.FocusedControlUses(new ComboBox(), Key.D1));
        Assert.False(Wpf.MainWindow.FocusedControlUses(new TreemapView(), Key.Enter));
        Assert.False(Wpf.MainWindow.FocusedControlUses(null, Key.Enter));
    });

    /// <summary>Inputs get the label beside them; a radio button whose content is a heading and a hint is named by the heading.</summary>
    [Fact]
    public void Labels_NameTheInputsBesideThem() => WpfHost.Run(() =>
    {
        var box = new TextBox();
        var slider = new Slider();
        var radio = new RadioButton { Content = new StackPanel { Children = { new TextBlock { Text = "Move to the Recycle Bin" }, new TextBlock { Text = "Can be restored." } } } };
        var row = new DockPanel { Children = { new TextBlock { Text = "API key" }, box } };
        var panel = new StackPanel { Children = { row, new TextBlock { Text = "Smoothness" }, slider, radio } };
        var w = new Window { Content = panel, Width = 400, Height = 300, Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        w.Show();
        Wpf.Controls.A11y.LinkLabels(w);
        string Name(UIElement e) => UIElementAutomationPeer.CreatePeerForElement(e).GetName();
        Assert.NotNull(System.Windows.Automation.AutomationProperties.GetLabeledBy(box));
        Assert.Equal("API key", Name(box));
        Assert.Equal("Smoothness", Name(slider));
        Assert.Equal("Move to the Recycle Bin", Name(radio));
        Assert.Equal("Go back", Wpf.Controls.A11y.ShortName("Go back (Backspace)"));
        Assert.Equal("Cleanup", Wpf.Controls.A11y.ShortName("Cleanup: large files, temporary files"));
        w.Close();
    });
}

/// <summary>Checks on the UI Automation tree (the peers WPF gives screen readers) and on the visual tree.</summary>
internal static class A11yAudit
{
    /// <summary>Control types a user operates: each needs a name a screen reader can say.</summary>
    private static readonly HashSet<AutomationControlType> Operated = new()
    {
        AutomationControlType.Button, AutomationControlType.CheckBox, AutomationControlType.RadioButton, AutomationControlType.ComboBox,
        AutomationControlType.Edit, AutomationControlType.Hyperlink, AutomationControlType.MenuItem, AutomationControlType.Slider,
        AutomationControlType.TabItem, AutomationControlType.ListItem, AutomationControlType.TreeItem, AutomationControlType.SplitButton,
        AutomationControlType.Spinner, AutomationControlType.DataItem,
    };

    public static IEnumerable<AutomationPeer> Peers(DependencyObject root)
    {
        if (root is not UIElement e || UIElementAutomationPeer.CreatePeerForElement(e) is not { } top) yield break;
        var stack = new Stack<AutomationPeer>();
        stack.Push(top);
        var seen = new HashSet<AutomationPeer>(ReferenceEqualityComparer.Instance);
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            if (!seen.Add(p)) continue;
            yield return p;
            List<AutomationPeer>? kids = null;
            try { kids = p.GetChildren(); } catch { }
            if (kids != null) foreach (var k in kids) stack.Push(k);
        }
    }

    /// <summary>A description of what is wrong with this element for a screen reader, or null.</summary>
    public static string? Problem(AutomationPeer p)
    {
        if (!p.IsControlElement() || p.IsOffscreen() && p.GetAutomationControlType() is AutomationControlType.ListItem or AutomationControlType.DataItem) return null;
        // the arrow and page buttons of a scroll bar are the WPF theme's; screen readers scroll with the scroll pattern
        if (p is UIElementAutomationPeer { Owner: var o } && o is not ScrollBar && (Ancestor<ScrollBar>(o) != null || Ancestor<Track>(o) != null)) return null;
        var type = p.GetAutomationControlType();
        string name = p.GetName() ?? "";
        string where = Describe(p);
        if (name.Length > 0 && name.All(c => IsIcon(c) || char.IsWhiteSpace(c)))
            return $"{type} {where} is read as an icon character (U+{(int)name.Trim()[0]:X4})";
        if (Operated.Contains(type))
        {
            if (string.IsNullOrWhiteSpace(name)) return $"{type} {where} has no name";
            if (name.StartsWith("ByteScape.", StringComparison.Ordinal) || name.StartsWith("System.", StringComparison.Ordinal) || name.Contains(" = ", StringComparison.Ordinal) && name.Contains('{'))
                return $"{type} {where} is read as a type name: {Short(name)}";
        }
        return null;
    }

    private static bool IsIcon(char c) => c >= '' && c <= '';

    private static T? Ancestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var x = VisualTreeHelper.GetParent(d); x != null; x = VisualTreeHelper.GetParent(x))
            if (x is T t) return t;
        return null;
    }

    private static string Short(string s) => s.Length > 60 ? s[..60] + "…" : s;

    private static string Describe(AutomationPeer p)
    {
        string cls = p.GetClassName();
        string id = p.GetAutomationId();
        var owner = (p as UIElementAutomationPeer)?.Owner as FrameworkElement;
        string tip = owner?.ToolTip as string ?? "";
        string content = (owner as ContentControl)?.Content as string ?? "";
        var parts = new List<string> { cls };
        if (id.Length > 0) parts.Add("#" + id);
        if (content.Length > 0) parts.Add("content " + Hex(content));
        if (tip.Length > 0) parts.Add("tip \"" + Short(tip) + "\"");
        if (owner != null) parts.Add("in " + Path(owner));
        return "[" + string.Join(", ", parts) + "]";
    }

    private static string Hex(string s) => string.Concat(s.Take(8).Select(c => IsIcon(c) ? $"U+{(int)c:X4}" : c.ToString()));

    /// <summary>The element's place: the names of its nearest named ancestors.</summary>
    private static string Path(DependencyObject d)
    {
        var names = new List<string>();
        for (var x = d; x != null && names.Count < 3; x = VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x))
            if (x is FrameworkElement { Name.Length: > 0 } fe) names.Add(fe.Name);
            else if (x is Window w) { names.Add(w.GetType().Name); break; }
        return names.Count == 0 ? "?" : string.Join(" < ", names);
    }

    /// <summary>The visible texts of a window (icons left out).</summary>
    public static IEnumerable<TextBlock> Texts(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is TextBlock tb and not Wpf.Controls.Glyph && tb.IsVisible && tb.ActualWidth > 0 && tb.Text.Any(char.IsLetterOrDigit)) yield return tb;
            int n = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(d) : 0;
            for (int i = 0; i < n; i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
    }

    /// <summary>
    /// Texts that fail: below the WCAG AA ratio, or (high contrast) drawn in a colour that is not one of the contrast theme's.
    /// Disabled texts and texts faded on purpose (an ancestor with opacity below 1) are left out.
    /// </summary>
    public static IEnumerable<(TextBlock, string)> LowContrast(DependencyObject root, bool highContrast)
    {
        // the pairs a contrast theme guarantees: its text colours on its window colour, highlight text on highlight
        var onWindow = new[] { SystemColors.WindowTextColor, SystemColors.GrayTextColor, SystemColors.HighlightColor, SystemColors.HotTrackColor };
        foreach (var tb in Texts(root))
        {
            if (!tb.IsEnabled || Faded(tb) || tb.Foreground is not SolidColorBrush fgb) continue;
            if (Behind(tb) is not { } bg) continue;
            var fg = Over(fgb.Color, bg);
            string what = $"\"{Short(tb.Text)}\" in {Path(tb)}: #{fg.R:X2}{fg.G:X2}{fg.B:X2} on #{bg.R:X2}{bg.G:X2}{bg.B:X2}";
            if (highContrast)
            {
                bool proper = bg == SystemColors.WindowColor && onWindow.Contains(fg) || bg == SystemColors.HighlightColor && fg == SystemColors.HighlightTextColor;
                if (!proper) yield return (tb, what + " (not a pair of the theme's colours)");
                continue;
            }
            double ratio = Contrast(fg, bg);
            bool large = tb.FontSize >= 24 || tb.FontSize >= 18.66 && tb.FontWeight.ToOpenTypeWeight() >= 700;
            if (ratio < (large ? 3.0 : 4.5) - 0.005) yield return (tb, $"{what} = {ratio:0.00}:1");
        }
    }

    private static bool Faded(DependencyObject d)
    {
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x))
            if (x is UIElement { Opacity: < 1 }) return true;
        return false;
    }

    /// <summary>The colour behind an element: the first opaque background up the visual tree, with the translucent ones above it laid over it.</summary>
    private static Color? Behind(DependencyObject d)
    {
        var layers = new List<Color>();
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x))
        {
            Brush? b = x switch
            {
                TextBlock t => t.Background,
                Border br => br.Background,
                Panel p => p.Background,
                Window w => w.Background,
                _ => null,
            };
            Color? c = b switch
            {
                SolidColorBrush s => s.Color,
                GradientBrush g when g.GradientStops.Count > 0 => Average(g.GradientStops.Select(s => s.Color)),
                _ => null,
            };
            if (c is not { } col || col.A == 0) continue;
            if (col.A == 255)
            {
                var result = col;
                for (int i = layers.Count - 1; i >= 0; i--) result = Over(layers[i], result);
                return result;
            }
            layers.Add(col);
        }
        return null;   // a popup or a detached tree: nothing known behind it
    }

    private static Color Average(IEnumerable<Color> colors)
    {
        var l = colors.ToList();
        return Color.FromRgb((byte)l.Average(c => c.R), (byte)l.Average(c => c.G), (byte)l.Average(c => c.B));
    }

    private static Color Over(Color top, Color under)
    {
        double a = top.A / 255.0;
        return Color.FromRgb((byte)Math.Round(top.R * a + under.R * (1 - a)), (byte)Math.Round(top.G * a + under.G * (1 - a)), (byte)Math.Round(top.B * a + under.B * (1 - a)));
    }

    public static double Contrast(Color a, Color b) =>
        Wpf.Controls.TreemapView.Contrast(System.Drawing.Color.FromArgb(a.R, a.G, a.B), System.Drawing.Color.FromArgb(b.R, b.G, b.B));

    /// <summary>Elements that show a hand cursor (so they react to clicks) but take no keyboard focus and are no button or link.</summary>
    public static IEnumerable<string> MouseOnly(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is FrameworkElement fe && fe.ReadLocalValue(FrameworkElement.CursorProperty) is Cursor c && c == Cursors.Hand
                && fe.IsVisible && !fe.Focusable && !InsideOperable(fe))
                yield return $"{fe.GetType().Name} in {Path(fe)} reacts to the mouse only (hand cursor, no keyboard focus)";
            int n = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(d) : 0;
            for (int i = 0; i < n; i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
    }

    private static bool InsideOperable(DependencyObject d)
    {
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x))
            if (x is ButtonBase or ListBoxItem or MenuItem or TextBoxBase or ComboBox) return true;
        return false;
    }
}
