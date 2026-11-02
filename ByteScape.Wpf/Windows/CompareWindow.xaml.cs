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

/// <summary>"What changed since that snapshot?": sortable list of grown / shrunk / added / removed elements with a map overlay.</summary>
public partial class CompareWindow : Window
{
    private readonly ScanView _view;
    private readonly DiffResult _diff;

    internal sealed record Row(DiffEntry Entry)
    {
        public string KindText => T(Entry.Kind switch { ChangeKind.Added => "added", ChangeKind.Removed => "removed", ChangeKind.Grown => "grown", _ => "shrunk" });
        public string DeltaText => (Entry.Delta >= 0 ? "+" : "−") + Format.Size(Entry.AbsDelta);
        public string OldText => Entry.Kind == ChangeKind.Added ? "—" : Format.Size(Entry.OldBytes);
        public string NewText => Entry.Kind == ChangeKind.Removed ? "—" : Format.Size(Entry.NewBytes);
        public string TypeText => T(Entry.IsFolder ? "folder" : "file");
        public string Path => Entry.Path;
    }

    public CompareWindow(ScanView view, DiffResult diff)
    {
        InitializeComponent();
        Localizer.Apply(this);
        Localizer.FitColumn(List, 0, "added", "removed", "grown", "shrunk");
        Localizer.FitColumn(List, 4, "folder", "file");
        _view = view;
        _diff = diff;
        HeadLine.Text = F($"{(diff.Delta >= 0 ? "+" : "−")}{Format.Size(Math.Abs(diff.Delta))} since {diff.BaselineTime.ToLocalTime():g}")
                        + "  (" + Format.Size(diff.OldTotal) + " → " + Format.Size(diff.NewTotal) + ")";
        BaselineLine.Text = T("Snapshot:") + " " + diff.BaselinePath;
        CurrentLine.Text = T("Now:") + " " + diff.CurrentPath;
        long grown = diff.Entries.Where(e => e.Kind is ChangeKind.Grown && !e.IsFolder).Sum(e => e.Delta) + diff.AddedBytes;
        long shrunk = diff.Entries.Where(e => e.Kind is ChangeKind.Shrunk && !e.IsFolder).Sum(e => e.AbsDelta) + diff.RemovedBytes;
        GrownText.Text = "+" + Format.Size(grown);
        ShrunkText.Text = "−" + Format.Size(shrunk);
        NetText.Text = (diff.Delta >= 0 ? "+" : "−") + Format.Size(Math.Abs(diff.Delta));
        Closed += (_, _) => { if (ReferenceEquals(_view.Model.Diff, _diff)) _view.Model.Diff = null; _view.RefreshMap(); };
        Fill();
        SummarizeBtn.Visibility = Core.Config.AppConfig.Current.AiEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Summarize_Click(object sender, RoutedEventArgs e)
    {
        SummarizeBtn.IsEnabled = false;
        SummaryBox.Visibility = Visibility.Visible;
        SummaryText.Text = T("Asking the local model…");
        try
        {
            var lm = Core.Ai.LocalModel.FromConfig(Core.Config.AppConfig.Current);
            var lang = Loc.Languages.FirstOrDefault(l => l.Code == Loc.Language)?.EnglishName ?? "English"; // the model answers in the interface language
            SummaryText.Text = await Core.Ai.Assistant.SummarizeDiffAsync(lm, _diff, lang, CancellationToken.None);
        }
        catch (Exception ex) { SummaryText.Text = T("Local model not reachable:") + " " + ex.Message; SummarizeBtn.IsEnabled = true; }
    }

    private void Fill()
    {
        long min = long.Parse((string)((ComboBoxItem)MinChange.SelectedItem).Tag);
        var rows = _diff.Entries.Where(e =>
                e.AbsDelta >= min
                && (e.IsFolder ? ShowFolders.IsChecked == true : ShowFiles.IsChecked == true)
                && e.Kind switch
                {
                    ChangeKind.Grown => ShowGrown.IsChecked == true,
                    ChangeKind.Shrunk => ShowShrunk.IsChecked == true,
                    ChangeKind.Added => ShowAdded.IsChecked == true,
                    _ => ShowRemoved.IsChecked == true,
                })
            .Take(5000).Select(e => new Row(e)).ToList();
        List.ItemsSource = rows;
        CountText.Text = F($"{rows.Count:N0} of {_diff.Entries.Count:N0} changes shown ({_diff.AddedCount:N0} added, {_diff.RemovedCount:N0} removed)") + (rows.Count == 5000 ? T(" — first 5000") : "");
    }

    private void Filter_Click(object sender, RoutedEventArgs e) { if (IsLoaded) Fill(); }

    private void Overlay_Click(object sender, RoutedEventArgs e)
    {
        _view.Model.Diff = Overlay.IsChecked == true ? _diff : null;
        _view.RefreshMap();
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is not Row row || row.Entry.Current is not { } node) return;
        _view.ShowNode(node);
        Owner?.Activate();
    }

    private string Text()
    {
        var sb = new StringBuilder();
        sb.AppendLine(F($"{Branding.Name} snapshot comparison"));
        sb.AppendLine(T("Snapshot:") + " " + _diff.BaselinePath + "  (" + _diff.BaselineTime.ToLocalTime().ToString("g") + ")");
        sb.AppendLine(T("Now:") + " " + _diff.CurrentPath);
        sb.AppendLine(T("Change:") + " " + HeadLine.Text);
        sb.AppendLine();
        sb.AppendLine("Change\tDelta\tBefore\tAfter\tType\tPath");
        foreach (Row r in List.Items) sb.Append(r.KindText).Append('\t').Append(r.DeltaText).Append('\t').Append(r.OldText).Append('\t').Append(r.NewText).Append('\t').Append(r.TypeText).Append('\t').AppendLine(r.Path);
        return sb.ToString();
    }

    private void Copy_Click(object sender, RoutedEventArgs e) { try { Dialogs.Copy(Text()); } catch { } }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = T("Text file (*.txt)|*.txt|Tab separated (*.tsv)|*.tsv"), FileName = F($"{Branding.Name} comparison.txt") };
        if (Dialogs.Pick(dlg, this) != true) return;
        try { File.WriteAllText(dlg.FileName, Text(), new UTF8Encoding(false)); }
        catch (Exception ex) { Dialogs.Show(this, ex.Message, T("Cannot write file"), MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
