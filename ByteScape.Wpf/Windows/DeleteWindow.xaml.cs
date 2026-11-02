using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ByteScape.Core.Cleanup;
using ByteScape.Core.Config;
using ByteScape.Core.Model;
using ByteScape.Core.Util;
using ByteScape.Wpf.Views;

namespace ByteScape.Wpf.Windows;

/// <summary>
/// "Delete these?": what would go, each with its verdict (safe, your files, protected...), its warnings and what stands in
/// the way (administrator rights, open in a program, protected by Windows); to the Recycle Bin or permanently, with an
/// explicit "I understand" when something is lost. Protected elements are shown but never deleted. Deletes, shows the
/// outcome, and has the view update its map.
/// </summary>
public partial class DeleteWindow : Window
{
    private readonly ScanView _view;
    private readonly IReadOnlyList<FsNode> _nodes;
    private readonly TimeSpan? _contentAge;
    private readonly CancellationTokenSource _cts = new();
    private DeletePlan? _plan;
    private bool _running, _done, _probed;

    internal DeletePlan? Plan => _plan;
    internal DeleteReport? Report { get; private set; }
    /// <summary>Tests: the plan is built and the rights are checked.</summary>
    internal Task Ready => _ready.Task;
    private readonly TaskCompletionSource _ready = new();

    public DeleteWindow(ScanView view, IReadOnlyList<FsNode> nodes, bool permanent, TimeSpan? contentAge = null)
    {
        InitializeComponent();
        Localizer.Apply(this);
        _view = view;
        _nodes = nodes;
        _contentAge = contentAge;
        (permanent ? ForeverOpt : RecycleOpt).IsChecked = true;
        Loaded += async (_, _) => await Prepare();
        Closing += (_, e) => { if (_running) { e.Cancel = true; _cts.Cancel(); } else _cts.Cancel(); };
    }

    private bool Recycle => RecycleOpt.IsChecked == true;

