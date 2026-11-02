using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ByteScape.Core.Compare;
using ByteScape.Core.Model;
using ByteScape.Core.Util;
using ByteScape.Wpf.Views;
using Microsoft.Win32;

namespace ByteScape.Wpf.Windows;

/// <summary>Two folders side by side: what is only on one side, what differs (length, time or content) and what is the same.</summary>
public partial class FolderCompareWindow : Window
{
    private readonly ScanView _view;
    private FolderCompareResult? _result;
    private readonly Func<bool, Task>? _again;

    internal sealed record Row(FolderDiffEntry Entry)
    {
        public string Path => Entry.RelativePath + (Entry.IsFolder ? "\\" : "");
        public string LeftSize => Entry.Left == null ? "" : Format.Size(Entry.LeftBytes);
        public string RightSize => Entry.Right == null ? "" : Format.Size(Entry.RightBytes);
        public string LeftTime => Entry.LeftModified == default ? "" : Entry.LeftModified.ToLocalTime().ToString("g");
        public string RightTime => Entry.RightModified == default ? "" : Entry.RightModified.ToLocalTime().ToString("g");
        public string Reason => Entry.Reason;
    }

    /// <param name="again">Runs the comparison again (true: with contents); null when it cannot.</param>
    public FolderCompareWindow(ScanView view, Func<bool, Task>? again)
    {
        InitializeComponent();
        Localizer.Apply(this);
        _view = view;
        _again = again;
        AgainBtn.IsEnabled = again != null;
        Closed += (_, _) => { _view.Model.Spotlight = null; _view.RefreshMap(); };
    }

    public void ShowWorking(string text) { HeadLine.Text = text; }

    public void SetResult(FolderCompareResult r)
    {
        _result = r;
        int diffs = r.Count(FolderDiffKind.OnlyLeft) + r.Count(FolderDiffKind.OnlyRight) + r.Count(FolderDiffKind.Different);
        HeadLine.Text = diffs == 0 ? T(r.ContentsCompared ? "The folders are the same (contents compared)" : "The folders are the same") : P(diffs, "{0:N0} difference", "{0:N0} differences");
        LeftLine.Text = T("Left:") + "  " + r.LeftPath;
        RightLine.Text = T("Right:") + " " + r.RightPath;
        SubLine.Text = T(r.ContentsCompared ? "Files of equal length were read and compared byte by byte." : "Files are compared by length and time (a 2 second or whole-hour difference counts as equal: FAT drives and time zones).")
                       + " " + P(r.SameFiles, "{0:N0} file is the same.", "{0:N0} files are the same.") + " " + r.Elapsed.TotalSeconds.ToString("0.0") + " s.";
        foreach (TabItem t in Tabs.Items)
        {
            var k = Enum.Parse<FolderDiffKind>((string)t.Tag);
            t.Header = FolderCompare.KindText(k) + "  ·  " + r.Count(k).ToString("N0") + (k == FolderDiffKind.Same ? "" : "  (" + Format.Size(r.Bytes(k)) + ")");
        }
        AgainBtn.Content = T(r.ContentsCompared ? "Compare by length and time" : "Compare contents too");
        if (diffs > 0 && Current == FolderDiffKind.Same) Tabs.SelectedIndex = 0;
        Fill();
    }

    internal FolderDiffKind Current => Enum.Parse<FolderDiffKind>((string)((TabItem)Tabs.SelectedItem).Tag);

    private void Fill()
    {
        if (_result == null) return;
        var q = Search.Text.Trim();
        var rows = _result.Entries.Where(e => e.Kind == Current && (q.Length == 0 || e.RelativePath.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
                                  .Select(e => new Row(e)).ToList();
        List.ItemsSource = rows;
        CountText.Text = P(rows.Count, "{0:N0} element", "{0:N0} elements");
        // the left side is this view: highlight what differs from the right
        var d = new Dictionary<FsNode, bool>(ReferenceEqualityComparer.Instance);
        foreach (var e in _result.Entries)
            if (e.Left != null && e.Kind is FolderDiffKind.OnlyLeft or FolderDiffKind.Different) d[e.Left] = true;
        _view.Model.Spotlight = d.Count > 0 ? d : null;
        _view.RefreshMap();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (e.Source == Tabs && IsLoaded) Fill(); }
    private void Search_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) Fill(); }

    private FolderDiffEntry? Selected => (List.SelectedItem as Row)?.Entry;

    private void ShowOnMap_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.Left is { Parent: not null } n) { _view.ShowNode(n); Owner?.Activate(); }
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ShowOnMap_Click(sender, e);

    private void Open(string root)
    {
        var rel = Selected?.RelativePath;
        var path = rel == null ? root : System.IO.Path.Combine(root, rel);
        var target = File.Exists(path) || Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path) ?? root;
        try { Dialogs.Start(new System.Diagnostics.ProcessStartInfo(Core.Util.SystemTools.Explorer, File.Exists(target) ? "/select,\"" + target + "\"" : "\"" + target + "\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Cannot open Explorer: " + ex.Message); }
    }

    private void OpenLeft_Click(object sender, RoutedEventArgs e) { if (_result != null) Open(_result.LeftPath); }
    private void OpenRight_Click(object sender, RoutedEventArgs e) { if (_result != null) Open(_result.RightPath); }

    private async void Contents_Click(object sender, RoutedEventArgs e)
    {
        if (_again == null || _result == null) return;
        AgainBtn.IsEnabled = false;
        try { await _again(!_result.ContentsCompared); }
        finally { AgainBtn.IsEnabled = true; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        var dlg = new SaveFileDialog { Filter = T("Text file (*.txt)|*.txt|CSV file (*.csv)|*.csv"), FileName = F($"{Branding.Name} folder comparison.txt") };
        if (Dialogs.Pick(dlg, this) != true) return;
        bool csv = dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
        char sep = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ListSeparator is { Length: 1 } s ? s[0] : ',';
        try { File.WriteAllText(dlg.FileName, csv ? FolderCompare.ToCsv(_result, sep) : FolderCompare.ToText(_result), new UTF8Encoding(csv)); }
        catch (Exception ex) { Dialogs.Show(this, ex.Message, T("Cannot write file"), MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
