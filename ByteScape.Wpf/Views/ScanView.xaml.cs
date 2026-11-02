using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ByteScape.Core.Apps;
using ByteScape.Core.Cleanup;
using ByteScape.Core.Compare;
using ByteScape.Core.Config;
using ByteScape.Core.Duplicates;
using ByteScape.Core.Dev;
using ByteScape.Core.Forecast;
using ByteScape.Core.Games;
using ByteScape.Core.Export;
using ByteScape.Core.Filtering;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using ByteScape.Core.Snapshot;
using ByteScape.Core.Util;
using ByteScape.Treemap;
using ByteScape.Wpf.Controls;
using ByteScape.Wpf.Windows;

namespace ByteScape.Wpf.Views;

/// <summary>One scan / snapshot view: breadcrumb, filter, treemap, details panel, status line.</summary>
public partial class ScanView : UserControl
{
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _tipTimer;
    private TreemapItem? _tipItem;
    private bool _autoExportStarted, _autoExportDone;

    public ViewModel Model { get; }
    public string Title { get; }
    public ScanTree Tree => Model.Tree;

    /// <summary>Pending command line exports: (config name, path).</summary>
    public List<(string name, string path)> AutoExports { get; } = new();
    public bool AutoExportsPending => (AutoExports.Count > 0 || HasReports || _autoExportStarted) && !_autoExportDone;
    /// <summary>A command line report waits for the scan (programs, games, dev, cleanup, duplicates, folder comparison).</summary>
    public bool HasReports => AppsReportPath != null || GamesReportPath != null || DevReportPath != null || CleanupReportPath != null || DuplicatesReportPath != null || CompareFolderReport != null;

    /// <summary>Command line "appsreport &lt;file&gt;": the program footprint report written with the exports once the scan is complete.</summary>
    public string? AppsReportPath { get; set; }

    /// <summary>Command line "gamesreport &lt;file&gt;": the game library report written with the exports once the scan is complete.</summary>
    public string? GamesReportPath { get; set; }

    /// <summary>Command line "devreport &lt;file&gt;": the developer leftovers report written with the exports once the scan is complete.</summary>
    public string? DevReportPath { get; set; }

    public event Action? StateChanged;

