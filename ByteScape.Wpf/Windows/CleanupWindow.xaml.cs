using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ByteScape.Core.Cleanup;
using ByteScape.Core.Model;
using ByteScape.Core.Util;
using ByteScape.Wpf.Views;

namespace ByteScape.Wpf.Windows;

/// <summary>
/// "What can go?": large files, temporary files, old installation files and programs not used for months, each with the
/// "is it safe to delete?" verdict. Safe items start ticked; deleting goes through the delete dialog (Recycle Bin by
/// default, protected items never), programs are uninstalled with their own uninstaller or through Settings.
/// </summary>
public partial class CleanupWindow : Window
{
    private readonly ScanView _view;
    private CleanupResult? _result;
    private readonly Dictionary<CleanupCategory, List<Row>> _rows = new();
    private CancellationTokenSource? _probeCts;

    internal sealed class Row : INotifyPropertyChanged
    {
        public required CleanupItem Item { get; init; }
        public required bool Live { get; init; }
        public string Name => Item.Title;
        public string SizeText => Format.Size(Item.Bytes);
        public string LevelText => Item.Category == CleanupCategory.UnusedPrograms ? T("Uninstall") : DeleteSafety.LevelText(Item.Level);
        public Brush LevelBrush => SafetyUi.LevelBrush(Item.Level);
        public string WhenText => Item.WhenText;
        public string Where => Item.Detail.Length > 0 ? Item.Detail : Item.Path;
        public bool CanDelete => Live && Item.CanDelete;
        public string CheckTip => CanDelete ? T("Include in the delete") : Item.Category == CleanupCategory.UnusedPrograms ? T("Programs are uninstalled, not deleted") : Item.Assessment is { Blocked: true } a ? a.Headline : T("Better done by Windows");
        private bool _checked;
        public bool Checked { get => _checked; set { if (_checked == value) return; _checked = value; Changed(); CheckedChanged?.Invoke(); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        internal Action? CheckedChanged;
        private void Changed([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public CleanupWindow(ScanView view)
    {
        InitializeComponent();
        Localizer.Apply(this);
        _view = view;
        Closed += (_, _) => { _probeCts?.Cancel(); _view.Model.Spotlight = null; _view.RefreshMap(); };
    }

    internal CleanupCategory Current => Enum.Parse<CleanupCategory>((string)((TabItem)Tabs.SelectedItem).Tag);

    public void SetResult(CleanupResult result, bool scanRunning)
    {
        var keepChecked = _rows.Values.SelectMany(r => r).Where(r => r.Checked).Select(r => r.Item.Path + "|" + r.Item.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool first = _result == null;
        _result = result;
        bool live = _view.Tree.IsLive;
        _rows.Clear();
        foreach (var c in Enum.GetValues<CleanupCategory>())
            _rows[c] = result.Of(c).Select(i => new Row { Item = i, Live = live }).ToList();
        foreach (var r in _rows.Values.SelectMany(x => x))
        {
            r.Checked = r.CanDelete && (first ? r.Item.Preselected : keepChecked.Contains(r.Item.Path + "|" + r.Item.Title));
            r.CheckedChanged = UpdateSelection;
        }
        long freeable = Enum.GetValues<CleanupCategory>().Where(c => c != CleanupCategory.UnusedPrograms).Sum(c => result.BytesOf(c));
        HeadLine.Text = F($"{Format.Size(freeable)} in large, temporary and old installation files") + (result.ProgramsIncluded ? ", " + P(result.Of(CleanupCategory.UnusedPrograms).Count, "{0} program not used for months", "{0} programs not used for months") : "");
        SubLine.Text = F($"In {result.RootPath}. Every item says whether it is safe to delete; protected ones cannot be deleted from here, and deleting goes to the Recycle Bin unless you choose otherwise.")
                       + (result.Notes.Count > 0 ? "\n" + string.Join("\n", result.Notes) : "");
        PartialLine.Text = scanRunning ? T("The scan is still running: more may appear (press Refresh when it completes).") : "";
        PartialLine.Visibility = scanRunning ? Visibility.Visible : Visibility.Collapsed;
        foreach (TabItem t in Tabs.Items)
        {
            var c = Enum.Parse<CleanupCategory>((string)t.Tag);
            t.Header = CleanupFinder.Title(c) + "  ·  " + (c == CleanupCategory.UnusedPrograms ? result.Of(c).Count.ToString() : Format.Size(result.BytesOf(c)));
        }
        RecycleBtn.IsEnabled = ForeverBtn.IsEnabled = live;
        Fill();
    }

    private void Fill()
    {
        if (_result == null) return;
        var c = Current;
        List.ItemsSource = _rows.GetValueOrDefault(c);
        bool programs = c == CleanupCategory.UnusedPrograms;
        RecycleBtn.Visibility = ForeverBtn.Visibility = programs ? Visibility.Collapsed : Visibility.Visible;
        if (List.Items.Count > 0) List.SelectedIndex = 0; else ShowDetail(null);
        UpdateSelection();
        UpdateSpotlight();
    }

    private IEnumerable<Row> Checked => _rows.Values.SelectMany(r => r).Where(r => r.Checked && r.CanDelete);

    private void UpdateSelection()
    {
        var picked = Checked.ToList();
        SelectionText.Text = picked.Count == 0 ? T("Tick what should go.") : P(picked.Count, "{0} item ticked in all lists: {1}", "{0} items ticked in all lists: {1}", Format.Size(picked.Sum(r => r.Item.Bytes)));
    }

    private void ShowDetail(Row? row)
    {
        _probeCts?.Cancel();
        DetailPanel.Children.Clear();
        WindowsBtn.Visibility = UsesBtn.Visibility = Visibility.Collapsed;
        if (row == null) { DetailPanel.Children.Add(new TextBlock { Text = T("Select an item to see whether it is safe to delete."), Style = (Style)FindResource("Secondary") }); return; }
        var item = row.Item;
        DetailPanel.Children.Add(new TextBlock { Text = item.Title + "  —  " + Format.Size(item.Bytes), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (item.Path.Length > 0) DetailPanel.Children.Add(new TextBlock { Text = item.Path, Style = (Style)FindResource("Secondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) });
        if (item.Category == CleanupCategory.UnusedPrograms && item.Program is { } u)
        {
            DetailPanel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = u.App.Name + ": " + item.WhenText + ". " + item.Detail + ".\n"
                + F($"Uninstall it with its own uninstaller (it removes the program folder, its registry entries and shortcuts); the data folders it leaves behind can be deleted afterwards from the map. {Branding.Name} never deletes a program's folder by hand.") });
            WindowsBtn.Content = T(u.App.Source == Core.Apps.AppSource.Store || u.App.UninstallCommand.Length == 0 ? "Uninstall in Settings…" : "Uninstall…");
            WindowsBtn.Visibility = Visibility.Visible;
            UsesBtn.Visibility = Visibility.Visible;
            return;
        }
        if (item.Assessment is { } a) DetailPanel.Children.Add(SafetyUi.Verdict(a));
        if (item.Detail.Length > 0 && item.Nodes.Count > 1) DetailPanel.Children.Add(new TextBlock { Text = item.Detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        if (item.WindowsAction.Length > 0) { WindowsBtn.Content = item.WindowsActionText; WindowsBtn.Visibility = Visibility.Visible; }
        if (!row.CanDelete || item.Nodes.Count == 0) return;
        // what stands in the way (rights, open files), checked in the background
        var line = new TextBlock { Text = T("Checking rights…"), Style = (Style)FindResource("Secondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        DetailPanel.Children.Add(line);
        var cts = _probeCts = new CancellationTokenSource();
        var sync = _view.Tree.Sync;
        var nodes = item.Nodes.Take(200).ToList();
        Task.Run(() =>
        {
            var total = new ProbeSummary();
            foreach (var n in nodes)
            {
                var s = AccessProbe.ProbeTree(n, n.RealPath(), sync, 300, cts.Token, includeRoot: item.Assessment is not { ContentOnly: true });
                total.Checked += s.Checked;
                if (!s.Complete) total.Complete = false;
                foreach (var p in s.NeedsAdmin) total.Add(p, AccessState.NeedsAdmin);
                foreach (var p in s.ProtectedByWindows) total.Add(p, AccessState.ProtectedByWindows);
                foreach (var p in s.InUse) total.Add(p, AccessState.InUse);
            }
            return total;
        }, cts.Token).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (cts.IsCancellationRequested || t.Status != TaskStatus.RanToCompletion) return;
            var (text, brush) = SafetyUi.ProbeLine(t.Result, AccessProbe.IsElevated);
            line.Text = text; line.Foreground = brush;
        }), TaskScheduler.Default);
    }

    private void UpdateSpotlight()
    {
        if (Spot.IsChecked != true || _result == null) { _view.Model.Spotlight = null; _view.RefreshMap(); return; }
        var d = new Dictionary<FsNode, bool>(ReferenceEqualityComparer.Instance);
        foreach (var r in _rows.GetValueOrDefault(Current) ?? new List<Row>())
        {
            foreach (var n in r.Item.Nodes) d[n] = true;
            if (r.Item.Program is { } u) foreach (var p in u.Pieces) d[p.Node] = true;
        }
        _view.Model.Spotlight = d.Count > 0 ? d : null;
        _view.RefreshMap();
    }

    private Row? Selected => List.SelectedItem as Row;

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (e.Source == Tabs && IsLoaded) Fill(); }
    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetail(Selected);
    private void Spot_Click(object sender, RoutedEventArgs e) => UpdateSpotlight();
    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ShowOnMap();
    private void ShowOnMap_Click(object sender, RoutedEventArgs e) => ShowOnMap();

    private void ShowOnMap()
    {
        var n = Selected?.Item.Nodes.FirstOrDefault() ?? Selected?.Item.Program?.Pieces.FirstOrDefault()?.Node;
        if (n == null || n.Parent == null) return;
        _view.ShowNode(n);
        Owner?.Activate();
    }

    private void Explorer_Click(object sender, RoutedEventArgs e)
    {
        var n = Selected?.Item.Nodes.FirstOrDefault() ?? Selected?.Item.Program?.Pieces.FirstOrDefault()?.Node;
        if (n != null && _view.Tree.IsLive) _view.OpenInExplorer(n);
    }

    private void WindowsAction_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected?.Item;
        if (item == null) return;
        if (item.Program is { } u) _view.Uninstall(u.App);
        else if (item.WindowsAction.Length > 0) MainWindow.OpenUrl(item.WindowsAction);
    }

    private void Uses_Click(object sender, RoutedEventArgs e) { if (Selected?.Item.Program is { } u) _view.ShowAppFootprint(u.App); }

    private void SelectSafe_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows.GetValueOrDefault(Current) ?? new()) r.Checked = r.CanDelete && r.Item.Level == SafetyLevel.Safe; }
    private void SelectNone_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows.Values.SelectMany(x => x)) r.Checked = false; }

    private void Recycle_Click(object sender, RoutedEventArgs e) => DeleteChecked(permanent: false);
    private void Forever_Click(object sender, RoutedEventArgs e) => DeleteChecked(permanent: true);

    /// <summary>The ticked items go through the delete dialog (verdicts, rights, "I understand"); temporary folders keep what changed in the last day.</summary>
    internal DeleteWindow? DeleteChecked(bool permanent, bool modal = true)
    {
        var nodes = Checked.SelectMany(r => r.Item.Nodes).ToList();
        if (nodes.Count == 0) { SelectionText.Text = T("Tick what should go first."); return null; }
        return _view.DeleteNodes(nodes, permanent, TimeSpan.FromDays(1), modal);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        try { Dialogs.Copy(CleanupFinder.ToText(_result)); } catch { }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _view.ShowCleanup();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
