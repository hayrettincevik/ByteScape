using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ByteScape.Core.Dev;
using ByteScape.Core.Util;
using ByteScape.Wpf.Views;
using Microsoft.Win32;

namespace ByteScape.Wpf.Windows;

/// <summary>
/// "Which old projects hold gigabytes of node_modules?": projects with folders their tools rebuild (node_modules, bin/obj,
/// target, .venv...), how long ago each project changed, and the commands that bring the folders back.
/// </summary>
public partial class DevWindow : Window
{
    private readonly ScanView _view;
    private DevScanResult? _result;

    internal sealed class ProjectRow
    {
        public required DevProject Project { get; init; }
        public string Name => Project.Name;
        public string Kinds => Project.KindsText;
        public string Path => Project.Path;
        public bool IsStale { get; init; }
        public string ArtifactText => Format.Size(Project.ArtifactBytes);
        public string SourceText => Format.Size(Project.SourceBytes);
        public string ChangedText => Project.LastActivity == default ? "—" : AppsWindow.Ago(Project.LastActivity);
    }

    internal sealed record PieceRow(DevArtifact Artifact, string Folder)
    {
        public string SizeText => Format.Size(Artifact.Bytes);
        public string Rebuild => Artifact.Rebuild;
        public string Note => Artifact.Note;
    }

    public DevWindow(ScanView view)
    {
        InitializeComponent();
        Localizer.Apply(this);
        _view = view;
        Closed += (_, _) => { _view.Model.Spotlight = null; _view.RefreshMap(); };
    }

    private TimeSpan StaleSpan => TimeSpan.FromDays(double.Parse((string)((ComboBoxItem)Stale.SelectedItem).Tag));

