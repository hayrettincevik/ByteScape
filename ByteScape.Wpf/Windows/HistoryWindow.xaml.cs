using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ByteScape.Core.Forecast;
using ByteScape.Core.Util;
using ByteScape.Wpf.Views;
using Microsoft.Win32;

namespace ByteScape.Wpf.Windows;

/// <summary>
/// The drive over time: its used space (free space measurements), every remembered folder with its trend, the chosen
/// folders as lines over the complete scans, and the biggest changes as bars.
/// </summary>
public partial class HistoryWindow : Window
{
    private readonly ScanView _view;
    private FolderHistoryResult? _result;

    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x1F, 0x6F, 0xDB), Color.FromRgb(0xE0, 0x9A, 0x1A), Color.FromRgb(0x2E, 0xA0, 0x4F),
        Color.FromRgb(0x9B, 0x59, 0xB6), Color.FromRgb(0xC8, 0x3A, 0x3A), Color.FromRgb(0x17, 0xA2, 0xB8),
    };

    internal sealed class Row
    {
        public required FolderSeries Series { get; init; }
        public string Path => Series.Path;
        public string FirstText => Series.Bytes[0] is { } b ? Format.Size(b) : "–";
        public string LastText => Series.Bytes[^1] is { } b ? Format.Size(b) : "–";
        public string ChangeText => (Series.Change >= 0 ? "+" : "−") + Format.Size(Math.Abs(Series.Change));
        public PointCollection Spark { get; init; } = new();
        public Brush SparkBrush => new SolidColorBrush(Series.Change >= 0 ? Color.FromRgb(0xC8, 0x3A, 0x3A) : Color.FromRgb(0x2E, 0xA0, 0x4F));
    }

    public HistoryWindow(ScanView view)
    {
        InitializeComponent();
        Localizer.Apply(this);
        _view = view;
    }

    public void SetResult(FolderHistoryResult r)
    {
        _result = r;
        HeadLine.Text = r.Scans.Count == 0 ? F($"No complete scan of {r.Drive} remembered yet")
            : r.Drive + ": " + P(r.Scans.Count, "{0} complete scan from {1} to {2}", "{0} complete scans from {1} to {2}", Format.Date(r.Scans[0]), Format.Date(r.Scans[^1]));
        SubLine.Text = r.Scans.Count < 2
            ? F($"The history grows with every complete scan of the whole drive (and with the free space measured when {Branding.Name} starts).") + " " + P(r.Samples.Count, "{0} measurement so far.", "{0} measurements so far.")
            : P(r.Folders.Count, "{0} folder remembered", "{0} folders remembered") + ", " + P(r.Samples.Count, "{0} free space measurement.", "{0} free space measurements.") + " " + T("Grew most:") + " "
              + string.Join(", ", r.Folders.Where(f => f.Change > 0).Take(3).Select(f => f.Path + " +" + Format.Size(f.Change))) + ".";
        Fill();
        Draw();
    }

    private void Fill()
    {
        if (_result == null) return;
        var q = Search.Text.Trim();
        int depth = int.Parse((string)((ComboBoxItem)Depth.SelectedItem).Tag);
        List.ItemsSource = _result.Folders
            .Where(f => f.Depth <= depth && (q.Length == 0 || f.Path.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
            .Select(f => new Row { Series = f, Spark = Spark(f) }).ToList();
        if (List.Items.Count > 0 && List.SelectedItems.Count == 0) List.SelectedIndex = 0;
    }

    private static PointCollection Spark(FolderSeries f)
    {
        var pts = new PointCollection();
        long max = Math.Max(1, f.Bytes.Max(b => b ?? 0));
        for (int i = 0; i < f.Bytes.Length; i++)
        {
            double x = f.Bytes.Length == 1 ? 40 : 80.0 * i / (f.Bytes.Length - 1);
            pts.Add(new Point(x, 17 - 16.0 * (f.Bytes[i] ?? 0) / max));
        }
        return pts;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) Fill(); }
    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => DrawFolders();
    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) => Draw();

    private void Draw() { DrawDrive(); DrawFolders(); DrawChanges(); }

    private Brush Res(string key) => (Brush)FindResource(key);

    private TextBlock Label(string text, double size = 10) => new() { Text = text, FontSize = size, Foreground = Res("TextSecondary") };

    /// <summary>Axes, a few size labels and the first / last date: the frame every line chart shares.</summary>
    private (Func<DateTime, double> X, Func<double, double> Y) Frame(Canvas c, DateTime from, DateTime to, double max)
    {
        c.Children.Clear();
        double w = c.ActualWidth, h = c.ActualHeight, left = 58, bottom = 16;
        if (to <= from) to = from.AddDays(1);
        max = Math.Max(1, max * 1.08);
        double X(DateTime t) => left + (w - left - 8) * (t - from).TotalSeconds / (to - from).TotalSeconds;
        double Y(double v) => 4 + (h - bottom - 4) * (1 - v / max);
        for (int i = 0; i <= 3; i++)
        {
            double v = max * i / 3, y = Y(v);
            c.Children.Add(new Line { X1 = left, X2 = w - 4, Y1 = y, Y2 = y, Stroke = Res("Border"), StrokeThickness = 1 });
            var t = Label(Format.Size((long)v));
            Canvas.SetLeft(t, 2); Canvas.SetTop(t, y - 7);
            c.Children.Add(t);
        }
        var d1 = Label(Format.Date(from)); Canvas.SetLeft(d1, left); Canvas.SetTop(d1, h - bottom + 1); c.Children.Add(d1);
        var d2 = Label(Format.Date(to)); d2.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(d2, Math.Max(left, w - 8 - d2.DesiredSize.Width)); Canvas.SetTop(d2, h - bottom + 1); c.Children.Add(d2);
        return (X, Y);
    }

    private void DrawDrive()
    {
        var c = DriveChart;
        c.Children.Clear();
        if (_result == null || c.ActualWidth < 80 || c.ActualHeight < 40) return;
        var s = _result.Samples;
        if (s.Count == 0) { c.Children.Add(Label(T("No free space measurements yet."), 12)); return; }
        var (X, Y) = Frame(c, s[0].Utc, s[^1].Utc, s.Max(x => (double)x.Total));
        var total = new Polyline { Stroke = Res("BorderStrong"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 } };
        var used = new Polyline { Stroke = Res("Accent"), StrokeThickness = 2 };
        foreach (var x in s) { total.Points.Add(new Point(X(x.Utc), Y(x.Total))); used.Points.Add(new Point(X(x.Utc), Y(x.Used))); }
        c.Children.Add(total);
        c.Children.Add(used);
        foreach (var t in _result.Scans)
        {
            // the complete scans the folders come from
            double x = X(t);
            c.Children.Add(new Line { X1 = x, X2 = x, Y1 = c.ActualHeight - 20, Y2 = c.ActualHeight - 16, Stroke = Res("TextSecondary"), StrokeThickness = 1 });
        }
    }

    private void DrawFolders()
    {
        var c = FolderChart;
        c.Children.Clear();
        Legend.Children.Clear();
        if (_result == null || c.ActualWidth < 80 || c.ActualHeight < 40) return;
        var chosen = List.SelectedItems.OfType<Row>().Take(Palette.Length).Select(r => r.Series).ToList();
        if (_result.Scans.Count == 0 || chosen.Count == 0) { c.Children.Add(Label(T(_result.Scans.Count == 0 ? "No complete scan remembered yet." : "Select folders in the list."), 12)); return; }
        var (X, Y) = Frame(c, _result.Scans[0], _result.Scans[^1], chosen.Max(f => (double)f.Bytes.Max(b => b ?? 0)));
        for (int k = 0; k < chosen.Count; k++)
        {
            var brush = new SolidColorBrush(Palette[k]);
            var line = new Polyline { Stroke = brush, StrokeThickness = 2 };
            for (int i = 0; i < _result.Scans.Count; i++)
            {
                var p = new Point(X(_result.Scans[i]), Y(chosen[k].Bytes[i] ?? 0));
                line.Points.Add(p);
                var dot = new Ellipse { Width = 5, Height = 5, Fill = brush, ToolTip = chosen[k].Path + "\n" + Format.Date(_result.Scans[i]) + ": " + (chosen[k].Bytes[i] is { } b ? Format.Size(b) : T("under 50 MB")) };
                Canvas.SetLeft(dot, p.X - 2.5); Canvas.SetTop(dot, p.Y - 2.5);
                c.Children.Add(dot);
            }
            c.Children.Add(line);
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
            item.Children.Add(new Border { Width = 10, Height = 10, Background = brush, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock { Text = chosen[k].Path, FontSize = 11 });
            Legend.Children.Add(item);
        }
    }

    private void DrawChanges()
    {
        var c = ChangeChart;
        c.Children.Clear();
        if (_result == null || c.ActualWidth < 80 || c.ActualHeight < 40) return;
        var top = _result.Folders.Where(f => f.Change != 0).OrderByDescending(f => Math.Abs(f.Change)).Take(12).ToList();
        ChangeTitle.Text = _result.Scans.Count < 2 ? T("Biggest changes (needs two complete scans)") : F($"Biggest changes from {Format.Date(_result.Scans[0])} to {Format.Date(_result.Scans[^1])}");
        if (top.Count == 0) { c.Children.Add(Label(T(_result.Scans.Count < 2 ? "Scan the whole drive again later to see what changed." : "Nothing changed."), 12)); return; }
        double w = c.ActualWidth, rowH = Math.Min(22, (c.ActualHeight - 4) / top.Count), labelW = Math.Min(260, w * 0.45), mid = labelW + (w - labelW - 70) / 2;
        long max = Math.Max(1, top.Max(f => Math.Abs(f.Change)));
        for (int i = 0; i < top.Count; i++)
        {
            var f = top[i];
            double y = 2 + i * rowH, len = (w - labelW - 70) / 2 * Math.Abs(f.Change) / max;
            var name = new TextBlock { Text = f.Path, FontSize = 11, Width = labelW - 6, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = f.Path };
            Canvas.SetLeft(name, 0); Canvas.SetTop(name, y + 2);
            c.Children.Add(name);
            var bar = new Rectangle { Width = Math.Max(1, len), Height = rowH - 6, Fill = new SolidColorBrush(f.Change > 0 ? Color.FromRgb(0xC8, 0x3A, 0x3A) : Color.FromRgb(0x2E, 0xA0, 0x4F)), RadiusX = 2, RadiusY = 2 };
            Canvas.SetLeft(bar, f.Change > 0 ? mid : mid - len); Canvas.SetTop(bar, y + 3);
            c.Children.Add(bar);
            var t = Label((f.Change > 0 ? "+" : "−") + Format.Size(Math.Abs(f.Change)), 11);
            // growth: after its bar; shrinking: right of the axis, where no bar is
            Canvas.SetLeft(t, f.Change > 0 ? mid + len + 4 : mid + 4); Canvas.SetTop(t, y + 2);
            c.Children.Add(t);
        }
        c.Children.Add(new Line { X1 = mid, X2 = mid, Y1 = 0, Y2 = top.Count * rowH + 4, Stroke = Res("BorderStrong"), StrokeThickness = 1 });
    }

    private void ShowOnMap_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null || List.SelectedItem is not Row row) return;
        if (_view.Tree.Find(System.IO.Path.Combine(_result.Drive + "\\", row.Path)) is { } node) { _view.ShowNode(node); Owner?.Activate(); }
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ShowOnMap_Click(sender, e);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        var dlg = new SaveFileDialog { Filter = T("CSV file (*.csv)|*.csv"), FileName = F($"{Branding.Name} history {_result.Drive.TrimEnd(':')}.csv") };
        if (Dialogs.Pick(dlg, this) != true) return;
        char sep = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ListSeparator is { Length: 1 } s ? s[0] : ',';
        try { File.WriteAllText(dlg.FileName, FolderHistory.ToCsv(_result, sep), new UTF8Encoding(true)); }
        catch (Exception ex) { Dialogs.Show(this, ex.Message, T("Cannot write file"), MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
