using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using ByteScape.Core.Config;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using ByteScape.Core.Snapshot;
using ByteScape.Core.Util;
using ByteScape.Wpf.Controls;
using ByteScape.Wpf.Views;
using ByteScape.Wpf.Windows;

namespace ByteScape.Wpf;

/// <summary>Main window: toolbar + tabbed views. Views live in <see cref="ScanView"/>.</summary>
public partial class MainWindow : Window
{
    public static MainWindow? Instance { get; private set; }

    private readonly List<CommandLine.ViewRequest> _pending;
    private bool _autoClose, _autoClosing;
    private int _pendingOpens;                   // network paths being checked on a background thread
    /// <summary>"autoclose" was given on the command line: never block on message boxes.</summary>
    public bool Unattended { get; }
    private LogWindow? _log;
    private bool _logDisabled;

    private readonly bool _openStartDialog;

    public MainWindow(List<CommandLine.ViewRequest> requests, bool autoClose, bool openStartDialog = true)
    {
        InitializeComponent();
        Localizer.Apply(this);
        Instance = this;
        _pending = requests;
        _openStartDialog = openStartDialog;
        AutoTour = openStartDialog && !autoClose;
        Unattended = autoClose;
        _autoClose = autoClose;
        Title = Branding.Name + " " + Branding.Version;
        Log.Added += OnLogAdded;
        Loaded += (_, _) => OnFirstShown();
        Closing += (_, _) =>
        {
            // an unfinished scan is kept to continue it next time, a complete one to bring it up to date from the NTFS change
            // journal (not in unattended runs: those scan from scratch)
            if (!Unattended) KeepScans();
            foreach (var v in Views.ToList()) v.Close();
            AppConfig.Save();
        };
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] p) OpenDropped(p); };
        UpdateToolbar();
    }

    /// <summary>
    /// On closing: every open tree whose scan is unfinished (a running scan is stopped) or complete with a change journal
    /// position still in the journal is kept, for the next time its path is opened.
    /// </summary>
    internal void KeepScans()
    {
        var cfg = AppConfig.Current;
        foreach (var tree in Views.Select(v => v.Tree).Distinct().Where(t => t.IsLive && t.Root.Enumerated).ToList())
        {
            bool unfinished = tree.IsMasterScanning || !tree.Root.ScanComplete;
            if (unfinished ? !cfg.KeepUnfinishedScans : !(cfg.QuickRescan && tree.TracksChanges && tree.ChangeMark != null)) continue;
            Cursor = Cursors.Wait;
            if (tree.IsMasterScanning)
            {
                tree.StopMasterScan();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (tree.IsMasterScanning && sw.ElapsedMilliseconds < 5000) Thread.Sleep(10);
            }
            if (tree.Root.ScanComplete && !ChangeJournal.IsCurrent(tree.ChangeMark!, tree.Root.FullPath, out var why))
            {
                Log.Debug("The scan of " + tree.Root.FullPath + " is not kept: " + why);
                ScanResume.Delete(tree.Root.FullPath);
                continue;
            }
            ScanResume.Save(tree);
        }
    }

    public IEnumerable<ScanView> Views => Tabs.Items.OfType<TabItem>().Select(t => (ScanView)t.Content);
    public ScanView? Active => (Tabs.SelectedItem as TabItem)?.Content as ScanView;

    private void OnFirstShown()
    {
        if (_pending.Count == 0) { if (_openStartDialog) ShowStartDialog(); return; }
        foreach (var r in _pending)
        {
            void Prepare(ScanView v)
            {
                if (!string.IsNullOrEmpty(r.Filter)) v.SetFilterText(r.Filter);
                foreach (var e in r.Exports) v.AutoExports.Add(e);
                if (!string.IsNullOrEmpty(r.CompareWith)) v.PendingCompare = r.CompareWith;
                if (!string.IsNullOrEmpty(r.AppsReport)) v.AppsReportPath = r.AppsReport;
                if (r.ShowApps) v.PendingApps = true;
                if (!string.IsNullOrEmpty(r.GamesReport)) v.GamesReportPath = r.GamesReport;
                if (r.ShowGames) v.PendingGames = true;
                if (!string.IsNullOrEmpty(r.DevReport)) v.DevReportPath = r.DevReport;
                if (r.ShowDev) v.PendingDev = true;
                if (r.ShowForecast) v.PendingForecast = true;
                if (!string.IsNullOrEmpty(r.CleanupReport)) v.CleanupReportPath = r.CleanupReport;
                if (r.ShowCleanup) v.PendingCleanup = true;
                if (!string.IsNullOrEmpty(r.DuplicatesReport)) v.DuplicatesReportPath = r.DuplicatesReport;
                if (r.ShowDuplicates) v.PendingDuplicates = true;
                if (!string.IsNullOrEmpty(r.CompareFolderReport)) v.CompareFolderReport = (r.CompareFolderWith, r.CompareFolderReport);
                else if (!string.IsNullOrEmpty(r.CompareFolderWith)) v.PendingCompareFolder = r.CompareFolderWith;
                if (r.IsLoad && (v.AutoExports.Count > 0 || v.HasReports)) v.RunAutoExports();
            }
            if (r.IsLoad) { var v = OpenSnapshot(r.Path); if (v != null) Prepare(v); }
            else OpenScan(r.Path, Prepare);
        }
        CheckIdleAutoClose();
    }

    /// <summary>Unattended run with nothing to wait for (bad paths, no exports): close right away.</summary>
    private void CheckIdleAutoClose()
    {
        if (!Unattended || _autoClosing || _pendingOpens > 0) return;
        if (Views.Any(v => v.AutoExportsPending || v.Tree.IsMasterScanning)) return;
        _autoClosing = true;
        Log.Debug("autoclose: nothing left to do");
        Dispatcher.BeginInvoke(Close);
    }

    /// <summary>Error to the user, or only to the log when running unattended (autoclose).</summary>
    private void ReportError(string message, string title = "Error")
    {
        Log.Error(message.Replace("\n", " "));
        if (!Unattended) Dialogs.Show(this, message, T(title), MessageBoxButton.OK, MessageBoxImage.Error);
    }

    // ------------------------------------------------------------------ views

    public void ShowStartDialog()
    {
        var w = new StartWindow { Owner = this };
        Dialogs.ShowModal(w);
    }

    public void OpenDropped(string[] paths)
    {
        foreach (var p in paths)
        {
            if (p.StartsWith(@"\\", StringComparison.Ordinal) || Directory.Exists(p)) OpenScan(p);
            else if (File.Exists(p) && (SnsFormat.IsSnapshotFile(p) || DuFormat.IsDuFile(p))) OpenSnapshot(p);
        }
    }

    public ScanView? OpenScan(string path) => OpenScan(path, null);

    /// <summary>
    /// Opens a scan view on <paramref name="path"/>. Local paths open synchronously and return the view.
    /// Network (UNC) paths are checked and acquired on a background thread first - an unreachable host
    /// blocks for seconds - so the method returns null and <paramref name="then"/> runs once the view exists.
    /// </summary>
    public ScanView? OpenScan(string path, Action<ScanView>? then)
    {
        path = DataCache.NormalizePath(path);
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            _pendingOpens++;
            Cursor = Cursors.AppStarting;
            Log.Debug("Checking network path " + path);
            Task.Run(() => Directory.Exists(path) ? DataCache.Acquire(path) : default((ScanTree tree, ContainerNode node)?))
                .ContinueWith(t =>
                {
                    _pendingOpens--;
                    Cursor = null;
                    ScanView? view = null;
                    if (t.IsFaulted) ReportError(F($"Unable to open {path}") + "\n\n" + (t.Exception?.GetBaseException().Message ?? "error"));
                    else if (t.Result is not { } acquired) ReportError(F($"{path} is not a valid path"), "Bad Path");
                    else view = Attach(acquired.tree, acquired.node, path);
                    if (view != null) then?.Invoke(view);
                    CheckIdleAutoClose();
                }, TaskScheduler.FromCurrentSynchronizationContext());
            return null;
        }
        try
        {
            if (!Directory.Exists(path)) { ReportError(F($"{path} is not a valid path"), "Bad Path"); return null; }
            var (tree, node) = DataCache.Acquire(path);
            var view = Attach(tree, node, path);
            then?.Invoke(view);
            return view;
        }
        catch (Exception ex)
        {
            Log.Error("Unable to open scan view on " + path + ": " + ex.Message);
            ReportError(ex.Message);
            return null;
        }
    }

    private ScanView Attach(ScanTree tree, ContainerNode node, string path)
    {
        AppConfig.Current.AddRecentPath(path);
        // a scan of this very path stopped earlier (or cut by closing ByteScape): continue it instead of starting over;
        // a complete one kept with the change journal's position: only the folders changed since are listed
        ResumeInfo? kept = null;
        if (!Unattended && !tree.IsMasterScanning && tree.Root.ChildCount == 0
            && string.Equals(DataCache.NormalizePath(tree.Root.FullPath), DataCache.NormalizePath(path), StringComparison.OrdinalIgnoreCase)
            && ScanResume.Find(path) is { } found)
        {
            string why = "";
            bool usable = found.Complete
                ? AppConfig.Current.QuickRescan && tree.TracksChanges && found.Mark is { } mark && ChangeJournal.IsCurrent(mark, path, out why)
                : AppConfig.Current.KeepUnfinishedScans;
            if (usable && ScanResume.Restore(tree, found)) kept = found;
            else if (found.Complete)
            {
                if (why.Length > 0) Log.Debug("The kept scan of " + path + " cannot be brought up to date (" + why + "): scanning everything");
                ScanResume.Delete(path);
            }
        }
        var view = AddView(tree, node, path);
        if (kept is { Complete: true })
        {
            tree.StartMasterScan(changesOnly: true);
            view.ShowNote(F($"Bringing the scan of {Format.Date(kept.SavedUtc)} {Format.Time(kept.SavedUtc)} up to date: only the folders the NTFS change journal says changed since are listed again. Shift+click on the scan button scans everything again."), 15);
        }
        else if (kept != null)
        {
            tree.StartMasterScan(resume: true);
            view.ShowNote(F($"Continuing the scan stopped on {Format.Date(kept.SavedUtc)} {Format.Time(kept.SavedUtc)}: {kept.Files:N0} files kept, {kept.PendingFolders:N0} folders to go. Shift+click on the scan button scans everything again."), 20);
        }
        else if (!tree.IsMasterScanning && !tree.Root.ScanComplete) tree.StartMasterScan();
        Log.Debug("Scan view opened on " + path);
        return view;
    }

    public ScanView? OpenSnapshot(string file)
    {
        try
        {
            DriveNode? root;
            bool cancelled = false;
            Cursor = Cursors.Wait;
            try
            {
                if (SnsFormat.IsSnapshotFile(file)) root = ReadResponsive(file, (p, t) => SnsFormat.Read(file, p, t), out cancelled);
                else if (DuFormat.IsDuFile(file)) root = ReadResponsive(file, (_, _) => DuFormat.Read(file), out cancelled);
                else return null;
            }
            finally { Cursor = null; }
            if (cancelled) { Log.Debug("Opening cancelled: " + file); return null; }
            if (root == null) { ReportError(F($"Unable to open file '{file}'")); return null; }
            var frame = new DriveNode("[SNAPSHOT: " + Path.GetFullPath(file) + "]") { IsDrive = false, IsSnapshotFrame = true, Enumerated = true, ScanComplete = true };
            frame.AddChild(root);
            root.Enumerated = true; root.ScanComplete = true;
            var tree = DataCache.Register(frame, live: false);
            var view = AddView(tree, frame, "[SNAPSHOT: " + Path.GetFileName(file) + "]");
            Log.Debug("Snapshot loaded: " + file);
            return view;
        }
        catch (Exception ex)
        {
            ReportError(F($"Unable to open file '{file}'") + "\n\n" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Reads a snapshot on a worker thread. A snapshot of millions of files takes seconds to read: after a moment a small
    /// dialog shows how far it got and can cancel it; the window keeps painting meanwhile (the dialog's own message loop)
    /// and Windows never calls it "not responding". Errors of the read come back to the caller as they are.
    /// </summary>
    internal T? ReadResponsive<T>(string file, Func<Action<long, long>, CancellationToken, T?> read, out bool cancelled) where T : class
    {
        cancelled = false;
        using var cts = new CancellationTokenSource();
        long pos = 0, len = 1;
        var task = Task.Run(() => read((p, l) => { Interlocked.Exchange(ref pos, p); Interlocked.Exchange(ref len, l); }, cts.Token));
        if (!task.Wait(300))
        {
            var text = new TextBlock { Text = F($"Opening {Path.GetFileName(file)}…"), TextTrimming = TextTrimming.CharacterEllipsis };
            var bar = new ProgressBar { Height = 6, Maximum = 1000, Margin = new Thickness(0, 12, 0, 0) };
            var cancel = new Button { Content = Loc.T("Cancel"), MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), IsCancel = true };
            var dlg = new Window
            {
                Title = Branding.Name, Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                WindowStartupLocation = IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Content = new StackPanel { Margin = new Thickness(20), Children = { text, bar, cancel } },
            };
            if (IsVisible) dlg.Owner = this;
            cancel.Click += (_, _) => { cts.Cancel(); cancel.IsEnabled = false; text.Text = Loc.T("Cancelling…"); };
            dlg.Closing += (_, e) => { if (!task.IsCompleted) { cts.Cancel(); e.Cancel = true; } }; // closing = cancel; it ends when the read has stopped
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                bar.Value = 1000.0 * Interlocked.Read(ref pos) / Math.Max(1, Interlocked.Read(ref len));
                if (task.IsCompleted) { timer.Stop(); dlg.Close(); }
            };
            timer.Start();
            Dialogs.ShowModal(dlg);
        }
        try { return task.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { cancelled = true; return null; }
    }

    /// <summary>The tour starts by itself once the first view is open: the program started the usual way (with the start
    /// dialog, not for a command line job) and the tour was not seen yet.</summary>
    internal bool AutoTour { get; set; }

    /// <summary>Starts the tour of this window (from the menu, or by itself the first time); null when it could not start.</summary>
    internal Tour? StartTour()
    {
        var tour = Tour.For(this);
        tour.Start();
        return Tour.Running == tour ? tour : null;
    }

    private void ScheduleTour()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += (_, _) =>
        {
            // after the start dialog (and any other window of ours) has gone: the map is filling in by then
            if (OwnedWindows.OfType<Window>().Any(o => o.IsVisible)) return;
            timer.Stop();
            if (!AppConfig.Current.TourShown && Tour.Running == null && Active != null) StartTour();
        };
        timer.Start();
    }

    private void Tour_Click(object s, RoutedEventArgs e) => StartTour();

    private ScanView AddView(ScanTree tree, ContainerNode node, string title)
    {
        var view = new ScanView(tree, node, title);
        view.StateChanged += UpdateToolbar;
        var header = new DockPanel();
        var close = new Button { Content = "", Style = (Style)FindResource("ToolButton"), Width = 22, Height = 22, FontSize = 9, Margin = new Thickness(10, 0, 0, 0), ToolTip = T("Close view") };
        DockPanel.SetDock(close, Dock.Right);
        var icon = new Glyph { Text = tree.IsLive ? "" : "", FontSize = 12, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { Text = ShortTitle(title), MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = title };
        header.Children.Add(close); header.Children.Add(icon); header.Children.Add(text);
        var tab = new TabItem { Header = header, Content = view };
        AutomationProperties.SetName(tab, title);   // the header is a panel: a screen reader would say its type
        close.Click += (_, _) => CloseView(tab);
        Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;
        Empty.Visibility = Visibility.Collapsed;
        UpdateToolbar();
        if (AutoTour && !AppConfig.Current.TourShown && Tour.Running == null && Tabs.Items.Count == 1) ScheduleTour();
        return view;
    }

    private static string ShortTitle(string t)
    {
        if (t.Length <= 40) return t;
        return t.Substring(0, 12) + "…" + t.Substring(t.Length - 26);
    }

    private void CloseView(TabItem tab)
    {
        if (tab.Content is ScanView v) v.Close();
        Tabs.Items.Remove(tab);
        if (Tabs.Items.Count == 0) Empty.Visibility = Visibility.Visible;
        UpdateToolbar();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateToolbar();

    public void UpdateToolbar()
    {
        var v = Active;
        bool has = v != null;
        BtnBack.IsEnabled = has && v!.CanGoBack;
        BtnForward.IsEnabled = has && v!.CanGoForward;
        BtnUp.IsEnabled = has && v!.CanGoUp;
        BtnHome.IsEnabled = has && v!.CanGoUp;
        BtnScan.IsEnabled = has && v!.Tree.IsLive;
        BtnCompare.IsEnabled = has;
        BtnApps.IsEnabled = has;
        BtnGames.IsEnabled = has;
        BtnDev.IsEnabled = has;
        BtnForecast.IsEnabled = has;
        BtnCleanup.IsEnabled = has;
        BtnDuplicates.IsEnabled = has;
        BtnHistory.IsEnabled = has && v!.Tree.Root.FullPath.Length > 1 && v.Tree.Root.FullPath[1] == ':';
        BtnScan.Content = has && v!.Tree.IsMasterScanning ? "\uE71A" : "\uE768";
        BtnScan.ToolTip = T(!has ? "Scan everything again"
            : v!.Tree.IsMasterScanning ? "Stop the scan (it can continue later)"
            : v.Tree.CanResume ? "Continue the scan where it stopped (Shift+click: scan everything again)"
            : v.Tree.CanRescanChanges ? "Rescan what changed since the last scan, from the NTFS change journal (Shift+click: scan everything again)"
            : "Scan everything again");
        BtnPause.IsEnabled = has && v!.Tree.IsMasterScanning;
        BtnPause.Content = has && v!.Tree.IsMasterPaused ? "\uE768" : "\uE769";
        BtnPause.ToolTip = has && v!.Tree.IsMasterPaused ? T("Resume the paused scan (Pause)") : T("Pause the scan (Pause)");
        BtnSub.IsEnabled = has && v!.Tree.IsLive && v.CanGoUp;
        BtnSub.Content = has && v!.Model.IsSubScanning ? "" : "";
        BtnClasses.IsChecked = has && v!.Model.Style == ViewStyle.FileClasses;
        BtnFree.IsChecked = has && v!.Model.ShowFreeSpace;
        BtnUnknown.IsChecked = has && v!.Model.ShowUnknownSpace;
        BtnFree.IsEnabled = BtnUnknown.IsEnabled = has && v!.Tree.Root.IsDrive;
        BtnDetails.IsChecked = has && v!.DetailsVisible;
        Title = (has ? Branding.Name + " – " + v!.Title : Branding.Name + " " + Branding.Version) + (Environment.IsPrivilegedProcess ? "  (" + T("Administrator") + ")" : "");
        // a packaged (Store) app cannot elevate itself; a process that already is elevated has nothing to gain
        ElevateItem.Visibility = Environment.IsPrivilegedProcess || AppConfig.IsPackaged() ? Visibility.Collapsed : Visibility.Visible;
        UpdateItem.Visibility = Core.Update.UpdateCheck.Available ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ toolbar handlers

    private void New_Click(object s, RoutedEventArgs e) => ShowStartDialog();
    private void Back_Click(object s, RoutedEventArgs e) => Active?.GoBack();
    private void Forward_Click(object s, RoutedEventArgs e) => Active?.GoForward();
    private void Up_Click(object s, RoutedEventArgs e) => Active?.GoUp();
    private void Home_Click(object s, RoutedEventArgs e) => Active?.GoHome();
    private void Scan_Click(object s, RoutedEventArgs e) => Active?.ToggleMasterScan(fullRescan: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    private void Pause_Click(object s, RoutedEventArgs e) { Active?.TogglePause(); UpdateToolbar(); }
    private void Sub_Click(object s, RoutedEventArgs e) => Active?.ToggleSubScan();
    private void Less_Click(object s, RoutedEventArgs e) => Active?.ChangeDetail(-1);
    private void More_Click(object s, RoutedEventArgs e) => Active?.ChangeDetail(+1);
    private void Classes_Click(object s, RoutedEventArgs e) { Active?.ToggleClasses(); UpdateToolbar(); }
    private void Free_Click(object s, RoutedEventArgs e) { Active?.ToggleFree(); UpdateToolbar(); }
    private void Unknown_Click(object s, RoutedEventArgs e) { Active?.ToggleUnknown(); UpdateToolbar(); }
    private void Filter_Click(object s, RoutedEventArgs e) => Active?.FocusFilter();
    private void Details_Click(object s, RoutedEventArgs e) { Active?.ToggleDetails(); UpdateToolbar(); }
    private void Export_Click(object s, RoutedEventArgs e) => ShowExport();
    private void Compare_Click(object s, RoutedEventArgs e)
    {
        // a snapshot or another folder
        var v = Active;
        if (v == null) return;
        var menu = new ContextMenu { PlacementTarget = BtnCompare, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var snap = new MenuItem { Header = T("Compare with a saved snapshot…"), InputGestureText = "Ctrl+D" };
        snap.Click += (_, _) => v.CompareWithSnapshot();
        var folder = new MenuItem { Header = T("Compare with another folder…"), InputGestureText = "Ctrl+Shift+C", IsEnabled = v.Tree.IsLive };
        folder.Click += (_, _) => v.CompareWithFolder();
        menu.Items.Add(snap); menu.Items.Add(folder);
        Dialogs.OpenMenu(menu);
    }
    private void CompareSnapshot_Click(object s, RoutedEventArgs e) => Active?.CompareWithSnapshot();
    private void CompareFolder_Click(object s, RoutedEventArgs e) { if (Active is { Tree.IsLive: true } v) v.CompareWithFolder(); }
    private void Duplicates_Click(object s, RoutedEventArgs e) => Active?.ShowDuplicates();
    private void History_Click(object s, RoutedEventArgs e) => Active?.ShowHistory();
    private void Apps_Click(object s, RoutedEventArgs e) => Active?.ShowAppFootprint();
    private void Games_Click(object s, RoutedEventArgs e) => Active?.ShowGames();
    private void Dev_Click(object s, RoutedEventArgs e) => Active?.ShowDev();
    private void Forecast_Click(object s, RoutedEventArgs e) => Active?.ShowForecast();
    private void Cleanup_Click(object s, RoutedEventArgs e) => Active?.ShowCleanup();

    /// <summary>
    /// Starts this exe again as administrator with the open live views as "scan" arguments (their master scans then read
    /// the NTFS master file table), then closes this instance. Nothing happens when the UAC prompt is declined.
    /// </summary>
    private void Elevate_Click(object s, RoutedEventArgs e) => RestartAsAdministrator();

    internal void RestartAsAdministrator() => Restart(elevated: true);

    /// <summary>Starts ByteScape again (as administrator, or after a language change) with the same views (their scans are kept on closing) and closes this instance.</summary>
    internal void Restart(bool elevated)
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        var paths = Views.Where(v => v.Tree.IsLive).Select(v => v.Model.ViewRoot.FullPath.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase);
        var args = string.Join(" ", paths.Select(p => "scan \"" + p + "\""));
        try { Dialogs.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = elevated ? "runas" : "" }); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return; }   // declined
        catch (Exception ex) { ReportError(T(elevated ? "Cannot restart as administrator:" : "Cannot restart:") + " " + ex.Message); return; }
        Close();
    }
    private async void Update_Click(object s, RoutedEventArgs e) => await UpdateUi.CheckNow(this);
    private void Log_Click(object s, RoutedEventArgs e) => ShowLog();
    private void Config_Click(object s, RoutedEventArgs e) => ShowConfig();
    private void Menu_Click(object s, RoutedEventArgs e) { MoreMenu.PlacementTarget = BtnMenu; Dialogs.OpenMenu(MoreMenu); }
    private void HelpFilter_Click(object s, RoutedEventArgs e) => new HelpWindow(HelpWindow.Topic.Filtering) { Owner = this }.Show();
    private void HelpTag_Click(object s, RoutedEventArgs e) => new HelpWindow(HelpWindow.Topic.Tagging) { Owner = this }.Show();
    private void HelpCmd_Click(object s, RoutedEventArgs e) => new HelpWindow(HelpWindow.Topic.CommandLine) { Owner = this }.Show();
    private void Debug_Click(object s, RoutedEventArgs e) => new LayoutDebugWindow(Active?.Model) { Owner = this }.Show();
    private void About_Click(object s, RoutedEventArgs e) => Dialogs.ShowModal(new AboutWindow { Owner = this });
    private void ReportProblem_Click(object s, RoutedEventArgs e) => Dialogs.ShowModal(new ProblemReportWindow { Owner = this });
    private void Exit_Click(object s, RoutedEventArgs e) => Close();

    public void ShowExport()
    {
        var v = Active;
        if (v == null) return;
        if (v.Model.IsFiltering) { Dialogs.Show(this, T("The filter is still being applied. Try again in a moment."), T("Export")); return; }
        Dialogs.ShowModal(new ExportWindow(v.Model) { Owner = this });
    }

    public void ShowConfig()
    {
        var w = new ConfigWindow { Owner = this };
        Dialogs.ShowModal(w);
        if (w.Applied) foreach (var v in Views) v.ApplyConfigChanges();
    }

    public void ShowLog(bool force = true)
    {
        if (_log == null || !_log.IsLoaded)
        {
            _log = new LogWindow { Owner = this };
            _log.PopupDisabledChanged += d => _logDisabled = d;
        }
        if (!force && _logDisabled) return;
        _log.Show();
        _log.Activate();
    }

    private void OnLogAdded(LogEntry e)
    {
        if (!AppConfig.Current.LogPopup || _logDisabled || Unattended || e.Level is LogLevel.Debug or LogLevel.Fine) return;
        Dispatcher.BeginInvoke(() => { if (IsLoaded) ShowLog(force: false); });
    }

    public static void OpenUrl(string url)
    {
        try { Dialogs.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Unable to open " + url + ": " + ex.Message); }
    }

    // ------------------------------------------------------------------ keyboard

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (HandleShortcut(e.Key, Keyboard.Modifiers, Keyboard.FocusedElement)) e.Handled = true;
        else base.OnPreviewKeyDown(e);
    }

    /// <summary>One key of the window's shortcuts (Ctrl+N, Backspace, Delete, the tag digits…); true when it was used.</summary>
    internal bool HandleShortcut(Key key, ModifierKeys modifiers, IInputElement? focused)
    {
        if (Tour.Running is { } tour && tour.HandleKey(key)) return true;
        var v = Active;
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
        bool shift = modifiers.HasFlag(ModifierKeys.Shift);
        bool inText = focused is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox;
        bool handled = true;
        if (key == Key.Pause) { v?.TogglePause(); UpdateToolbar(); return true; }
        if (ctrl)
        {
            switch (key)
            {
                case Key.N: ShowStartDialog(); break;
                case Key.Up: v?.GoUp(); break;
                case Key.Home: v?.GoHome(); break;
                case Key.Add: case Key.OemPlus: v?.ChangeDetail(+1); break;
                case Key.Subtract: case Key.OemMinus: v?.ChangeDetail(-1); break;
                case Key.T: v?.ToggleClasses(); UpdateToolbar(); break;
                case Key.F: v?.ToggleFree(); UpdateToolbar(); break;
                case Key.U: v?.ToggleUnknown(); UpdateToolbar(); break;
                case Key.L: v?.FocusFilter(); break;
                case Key.E: ShowExport(); break;
                case Key.D: if (shift) v?.ShowDuplicates(); else v?.CompareWithSnapshot(); break;
                case Key.P: v?.ShowAppFootprint(); break;
                case Key.G: v?.ShowGames(); break;
                case Key.B: v?.ShowDev(); break;
                case Key.K: v?.ShowForecast(); break;
                case Key.Delete when shift: v?.ShowCleanup(); break;
                case Key.H: v?.ShowHistory(); break;
                case Key.C:
                    if (inText) handled = false;
                    else if (!shift) v?.CopySelectedPath();
                    else if (v is { Tree.IsLive: true }) v.CompareWithFolder();
                    break;
                case Key.W: if (Tabs.SelectedItem is TabItem t) CloseView(t); break;
                case Key.D1: case Key.NumPad1: v?.Tag(TagColor.Red); break;
                case Key.D2: case Key.NumPad2: v?.Tag(TagColor.Yellow); break;
                case Key.D3: case Key.NumPad3: v?.Tag(TagColor.Green); break;
                case Key.D4: case Key.NumPad4: v?.Tag(TagColor.Blue); break;
                case Key.D0: case Key.NumPad0: v?.ClearTags(); break;
                default: handled = false; break;
            }
        }
        else if (!inText && !FocusedControlUses(focused, key))
        {
            switch (key)
            {
                case Key.Back: if (shift) v?.GoForward(); else v?.GoBack(); break;
                case Key.Enter: v?.OpenSelectedInExplorer(); break;
                case Key.Delete: v?.DeleteSelected(shift ? true : null); break;
                case Key.D1: case Key.NumPad1: v?.Tag(TagColor.Red); break;
                case Key.D2: case Key.NumPad2: v?.Tag(TagColor.Yellow); break;
                case Key.D3: case Key.NumPad3: v?.Tag(TagColor.Green); break;
                case Key.D4: case Key.NumPad4: v?.Tag(TagColor.Blue); break;
                case Key.D0: case Key.NumPad0: v?.ClearTags(); break;
                default: handled = false; break;
            }
        }
        else handled = false;
        return handled;
    }

    /// <summary>
    /// Plain keys the focused control needs itself, so the map shortcuts leave them alone: Enter presses a focused button or
    /// menu item (for a keyboard user it would otherwise open the map's selection in Explorer), and lists and combo boxes
    /// use Enter, Delete, Backspace and digits for their own items and type-ahead.
    /// </summary>
    internal static bool FocusedControlUses(IInputElement? focused, Key key) =>
        focused is ComboBox or ComboBoxItem or ListBoxItem or System.Windows.Controls.Primitives.Selector
        || key == Key.Enter && focused is System.Windows.Controls.Primitives.ButtonBase or MenuItem;

    // ------------------------------------------------------------------ auto close

    public void DisarmAutoClose() { if (_autoClose) { _autoClose = false; Log.Debug("Auto close disabled by user interaction"); } }

    public void AutoExportFinished(ScanView view)
    {
        if (!_autoClose || _autoClosing) return;
        // a comparison still running (command line "compare") would open its window after this one closed: wait for it
        bool pending = Views.Any(v => v.AutoExportsPending || v.Tree.IsMasterScanning || v.CompareRunning);
        if (!pending) { _autoClosing = true; Log.Debug("autoclose: done"); Dispatcher.BeginInvoke(Close); }
    }
}
