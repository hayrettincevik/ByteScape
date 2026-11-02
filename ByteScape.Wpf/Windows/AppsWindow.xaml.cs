using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ByteScape.Core.Apps;
using ByteScape.Core.Model;
using ByteScape.Core.Util;
using ByteScape.Wpf.Views;
using Microsoft.Win32;

namespace ByteScape.Wpf.Windows;

/// <summary>
/// "Which program uses my disk?": installed programs with everything they occupy (install folder, app data,
/// caches, Store package data), the folders no program claims, and a map highlight of the selected program.
/// </summary>
public partial class AppsWindow : Window
{
    private readonly ScanView _view;
    private FootprintResult? _result;
    private string _sortKey = "Total";
    private readonly string[] _columnKeys;
    private bool _sortDescending = true;

    internal sealed class AppRow
    {
        public required AppUsage? Usage { get; init; }
        public required IReadOnlyList<AppPiece> Pieces { get; init; }
        public required string Name { get; init; }
        public string Publisher { get; init; } = "";
        public string TypeText { get; init; } = "";
        public long Total { get; init; }
        public long Program { get; init; }
        public long Data { get; init; }
        public long Reclaim { get; init; }
        public DateTime Last { get; init; }
        public bool IsUnlinked => Usage == null;
        public string TotalText => Format.Size(Total);
        public string ProgramText => IsUnlinked ? "" : Format.Size(Program);
        public string DataText => IsUnlinked ? "" : Format.Size(Data);
        public string ReclaimText => Reclaim > 0 ? Format.Size(Reclaim) : "";
        public string LastText => Ago(Last);
        public string FoldersText => Pieces.Count.ToString("N0");
    }

    internal sealed record PieceRow(AppPiece Piece)
    {
        public string Where => Piece.KindText;
        public string SizeText => Format.Size(Piece.Bytes);
        public string ReclaimText => Piece.ReclaimableBytes > 0 ? Format.Size(Piece.ReclaimableBytes) : "";
        public string LastText => Ago(Piece.LastWrite);
        public string Note => Piece.Note;
        public string Path => Piece.Path;
    }

