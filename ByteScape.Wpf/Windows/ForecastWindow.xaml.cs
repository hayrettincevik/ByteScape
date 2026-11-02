using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ByteScape.Core.Config;
using ByteScape.Core.Forecast;
using ByteScape.Core.Model;
using ByteScape.Core.Util;
using ByteScape.Wpf.Views;

namespace ByteScape.Wpf.Windows;

/// <summary>"When will my disk be full?": the measured used space, its trend, the day it meets the drive's size, and where the new data goes.</summary>
public partial class ForecastWindow : Window
{
    private readonly ScanView _view;
    private ForecastResult? _result;
    private (long Bytes, int Files, List<(FsNode Folder, string Path, long Bytes)> Where)? _added;

    internal sealed record AddedRow(FsNode Node, string Folder, long Bytes) { public string SizeText => Format.Size(Bytes); }
    internal sealed record GrewRow(string Folder, long Then, long Now)
    {
        public string ThenText => Format.Size(Then);
        public string NowText => Format.Size(Now);
        public string GrewText => "+" + Format.Size(Now - Then);
    }

    public ForecastWindow(ScanView view)
    {
        InitializeComponent();
        Localizer.Apply(this);
        _view = view;
    }

    public void SetResult(ForecastResult r, (long Bytes, int Files, List<(FsNode Folder, string Path, long Bytes)> Where)? added,
                          List<(string Path, long Then, long Now)>? grew, DateTime? grewSince, string historyNote)
    {
        _result = r;
        _added = added;
        var now = DateTime.Now;
        FreeText.Text = F($"{Format.Size(r.Free)} of {Format.Size(r.Total)}");
        var notes = new List<string>();
        if (r.HasTrend)
        {
            RateText.Text = DiskForecast.Rate(r.BytesPerDay);
            HeadLine.Text = r.FullDate is { } full
                ? F($"{r.Drive} will be full {DiskForecast.When(full, now)} (around {Format.Date(full)})")
                : F($"{r.Drive} is not filling up");
            SubLine.Text = F($"{r.Samples} measurements over {r.SpanDays:0} days · confidence: {T(r.Confidence)} · the pace data is added at; one-off cleanups are left out (they start a new level, not a pace).");
            if (r.Confidence == "low") notes.Add(F($"Less than two weeks of measurements: the date will settle as {Branding.Name} measures more days."));
        }
        else
        {
            RateText.Text = "—";
            HeadLine.Text = F($"{r.Drive}: not enough measurements yet");
            SubLine.Text = F($"{Branding.Name} measures the drive each time it starts and after every complete scan of the drive; a forecast needs at least two different days.");
            if (added is { Bytes: > 0 } a && r.Free > 0)
            {
                double perDay = a.Bytes / 30.0;
                var at = now.Date.AddDays(r.Free / Math.Max(1, perDay));
                notes.Add(F($"Meanwhile, by file dates: {Format.Size(a.Bytes)} was added in the last 30 days. If nothing were deleted, {r.Drive} would be full {DiskForecast.When(at, now)}."));
            }
        }
        NoteLine.Text = string.Join("  ", notes);
        NoteLine.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AddedText.Text = added is { } ad ? Format.Size(ad.Bytes) + " · " + P(ad.Files, "{0:N0} file", "{0:N0} files") : "—";
        AddedList.ItemsSource = added?.Where.Select(w => new AddedRow(w.Folder, w.Path, w.Bytes)).ToList();

        if (grew != null && grewSince != null)
        {
            GrewHead.Text = F($"Grew since the scan of {Format.Date(grewSince.Value)} ({Format.Age(grewSince.Value)} ago)");
            GrewList.ItemsSource = grew.Select(g => new GrewRow(g.Path, g.Then, g.Now)).ToList();
            GrewEmpty.Visibility = grew.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            GrewEmpty.Text = T("Nothing grew.");
        }
        else
        {
            GrewList.ItemsSource = null;
            GrewEmpty.Visibility = Visibility.Visible;
            GrewEmpty.Text = T("No earlier complete scan of this drive to compare with yet. Each complete scan of the whole drive is remembered (folder sizes only), and the next one is compared with it.");
        }
        HistoryHint.Text = historyNote;
        DrawChart();
    }

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    private void DrawChart()
    {
        Chart.Children.Clear();
        var r = _result;
        double w = Chart.ActualWidth, h = Chart.ActualHeight;
        if (r == null || w < 50 || h < 50) return;
        var text = (Brush)FindResource("TextSecondary");
        var accent = (Brush)FindResource("Accent");
        var danger = (Brush)FindResource("Error");
        var trendBrush = (Brush)FindResource("Warning");
        const double right = 20, top = 22, bottom = 26;
        var today = DateTime.Now.Date;
        var first = r.Points.Count > 0 ? r.Points[0].Day : today;
        var last = today.AddDays(14);
        if (r.FullDate is { } fd) last = fd > last ? (fd < today.AddDays(730) ? fd.AddDays(7) : today.AddDays(730)) : last;
        if (first >= last) first = last.AddDays(-1);
        long minUsed = r.Points.Count > 0 ? r.Points.Min(p => p.Used) : r.Used;
        double yMin = Math.Max(0, minUsed - Math.Max(1, r.Total - minUsed) * 0.25), yMax = r.Total;
        string fullLabel = F($"Full: {Format.Size(r.Total)}");
        double left = Math.Max(70, Math.Max(LabelWidth(fullLabel), LabelWidth(Format.Size((long)yMin))) + 12);   // the scale labels sit left of the chart
        double X(DateTime d) => left + (d - first).TotalDays / Math.Max(1e-9, (last - first).TotalDays) * (w - left - right);
        double Y(double used) => top + (1 - (used - yMin) / Math.Max(1, yMax - yMin)) * (h - top - bottom);

        void Line(double x1, double y1, double x2, double y2, Brush b, double thick, bool dashed = false)
        {
            var l = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = b, StrokeThickness = thick };
            if (dashed) l.StrokeDashArray = new DoubleCollection { 4, 3 };
            Chart.Children.Add(l);
        }
        static double LabelWidth(string s)
        {
            var t = new TextBlock { Text = s, FontSize = 11 };
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return t.DesiredSize.Width;
        }
        void Label(string s, double x, double y, Brush b, bool right2left = false)
        {
            var t = new TextBlock { Text = s, FontSize = 11, Foreground = b };
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(t, right2left ? x - t.DesiredSize.Width : x);
            Canvas.SetTop(t, y);
            Chart.Children.Add(t);
        }

