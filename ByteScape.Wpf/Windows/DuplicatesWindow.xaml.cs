using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ByteScape.Core.Cleanup;
using ByteScape.Core.Duplicates;
using ByteScape.Core.Model;
using ByteScape.Core.Util;
using ByteScape.Wpf.Views;
using Microsoft.Win32;

namespace ByteScape.Wpf.Windows;

/// <summary>
/// "Which files are there twice?": groups of files with the same content, the extra space they take, each copy with its
/// "is it safe to delete?" verdict. Ticking keeps at least one copy per group; deleting goes through the delete dialog.
/// </summary>
public partial class DuplicatesWindow : Window
{
    private readonly ScanView _view;
    private DuplicateResult? _result;
    private readonly List<GroupRow> _groups = new();

    internal sealed class CopyRow : INotifyPropertyChanged
    {
        public required DuplicateCopy Copy { get; init; }
        public required Assessment Assessment { get; init; }
        public required GroupRow Group { get; init; }
        public string Path => Copy.Path;
        public string ModifiedText => Copy.Modified == default ? "" : Format.Date(Copy.Modified) + " " + Copy.Modified.ToLocalTime().ToShortTimeString();
        public string LevelText => DeleteSafety.LevelText(Assessment.Level);
        public Brush LevelBrush => SafetyUi.LevelBrush(Assessment.Level);
        public string Verdict => Assessment.Headline + (Assessment.Why.Length > 0 ? "\n" + Assessment.Why : "");
        public bool CanDelete => !Assessment.Blocked;
        public string Note => Copy.OtherNames.Count > 0 ? "+ " + P(Copy.OtherNames.Count, "{0} hard link: same file, deleting this name alone frees nothing", "{0} hard links: same file, deleting this name alone frees nothing") : "";
        private bool _checked;
        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                // one copy of each group always stays
                if (value && Group.Copies.Count(c => c.Checked) + 1 >= Group.Copies.Count) { Changed(); Group.Owner?.Refuse(Group); return; }
                _checked = value; Changed(); Group.Changed(); Group.Owner?.UpdateSelection();
            }
        }
        internal void Set(bool value) { _checked = value; Changed(); }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p == nameof(Set) ? nameof(Checked) : p));
    }

    internal sealed class GroupRow : INotifyPropertyChanged
    {
        public required DuplicateGroup Group { get; init; }
        public DuplicatesWindow? Owner { get; init; }
        public List<CopyRow> Copies { get; } = new();
        public string Name => Group.Name;
        public string SizeText => Format.Size(Group.Length);
        public int Count => Group.Copies.Count;
        public string WastedText => Format.Size(Group.Wasted);
        public string TickedText => Copies.Count(c => c.Checked) is var n && n > 0 ? n.ToString() : "";
        public string Folders => string.Join("  ·  ", Group.Copies.Select(c => System.IO.Path.GetDirectoryName(c.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Take(4));
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TickedText)));
    }

    public DuplicatesWindow(ScanView view)
    {
        InitializeComponent();
        Localizer.Apply(this);
        _view = view;
        Closed += (_, _) => { _view.Model.Spotlight = null; _view.RefreshMap(); };
    }

    internal long MinBytes => long.Parse((string)((ComboBoxItem)MinSize.SelectedItem).Tag);

    public void ShowProgress(DuplicateProgress p)
    {
        HeadLine.Text = T(p.Phase) + "…";
        Progress.Visibility = Visibility.Visible;
        Progress.Value = p.Total <= 0 ? 0 : (double)p.Done / p.Total;
        SubLine.Text = p.Phase.StartsWith("Comparing whole") ? F($"{Format.Size(p.Done)} of {Format.Size(p.Total)}") : F($"{p.Done:N0} of {p.Total:N0} files");
    }

    public void SetResult(DuplicateResult r)
    {
        _result = r;
        Progress.Visibility = Visibility.Collapsed;
        HeadLine.Text = r.Groups.Count == 0 ? T("No duplicate files found") : P(r.Groups.Count, "{1} in extra copies of {0:N0} file", "{1} in extra copies of {0:N0} files", Format.Size(r.WastedBytes));
        var notes = new List<string> { F($"{r.Candidates:N0} files of equal length compared, {Format.Size(r.BytesRead)} read in {r.Elapsed.TotalSeconds:0.0} s.") };
        if (r.HardLinkNames > 0) notes.Add(P(r.HardLinkNames, "{0:N0} hard-linked name is the same file and is not counted as a copy.", "{0:N0} hard-linked names are the same file and are not counted as copies."));
        if (r.SkippedOnline > 0) notes.Add(P(r.SkippedOnline, "{0:N0} online-only cloud file was not read (that would download it).", "{0:N0} online-only cloud files were not read (that would download them)."));
        if (r.SkippedUnreadable > 0) notes.Add(P(r.SkippedUnreadable, "{0:N0} file could not be read.", "{0:N0} files could not be read."));
        SubLine.Text = string.Join(" ", notes);
        var ctx = SafetyContext.For(_view.Tree);
        _groups.Clear();
        foreach (var g in r.Groups)
        {
            var row = new GroupRow { Group = g, Owner = this };
            foreach (var c in g.Copies) row.Copies.Add(new CopyRow { Copy = c, Group = row, Assessment = DeleteSafety.Assess(c.Node, ctx) });
            _groups.Add(row);
        }
        Groups.ItemsSource = null;
        Groups.ItemsSource = _groups;
        if (_groups.Count > 0) Groups.SelectedIndex = 0; else Copies.ItemsSource = null;
        bool live = _view.Tree.IsLive;
        RecycleBtn.IsEnabled = ForeverBtn.IsEnabled = live;
        UpdateSelection();
    }

    /// <summary>After a delete: copies that left the tree go, groups with one copy left too.</summary>
    internal void Prune()
    {
        if (_result == null) return;
        bool Attached(FsNode n) { for (FsNode? p = n; p != null; p = p.Parent) if (ReferenceEquals(p, _view.Tree.Root)) return true; return false; }
        foreach (var g in _result.Groups) g.Copies.RemoveAll(c => !Attached(c.Node));
        _result.Groups.RemoveAll(g => g.Copies.Count < 2);
        SetResult(_result);
    }

    private GroupRow? Selected => Groups.SelectedItem as GroupRow;

    private void Groups_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Copies.ItemsSource = Selected?.Copies;
        UpdateSpotlight();
    }

    internal void Refuse(GroupRow g)
    {
        SelectionText.Text = F($"One copy of \"{g.Name}\" always stays: untick another copy first.");
    }

    internal IEnumerable<CopyRow> Ticked => _groups.SelectMany(g => g.Copies).Where(c => c.Checked);

    internal void UpdateSelection()
    {
        var t = Ticked.ToList();
        SelectionText.Text = t.Count == 0 ? T("Tick the copies that should go (one copy of each file always stays).") : P(t.Count, "{0} copy ticked", "{0} copies ticked") + " " + P(t.Select(c => c.Group).Distinct().Count(), "in {0} group: {1} freed", "in {0} groups: {1} freed", Format.Size(t.Where(c => c.Copy.OtherNames.Count == 0).Sum(c => c.Copy.DiskSize)));
    }

    private void Keep(DuplicateFinder.KeepRule rule)
    {
        var groups = EveryGroup.IsChecked == true ? _groups : Selected != null ? new List<GroupRow> { Selected } : new List<GroupRow>();
        foreach (var g in groups)
        {
            var go = DuplicateFinder.AllButOne(g.Group, rule, c => g.Copies.First(r => ReferenceEquals(r.Copy, c)).Assessment.Blocked);
            foreach (var c in g.Copies) c.Set(go.Contains(c.Copy));
            // never all of them (a protected copy may already stay; otherwise AllButOne kept one)
            if (g.Copies.All(c => c.Checked)) g.Copies[0].Set(false);
            g.Changed();
        }
        UpdateSelection();
    }

    private void KeepOldest_Click(object sender, RoutedEventArgs e) => Keep(DuplicateFinder.KeepRule.Oldest);
    private void KeepNewest_Click(object sender, RoutedEventArgs e) => Keep(DuplicateFinder.KeepRule.Newest);
    private void KeepFirst_Click(object sender, RoutedEventArgs e) => Keep(DuplicateFinder.KeepRule.FirstPath);
    private void Untick_Click(object sender, RoutedEventArgs e) { foreach (var g in _groups) { foreach (var c in g.Copies) c.Set(false); g.Changed(); } UpdateSelection(); }

    /// <summary>Tests: ticks the copies <see cref="DuplicateFinder.AllButOne"/> gives in every group.</summary>
    internal void TickAllButOldest() { EveryGroup.IsChecked = true; Keep(DuplicateFinder.KeepRule.Oldest); }

    private void UpdateSpotlight()
    {
        if (Spot.IsChecked != true || Selected == null) { _view.Model.Spotlight = null; _view.RefreshMap(); return; }
        var d = new Dictionary<FsNode, bool>(ReferenceEqualityComparer.Instance);
        foreach (var c in Selected.Group.Copies) d[c.Node] = true;
        _view.Model.Spotlight = d;
        _view.RefreshMap();
    }

    private void Spot_Click(object sender, RoutedEventArgs e) => UpdateSpotlight();
    private void Refresh_Click(object sender, RoutedEventArgs e) => _view.ShowDuplicates(MinBytes, IncludeWindows.IsChecked == true);

    private FsNode? Chosen => (Copies.SelectedItem as CopyRow)?.Copy.Node ?? Selected?.Group.Copies[0].Node;
    private void ShowOnMap_Click(object sender, RoutedEventArgs e) { if (Chosen is { Parent: not null } n) { _view.ShowNode(n); Owner?.Activate(); } }
    private void Copies_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ShowOnMap_Click(sender, e);
    private void Explorer_Click(object sender, RoutedEventArgs e) { if (Chosen is { } n && _view.Tree.IsLive) _view.OpenInExplorer(n); }

    private void Recycle_Click(object sender, RoutedEventArgs e) => DeleteTicked(permanent: false);
    private void Forever_Click(object sender, RoutedEventArgs e) => DeleteTicked(permanent: true);

    internal DeleteWindow? DeleteTicked(bool permanent, bool modal = true)
    {
        var ticked = Ticked.ToList();
        if (ticked.Count == 0) { SelectionText.Text = T("Tick the copies that should go first."); return null; }
        // the last line of defence: never every copy of a file
        var all = ticked.GroupBy(c => c.Group).FirstOrDefault(g => g.Count() >= g.Key.Copies.Count);
        if (all != null) { Refuse(all.Key); return null; }
        return _view.DeleteNodes(ticked.Select(c => (FsNode)c.Copy.Node).ToList(), permanent, modal: modal);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        var dlg = new SaveFileDialog { Filter = T("Text file (*.txt)|*.txt|CSV file (*.csv)|*.csv|JSON file (*.json)|*.json"), FileName = F($"{Branding.Name} duplicates.txt") };
        if (Dialogs.Pick(dlg, this) != true) return;
        try { File.WriteAllText(dlg.FileName, ScanView.DuplicatesReport(_result, dlg.FileName), new UTF8Encoding(dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))); }
        catch (Exception ex) { Dialogs.Show(this, ex.Message, T("Cannot write file"), MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
