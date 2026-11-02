using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ByteScape.Core.Model;
using ByteScape.Treemap;
using ByteScape.Treemap.Layout;

namespace ByteScape.Wpf.Windows;

/// <summary>Layout engine debug view: slice-and-dice vs squarified on the same data, plus a table of every rectangle.</summary>
public sealed class LayoutDebugWindow : Window
{
    private readonly LayoutPanel _raw = new() { Title = "Slice and dice (raw)" };
    private readonly LayoutPanel _sq = new() { Title = "Squarified" };
    private readonly ListView _grid = new();
    private readonly TextBlock _stats = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly Controls.NumberBox _depth = new() { Value = 3, Minimum = 1, Maximum = 8, Width = 60 };
    private readonly Controls.NumberBox _seed = new() { Value = 1, Minimum = 0, Maximum = 9999, Width = 60 };
    private readonly ComboBox _ratio = new() { Width = 130 };
    private readonly ComboBox _source = new() { Width = 200 };
    private readonly ViewModel? _view;
    private readonly TreemapLayoutEngine _engine = new();

    private sealed record Row(string Name, string Value, string X, string Y, string W, string H, string Area, string Aspect, string Parent, int Depth, TreemapNode Node);

    public LayoutDebugWindow(ViewModel? view)
    {
        _view = view;
        Title = "Treemap layout debug";
        Width = 1200; Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _source.Items.Add("Synthetic disk tree");
        if (view != null) _source.Items.Add("Current view (zoomed folder)");
        _source.SelectedIndex = 0;
        _ratio.ItemsSource = new[] { "1 (squares)", "1.618 (golden)", "2", "3" };
        _ratio.SelectedIndex = 0;
        var run = new Button { Content = "Layout", Margin = new Thickness(10, 0, 0, 0) };
        run.Click += (_, _) => Run();
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 10, 12, 6) };
        void L(string t) => tools.Children.Add(new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) });
        L("Source"); tools.Children.Add(_source); L("Depth"); tools.Children.Add(_depth); L("Seed"); tools.Children.Add(_seed); L("Ratio"); tools.Children.Add(_ratio);
        tools.Children.Add(run); tools.Children.Add(_stats);

        var panels = new Grid { Margin = new Thickness(12, 0, 12, 0) };
        panels.ColumnDefinitions.Add(new ColumnDefinition()); panels.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_sq, 1);
        _raw.Margin = new Thickness(0, 0, 4, 0); _sq.Margin = new Thickness(4, 0, 0, 0);
        panels.Children.Add(_raw); panels.Children.Add(_sq);

        var gv = new GridView();
        foreach (var (h, w, b) in new[] { ("Name", 220, "Name"), ("Value", 100, "Value"), ("X", 70, "X"), ("Y", 70, "Y"), ("Width", 70, "W"), ("Height", 70, "H"), ("Area", 90, "Area"), ("Aspect", 60, "Aspect"), ("Parent", 160, "Parent"), ("Depth", 50, "Depth") })
            gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new System.Windows.Data.Binding(b) });
        _grid.View = gv;
        _grid.Margin = new Thickness(12, 6, 12, 12);
        _grid.SelectionChanged += (_, _) => { var n = (_grid.SelectedItem as Row)?.Node; _raw.Highlight = n; _sq.Highlight = n; };
        _raw.Clicked += n => SelectRow(n); _sq.Clicked += n => SelectRow(n);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.2, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(panels, 1); Grid.SetRow(_grid, 2);
        root.Children.Add(tools); root.Children.Add(panels); root.Children.Add(_grid);
        Content = root;
        Loaded += (_, _) => Run();
    }

    private void SelectRow(TreemapNode? n)
    {
        foreach (var item in _grid.Items) if (item is Row r && ReferenceEquals(r.Node, n)) { _grid.SelectedItem = r; _grid.ScrollIntoView(r); return; }
    }

    private void Run()
    {
        var root = _source.SelectedIndex == 1 && _view != null ? FromView(_view, _depth.Value) : Synthetic(_seed.Value, _depth.Value);
        root.SumValues();
        var o = new LayoutOptions
        {
            Ratio = _ratio.SelectedIndex switch { 1 => Squarify.GoldenRatio, 2 => 2, 3 => 3, _ => 1 },
            PaddingTop = 14, PaddingOuter = 2, MaxDepth = _depth.Value, Stable = false,
        };
        var bounds = new RectangleD(0, 0, Math.Max(10, _sq.ActualWidth - 2), Math.Max(10, _sq.ActualHeight - 24));
        var rawRoot = Clone(root); rawRoot.SumValues();
        TreemapLayoutEngine.LayoutSliceDice(rawRoot, bounds, o);
        _engine.Layout(root, bounds, o);
        _raw.Root = rawRoot; _sq.Root = root;
        _raw.InvalidateVisual(); _sq.InvalidateVisual();
        var ci = CultureInfo.InvariantCulture;
        _grid.ItemsSource = new[] { root }.Concat(root.Descendants()).Take(5000).Select(n => new Row(new string('·', n.Depth) + n.Name, n.Value.ToString("0", ci),
            n.Bounds.X.ToString("0.00", ci), n.Bounds.Y.ToString("0.00", ci), n.Bounds.Width.ToString("0.00", ci), n.Bounds.Height.ToString("0.00", ci),
            n.Bounds.Area.ToString("0.0", ci), n.Bounds.IsEmpty ? "-" : n.Bounds.AspectRatio.ToString("0.00", ci), n.Parent?.Name ?? "", n.Depth, n)).ToList();
        _stats.Text = $"raw: {Stats(rawRoot)}    squarified: {Stats(root)}";
    }

    private static string Stats(TreemapNode root)
    {
        double worst = 1, sum = 0; int n = 0;
        foreach (var d in root.Descendants()) { if (d.Bounds.IsEmpty) continue; double a = d.Bounds.AspectRatio; worst = Math.Max(worst, a); sum += a; n++; }
        return n == 0 ? "-" : $"{n} rects, worst aspect {worst:0.00}, mean {sum / n:0.00}";
    }

    private static TreemapNode Synthetic(int seed, int depth)
    {
        var rnd = new Random(seed); int id = 0;
        var root = new TreemapNode("root", "Disk");
        void Fill(TreemapNode parent, int level)
        {
            int files = 3 + rnd.Next(8);
            for (int i = 0; i < files; i++) parent.Add("f" + id++, "file" + i + ".bin", Math.Pow(10, 3 + rnd.NextDouble() * 6));
            if (level >= depth) return;
            int folders = 1 + rnd.Next(4);
            for (int i = 0; i < folders; i++) Fill(parent.Add("d" + id++, "folder" + i), level + 1);
        }
        Fill(root, 1);
        return root;
    }

    private static TreemapNode FromView(ViewModel view, int depth)
    {
        lock (view.Tree.Sync)
        {
            var root = new TreemapNode("root", view.ZoomRoot.Name);
            void Fill(TreemapNode t, ContainerNode c, int level)
            {
                int count = 0;
                foreach (var k in c.Children)
                {
                    if (k.IsSynthetic) continue;
                    long s = view.SizeOf(k);
                    if (s <= 0) continue;
                    var tn = t.Add(k.FullPath, k.Name, s);
                    if (k is ContainerNode kc && level < depth) Fill(tn, kc, level + 1);
                    if (++count > 2000) break;
                }
            }
            Fill(root, view.ZoomRoot, 1);
            return root;
        }
    }

    private static TreemapNode Clone(TreemapNode n)
    {
        var c = new TreemapNode(n.Id, n.Name, n.Value);
        foreach (var ch in n.Children) c.Add(Clone(ch));
        return c;
    }

    private sealed class LayoutPanel : FrameworkElement
    {
        public TreemapNode? Root;
        public string Title = "";
        private TreemapNode? _hl;
        public TreemapNode? Highlight { get => _hl; set { _hl = value; InvalidateVisual(); } }
        public event Action<TreemapNode?>? Clicked;
        private readonly Typeface _tf = new("Segoe UI");

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
            dc.DrawText(new FormattedText(Title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _tf, 12, Brushes.Black, 1), new Point(4, 3));
            if (Root == null) return;
            dc.PushTransform(new TranslateTransform(1, 22));
            Paint(dc, Root);
            dc.Pop();
        }

        private void Paint(DrawingContext dc, TreemapNode n)
        {
            var b = n.Bounds;
            if (b.IsEmpty) return;
            var rect = new Rect(b.X, b.Y, b.Width, b.Height);
            bool folder = n.Children.Count > 0;
            Color color = ReferenceEquals(n, _hl) ? Colors.Yellow : folder ? Color.FromRgb(255, 220, 170) : AspectColor(b.AspectRatio);
            dc.DrawRectangle(new SolidColorBrush(color), new Pen(Brushes.DimGray, 1), rect);
            if (rect.Width > 30 && rect.Height > 12)
            {
                string t = folder ? n.Name : n.Name + " " + b.AspectRatio.ToString("0.0", CultureInfo.InvariantCulture);
                var ft = new FormattedText(t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _tf, 11, Brushes.Black, 1) { MaxTextWidth = rect.Width - 4, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(ft, folder ? new Point(rect.X + 3, rect.Y + 1) : new Point(rect.X + (rect.Width - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
            }
            foreach (var c in n.Children) Paint(dc, c);
        }

        private static Color AspectColor(double aspect)
        {
            double t = Math.Clamp((aspect - 1) / 3.0, 0, 1);
            return Color.FromRgb((byte)(150 + 105 * t), (byte)(230 - 120 * t), (byte)(150 - 100 * t));
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            if (Root == null) return;
            var p = e.GetPosition(this);
            double x = p.X - 1, y = p.Y - 22;
            TreemapNode? hit = null;
            void Find(TreemapNode n) { if (n.Bounds.IsEmpty || !n.Bounds.Contains(x, y)) return; hit = n; foreach (var c in n.Children) Find(c); }
            Find(Root);
            Clicked?.Invoke(hit);
        }
    }
}