    public ScanView(ScanTree tree, ContainerNode viewRoot, string title)
    {
        InitializeComponent();
        Localizer.Apply(this);
        Model = new ViewModel(tree, viewRoot);
        Title = title;
        Map.Model = Model;
        Map.SelectionChanged += _ => { UpdateDetails(); UpdateStatus(); };
        Map.NavigationChanged += () => { UpdateBreadcrumb(); UpdateStatus(); StateChanged?.Invoke(); };
        Map.ContextMenuRequested += (node, pt) => ShowContextMenu(node, pt);
        Map.HoverChanged += OnHover;
        Model.ReclaimChanged += () => Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded) UpdateReclaimText(); }));
        FilterBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyFilter(); e.Handled = true; } else if (e.Key == Key.Escape) { Map.Focus(); e.Handled = true; } };
        FilterBox.TextChanged += (_, _) => FilterClear.Visibility = FilterBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        FilterClear.Click += (_, _) => { FilterBox.Text = ""; ApplyFilter(); };
        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _poll.Tick += (_, _) => { UpdateStatus(); if (Tree.IsMasterScanning || Model.IsSubScanning || Model.IsFiltering) StateChanged?.Invoke(); };
        _tipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AppConfig.Current.TooltipDelayMs) };
        _tipTimer.Tick += (_, _) => { _tipTimer.Stop(); ShowTip(); };
        tree.MasterScanFinished += OnMasterScanFinished;
        _ = AppCatalog.GetAsync();   // installed programs, read in the background for "Program:" in details, tooltip and menu
        // Steam / Epic / GOG games: names of numbered Steam folders on the map, "Game:" in details, tooltip and menu
        GameLibrary.GetAsync().ContinueWith(_ => Dispatcher.BeginInvoke(RefreshMap), TaskScheduler.Default);
        Loaded += (_, _) => { _poll.Start(); UpdateBreadcrumb(); UpdateStatus(); UpdateDetails(); };
        Unloaded += (_, _) => _poll.Stop();
    }

    public void Close()
    {
        Tree.MasterScanFinished -= OnMasterScanFinished;
        _poll.Stop();
        _appsCts?.Cancel();
        _apps?.Close();
        _gamesCts?.Cancel();
        _games?.Close();
        _devCts?.Cancel();
        _dev?.Close();
        _forecast?.Close();
        _compare?.Close();
        Model.Dispose();
        DataCache.Release(Tree);
    }

    // ------------------------------------------------------------------ navigation

    public bool CanGoBack => Model.CanGoBack;
    public bool CanGoForward => Model.CanGoForward;
    public bool CanGoUp => Model.CanGoUp;

    public void GoBack() { if (Model.CanGoBack) { var t = Model.PeekBack()!; Map.AnimateZoom(t, zoomIn: !t.IsAncestorOf(Model.ZoomRoot), () => Model.GoBack()); } }
    public void GoForward() { if (Model.CanGoForward) { var t = Model.PeekForward()!; Map.AnimateZoom(t, zoomIn: !t.IsAncestorOf(Model.ZoomRoot), () => Model.GoForward()); } }
    public void GoUp() { if (Model.ZoomRoot.Parent is ContainerNode p && Model.CanGoUp) Map.AnimateZoom(p, zoomIn: false); }
    public void GoHome() { if (Model.CanGoUp) Map.AnimateZoom(Model.ViewRoot, zoomIn: false); }
    public void ZoomTo(ContainerNode c) { if (!ReferenceEquals(c, Model.ZoomRoot)) Map.AnimateZoom(c, zoomIn: c.Level > Model.ZoomRoot.Level); }

    /// <summary>
    /// Stops a running scan; otherwise continues a stopped one, or rescans only what the NTFS change journal says changed
    /// (or scans everything again: <paramref name="fullRescan"/>, or neither is possible).
    /// </summary>
    public void ToggleMasterScan(bool fullRescan = false)
    {
        DisarmAuto();
        if (Tree.IsMasterScanning) Tree.StopMasterScan();
        else if (Tree.CanResume && !fullRescan) Tree.StartMasterScan(resume: true);
        else if (Tree.CanRescanChanges && !fullRescan) Tree.StartMasterScan(changesOnly: true);
        else
        {
            ScanResume.Delete(Tree.Root.FullPath);
            Tree.StartMasterScan();
        }
        StateChanged?.Invoke();
    }

    public void TogglePause()
    {
        if (!Tree.IsMasterScanning) return;
        if (Tree.IsMasterPaused) Tree.ContinueMasterScan(); else Tree.PauseMasterScan();
        UpdateStatus();
        StateChanged?.Invoke();
    }

    public void ToggleSubScan()
    {
        DisarmAuto();
        if (Model.IsSubScanning) Model.StopSubScan(); else Model.StartSubScan();
        StateChanged?.Invoke();
    }

    public void ChangeDetail(int delta) { Model.DetailLevel = Math.Clamp(Model.DetailLevel + delta, 1, 64); Map.MarkDirty(); }
    public void ToggleClasses() { Model.Style = Model.Style == ViewStyle.Flat ? ViewStyle.FileClasses : ViewStyle.Flat; Map.MarkDirty(); StateChanged?.Invoke(); }
    public void ToggleFree() { Model.ShowFreeSpace = !Model.ShowFreeSpace; Map.MarkDirty(); StateChanged?.Invoke(); }
    public void ToggleUnknown() { Model.ShowUnknownSpace = !Model.ShowUnknownSpace; Map.MarkDirty(); StateChanged?.Invoke(); }

    public void ToggleDetails()
    {
        DetailsColumn.Width = DetailsColumn.Width.Value > 0 ? new GridLength(0) : new GridLength(300);
        DetailsPanel.Visibility = DetailsColumn.Width.Value > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateDetails();
    }
    public bool DetailsVisible => DetailsColumn.Width.Value > 0;

    public void FocusFilter() { FilterBox.Focus(); FilterBox.SelectAll(); }

    public void ApplyFilter()
    {
        DisarmAuto();
        // plain language ("6 aydır açılmamış 100 MB üstü videolar") becomes filter syntax; the box shows the result so it can be edited
        if (NaturalFilter.LooksNatural(FilterBox.Text))
        {
            var nl = NaturalFilter.Translate(FilterBox.Text);
            if (!nl.IsEmpty)
            {
                Log.Debug("Filter understood: \"" + FilterBox.Text.Trim() + "\" -> " + nl.Filter);
                FilterBox.Text = nl.Filter;
                FilterBox.CaretIndex = FilterBox.Text.Length;
                ShowNote(T("Understood as:") + " " + nl.Explanation);
            }
            else if (AppConfig.Current.AiEnabled)
            {
                // the rules gave up: let the local model try; the result is validated by the real parser before use
                AskModelForFilter(FilterBox.Text.Trim());
                return;
            }
        }
        try { Model.SetFilter(FilterBox.Text); Map.MarkDirty(); }
        catch (FilterException ex)
        {
            Dialogs.Show(Window.GetWindow(this), T("The filter could not be read:") + "\n" + ex.Message + "\n" + T("Correct it and press Enter again."), T("Filter"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void AskModelForFilter(string request)
    {
        ShowNote(T("Asking the local model…"), 60);
        try
        {
            var lm = Core.Ai.LocalModel.FromConfig(AppConfig.Current);
            var r = await Core.Ai.Assistant.TranslateFilterAsync(lm, request, CancellationToken.None);
            if (r == null) { ShowNote(F($"The local model could not turn \"{request}\" into a filter; using it as a name mask.")); }
            else
            {
                Log.Debug("Filter by local model: \"" + request + "\" -> " + r.Value.filter);
                if (FilterBox.Text.Trim() == request) FilterBox.Text = r.Value.filter;
                ShowNote(F($"Understood by the local model as: {r.Value.filter}   (check before trusting it)"));
            }
        }
        catch (Exception ex) { ShowNote("Local model not reachable: " + ex.Message); Log.Warn("Local model: " + ex.Message); }
        try { Model.SetFilter(FilterBox.Text); Map.MarkDirty(); } catch (FilterException) { }
    }

    /// <summary>Command line "filter": syntax or plain language, like the box.</summary>
    public void SetFilterText(string text)
    {
        if (NaturalFilter.LooksNatural(text))
        {
            var nl = NaturalFilter.Translate(text);
            if (!nl.IsEmpty) { Log.Debug("Filter understood: \"" + text.Trim() + "\" -> " + nl.Filter); text = nl.Filter; ShowNote(T("Understood as:") + " " + nl.Explanation); }
            else if (AppConfig.Current.AiEnabled) { FilterBox.Text = text; AskModelForFilter(text.Trim()); return; }
        }
        FilterBox.Text = text;
        try { Model.SetFilter(text); } catch (FilterException ex) { Log.Error("Filter not understood: " + ex.Message); }
    }

    public void Tag(TagColor color)
    {
        var node = Map.HoveredNode ?? Model.Selected;
        if (node == null || node.IsSynthetic) return;
        if (Model.IsFiltering) { Dialogs.Show(Window.GetWindow(this), T("The filter is still being applied. Try again in a moment."), T("Tag")); return; }
        if (Model.IsSubScanning && (ReferenceEquals(node, Model.ZoomRoot) || Model.ZoomRoot.IsAncestorOf(node)))
        { Dialogs.Show(Window.GetWindow(this), T("This folder is still being scanned. Tag it when the scan is finished."), T("Tag")); return; }
        Model.ToggleTag(node, color);
        Map.MarkDirty();
        UpdateDetails();
    }

    public void ClearTags() { Model.ClearTags(); Map.MarkDirty(); UpdateDetails(); }

    /// <summary>Enter: opens the selected element in Explorer.</summary>
    public void OpenSelectedInExplorer()
    {
        var n = Model.Selected;
        if (n != null && !n.IsSynthetic && Tree.IsLive) OpenInExplorer(n);
    }

    /// <summary>Ctrl+C: copies the full path of the selected element.</summary>
    public void CopySelectedPath()
    {
        var n = Model.Selected;
        if (n == null || n.IsSynthetic) return;
        try { Dialogs.Copy(n.FullPath); } catch { }
    }

    public void ApplyConfigChanges()
    {
        Map.ApplyConfig();
        _tipTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, AppConfig.Current.TooltipDelayMs));
        UpdateStatus();
    }

    private void DisarmAuto()
    {
        if (AutoExports.Count > 0 || HasReports) { AutoExports.Clear(); AppsReportPath = null; GamesReportPath = null; DevReportPath = null; CleanupReportPath = null; DuplicatesReportPath = null; CompareFolderReport = null; Log.Debug("Auto export disabled by user interaction"); }
        MainWindow.Instance?.DisarmAutoClose();
    }

    // ------------------------------------------------------------------ breadcrumb

    private void UpdateBreadcrumb()
    {
        var chain = new List<ContainerNode>();
        for (ContainerNode? c = Model.ZoomRoot; c != null; c = c.Parent)
        {
            chain.Insert(0, c);
            if (ReferenceEquals(c, Model.ViewRoot)) break;
        }
        Breadcrumb.Items.Clear();
        for (int i = 0; i < chain.Count; i++)
        {
            var node = chain[i];
            if (i > 0) Breadcrumb.Items.Add(new Glyph { Text = "", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0), Opacity = 0.6 });
            var b = new Button { Content = node is DriveNode d ? d.FullPath : node.Name, Style = (Style)FindResource("LinkButton"), Tag = node };
            if (i == chain.Count - 1) b.FontWeight = FontWeights.SemiBold;
            b.Click += (_, _) => ZoomTo(node);
            Breadcrumb.Items.Add(b);
        }
    }

    // ------------------------------------------------------------------ status / details

    internal void UpdateStatusForTest() => UpdateStatus();

    private void UpdateStatus()
    {
        var tree = Tree;
        if (Model.IsFiltering)
        {
            Progress.Visibility = Visibility.Visible;
            Progress.Value = 1000 * Math.Clamp(Model.FilterProgress, 0, 1);
            ProgressText.Text = T("Filtering...");
        }
        else if (tree.IsMasterScanning || Model.IsSubScanning)
        {
            double p = tree.Progress;
            Progress.Visibility = p >= 0 ? Visibility.Visible : Visibility.Collapsed;
            if (p >= 0) Progress.Value = 1000 * p;
            ProgressText.Text = Model.IsSubScanning ? T("rescanning the folder…") : tree.MasterScan is { Phase.Length: > 0 } job ? T(job.Phase) + "..." : T("scanning…");
        }
        else
        {
            Progress.Visibility = Visibility.Collapsed;
            ProgressText.Text = T(tree.IsLive ? "Ready" : "snapshot");
        }
        if (_note != null && DateTime.UtcNow < _noteUntil)
        {
            StatusText.Text = _note;
            UpdateReclaimText();
            double f0 = Model.ViewablePercent;
            PercentFill.Height = Math.Max(0, (PercentFill.Parent as FrameworkElement)!.ActualHeight - 2) * f0;
            return;
        }
        string master = T(tree.IsLive
            ? (tree.IsMasterPaused ? "paused" : tree.IsMasterScanning ? (tree.MasterScan is { Resume: true } ? "continuing" : tree.MasterScan is { Since: not null } ? "updating" : "scanning")
               : tree.Root.ScanComplete ? "complete" : tree.CanResume ? "stopped (the scan button continues it)" : "stopped")
            : "snapshot");
        if (tree.IsLive && !tree.IsMasterScanning && tree.Root.ScanComplete && tree.MasterScan is { Method: "NTFS MFT" } fast)
            master += " (" + F($"fast NTFS scan, {fast.Elapsed.TotalSeconds:0.0} s") + ")";
        else if (tree.IsLive && tree.MasterScan is { Method: "NTFS change journal", Journal: { } js } quick)
            master += " (" + T("quick rescan:") + " " + (js.ToList == 0 ? T("nothing changed") : P(js.ToList, "{0:N0} folder changed", "{0:N0} folders changed"))
                      + " " + F($"since {(js.SinceUtc.ToLocalTime().Date == DateTime.Today ? "" : Format.Date(js.SinceUtc) + " ")}{Format.Time(js.SinceUtc)}") + (tree.IsMasterScanning ? "" : ", " + quick.Elapsed.TotalSeconds.ToString("0.0") + " s") + ")";
        else if (tree.IsLive && tree.MasterScan is { Method: "directory listing", Strategy: { } st } job)
            master += " (" + st.Short + (tree.IsMasterScanning ? "" : ", " + job.Elapsed.TotalSeconds.ToString("0.0") + " s") + ")";
        string sel = Model.Selected != null ? "   •   " + Model.Selected.FullPath + "  [" + Format.Size(Model.SizeOf(Model.Selected)) + "]" : "";
        if (tree.IsLive && !tree.IsMasterScanning && tree.HardLinks.SavedBytes > 0)
            master += " · " + F($"hard links counted once: {Format.Size(tree.HardLinks.SavedBytes)}");
        StatusText.Text = T("Master scan:") + " " + master + (AppConfig.Current.ScanADS && tree.IsLive ? " (ADS)" : "") + sel;
        UpdateReclaimText();
        double frac = Model.ViewablePercent;
        PercentFill.Height = Math.Max(0, (PercentFill.Parent as FrameworkElement)!.ActualHeight - 2) * frac;
    }

    private string? _note;
    private DateTime _noteUntil;

    /// <summary>Shows a transient message in the status line (e.g. how a plain language filter was understood).</summary>
    internal void ShowNote(string text, int seconds = 10)
    {
        _note = text; _noteUntil = DateTime.UtcNow.AddSeconds(seconds);
        UpdateStatus();
    }

    private sealed record ChildRow(string Name, string Size, string Percent);

    private void UpdateDetails()
    {
        if (!DetailsVisible) return;
        var n = Model.Selected ?? Model.ZoomRoot;
        DetName.Text = n is DriveNode d ? d.FullPath : n.Name;
        DetPath.Text = n.FullPath;
        DetFacts.Children.Clear();
        void Fact(string k, string v)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            // labels in any language: the label column is as wide as the widest label of the panel
            Grid.SetIsSharedSizeScope(DetFacts, true);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 90, SharedSizeGroup = "FactLabel" });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var kt = new TextBlock { Text = k, Style = (Style)FindResource("Secondary"), Margin = new Thickness(0, 0, 10, 0) };
            var vt = new TextBlock { Text = v, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(vt, 1);
            g.Children.Add(kt); g.Children.Add(vt);
            DetFacts.Children.Add(g);
        }
        Fact(T("Size"), Format.Size(n.LogicalSize) + "  (" + F($"{Format.Size(n.DiskSize)} on disk") + ")");
        if (n is FileNode f)
        {
            var now = DateTime.UtcNow;
            Fact(T("Created"), Format.Date(f.Created) + " " + Format.Time(f.Created) + "  · " + Format.Age(f.Created, now));
            Fact(T("Modified"), Format.Date(f.Modified) + " " + Format.Time(f.Modified) + "  · " + Format.Age(f.Modified, now));
            Fact(T("Accessed"), Format.Date(f.Accessed) + " " + Format.Time(f.Accessed) + "  · " + Format.Age(f.Accessed, now));
            Fact(T("Attributes"), Format.Attributes(f.Attributes));
            var cls = AppConfig.Current.ClassOf(f.Extension);
            if (cls != null) Fact(T("Class"), cls.DisplayName);
            AddHardLinkFact(f);
        }
        else if (n is ContainerNode c)
        {
            int files, folders;
            lock (Tree.Sync) { files = c.FileCount; folders = c.FolderCount; }
            Fact(T("Contents"), P(files, "{0:N0} file", "{0:N0} files") + ", " + P(folders, "{0:N0} folder", "{0:N0} folders"));
            if (c.AccessDenied) Fact(T("Access"), T("denied"));
        }
        var tag = n.EffectiveTag;
        Fact(T("Tags"), tag == TagColor.None ? T("none") : TagText(tag));
        AddProgramFact(n);
        AddGameFact(n);
        AddDevFact(n);
        AddCloudFacts(n);
        AddKnowledgeFacts(n);
        AddSafetyFacts(n);
        var rows = new List<ChildRow>();
        if (n is ContainerNode cc)
        {
            lock (Tree.Sync)
            {
                long total = Math.Max(1, Model.SizeOf(cc));
                foreach (var k in cc.Children.Where(k => !k.IsSynthetic).OrderByDescending(k => Model.SizeOf(k)).Take(12))
                {
                    long s = Model.SizeOf(k);
                    rows.Add(new ChildRow(k.Name, Format.Size(s), (100.0 * s / total).ToString("0.0")));
                }
            }
        }
        DetChildren.ItemsSource = rows;
    }

    // ------------------------------------------------------------------ tooltip

    private void OnHover(TreemapItem? it, Point p)
    {
        _tipItem = it;
        var cfg = AppConfig.Current;
        if (it == null || it.Node is MissingDataNode) { Tip.IsOpen = false; _tipTimer.Stop(); return; }
        if (Tip.IsOpen && cfg.KeepTooltip) ShowTip();
        else { Tip.IsOpen = false; _tipTimer.Stop(); _tipTimer.Start(); }
    }

    private void ShowTip()
    {
        var it = _tipItem;
        if (it == null || !Map.IsMouseOver) { Tip.IsOpen = false; return; }
        var cfg = AppConfig.Current;
        var n = it.Node;
        var body = new System.Text.StringBuilder();
        body.Append(Format.Size(n.LogicalSize)).Append(" [").Append(F($"{Format.Size(it.Size)} on disk")).Append("]");
        string title = n switch { DriveNode d => d.FullPath, RestNode rn => P(rn.Count, "{0:N0} smaller item", "{0:N0} smaller items"), FreeSpaceNode => T("Free Space"), UnknownSpaceNode u => u.DisplayName, _ => n.Name };
        if (n is FileNode f)
        {
            var now = DateTime.UtcNow;
            void L(string k, DateTime t) { if (t == default) return; body.Append('\n').Append(k); if (cfg.ShowFileTooltipDateTime) body.Append(Format.Date(t)).Append(' ').Append(Format.Time(t)); if (cfg.ShowFileTooltipAge) body.Append(cfg.ShowFileTooltipDateTime ? " (" : "").Append(Format.Age(t, now)).Append(cfg.ShowFileTooltipDateTime ? ")" : ""); }
            if (cfg.ShowFileTooltipDateTime || cfg.ShowFileTooltipAge) { L(T("Creation:") + " ", f.Created); L(T("Modified:") + " ", f.Modified); L(T("Accessed:") + " ", f.Accessed); }
        }
        else if (n is ContainerNode c)
        {
            int count; lock (Tree.Sync) count = c.FileCount + c.FolderCount;
            body.Append('\n').Append(P(count, "Containing: {0:N0} item", "Containing: {0:N0} items"));
            if (!ReferenceEquals(c, Model.ZoomRoot)) body.Append('\n').Append(T("(double-click to go inside)"));
        }
        else if (n is RestNode) body.Append('\n').Append(T("Elements too small to be shown individually\n(zoom in or lower the minimum element size)"));
        var tag = n.EffectiveTag;
        if (Model.Diff is { } diff && !n.IsSynthetic)
        {
            if (diff.AddedNodes.Contains(n)) body.Append('\n').Append(T("Since snapshot:")).Append(' ').Append(T("new"));
            else if (diff.DeltaOf.TryGetValue(n, out long d) && d != 0) body.Append('\n').Append(T("Since snapshot:")).Append(' ').Append(d > 0 ? "+" : "\u2212").Append(Format.Size(Math.Abs(d)));
            else body.Append('\n').Append(T("Since snapshot:")).Append(' ').Append(T("unchanged"));
        }
        if (AppConfig.Current.ShowKnowledgeBadges && Core.Knowledge.KnowledgeBase.Classify(n) is { } km)
            body.Append('\n').Append(SafetyWord(km.Location.Safety)).Append(": ").Append(km.Location.Title).Append(km.Inherited ? " (" + F($"inside {System.IO.Path.GetFileName(km.MatchedPath)}") + ")" : "");
        if (ProgramOf(n) is { } prog) body.Append('\n').Append(T("Program:")).Append(' ').Append(prog.Claim.App.Name);
        if (n is FileNode hf && Tree.HardLinks.GroupOf(hf) is { } names)
            body.Append('\n').Append(T("Hard link:")).Append(' ').Append(ReferenceEquals(names[0], hf) ? F($"{names.Length} names, space counted here") : F($"{names.Length} names, space counted at {names[0].FullPath}"));
        if (DevProjects.ArtifactOf(n, Tree.Sync) is { } dev) body.Append('\n').Append(F($"Build output of {dev.Project} ({dev.Ecosystem}), rebuilt by {dev.Rebuild}"));
        if (GameOf(n) is { } game) body.Append('\n').Append(T("Game:")).Append(' ').Append(game.Game.Name).Append(game.Kind == GamePieceKind.Game ? "" : " (" + GamePieceText(game.Kind) + ")");
        if (!n.IsSynthetic && Core.Cloud.CloudFiles.SyncRootOf(n) is { } cloudRoot)
            body.Append('\n').Append(T("Cloud:")).Append(' ').Append(cloudRoot.Provider).Append(n is FileNode ? " — " + CloudStateText(Core.Cloud.CloudFiles.StateOf(n)).ToLowerInvariant() : " " + T("folder"));
        if (tag != TagColor.None) body.Append('\n').Append(T("Tags:")).Append(' ').Append(TagText(tag));
        TipTitle.Text = title;
        TipBody.Text = body.ToString();
        var pos = Mouse.GetPosition(Map);
        Tip.HorizontalOffset = pos.X + 16;
        Tip.VerticalOffset = pos.Y + 20;
        Tip.IsOpen = true;
    }

    // ------------------------------------------------------------------ shell menu, auto export



    // ------------------------------------------------------------------ snapshot comparison

    private CompareWindow? _compare;

    /// <summary>Command line "compare &lt;file&gt;": runs when the master scan is complete (immediately for snapshot views).</summary>
    public string? PendingCompare
    {
        get => _pendingCompare;
        set { _pendingCompare = value; if (value != null && (!Tree.IsLive || Tree.Root.ScanComplete)) Dispatcher.BeginInvoke(RunPendingCompare); }
    }
    private string? _pendingCompare;

    private void RunPendingCompare()
    {
        var f = _pendingCompare; _pendingCompare = null;
        if (f != null) CompareWithSnapshot(f);
    }

    /// <summary>Asks for a .sns file, diffs it against this view's root on a background thread and opens the comparison window.</summary>
    public void CompareWithSnapshot()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = F($"{Branding.Name} snapshot (*.sns)|*.sns|All files|*.*"), Title = T("Compare with snapshot") };
        if (Dialogs.Pick(dlg, Window.GetWindow(this)) != true) return;
        CompareWithSnapshot(dlg.FileName);
    }

    /// <summary>A snapshot comparison runs (autoclose waits for it).</summary>
    public bool CompareRunning { get; private set; }

    public void CompareWithSnapshot(string file)
    {
        bool unattended = MainWindow.Instance is { Unattended: true };
        if (!unattended) DisarmAuto();
        CompareRunning = true;
        var root = Model.ViewRoot is DriveNode { IsSnapshotFrame: true } f && f.ChildCount == 1 && f.Children[0] is ContainerNode inner ? inner : Model.ViewRoot;
        string currentPath = root.RealPath();
        var owner = Window.GetWindow(this);
        Cursor = Cursors.Wait;
        Task.Run(() =>
        {
            var baselineRoot = SnsFormat.Read(file);
            var aligned = TreeDiff.Align(baselineRoot, currentPath);
            if (aligned == null)
            {
                // the snapshot may be the smaller area: compare that part of the current tree instead
                var here = TreeDiff.Align(root, baselineRoot.FullPath);
                if (here == null)
                    throw new InvalidOperationException(F($"The snapshot covers {baselineRoot.FullPath}, which does not overlap this view ({currentPath})."));
                aligned = baselineRoot; root = here;
            }
            return TreeDiff.Compare(aligned, file, System.IO.File.GetLastWriteTimeUtc(file), root, Tree.Sync);
        }).ContinueWith(t =>
        {
            Cursor = null;
            CompareRunning = false;
            if (t.IsFaulted)
            {
                var msg = t.Exception?.GetBaseException().Message ?? "error";
                Log.Error("Compare failed: " + msg);
                // an unattended run has nobody to close a message box: the log says it
                if (!unattended) Dialogs.Show(owner, F($"Unable to compare with '{file}'") + "\n\n" + msg, T("Compare with snapshot"), MessageBoxButton.OK, MessageBoxImage.Error);
                else MainWindow.Instance?.AutoExportFinished(this);
                return;
            }
            var diff = t.Result;
            Log.Debug($"Compared with {file}: {diff.Entries.Count} changes, net {diff.Delta:N0} bytes");
            if (unattended)
            {
                Model.Diff = diff;
                MainWindow.Instance?.AutoExportFinished(this);
                return;
            }
            _compare?.Close();
            Model.Diff = diff;
            RefreshMap();
            _compare = new CompareWindow(this, diff) { Owner = owner };
            _compare.Closed += (_, _) => _compare = null;
            _compare.Show();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void RefreshMap() => Map.MarkDirty();

    /// <summary>Navigates the map to a node (zooms to its folder) and selects it.</summary>
    public void ShowNode(FsNode node)
    {
        var target = node as ContainerNode ?? node.Parent;
        if (target == null) return;
        if (node is ContainerNode c && c.Parent is ContainerNode p && !ReferenceEquals(c, Model.ViewRoot)) target = p;   // show the folder inside its parent
        if (!ReferenceEquals(target, Model.ZoomRoot)) Model.ZoomTo(target);
        Model.Selected = node;
        Map.MarkDirty();
        UpdateBreadcrumb(); UpdateDetails(); UpdateStatus(); StateChanged?.Invoke();
    }
    // ------------------------------------------------------------------ program footprint ("which program uses my disk?")

    private AppsWindow? _apps;
    private CancellationTokenSource? _appsCts;

    /// <summary>Command line "apps": opens the program window once the scan is complete (immediately for snapshot views).</summary>
    public bool PendingApps
    {
        get => _pendingApps;
        set { _pendingApps = value; if (value && (!Tree.IsLive || Tree.Root.ScanComplete)) Dispatcher.BeginInvoke(RunPendingApps); }
    }
    private bool _pendingApps;

    private void RunPendingApps()
    {
        if (!_pendingApps) return;
        _pendingApps = false;
        ShowAppFootprint(null, fromUser: false);
    }

    /// <summary>Owner of a node by path (null until the installed programs have been read).</summary>
    private static (AppClaim Claim, string Folder)? ProgramOf(FsNode n)
    {
        if (n.IsSynthetic || AppCatalog.Loaded is not { } catalog) return null;
        try { return catalog.OwnerOf(n.RealPath()); } catch { return null; }
    }

    /// <summary>Matches this view's folders to the installed programs on a background thread and shows (or refreshes) the window.</summary>
    public async void ShowAppFootprint(InstalledApp? select = null, bool fromUser = true)
    {
        if (fromUser) DisarmAuto();
        _appsCts?.Cancel();
        var cts = _appsCts = new CancellationTokenSource();
        var owner = Window.GetWindow(this);
        Cursor = Cursors.Wait;
        ShowNote(T("Matching folders to installed programs…"), 30);
        try
        {
            var root = Model.ViewRoot;
            var sync = Tree.Sync;
            var catalog = await AppCatalog.GetAsync(reload: fromUser);   // programs may have been (un)installed since the last look
            var result = await Task.Run(() => AppFootprint.Compute(root, catalog, n => n.DiskSize, cts.Token, sync), cts.Token);
            if (cts.IsCancellationRequested) return;
            Log.Debug($"Program footprint of {result.RootPath}: {result.Apps.Count} programs, {Format.Size(result.AppBytes)}, {result.Unlinked.Count} unlinked folders ({result.Elapsed.TotalMilliseconds:0} ms)");
            ShowNote(P(result.Apps.Count(a => !a.App.IsComponent), "{1} used by {0} program", "{1} used by {0} programs", Format.Size(result.AppBytes)), 5);
            if (_apps == null)
            {
                _apps = new AppsWindow(this) { Owner = owner };
                _apps.Closed += (_, _) => _apps = null;
                _apps.Show();
            }
            _apps.SetResult(result, Tree.IsMasterScanning || Model.IsSubScanning);
            if (select != null) _apps.Select(select);
            _apps.Activate();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Program footprint failed: " + ex.Message);
            ShowNote(T("Could not match folders to programs:") + " " + ex.Message);
        }
        finally { if (ReferenceEquals(_appsCts, cts)) Cursor = null; }
    }

    /// <summary>"Program" row of the details panel: the owning program as a link to its footprint.</summary>
    private void AddProgramFact(FsNode n)
    {
        if (ProgramOf(n) is not { } prog) return;
        var app = prog.Claim.App;
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = T("Program"), Style = (Style)FindResource("Secondary") });
        var link = new Button { Content = new TextBlock { Text = app.Name, TextTrimming = TextTrimming.CharacterEllipsis }, Style = (Style)FindResource("LinkButton"), HorizontalAlignment = HorizontalAlignment.Left,
                                ToolTip = F($"{AppPiece.KindLabel(prog.Claim.Kind)} of {app.Name} ({prog.Folder})") + "\n" + T("Click: everything this program uses") };
        link.Click += (_, _) => ShowAppFootprint(app);
        Grid.SetColumn(link, 1);
        g.Children.Add(link);
        DetFacts.Children.Add(g);
    }

    // ------------------------------------------------------------------ game library ("which games take my space?")

    private GamesWindow? _games;
    private CancellationTokenSource? _gamesCts;

    /// <summary>Command line "games": opens the games window once the scan is complete (immediately for snapshot views).</summary>
    public bool PendingGames
    {
        get => _pendingGames;
        set { _pendingGames = value; if (value && (!Tree.IsLive || Tree.Root.ScanComplete)) Dispatcher.BeginInvoke(RunPendingGames); }
    }
    private bool _pendingGames;

    private void RunPendingGames()
    {
        if (!_pendingGames) return;
        _pendingGames = false;
        ShowGames(null, fromUser: false);
    }

    /// <summary>The game a node belongs to (null until the launchers have been read).</summary>
    private static (GameInfo Game, GamePieceKind Kind, string Folder)? GameOf(FsNode n)
    {
        if (n.IsSynthetic || GameLibrary.Loaded is not { Games.Count: > 0 } lib) return null;
        try { return lib.OwnerOf(n.RealPath()); } catch { return null; }
    }

    private static string GamePieceText(GamePieceKind k) => new GamePiece { Path = "", Kind = k }.KindText.ToLowerInvariant();

    /// <summary>Finds every game's folders in this view on a background thread and shows (or refreshes) the games window.</summary>
    public async void ShowGames(GameInfo? select = null, bool fromUser = true)
    {
        if (fromUser) DisarmAuto();
        _gamesCts?.Cancel();
        var cts = _gamesCts = new CancellationTokenSource();
        var owner = Window.GetWindow(this);
        Cursor = Cursors.Wait;
        ShowNote(T("Reading the game launchers…"), 30);
        try
        {
            var root = Model.ViewRoot;
            var sync = Tree.Sync;
            var lib = await GameLibrary.GetAsync(reload: fromUser);   // games may have been (un)installed or played since the last look
            var result = await Task.Run(() => GameAudit.Compute(root, lib, n => n.DiskSize, cts.Token, sync), cts.Token);
            if (cts.IsCancellationRequested) return;
            Log.Debug($"Games of {result.RootPath}: {result.Games.Count} games, {result.Leftovers.Count} leftovers ({result.Elapsed.TotalMilliseconds:0} ms)");
            ShowNote(P(result.Games.Count(g => !g.Game.IsTool), "{0} game, {1}", "{0} games, {1}", Format.Size(result.Games.Where(g => !g.Game.IsTool).Sum(g => g.ShownBytes))), 5);
            if (_games == null)
            {
                _games = new GamesWindow(this) { Owner = owner };
                _games.Closed += (_, _) => _games = null;
                _games.Show();
            }
            _games.SetResult(result, Tree.IsMasterScanning || Model.IsSubScanning);
            if (select != null) _games.Select(select);
            _games.Activate();
            RefreshMap();   // labels of numbered Steam folders
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Game library failed: " + ex.Message);
            ShowNote(T("Could not read the game library:") + " " + ex.Message);
        }
        finally { if (ReferenceEquals(_gamesCts, cts)) Cursor = null; }
    }

    /// <summary>"Game" row of the details panel: the game a folder belongs to, as a link to the games window.</summary>
    private void AddGameFact(FsNode n)
    {
        if (GameOf(n) is not { } game) return;
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = T("Game"), Style = (Style)FindResource("Secondary") });
        var info = game.Game;
        string text = info.Name + (game.Kind == GamePieceKind.Game ? "" : " (" + GamePieceText(game.Kind) + ")");
        string played = info.LastPlayed is { } lp ? F($"last played {Format.Date(lp)}") : info.Store == GameStore.Steam ? T("never played") : "";
        var link = new Button { Content = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis }, Style = (Style)FindResource("LinkButton"), HorizontalAlignment = HorizontalAlignment.Left,
                                ToolTip = F($"{info.StoreText} game") + (played.Length > 0 ? ", " + played : "") + (info.Playtime is { } pt ? ", " + F($"{GameAudit.PlaytimeText(pt)} in total") : "") + "\n" + T("Click: all its folders and the other games") };
        link.Click += (_, _) => ShowGames(info);
        Grid.SetColumn(link, 1);
        g.Children.Add(link);
        DetFacts.Children.Add(g);
    }

    // ------------------------------------------------------------------ unfinished scans

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScanJob, object> Kept = new();

    /// <summary>A stopped scan is kept to continue it later (even after ByteScape closes); a complete one needs nothing kept.</summary>
    private void KeepOrForgetUnfinished()
    {
        if (!Tree.IsLive || Tree.MasterScan is not { } job) return;
        lock (Kept) { if (Kept.TryGetValue(job, out _)) return; Kept.Add(job, this); }
        var tree = Tree;
        // at once (three small files): a save when ByteScape closes right after must not be deleted by a late delete
        if (job.State == ScanState.Completed) ScanResume.Delete(tree.Root.FullPath);
        else if (job.State == ScanState.Aborted && AppConfig.Current.KeepUnfinishedScans && MainWindow.Instance is not { Unattended: true })
            Task.Run(() => ScanResume.Save(tree));
    }

    // ------------------------------------------------------------------ disk forecast ("when will it be full?")

    private ForecastWindow? _forecast;
    /// <summary>Complete scans already measured (every view of a tree hears the same scan end).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScanJob, object> Measured = new();

    /// <summary>Command line "forecast": opens the forecast once the scan is complete.</summary>
    public bool PendingForecast
    {
        get => _pendingForecast;
        set { _pendingForecast = value; if (value && (!Tree.IsLive || Tree.Root.ScanComplete)) Dispatcher.BeginInvoke(RunPendingForecast); }
    }
    private bool _pendingForecast;

    private void RunPendingForecast()
    {
        if (!_pendingForecast) return;
        _pendingForecast = false;
        ShowForecast(fromUser: false);
    }

    /// <summary>After a complete scan of a whole drive: a measurement and the sizes of its big folders go to the history.</summary>
    private void RecordScan()
    {
        if (DiskHistory.Current is not { } history || !Tree.IsLive || !Tree.Root.IsDrive || !Tree.Root.ScanComplete || Tree.MasterScan is not { } job) return;
        lock (Measured) { if (Measured.TryGetValue(job, out _)) return; Measured.Add(job, this); }
        var root = Tree.Root;
        var sync = Tree.Sync;
        Task.Run(() =>
        {
            var drive = root.FullPath.Substring(0, 2).ToUpperInvariant();
            var now = DateTime.UtcNow;
            history.Record(new DiskSample(now, drive, root.TotalBytes, root.FreeBytes), TimeSpan.FromMinutes(10));
            history.SaveSummary(drive, now, DiskHistory.SummaryOf(root, sync));
        });
    }

    /// <summary>The drive of this view: its measured trend, what the scan says was added lately, what grew since an earlier scan.</summary>
    public async void ShowForecast(bool fromUser = true)
    {
        if (fromUser) DisarmAuto();
        var owner = Window.GetWindow(this);
        string path;
        try { path = Model.ViewRoot.RealPath(); } catch { path = Model.ViewRoot.FullPath; }
        if (path.Length < 2 || path[1] != ':')
        {
            ShowNote(T("The disk forecast is for local drives."));
            return;
        }
        var drive = path.Substring(0, 2).ToUpperInvariant();
        Cursor = Cursors.Wait;
        try
        {
            var root = Model.ViewRoot;
            var sync = Tree.Sync;
            var live = Tree.IsLive;
            var driveRoot = Tree.Root.IsDrive ? Tree.Root : null;
            var history = DiskHistory.Current;
            var (result, added, grew, since) = await Task.Run(() =>
            {
                var now = DateTime.UtcNow;
                var info = new DriveInfo(drive);
                var r = DiskForecast.FromHistory(history?.Load() ?? new List<DiskSample>(), drive, info.TotalSize, info.TotalFreeSpace, now);
                var a = live ? DiskForecast.RecentlyAdded(root, TimeSpan.FromDays(30), now, sync) : ((long, int, List<(FsNode, string, long)>)?)null;
                List<(string, long, long)>? g = null;
                DateTime? at = null;
                // the oldest remembered scan of at least a week ago, else the oldest one before this scan
                if (history != null && driveRoot != null && driveRoot.ScanComplete)
                {
                    var sums = history.Summaries(drive).Where(s => s.Utc < now.AddHours(-1)).ToList();
                    var pick = sums.Where(s => s.Utc <= now.AddDays(-7)).Select(s => ((DateTime, string)?)s).LastOrDefault() ?? sums.Select(s => ((DateTime, string)?)s).FirstOrDefault();
                    if (pick is { } p) { g = DiskForecast.GrewSince(DiskHistory.ReadSummary(p.Item2), driveRoot, sync); at = p.Item1; }
                }
                return (r, a, g, at);
            });
            Log.Debug($"Forecast {drive}: {result.Samples} measurements over {result.SpanDays:0} days, {(result.HasTrend ? DiskForecast.Rate(result.BytesPerDay) : "no trend")}");
            if (_forecast == null)
            {
                _forecast = new ForecastWindow(this) { Owner = owner };
                _forecast.Closed += (_, _) => _forecast = null;
                _forecast.Show();
            }
            string note = history == null
                ? T("Measuring is turned off (Configuration → Behavior).")
                : F($"Measured when {Branding.Name} starts and after each complete drive scan ({history.FilePath}). For a measurement every day, add the copied command to Task Scheduler.");
            _forecast.SetResult(result, added, grew, since, note);
            _forecast.Activate();
        }
        catch (Exception ex)
        {
            Log.Error("Disk forecast failed: " + ex.Message);
            ShowNote(T("Could not make the forecast:") + " " + ex.Message);
        }
        finally { Cursor = null; }
    }

    // ------------------------------------------------------------------ developer leftovers ("old node_modules")

    private DevWindow? _dev;
    private CancellationTokenSource? _devCts;

    /// <summary>Command line "dev": opens the developer leftovers window once the scan is complete (immediately for snapshot views).</summary>
    public bool PendingDev
    {
        get => _pendingDev;
        set { _pendingDev = value; if (value && (!Tree.IsLive || Tree.Root.ScanComplete)) Dispatcher.BeginInvoke(RunPendingDev); }
    }
    private bool _pendingDev;

    private void RunPendingDev()
    {
        if (!_pendingDev) return;
        _pendingDev = false;
        ShowDev(null, fromUser: false);
    }

    /// <summary>Finds the projects and their rebuildable folders in this view on a background thread and shows (or refreshes) the window.</summary>
    public async void ShowDev(string? select = null, bool fromUser = true)
    {
        if (fromUser) DisarmAuto();
        _devCts?.Cancel();
        var cts = _devCts = new CancellationTokenSource();
        var owner = Window.GetWindow(this);
        Cursor = Cursors.Wait;
        ShowNote(T("Looking for projects…"), 30);
        try
        {
            var root = Model.ViewRoot;
            var sync = Tree.Sync;
            var result = await Task.Run(() => DevProjects.Compute(root, n => n.DiskSize, cts.Token, sync), cts.Token);
            if (cts.IsCancellationRequested) return;
            Log.Debug($"Developer leftovers of {result.RootPath}: {result.Projects.Count} projects, {Format.Size(result.ArtifactBytes)} ({result.Elapsed.TotalMilliseconds:0} ms)");
            ShowNote(P(result.Projects.Count, "{1} rebuildable in {0} project", "{1} rebuildable in {0} projects", Format.Size(result.ArtifactBytes)), 5);
            if (_dev == null)
            {
                _dev = new DevWindow(this) { Owner = owner };
                _dev.Closed += (_, _) => _dev = null;
                _dev.Show();
            }
            _dev.SetResult(result, Tree.IsMasterScanning || Model.IsSubScanning);
            if (select != null) _dev.Select(select);
            _dev.Activate();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Developer leftovers failed: " + ex.Message);
            ShowNote(T("Could not look for projects:") + " " + ex.Message);
        }
        finally { if (ReferenceEquals(_devCts, cts)) Cursor = null; }
    }

    /// <summary>"Hard link" row of the details panel: the file's other names and where its space is counted.</summary>
    private void AddHardLinkFact(FileNode f)
    {
        if (Tree.HardLinks.GroupOf(f) is not { } names) return;
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = T("Hard link"), Style = (Style)FindResource("Secondary") });
        bool here = ReferenceEquals(names[0], f);
        var owner = names[0];
        var link = new Button
        {
            Content = new TextBlock { Text = here ? F($"{names.Length} names, space counted here") : F($"{names.Length} names, space counted at {owner.Name}"), TextTrimming = TextTrimming.CharacterEllipsis },
            Style = (Style)FindResource("LinkButton"), HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = F($"One file with {names.Length} names; its {Format.Size(Math.Max(owner.DiskSize, f.DiskSize))} on disk are counted once, at the first name:") + "\n"
                      + string.Join("\n", names.Select(x => x.FullPath)) + (here ? "" : "\n" + T("Click: show the name where it is counted")),
        };
        if (!here) link.Click += (_, _) => ShowNode(owner);
        Grid.SetColumn(link, 1);
        g.Children.Add(link);
        DetFacts.Children.Add(g);
    }

    /// <summary>"Project" row of the details panel for a rebuildable folder (node_modules, bin, target...).</summary>
    private void AddDevFact(FsNode n)
    {
        if (DevProjects.ArtifactOf(n, Tree.Sync) is not { } dev) return;
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = T("Project"), Style = (Style)FindResource("Secondary") });
        var link = new Button { Content = new TextBlock { Text = dev.Project + " (" + dev.Ecosystem + ")", TextTrimming = TextTrimming.CharacterEllipsis }, Style = (Style)FindResource("LinkButton"), HorizontalAlignment = HorizontalAlignment.Left,
                                ToolTip = T("Rebuildable:") + " " + dev.Rebuild + "\n" + T("Click: this project and the other developer leftovers") };
        link.Click += (_, _) => ShowDev(dev.ProjectPath);
        Grid.SetColumn(link, 1);
        g.Children.Add(link);
        DetFacts.Children.Add(g);
    }

    // ------------------------------------------------------------------ knowledge base ("what is this folder?")

    /// <summary>"Red, Blue" in the interface language.</summary>
    internal static string TagText(TagColor t) => string.Join(", ", new[] { TagColor.Red, TagColor.Yellow, TagColor.Green, TagColor.Blue }
        .Where(c => (t & c) != 0).Select(c => c switch { TagColor.Red => T("Red"), TagColor.Yellow => T("Yellow"), TagColor.Green => T("Green"), _ => T("Blue") }));

    private static string SafetyWord(Core.Knowledge.Safety s) => s switch
    {
        Core.Knowledge.Safety.Regenerable => T("Reclaimable"),
        Core.Knowledge.Safety.SafeWhenClosed => T("Reclaimable (close the app first)"),
        Core.Knowledge.Safety.Caution => T("Removable with care"),
        _ => T("Protected"),
    };

    private static Brush SafetyBrush(Core.Knowledge.Safety s) => new SolidColorBrush(s switch
    {
        Core.Knowledge.Safety.Regenerable => SafetyUi.LevelColor(Core.Cleanup.SafetyLevel.Safe),
        Core.Knowledge.Safety.SafeWhenClosed => SafetyUi.LevelColor(Core.Cleanup.SafetyLevel.SafeWhenClosed),
        Core.Knowledge.Safety.Caution => SafetyUi.LevelColor(Core.Cleanup.SafetyLevel.Caution),
        _ => SafetyUi.LevelColor(Core.Cleanup.SafetyLevel.Protected),
    });

    /// <summary>"What is this?" block of the details panel: title, safety, description, consequence, cleanup command.</summary>
    private static readonly Dictionary<string, string> AiFolderCache = new(StringComparer.OrdinalIgnoreCase);

    private void AddKnowledgeFacts(FsNode n)
    {
        var km = Core.Knowledge.KnowledgeBase.Classify(n);
        if (km == null)
        {
            if (AppConfig.Current.AiEnabled && n is ContainerNode folder && !folder.IsSynthetic) AddAiFolderBlock(folder);
            return;
        }
        var loc = km.Location;
        var box = new Border { Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(10), CornerRadius = new CornerRadius(4), Background = (Brush)FindResource("LayerAlt"), BorderBrush = (Brush)FindResource("Border"), BorderThickness = new Thickness(1) };
        var sp = new StackPanel();
        var head = new DockPanel();
        var pill = SafetyUi.Pill(SafetyBrush(loc.Safety), SafetyWord(loc.Safety));
        DockPanel.SetDock(pill, Dock.Left);
        head.Children.Add(pill);
        head.Children.Add(new TextBlock { Text = loc.Title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(head);
        if (km.Inherited) sp.Children.Add(new TextBlock { Text = F($"Part of {km.MatchedPath}"), Style = (Style)FindResource("Secondary"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        sp.Children.Add(new TextBlock { Text = loc.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        sp.Children.Add(new TextBlock { Text = F($"If deleted: {loc.Consequence}"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Style = (Style)FindResource("Secondary") });
        if (loc.Cleanup.Length > 0)
        {
            var howLabel = new TextBlock { Text = T("How to clean:"), Style = (Style)FindResource("Secondary"), Margin = new Thickness(0, 8, 0, 2) };
            sp.Children.Add(howLabel);
            var row = new DockPanel();
            var copy = new Button { Content = T("Copy"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, ToolTip = T("Copy the command / hint to the clipboard") };
            copy.Click += (_, _) => { try { Dialogs.Copy(loc.Cleanup); } catch { } };
            DockPanel.SetDock(copy, Dock.Right);
            row.Children.Add(copy);
            var how = new TextBox { Text = loc.Cleanup, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), FontFamily = new FontFamily("Consolas, Segoe UI"), FontSize = 12 };
            A11y.Link(how, howLabel);
            row.Children.Add(how);
            sp.Children.Add(row);
        }
        box.Child = sp;
        DetFacts.Children.Add(box);
    }

    /// <summary>"What is this?" for folders outside the knowledge base: asks the configured local model (names and sizes only), answer cached per path.</summary>
    private void AddAiFolderBlock(ContainerNode folder)
    {
        var box = new Border { Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(10), CornerRadius = new CornerRadius(4), Background = (Brush)FindResource("LayerAlt"), BorderBrush = (Brush)FindResource("Border"), BorderThickness = new Thickness(1) };
        var sp = new StackPanel();
        var answer = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        if (AiFolderCache.TryGetValue(folder.FullPath, out var cached))
        {
            sp.Children.Add(new TextBlock { Text = T("Local model says (unverified)"), FontWeight = FontWeights.SemiBold });
            answer.Text = cached;
            sp.Children.Add(answer);
        }
        else
        {
            var ask = new Button { Content = T("Ask the local model what this folder is"), Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left };
            sp.Children.Add(ask);
            sp.Children.Add(answer);
            ask.Click += async (_, _) =>
            {
                ask.IsEnabled = false; answer.Text = T("Asking…");
                try
                {
                    var lm = Core.Ai.LocalModel.FromConfig(AppConfig.Current);
                    var lang = Loc.Languages.FirstOrDefault(l => l.Code == Loc.Language)?.EnglishName ?? "English"; // the model answers in the interface language
                    var text = await Core.Ai.Assistant.DescribeFolderAsync(lm, folder, Tree.Sync, lang, CancellationToken.None);
                    AiFolderCache[folder.FullPath] = text;
                    answer.Text = text;
                    ask.Content = T("Local model says (unverified)"); ask.IsEnabled = false;
                }
                catch (Exception ex) { answer.Text = T("Local model not reachable:") + " " + ex.Message; ask.IsEnabled = true; }
            };
        }
        sp.Children.Add(new TextBlock { Text = T("Generated by the model you configured; may be wrong."), Style = (Style)FindResource("Secondary"), FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
        box.Child = sp;
        DetFacts.Children.Add(box);
    }

    /// <summary>Status line: total of known caches below the view root, refreshed off thread when the tree changed.</summary>
    private void UpdateReclaimText()
    {
        if (!Model.IsSummarizing && (!Tree.IsMasterScanning || (DateTime.UtcNow - _lastReclaimRequest).TotalSeconds > 3))
        {
            _lastReclaimRequest = DateTime.UtcNow;
            Model.RequestReclaimSummary();
        }
        UpdateCloudText();
        if (!AppConfig.Current.ShowKnowledgeBadges) { ReclaimText.Visibility = Visibility.Collapsed; return; }
        var r = Model.Reclaim;
        ReclaimText.Visibility = r.TotalBytes > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (r.TotalBytes > 0)
        {
            ReclaimText.Content = "♻ " + F($"{Format.Size(r.TotalBytes)} reclaimable");
            ReclaimText.ToolTip = string.Join("\n", r.Items.Take(8).Select(i => Format.Size(i.Bytes).PadLeft(9) + "  " + i.Location.Title + "  —  " + i.Node.FullPath)) + (r.Items.Count > 8 ? "\n…" : "") + "\n\n" + T("Click: show only reclaimable files (filter :reclaimable)");
        }
    }

    private DateTime _lastReclaimRequest = DateTime.MinValue;

    // ------------------------------------------------------------------ cloud files (OneDrive / Dropbox Files On-Demand)

    /// <summary>Status line: downloaded copies of cloud files in this view that "Free up space" could remove.</summary>
    private void UpdateCloudText()
    {
        var c = Model.Cloud;
        CloudText.Visibility = c.Roots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (c.Roots.Count == 0) return;
        CloudText.Content = c.LocalBytes > 0 ? "☁ " + F($"{Format.Size(c.LocalBytes)} can be freed ({c.Providers})") : "☁ " + F($"{c.Providers}: nothing to free");
        CloudText.ToolTip = string.Join("\n\n", c.Roots.Select(r =>
                r.Root.Provider + "  —  " + r.Node.RealPath() + "\n"
                + "  " + F($"Local copies (Free up space can remove them): {Format.Size(r.LocalBytes)} in {Files(r.LocalFiles)}") + "\n"
                + "  " + F($"Always kept on this device: {Format.Size(r.KeptBytes)} in {Files(r.KeptFiles)}") + "\n"
                + "  " + F($"Online-only (no space here): {Format.Size(r.OnlineBytes)} in {Files(r.OnlineFiles)}")))
            + "\n\n" + T("Click: show only the local copies (filter :freeable). Free up space: right click > Windows menu.");
    }

    private static string Files(int n) => P(n, "{0:N0} file", "{0:N0} files");

    private void CloudText_Click(object sender, RoutedEventArgs e)
    {
        FilterBox.Text = FilterBox.Text.Trim().Length == 0 ? ":freeable" : FilterBox.Text.Trim().TrimEnd(';') + ";:freeable";
        ApplyFilter();
    }

    private static string CloudStateText(Core.Cloud.CloudState s) => s switch
    {
        Core.Cloud.CloudState.OnlineOnly => T("Online-only"),
        Core.Cloud.CloudState.AlwaysKept => T("Always kept on this device"),
        Core.Cloud.CloudState.LocalCopy => T("Local copy"),
        _ => "",
    };

    /// <summary>Cloud block of the details panel: where a cloud file's data is, or how a cloud folder's files are held.</summary>
    private void AddCloudFacts(FsNode n)
    {
        if (n.IsSynthetic || Core.Cloud.CloudFiles.SyncRootOf(n) is not { } root) return;
        var box = new Border { Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(10), CornerRadius = new CornerRadius(4), Background = (Brush)FindResource("LayerAlt"), BorderBrush = (Brush)FindResource("Border"), BorderThickness = new Thickness(1) };
        var sp = new StackPanel();
        var head = new DockPanel();
        var pill = SafetyUi.Pill((Brush)FindResource("Accent"), "☁ " + root.Provider);
        DockPanel.SetDock(pill, Dock.Left);
        head.Children.Add(pill);
        string body;
        if (n is FileNode f)
        {
            var state = Core.Cloud.CloudFiles.StateOf(f);
            head.Children.Add(new TextBlock { Text = CloudStateText(state), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            body = state switch
            {
                Core.Cloud.CloudState.OnlineOnly => F($"Only a placeholder is on this disk; the data is in {root.Provider}. It uses no space here and is downloaded when opened."),
                Core.Cloud.CloudState.AlwaysKept => T("Kept on this disk on purpose (\"Always keep on this device\"). \"Free up space\" in the Windows menu turns it back into an online-only file."),
                _ => F($"A downloaded copy of a {root.Provider} file. \"Free up space\" in the Windows menu removes it from this disk and keeps it in the cloud and on your other devices."),
            };
        }
        else
        {
            head.Children.Add(new TextBlock { Text = T("Cloud folder"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var c = (ContainerNode)n;
            int files; lock (Tree.Sync) files = c.FileCount;
            if (files <= 300_000)
            {
                var s = Core.Cloud.CloudFiles.CountIn(c, root, CancellationToken.None, Tree.Sync);
                body = F($"Local copies: {Format.Size(s.LocalBytes)} ({Files(s.LocalFiles)}) — \"Free up space\" can remove them.") + "\n"
                       + F($"Always kept on this device: {Format.Size(s.KeptBytes)} ({Files(s.KeptFiles)}).") + "\n"
                       + F($"Online-only: {Format.Size(s.OnlineBytes)} ({Files(s.OnlineFiles)}), none of it on this disk.");
            }
            else body = T("Totals for the whole cloud folder are in the status line (☁).");
        }
        sp.Children.Add(head);
        sp.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        sp.Children.Add(new TextBlock { Text = F($"Deleting a cloud file here deletes it everywhere; freeing up space does not. {Branding.Name} changes nothing itself."), Style = (Style)FindResource("Secondary"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        box.Child = sp;
        DetFacts.Children.Add(box);
    }

    private void ReclaimText_Click(object sender, RoutedEventArgs e)
    {
        FilterBox.Text = FilterBox.Text.Trim().Length == 0 ? ":reclaimable" : FilterBox.Text.Trim().TrimEnd(';') + ";:reclaimable";
        ApplyFilter();
    }
    // ------------------------------------------------------------------ context menu

    /// <summary>Right click on an element: ByteScape actions first, the Windows shell menu as the last entry.</summary>
    internal ContextMenu BuildContextMenu(FsNode node, Point screen)
    {

        string path = node.FullPath;
        bool isFolder = node is ContainerNode;
        bool exists = Tree.IsLive && (isFolder ? Directory.Exists(path) : File.Exists(path));
        var cfg = AppConfig.Current;

        var menu = new ContextMenu { PlacementTarget = Map, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

        // header: what was clicked
        menu.Items.Add(new MenuItem
        {
            Header = new TextBlock { Text = node.Name.Length > 48 ? node.Name[..46] + "…" : node.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
            IsEnabled = false,
            Icon = MenuIcon(isFolder ? "" : ""),
        });
        menu.Items.Add(new Separator());

        // Explorer
        var open = Item(T("Open in Explorer"), "", () => OpenInExplorer(node), "Enter");
        open.FontWeight = FontWeights.SemiBold;
        open.IsEnabled = exists;
        menu.Items.Add(open);
        if (!isFolder)
        {
            var run = Item(T("Open"), "", () => Start(path));
            run.IsEnabled = exists;
            menu.Items.Add(run);
        }
        var props = Item(T("Properties"), "", () => ShellExecute(path, "properties"));
        props.IsEnabled = exists;
        menu.Items.Add(props);
        menu.Items.Add(Item(T("Copy path"), "", () => { try { Dialogs.Copy(path); } catch { } }, "Ctrl+C"));
        if (Core.Knowledge.KnowledgeBase.Classify(node) is { } known)
        {
            var what = Item(T("What is this?") + "  —  " + known.Location.Title, "", () => { Model.Selected = node; if (!DetailsVisible) ToggleDetails(); UpdateDetails(); MainWindow.Instance?.UpdateToolbar(); });
            menu.Items.Add(what);
        }
        if (Core.Cloud.CloudFiles.SyncRootOf(node) is { } cloud)
        {
            // the provider's own "Free up space" / "Always keep on this device" live in the Windows menu
            var state = Core.Cloud.CloudFiles.StateOf(node);
            string what = node is FileNode ? CloudStateText(state).ToLowerInvariant() : T("cloud folder");
            var ci = Item(cloud.Provider + " (" + what + "): " + T("free up space / always keep…"), "", () => ShowShellMenu(path, screen));
            ci.IsEnabled = exists;
            ci.ToolTip = F($"Opens the Windows menu, where {cloud.Provider} offers \"Free up space\" and \"Always keep on this device\"");
            menu.Items.Add(ci);
        }
        if (ProgramOf(node) is { } prog)
        {
            var app = prog.Claim.App;
            menu.Items.Add(Item(T("Program:") + " " + (app.Name.Length > 40 ? app.Name[..38] + "…" : app.Name) + "  —  " + T("everything it uses"), "", () => ShowAppFootprint(app)));
        }
        if (GameOf(node) is { } game)
        {
            var g = game.Game;
            menu.Items.Add(Item(T("Game:") + " " + (g.Name.Length > 40 ? g.Name[..38] + "…" : g.Name) + "  —  " + T("library details"), "", () => ShowGames(g)));
        }
        if (DevProjects.ArtifactOf(node, Tree.Sync) is { } devArt)
            menu.Items.Add(Item(T("Project:") + " " + (devArt.Project.Length > 40 ? devArt.Project[..38] + "…" : devArt.Project) + "  —  " + T("developer leftovers"), "\uE943", () => ShowDev(devArt.ProjectPath)));
        menu.Items.Add(new Separator());

        // navigation
        if (node is ContainerNode c)
        {
            var zoom = Item(T("Zoom into"), "", () => ZoomTo(c));
            zoom.IsEnabled = !ReferenceEquals(c, Model.ZoomRoot);
            menu.Items.Add(zoom);
        }
        else if (node.Parent is ContainerNode parent)
        {
            var zoom = Item(T("Zoom into folder"), "", () => ZoomTo(parent));
            zoom.IsEnabled = !ReferenceEquals(parent, Model.ZoomRoot);
            menu.Items.Add(zoom);
        }
        var up = Item(T("Go upper level"), "", GoUp, "Ctrl+Up");
        up.IsEnabled = CanGoUp;
        menu.Items.Add(up);
        if (isFolder && Tree.IsLive)
            menu.Items.Add(Item(T("Compare with another folder…"), "\uE8AB", () => CompareWithFolder((ContainerNode)node)));
        if (isFolder && Tree.IsLive)
        {
            var folder = (ContainerNode)node;
            var sub = Item(T("Scan this folder now"), "", () =>
            {
                DisarmAuto();
                if (ReferenceEquals(folder, Model.ZoomRoot)) { Model.StartSubScan(); StateChanged?.Invoke(); return; }
                Map.AnimateZoom(folder, zoomIn: folder.Level > Model.ZoomRoot.Level, () => { Model.ZoomTo(folder); Model.StartSubScan(); StateChanged?.Invoke(); });
            });
            sub.IsEnabled = !Model.IsSubScanning && !Tree.IsMasterScanning && !ReferenceEquals(folder, Model.ViewRoot);
            menu.Items.Add(sub);
        }
        menu.Items.Add(new Separator());

        // tags
        var tags = new MenuItem { Header = T("Tag"), Icon = MenuIcon("") };
        foreach (var (color, label, key) in new[] { (TagColor.Red, T("Red"), "Ctrl+1"), (TagColor.Yellow, T("Yellow"), "Ctrl+2"), (TagColor.Green, T("Green"), "Ctrl+3"), (TagColor.Blue, T("Blue"), "Ctrl+4") })
        {
            var swatch = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(ViewModels.ConfigViewModel.ToWpf(TreemapStyle.TagBrush(color, cfg))), BorderBrush = (Brush)FindResource("BorderStrong"), BorderThickness = new Thickness(1) };
            var mi = new MenuItem { Header = label, Icon = swatch, InputGestureText = key, IsCheckable = true, IsChecked = (node.Tag & color) != 0 };
            var col = color;
            mi.Click += (_, _) => TagNode(node, col);
            tags.Items.Add(mi);
        }
        tags.Items.Add(new Separator());
        var clear = Item(T("Clear tags in this zoom"), "", ClearTags, "Ctrl+0");
        tags.Items.Add(clear);
        menu.Items.Add(tags);
        string ext = (node as FileNode)?.Extension ?? string.Empty;
        menu.Items.Add(Item(F($"Filter by this extension (*.{ext})"), "\uE71C", () => SetFilter("*." + ext), enabled: ext.Length > 0));
        menu.Items.Add(new Separator());

        // deleting (live file systems only): the verdict, then the commands
        if (Tree.IsLive)
        {
            AddDeleteItems(menu, node);
            menu.Items.Add(new Separator());
        }

        // Windows shell menu (live file systems only)
        var shell = Item(T("Windows menu…"), "", () => ShowShellMenu(path, screen));
        shell.IsEnabled = exists;
        menu.Items.Add(shell);

        return menu;
    }

    private void ShowContextMenu(FsNode node, Point screen)
    {
        Tip.IsOpen = false;
        _tipTimer.Stop();
        var menu = BuildContextMenu(node, screen);
        menu.Closed += (_, _) => Map.Focus();
        Dialogs.OpenMenu(menu);
    }

    private static Glyph MenuIcon(string glyph) => new()
    {
        Text = glyph,
        FontSize = 14,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = (Brush)Application.Current.FindResource("TextSecondary"),
    };

    private static MenuItem Item(string header, string glyph, Action action, string? gesture = null, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, Icon = MenuIcon(glyph), InputGestureText = gesture ?? string.Empty, IsEnabled = enabled };
        mi.Click += (_, _) => action();
        return mi;
    }

    private void TagNode(FsNode node, TagColor color)
    {
        if (Model.IsFiltering) { Dialogs.Show(Window.GetWindow(this), T("The filter is still being applied. Try again in a moment."), T("Tag")); return; }
        Model.ToggleTag(node, color);
        Map.MarkDirty();
        UpdateDetails();
    }

    private void SetFilter(string text)
    {
        FilterBox.Text = text;
        ApplyFilter();
    }

    /// <summary>Opens the element in Windows Explorer: folders are opened, files are selected in their folder.</summary>
    public void OpenInExplorer(FsNode node)
    {
        string path = node.FullPath;
        try
        {
            if (node is ContainerNode) Dialogs.Start(new ProcessStartInfo(SystemTools.Explorer, "\"" + path + "\"") { UseShellExecute = true });
            else Dialogs.Start(new ProcessStartInfo(SystemTools.Explorer, "/select,\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("Cannot open Explorer for " + path + ": " + ex.Message); }
    }

    private void Start(string path)
    {
        try { Dialogs.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Dialogs.Show(Window.GetWindow(this), T("Cannot open") + "\n" + path + "\n\n" + ex.Message, T("Open"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ShellExecute(string path, string verb)
    {
        try { Dialogs.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = verb }); }
        catch (Exception ex) { Log.Error("Shell verb '" + verb + "' failed for " + path + ": " + ex.Message); }
    }

    private void ShowShellMenu(string path, Point screen)
    {
        var w = Window.GetWindow(this);
        var hwnd = w != null ? new WindowInteropHelper(w).Handle : IntPtr.Zero;
        Dialogs.ShellMenu(hwnd, path, (int)screen.X, (int)screen.Y);
    }

    private void OnMasterScanFinished(ScanTree tree)
    {
        Dispatcher.BeginInvoke(() =>
        {
            UpdateStatus();
            StateChanged?.Invoke();
            var w = Window.GetWindow(this);
            if (AppConfig.Current.FlashWindow && w != null && (w.WindowState == WindowState.Minimized || !w.IsActive))
                Interop.Native.FlashWindow(new WindowInteropHelper(w).Handle);
            RunAutoExports();
            RunPendingCompare();
            RunPendingApps();
            RunPendingGames();
            RunPendingDev();
            RunPendingCleanup();
            RunPendingDuplicates();
            RunPendingCompareFolder();
            RecordScan();
            KeepOrForgetUnfinished();
            RunPendingForecast();
            MainWindow.Instance?.AutoExportFinished(this);   // unattended runs without exports close here
        });
    }

    /// <summary>
    /// Command line exports run on a background thread (a 500k line list takes seconds) while the
    /// window stays responsive; <see cref="AutoExportsPending"/> stays true until they are written so
    /// autoclose waits for them.
    /// </summary>
    public void RunAutoExports()
    {
        if (_autoExportStarted || (AutoExports.Count == 0 && !HasReports)) return;
        _autoExportStarted = true;
        var jobs = AutoExports.ToList();
        AutoExports.Clear();
        var appsReport = AppsReportPath;
        var gamesReport = GamesReportPath;
        var devReport = DevReportPath;
        var cleanupReport = CleanupReportPath;
        var duplicatesReport = DuplicatesReportPath;
        var compareReport = CompareFolderReport;
        var model = Model;
        Task.Run(() =>
        {
            var failures = new List<string>();
            bool disarm = false;
            while (model.IsFiltering || model.FilterResultStale)
            {
                if (!model.IsFiltering && model.FilterResultStale) model.RequestRefilter();
                Thread.Sleep(50);
            }
            foreach (var (name, path) in jobs)
            {
                var cfg = ExportConfig.FindByName(name);
                if (cfg == null) { Log.Error("No export setting called \"" + name + "\" (command line)"); failures.Add(name); continue; }
                try { ExportWindow.RunExport(cfg, model, path, CancellationToken.None, null); Log.Debug("Export completed: " + path); }
                catch (Exception ex) { Log.Error("Export failed, the file is incomplete: " + ex.Message); disarm = true; }
            }
            if (appsReport != null)
            {
                try
                {
                    var catalog = AppCatalog.GetAsync().GetAwaiter().GetResult();
                    var r = AppFootprint.Compute(model.ViewRoot, catalog, n => n.DiskSize, CancellationToken.None, model.Tree.Sync);
                    File.WriteAllText(appsReport, AppFootprint.ToText(r), new System.Text.UTF8Encoding(false));
                    Log.Debug($"Program report completed: {appsReport} ({r.Apps.Count} programs, {r.Elapsed.TotalMilliseconds:0} ms)");
                }
                catch (Exception ex) { Log.Error("Program report failed: " + ex.Message); disarm = true; }
            }
            if (gamesReport != null)
            {
                try
                {
                    var lib = GameLibrary.GetAsync().GetAwaiter().GetResult();
                    var r = GameAudit.Compute(model.ViewRoot, lib, n => n.DiskSize, CancellationToken.None, model.Tree.Sync);
                    File.WriteAllText(gamesReport, GameAudit.ToText(r, DateTime.UtcNow), new System.Text.UTF8Encoding(false));
                    Log.Debug($"Games report completed: {gamesReport} ({r.Games.Count} games, {r.Leftovers.Count} leftovers, {r.Elapsed.TotalMilliseconds:0} ms)");
                }
                catch (Exception ex) { Log.Error("Games report failed: " + ex.Message); disarm = true; }
            }
            if (devReport != null)
            {
                try
                {
                    var r = DevProjects.Compute(model.ViewRoot, n => n.DiskSize, CancellationToken.None, model.Tree.Sync);
                    File.WriteAllText(devReport, DevProjects.ToText(r, DateTime.UtcNow), new System.Text.UTF8Encoding(false));
                    Log.Debug($"Developer leftovers report completed: {devReport} ({r.Projects.Count} projects, {r.Elapsed.TotalMilliseconds:0} ms)");
                }
                catch (Exception ex) { Log.Error("Developer leftovers report failed: " + ex.Message); disarm = true; }
            }
            if (cleanupReport != null)
            {
                try
                {
                    var r = ComputeCleanup(model.ViewRoot, model.Tree, CancellationToken.None);
                    File.WriteAllText(cleanupReport, CleanupFinder.ToText(r), new System.Text.UTF8Encoding(false));
                    Log.Debug($"Cleanup report completed: {cleanupReport} ({r.Elapsed.TotalMilliseconds:0} ms)");
                }
                catch (Exception ex) { Log.Error("Cleanup report failed: " + ex.Message); disarm = true; }
            }
            if (duplicatesReport != null)
            {
                try
                {
                    var r = DuplicateFinder.Find(model.ViewRoot, model.Tree.Sync, new DuplicateOptions(), null, CancellationToken.None);
                    File.WriteAllText(duplicatesReport, DuplicatesReport(r, duplicatesReport), new System.Text.UTF8Encoding(duplicatesReport.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)));
                    Log.Debug($"Duplicates report completed: {duplicatesReport} ({r.Groups.Count} groups, {r.Elapsed.TotalMilliseconds:0} ms)");
                }
                catch (Exception ex) { Log.Error("Duplicates report failed: " + ex.Message); disarm = true; }
            }
            if (compareReport is { } cr)
            {
                try
                {
                    var r = CompareFolders(model.Tree, model.ViewRoot, cr.Other, false, CancellationToken.None);
                    bool csv = cr.File.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
                    File.WriteAllText(cr.File, csv ? FolderCompare.ToCsv(r, System.Globalization.CultureInfo.CurrentCulture.TextInfo.ListSeparator is { Length: 1 } ls ? ls[0] : ',') : FolderCompare.ToText(r), new System.Text.UTF8Encoding(csv));
                    Log.Debug($"Folder comparison report completed: {cr.File}");
                }
                catch (Exception ex) { Log.Error("Folder comparison report failed: " + ex.Message); disarm = true; }
            }
            Dispatcher.BeginInvoke(() =>
            {
                _autoExportDone = true;
                if (MainWindow.Instance is { Unattended: false } mw)
                {
                    foreach (var name in failures)
                        Dialogs.Show(F($"There is no export setting called \"{name}\".\nCheck the name on the command line.\n\nThis export is skipped."), T("Automatic export"), MessageBoxButton.OK, MessageBoxImage.Error);
                    if (failures.Count > 0 || disarm) mw.DisarmAutoClose();
                }
                else if (disarm) MainWindow.Instance?.DisarmAutoClose();
                MainWindow.Instance?.AutoExportFinished(this);
            });
        });
    }
}