    public void SetResult(DevScanResult result, bool scanRunning)
    {
        var selected = (ProjectList.SelectedItem as ProjectRow)?.Project.Path;
        _result = result;
        var now = DateTime.UtcNow;
        PartialLine.Text = scanRunning ? T("The scan is still running: more projects may appear (press Refresh when it completes).") : "";
        PartialLine.Visibility = scanRunning ? Visibility.Visible : Visibility.Collapsed;
        HeadLine.Text = P(result.Projects.Count, "{1} rebuildable in {0} project", "{1} rebuildable in {0} projects", Format.Size(result.ArtifactBytes));
        SubLine.Text = result.Projects.Count == 0
            ? T("No projects with build output, dependencies or environments found here.")
            : string.Join(" · ", result.Projects.SelectMany(p => p.Artifacts).GroupBy(a => a.Ecosystem).OrderByDescending(g => g.Sum(a => a.Bytes)).Select(g => g.Key + " " + Format.Size(g.Sum(a => a.Bytes))))
              + ". " + T("Program folders are never included.");
        var stale = result.Untouched(TimeSpan.FromDays(182), now).ToList();
        StaleText.Text = stale.Count + " · " + Format.Size(stale.Sum(p => p.ArtifactBytes));
        NodeText.Text = Format.Size(result.Projects.SelectMany(p => p.Artifacts).Where(a => a.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)).Sum(a => a.Bytes));
        WarnText.Text = result.Projects.SelectMany(p => p.Artifacts).Count(a => a.Note.Length > 0).ToString();
        Fill();
        if (selected != null) Select(selected);
    }

    /// <summary>Selects the project a folder belongs to (a rebuildable folder, or the project folder itself).</summary>
    public void Select(string path)
    {
        ProjectRow? Find() => ProjectList.Items.OfType<ProjectRow>()
            .Where(r => path.Equals(r.Path, StringComparison.OrdinalIgnoreCase) || path.StartsWith(r.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Path.Length).FirstOrDefault();
        var row = Find();
        if (row == null && (Stale.SelectedIndex != 0 || Search.Text.Length > 0)) { Stale.SelectedIndex = 0; Search.Text = ""; Fill(); row = Find(); }
        if (row == null) return;
        ProjectList.SelectedItem = row;
        ProjectList.ScrollIntoView(row);
    }

    private void Fill()
    {
        var r = _result;
        if (r == null) return;
        var q = Search.Text.Trim();
        bool Hit(string s) => q.Length == 0 || s.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        var now = DateTime.UtcNow;
        var span = StaleSpan;
        var stale = new HashSet<DevProject>(r.Untouched(TimeSpan.FromDays(182), now));
        ProjectList.ItemsSource = r.Projects
            .Where(p => span == TimeSpan.Zero || p.LastActivity == default || now - p.LastActivity > span)
            .Where(p => Hit(p.Name) || Hit(p.KindsText) || Hit(p.Path) || p.Artifacts.Any(a => Hit(a.Name)))
            .Select(p => new ProjectRow { Project = p, IsStale = stale.Contains(p) })
            .ToList();
        if (ProjectList.SelectedItem == null) ShowPieces(null);
    }

    private void ShowPieces(ProjectRow? row)
    {
        CommandsBtn.IsEnabled = row != null;
        DeleteBtn.IsEnabled = row != null && _view.Tree.IsLive;
        if (row == null)
        {
            PieceList.ItemsSource = null;
            PieceHead.Text = T("Select a project to see its rebuildable folders.");
            _view.Model.Spotlight = null;
            _view.RefreshMap();
            return;
        }
        var p = row.Project;
        PieceList.ItemsSource = p.Artifacts.Select(a => new PieceRow(a, System.IO.Path.GetRelativePath(p.Path, a.Path))).ToList();
        PieceHead.Text = p.Name + " (" + p.KindsText + ") — " + F($"{Format.Size(p.ArtifactBytes)} rebuildable, {Format.Size(p.SourceBytes)} of its own")
                         + (p.LastActivity == default ? "" : ", " + F($"last change {Format.Date(p.LastActivity)}")) + (p.IsGitRepo ? ", " + T("git repository") : "");
        UpdateSpotlight(row);
    }

    private void UpdateSpotlight(ProjectRow? row)
    {
        _view.Model.Spotlight = Spot.IsChecked == true && row != null && _result != null ? _result.SpotlightFor(row.Project.Artifacts) : null;
        _view.RefreshMap();
    }

    private IEnumerable<DevArtifact> ChosenArtifacts()
    {
        var picked = PieceList.SelectedItems.OfType<PieceRow>().Select(r => r.Artifact).ToList();
        return picked.Count > 0 ? picked : (ProjectList.SelectedItem as ProjectRow)?.Project.Artifacts ?? new List<DevArtifact>();
    }

    private void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowPieces(ProjectList.SelectedItem as ProjectRow);
    private void Search_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) Fill(); }
    private void Filter_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) Fill(); }
    private void Spot_Click(object sender, RoutedEventArgs e) => UpdateSpotlight(ProjectList.SelectedItem as ProjectRow);
    private void Refresh_Click(object sender, RoutedEventArgs e) => _view.ShowDev((ProjectList.SelectedItem as ProjectRow)?.Project.Path);
    private void PieceList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ShowOnMap(fromPiece: true);
    private void ProjectList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ShowOnMap(fromPiece: false);
    private void ShowOnMap_Click(object sender, RoutedEventArgs e) => ShowOnMap(fromPiece: PieceList.SelectedItem != null);

    private void ShowOnMap(bool fromPiece)
    {
        if (fromPiece && PieceList.SelectedItem is PieceRow pr) _view.ShowNode(pr.Artifact.Node);
        else if (ProjectList.SelectedItem is ProjectRow row) _view.ShowNode(row.Project.Node);
        else return;
        Owner?.Activate();
    }

    private void Explorer_Click(object sender, RoutedEventArgs e)
    {
        var node = (PieceList.SelectedItem as PieceRow)?.Artifact.Node ?? (ProjectList.SelectedItem as ProjectRow)?.Project.Node;
        if (node != null && _view.Tree.IsLive) _view.OpenInExplorer(node);
    }

    private void Commands_Click(object sender, RoutedEventArgs e)
    {
        var list = ChosenArtifacts().ToList();
        if (list.Count == 0) return;
        try { Dialogs.Copy(DevProjects.DeleteCommands(list)); } catch { }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var nodes = ChosenArtifacts().Select(a => a.Node).ToList();
        if (nodes.Count > 0) _view.DeleteNodes(nodes, permanent: false);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        try { Dialogs.Copy(DevProjects.ToText(_result, DateTime.UtcNow)); } catch { }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        var dlg = new SaveFileDialog { Filter = T("Text file (*.txt)|*.txt|Tab separated (*.tsv)|*.tsv"), FileName = F($"{Branding.Name} developer leftovers.txt") };
        if (Dialogs.Pick(dlg, this) != true) return;
        try { File.WriteAllText(dlg.FileName, DevProjects.ToText(_result, DateTime.UtcNow), new UTF8Encoding(false)); }
        catch (Exception ex) { Dialogs.Show(this, ex.Message, T("Cannot write file"), MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