    public AppsWindow(ScanView view)
    {
        InitializeComponent();
        _columnKeys = ((GridView)AppList.View).Columns.Select(c => c.Header as string ?? "").ToArray(); // English names, before translation
        Localizer.Apply(this);
        Localizer.FitColumn(AppList, 2, "Desktop", "Store", "Component", "Windows");
        _view = view;
        AppList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(Header_Click));
        Closed += (_, _) => { _view.Model.Spotlight = null; _view.RefreshMap(); };
    }

    /// <summary>"3 months ago": coarse age of the newest write.</summary>
    internal static string Ago(DateTime utc)
    {
        if (utc == default) return "—";
        var days = (DateTime.UtcNow - utc).TotalDays;
        if (days < 1) return T("today");
        if (days < 2) return T("yesterday");
        if (days < 31) return P((int)days, "{0} day ago", "{0} days ago");
        if (days < 365) { int m = Math.Max(1, (int)(days / 30.4375)); return P(m, "{0} month ago", "{0} months ago"); }
        double y = days / 365.25;
        return y < 2 ? T("over a year ago") : P((int)y, "{0} year ago", "{0} years ago");
    }

    /// <summary>Shows a (new) result; the selected program stays selected when it is still there.</summary>
    public void SetResult(FootprintResult result, bool scanRunning)
    {
        var selected = (AppList.SelectedItem as AppRow)?.Usage?.App;
        bool selectedUnlinked = AppList.SelectedItem is AppRow { IsUnlinked: true };
        _result = result;
        PartialLine.Visibility = Visibility.Collapsed;
        var notes = new List<string>();
        if (scanRunning) notes.Add(T("The scan is still running: totals grow until it completes (press Refresh then)."));
        if (!result.IsWholeDrive) notes.Add(F($"Only folders inside {AppFootprint.DisplayPath(result.RootPath)} are counted: open the whole drive for complete totals."));
        if (notes.Count > 0) { PartialLine.Text = string.Join("  ", notes); PartialLine.Visibility = Visibility.Visible; }
        Fill();
        if (selected != null) Select(selected);
        else if (selectedUnlinked) AppList.SelectedItem = AppList.Items.OfType<AppRow>().FirstOrDefault(r => r.IsUnlinked);
    }

    public void Select(InstalledApp app)
    {
        var row = AppList.Items.OfType<AppRow>().FirstOrDefault(r => ReferenceEquals(r.Usage?.App, app));
        if (row == null && app.IsComponent && ShowComponents.IsChecked != true)
        {
            ShowComponents.IsChecked = true;
            Fill();
            row = AppList.Items.OfType<AppRow>().FirstOrDefault(r => ReferenceEquals(r.Usage?.App, app));
        }
        if (row == null) return;
        AppList.SelectedItem = row;
        AppList.ScrollIntoView(row);
    }

    private void Fill()
    {
        var r = _result;
        if (r == null) return;
        var q = Search.Text.Trim();
        bool Hit(string s) => q.Length == 0 || s.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        var visible = r.Apps.Where(u => ShowComponents.IsChecked == true || !u.App.IsComponent).ToList();
        var rows = visible
            .Where(u => Hit(u.App.Name) || Hit(u.App.Publisher) || u.Pieces.Any(p => Hit(p.Path)))
            .Select(u => new AppRow
            {
                Usage = u, Pieces = u.Pieces, Name = u.App.Name, Publisher = u.App.Publisher, TypeText = u.App.TypeText,
                Total = u.Bytes, Program = u.ProgramBytes, Data = u.DataBytes, Reclaim = u.ReclaimableBytes, Last = u.LastDataWrite != default ? u.LastDataWrite : u.LastWrite,
            });
        rows = _sortKey switch
        {
            "Program" => Order(rows, x => x.Name, ascendingFirst: true),
            "Publisher" => Order(rows, x => x.Publisher, ascendingFirst: true),
            "Type" => Order(rows, x => x.TypeText, ascendingFirst: true),
            "Program files" => Order(rows, x => x.Program),
            "App data" => Order(rows, x => x.Data),
            "Reclaimable" => Order(rows, x => x.Reclaim),
            "Last activity" => Order(rows, x => x.Last),
            "Folders" => Order(rows, x => x.Pieces.Count),
            _ => Order(rows, x => x.Total),
        };
        var list = rows.ToList();
        var unlinked = r.Unlinked.Where(p => Hit(p.Path) || Hit(p.Note)).ToList();
        if (unlinked.Count > 0 || q.Length == 0)
            list.Add(new AppRow
            {
                Usage = null, Pieces = unlinked, Name = T("Not linked to an installed program"), TypeText = "—",
                Total = unlinked.Sum(p => p.Bytes), Reclaim = unlinked.Sum(p => p.ReclaimableBytes), Last = unlinked.Select(p => p.LastWrite).DefaultIfEmpty().Max(),
            });
        AppList.ItemsSource = list;

        long program = visible.Sum(u => u.ProgramBytes), data = visible.Sum(u => u.DataBytes), total = program + data;
        HeadLine.Text = P(visible.Count, "{1} used by {0:N0} program", "{1} used by {0:N0} programs", Format.Size(total));
        SubLine.Text = (r.ScannedBytes > 0 ? F($"{AppFootprint.Pct(total, r.ScannedBytes)} of the {Format.Size(r.ScannedBytes)} in {AppFootprint.DisplayPath(r.RootPath)}.") + " " : "")
                       + T("Install folders, app data, caches and Store app data are counted for the program that owns them.");
        ProgramText.Text = Format.Size(program);
        DataText.Text = Format.Size(data);
        ReclaimText.Text = Format.Size(visible.Sum(u => u.ReclaimableBytes));
        UnlinkedText.Text = Format.Size(r.UnlinkedBytes);
        if (AppList.SelectedItem == null) ShowPieces(null);
    }

    private IEnumerable<AppRow> Order<T>(IEnumerable<AppRow> rows, Func<AppRow, T> key, bool ascendingFirst = false)
    {
        bool desc = ascendingFirst ? !_sortDescending : _sortDescending;
        var o = desc ? rows.OrderByDescending(key) : rows.OrderBy(key);
        return o.ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column }) return;
        int index = ((GridView)AppList.View).Columns.IndexOf(column);
        if (index < 0 || index >= _columnKeys.Length) return;
        var header = _columnKeys[index];
        if (header == _sortKey) _sortDescending = !_sortDescending;
        else { _sortKey = header; _sortDescending = true; }
        var sel = (AppList.SelectedItem as AppRow)?.Usage?.App;
        Fill();
        if (sel != null) Select(sel);
    }

    private void ShowPieces(AppRow? row)
    {
        if (row == null)
        {
            PieceList.ItemsSource = null;
            PieceHead.Text = T("Select a program to see where its space is.");
            _view.Model.Spotlight = null;
            _view.RefreshMap();
            return;
        }
        PieceList.ItemsSource = row.Pieces.Select(p => new PieceRow(p)).ToList();
        PieceHead.Text = row.IsUnlinked
            ? P(row.Pieces.Count, "{0} folder no installed program claims (portable tools, programs that do not register, leftovers of removed programs, Windows or developer parts). Check before deleting anything.", "{0} folders no installed program claims (portable tools, programs that do not register, leftovers of removed programs, Windows or developer parts). Check before deleting anything.")
            : row.Name + (row.Usage!.App.Version.Length > 0 ? " " + row.Usage.App.Version : "") + " — " + P(row.Pieces.Count, "{1} in {0} folder", "{1} in {0} folders", Format.Size(row.Total))
              + (row.Reclaim > 0 ? ", " + F($"{Format.Size(row.Reclaim)} of it in caches") : "")
              + (row.Usage.App.EstimatedBytes > 0 ? ".  " + F($"The installer reported {Format.Size(row.Usage.App.EstimatedBytes)}.") : ".");
        UpdateSpotlight(row);
    }

    private void UpdateSpotlight(AppRow? row)
    {
        _view.Model.Spotlight = Spot.IsChecked == true && row != null && _result != null && row.Pieces.Count > 0 ? _result.SpotlightFor(row.Pieces) : null;
        _view.RefreshMap();
    }

    private void AppList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowPieces(AppList.SelectedItem as AppRow);

    private void PieceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ExplorerBtn.IsEnabled = PieceList.SelectedItem == null || _view.Tree.IsLive;
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) Fill(); }
    private void Filter_Click(object sender, RoutedEventArgs e) => Fill();
    private void Spot_Click(object sender, RoutedEventArgs e) => UpdateSpotlight(AppList.SelectedItem as AppRow);
    private void Refresh_Click(object sender, RoutedEventArgs e) => _view.ShowAppFootprint((AppList.SelectedItem as AppRow)?.Usage?.App);

    private void PieceList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ShowOnMap_Click(sender, e);
    private void AppList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ShowOnMap_Click(sender, e);

    /// <summary>The selected folder, or the smallest folder holding all folders of the selected program.</summary>
    private void ShowOnMap_Click(object sender, RoutedEventArgs e)
    {
        if (PieceList.SelectedItem is PieceRow pr && (sender != AppList))
        {
            _view.ShowNode(pr.Piece.Node);
        }
        else if (AppList.SelectedItem is AppRow { Pieces.Count: > 0 } row)
        {
            if (row.Pieces.Count == 1) _view.ShowNode(row.Pieces[0].Node);
            else if (CommonAncestor(row.Pieces.Select(p => p.Node)) is { } common) _view.ZoomTo(common);
        }
        else return;
        Owner?.Activate();
    }

    internal static ContainerNode? CommonAncestor(IEnumerable<FsNode> nodes)
    {
        List<ContainerNode>? chain = null;
        foreach (var n in nodes)
        {
            var own = new List<ContainerNode>();
            for (var p = n as ContainerNode ?? n.Parent; p != null; p = p.Parent) own.Insert(0, p);
            if (chain == null) { chain = own; continue; }
            int i = 0;
            while (i < chain.Count && i < own.Count && ReferenceEquals(chain[i], own[i])) i++;
            chain.RemoveRange(i, chain.Count - i);
        }
        return chain is { Count: > 0 } ? chain[^1] : null;
    }

    private void Explorer_Click(object sender, RoutedEventArgs e)
    {
        var node = (PieceList.SelectedItem as PieceRow)?.Piece.Node ?? (AppList.SelectedItem as AppRow)?.Pieces.FirstOrDefault()?.Node;
        if (node != null && _view.Tree.IsLive) _view.OpenInExplorer(node);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => MainWindow.OpenUrl("ms-settings:appsfeatures");

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        try { Dialogs.Copy(AppFootprint.ToText(_result)); } catch { }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        var dlg = new SaveFileDialog { Filter = T("Text file (*.txt)|*.txt|Tab separated (*.tsv)|*.tsv"), FileName = F($"{Branding.Name} programs.txt") };
        if (Dialogs.Pick(dlg, this) != true) return;
        try { File.WriteAllText(dlg.FileName, AppFootprint.ToText(_result), new UTF8Encoding(false)); }
        catch (Exception ex) { Dialogs.Show(this, ex.Message, T("Cannot write file"), MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