        // capacity and the bottom of the scale
        Line(left, Y(r.Total), w - right, Y(r.Total), danger, 1.5, dashed: true);
        Label(fullLabel, left - 6, Y(r.Total) - 8, danger, right2left: true);
        Line(left, Y(yMin), w - right, Y(yMin), text, 0.5);
        Label(Format.Size((long)yMin), left - 6, Y(yMin) - 8, text, right2left: true);
        Label(Format.Date(first), left, h - bottom + 6, text);
        Label(T("today"), X(today) - 14, h - bottom + 6, text);
        Line(X(today), top, X(today), h - bottom, text, 0.5, dashed: true);

        // the pace, from today to the day the free space runs out (or the right edge)
        if (r.HasTrend)
        {
            var endDay = r.FullDate ?? last;
            if (endDay > last) endDay = last;
            double endUsed = r.TrendToday + r.BytesPerDay * (endDay - today).TotalDays;
            Line(X(today), Y(r.TrendToday), X(endDay), Y(Math.Clamp(endUsed, yMin, r.Total)), trendBrush, 1.5, dashed: true);
            if (r.FullDate is { } full && full <= last)
            {
                var dot = new Ellipse { Width = 8, Height = 8, Fill = danger };
                Canvas.SetLeft(dot, X(full) - 4); Canvas.SetTop(dot, Y(r.Total) - 4);
                Chart.Children.Add(dot);
                Label(Format.Date(full), X(full), Y(r.Total) + 6, danger, right2left: X(full) > w - 120);
            }
        }

        // the measurements
        if (r.Points.Count > 0)
        {
            var poly = new Polyline { Stroke = accent, StrokeThickness = 2 };
            foreach (var (day, used) in r.Points) poly.Points.Add(new Point(X(day), Y(used)));
            Chart.Children.Add(poly);
            foreach (var (day, used) in r.Points)
            {
                var dot = new Ellipse { Width = 5, Height = 5, Fill = accent };
                Canvas.SetLeft(dot, X(day) - 2.5); Canvas.SetTop(dot, Y(used) - 2.5);
                Chart.Children.Add(dot);
            }
            Label(F($"Used: {Format.Size(r.Used)}"), X(r.Points[^1].Day) + 6, Y(r.Points[^1].Used) + 4, accent, right2left: X(r.Points[^1].Day) > w - 140);
        }
    }

    private void AddedList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AddedList.SelectedItem is AddedRow row) { _view.ShowNode(row.Node); Owner?.Activate(); }
    }

    private void History_Click(object sender, RoutedEventArgs e) => _view.ShowHistory();

    private void GrewList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GrewList.SelectedItem is GrewRow row && _result != null && _view.Tree.Find(System.IO.Path.Combine(_result.Drive + "\\", row.Folder)) is { } node) { _view.ShowNode(node); Owner?.Activate(); }
    }

    /// <summary>What Task Scheduler should run to measure the drives daily.</summary>
    internal static string MeasureCommand() =>
        AppConfig.IsPackaged() ? "bytescape.exe measure" : "\"" + (Environment.ProcessPath ?? "ByteScape.exe") + "\" measure";

    private void CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        try { Dialogs.Copy(MeasureCommand()); } catch { }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        try { Dialogs.Copy(DiskForecast.ToText(_result, _added, DateTime.Now)); } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