    private async Task Prepare()
    {
        try
        {
            var ctx = SafetyContext.For(_view.Tree);
            var sync = _view.Tree.Sync;
            _plan = await Task.Run(() => { var p = DeletePlan.Build(_nodes, ctx, _contentAge); p.CheckRecycleBins(); return p; });
            Render();
            CheckText.Text = T("Checking rights and whether programs have them open…");
            await Task.Run(() => _plan.Probe(sync, _cts.Token));
            _probed = true;
            CheckText.Text = "";
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { CheckText.Text = T("Could not check everything:") + " " + ex.Message; }
        finally { _ready.TrySetResult(); }
    }

    private void Render()
    {
        var plan = _plan;
        if (plan == null || _done) return;
        var deletable = plan.Deletable.ToList();
        var blocked = plan.Protected.ToList();
        int targets = plan.TargetCount;
        HeadLine.Text = deletable.Count == 0 ? T("Nothing here can be deleted")
            : P(targets, "Delete {0:N0} element, {1}?", "Delete {0:N0} elements, {1}?", Format.Size(plan.Bytes));
        var sub = new List<string>();
        if (blocked.Count > 0) sub.Add(P(blocked.Count, "{0} protected element is left out (see below).", "{0} protected elements are left out (see below)."));
        int kept = deletable.Sum(i => i.KeptRecent);
        if (kept > 0) sub.Add(P(kept, "{0:N0} element changed in the last day stays (a program may be using it).", "{0:N0} elements changed in the last day stay (a program may be using them)."));
        SubLine.Text = string.Join(" ", sub);
        HowPanel.IsEnabled = deletable.Count > 0;

        ItemsPanel.Children.Clear();
        bool elevated = Core.Cleanup.AccessProbe.IsElevated;
        foreach (var item in deletable) ItemsPanel.Children.Add(Card(item, elevated));
        if (blocked.Count > 0)
        {
            ItemsPanel.Children.Add(new TextBlock { Text = F($"Not deleted — protected ({blocked.Count})"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4), Foreground = SafetyUi.LevelBrush(SafetyLevel.Protected) });
            foreach (var item in blocked) ItemsPanel.Children.Add(Card(item, elevated));
        }

        // Recycle Bin notes
        var notes = Recycle ? plan.RecycleNotes : new List<string>();
        NoteBox.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoteText.Text = string.Join("\n", notes);

        // rights
        var probes = deletable.Select(i => i.Probe).Where(p => p != null).ToList();
        int admin = probes.Sum(p => p!.NeedsAdminCount), prot = probes.Sum(p => p!.ProtectedCount), inUse = probes.Sum(p => p!.InUseCount);
        var lines = new List<string>();
        if (admin > 0) lines.Add(P(admin, "{0:N0} element needs administrator rights:", "{0:N0} elements need administrator rights:") + " " + (elevated ? F($"{Branding.Name} runs as administrator, they will be deleted too.") : T("they will not be deleted.")));
        if (prot > 0) lines.Add(P(prot, "{0:N0} element is protected by Windows (owner TrustedInstaller) and will not be deleted, not even by an administrator.", "{0:N0} elements are protected by Windows (owner TrustedInstaller) and will not be deleted, not even by an administrator."));
        if (inUse > 0) lines.Add(P(inUse, "{0:N0} element is open in a program: close it first, or it stays.", "{0:N0} elements are open in a program: close it first, or they stay."));
        AdminBox.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AdminText.Text = string.Join("\n", lines);
        ElevateBtn.Visibility = admin > 0 && !elevated && !AppConfig.IsPackaged() ? Visibility.Visible : Visibility.Collapsed;

        // "I understand"
        var reasons = new List<string>();
        if (!Recycle) reasons.Add(T("this cannot be undone"));
        if (deletable.Any(i => i.Assessment.Level == SafetyLevel.Personal)) reasons.Add(T("your own files are deleted and cannot be recreated"));
        if (deletable.Any(i => i.Assessment.Level == SafetyLevel.Caution)) reasons.Add(T("something is lost (see the warnings)"));
        if (deletable.Any(i => i.Assessment.Warnings.Any(w => w.Level == WarningLevel.Serious))) reasons.Add(T("serious warnings apply (marked red)"));
        Understand.Visibility = deletable.Count > 0 && reasons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UnderstandText.Text = T("I understand:") + " " + string.Join("; ", reasons.Distinct()) + ".";
        if (Understand.Visibility != Visibility.Visible) Understand.IsChecked = false;
        DeleteBtn.Content = T(Recycle ? "Move to the Recycle Bin" : "Delete permanently");
        UpdateButton();
    }

    private FrameworkElement Card(PlanItem item, bool elevated)
    {
        var a = item.Assessment;
        var box = new Border { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1, 1, 1, 1),
                               BorderBrush = (Brush)FindResource("Border"), Background = (Brush)FindResource(a.Blocked ? "LayerAlt" : "Layer") };
        var sp = new StackPanel();
        var title = new DockPanel();
        var size = new TextBlock { Text = a.Blocked ? Format.Size(item.Node.DiskSize) : Format.Size(item.Bytes), Margin = new Thickness(10, 0, 0, 0), Foreground = (Brush)FindResource("TextSecondary") };
        DockPanel.SetDock(size, Dock.Right);
        title.Children.Add(size);
        title.Children.Add(new TextBlock { Text = a.Path, FontFamily = new FontFamily("Consolas, Segoe UI"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = a.Path });
        sp.Children.Add(title);
        var verdict = SafetyUi.Verdict(a);
        verdict.Margin = new Thickness(0, 6, 0, 0);
        sp.Children.Add(verdict);
        if (a.ContentOnly && !a.Blocked)
            sp.Children.Add(new TextBlock { Text = P(item.Targets.Count, "What is inside goes: {0:N0} element", "What is inside goes: {0:N0} elements") + (item.KeptRecent > 0 ? "; " + P(item.KeptRecent, "{0:N0} changed in the last day stays", "{0:N0} changed in the last day stay") : "") + ". " + T("The folder itself stays."),
                                            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        if (!a.Blocked)
        {
            var (text, brush) = SafetyUi.ProbeLine(item.Probe, elevated);
            sp.Children.Add(new TextBlock { Text = _probed || item.Probe != null ? text : T("Checking whether it can be deleted…"), Foreground = brush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        }
        box.Child = sp;
        return box;
    }

    private void UpdateButton()
    {
        DeleteBtn.IsEnabled = !_running && !_done && _plan != null && _plan.TargetCount > 0 && (Understand.Visibility != Visibility.Visible || Understand.IsChecked == true);
    }

    private void How_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) { Understand.IsChecked = false; Render(); } }
    private void Understand_Changed(object sender, RoutedEventArgs e) => UpdateButton();

    private async void Delete_Click(object sender, RoutedEventArgs e) => await RunDelete();

    /// <summary>Deletes what the plan allows (also the tests' entry point), then shows the outcome and updates the map.</summary>
    internal async Task RunDelete()
    {
        if (_plan == null || _running || _done || !DeleteBtn.IsEnabled) return;
        _running = true;
        bool recycle = Recycle;
        HowPanel.IsEnabled = false; Understand.IsEnabled = false; DeleteBtn.IsEnabled = false; ElevateBtn.IsEnabled = false;
        CancelBtn.Content = T("Stop");
        ProgressPanel.Visibility = Visibility.Visible;
        var targets = _plan.TargetsOf();
        var owner = new WindowInteropHelper(this).Handle;
        var progress = new Progress<(int Done, int Total, string Path)>(p =>
        {
            Progress.Value = p.Total == 0 ? 1 : (double)p.Done / p.Total;
            ProgressText.Text = p.Path.Length > 0 ? T(recycle ? "Moving to the Recycle Bin:" : "Deleting:") + " " + p.Path : "";
        });
        try { Report = await Core.Cleanup.Deleter.RunAsync(targets, recycle, owner, progress, _cts.Token, Core.Cleanup.DeleteGuard.For(_view.Tree)); }
        catch (Exception ex) { Log.Error("Delete failed: " + ex.Message); Report = new DeleteReport { Recycled = recycle }; }
        _running = false; _done = true;
        ShowOutcome(Report);
        _view.AfterDelete(_plan, Report);
    }

    private void ShowOutcome(DeleteReport r)
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        HowPanel.Visibility = Visibility.Collapsed; Understand.Visibility = Visibility.Collapsed; AdminBox.Visibility = Visibility.Collapsed; NoteBox.Visibility = Visibility.Collapsed;
        DeleteBtn.Visibility = Visibility.Collapsed;
        CancelBtn.Content = T("Close");
        HeadLine.Text = r.DeletedCount == 0 ? T("Nothing was deleted")
            : r.Recycled ? P(r.DeletedCount, "Moved {0:N0} element ({1}) to the Recycle Bin", "Moved {0:N0} elements ({1}) to the Recycle Bin", Format.Size(r.DeletedBytes))
            : P(r.DeletedCount, "Deleted {0:N0} element permanently: {1} freed", "Deleted {0:N0} elements permanently: {1} freed", Format.Size(r.DeletedBytes));
        SubLine.Text = (r.Recycled && r.DeletedCount > 0 ? T("They still take their space until the Recycle Bin is emptied; they can be restored from there.") + " " : "")
                       + (r.Cancelled ? T("Stopped before the end.") + " " : "")
                       + (r.FailedCount > 0 ? P(r.FailedCount, "{0:N0} could not be deleted:", "{0:N0} could not be deleted:") : "");
        ItemsPanel.Children.Clear();
        foreach (var f in r.Items.Where(i => !i.Deleted).Take(200))
            ItemsPanel.Children.Add(new TextBlock { Text = "✗  " + f.Path + "  —  " + f.Error, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2), Foreground = SafetyUi.WarningBrush(WarningLevel.Caution) });
        if (r.FailedCount == 0)
            foreach (var d in r.Items.Take(200))
                ItemsPanel.Children.Add(new TextBlock { Text = "✓  " + d.Path, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2), Foreground = (Brush)FindResource("TextSecondary") });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_running) { _cts.Cancel(); CancelBtn.IsEnabled = false; return; }
        Close();
    }

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        Close();
        MainWindow.Instance?.RestartAsAdministrator();
    }
}
