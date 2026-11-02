using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ByteScape.Core.Apps;
using ByteScape.Core.Cleanup;
using ByteScape.Core.Config;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using ByteScape.Core.Util;
using ByteScape.Wpf.Windows;

namespace ByteScape.Wpf.Views;

/// <summary>Deleting from the map (with the safety checks of <see cref="DeleteSafety"/>) and the cleanup categories.</summary>
public partial class ScanView
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    // ------------------------------------------------------------------ deleting

    /// <summary>Delete / Shift+Delete on the map: the selected element, to the Recycle Bin or (<paramref name="permanent"/>) for good; null = as the settings say.</summary>
    public void DeleteSelected(bool? permanent = null)
    {
        if (Model.Selected is not { IsSynthetic: false } node) return;
        DeleteNodes(new[] { node }, permanent ?? !AppConfig.Current.DeleteToRecycleBin);
    }

    /// <summary>Opens the delete dialog for <paramref name="nodes"/> (modal; <paramref name="modal"/> false for tests).</summary>
    internal DeleteWindow? DeleteNodes(IReadOnlyList<FsNode> nodes, bool permanent, TimeSpan? contentAge = null, bool modal = true)
    {
        DisarmAuto();
        if (!Tree.IsLive) { ShowNote(T("This is a snapshot: nothing in it can be deleted.")); return null; }
        var list = nodes.Where(n => !n.IsSynthetic).ToList();
        if (list.Count == 0) return null;
        if (list.Any(n => ReferenceEquals(n, Tree.Root) || ReferenceEquals(n, Model.ViewRoot)))
        {
            ShowNote(T("The folder this view shows cannot be deleted from inside it: open its parent folder first."));
            return null;
        }
        var w = new DeleteWindow(this, list, permanent, contentAge) { Owner = Window.GetWindow(this) };
        if (modal) Dialogs.ShowModal(w); else w.Show();
        return w;
    }

    /// <summary>After a delete: the folders involved are listed again (the Recycle Bin too), hard links regrouped, the map redrawn.</summary>
    internal async void AfterDelete(DeletePlan plan, DeleteReport report)
    {
        if (report.Items.Count == 0) return;
        ShowNote(report.DeletedCount == 0 ? T("Nothing was deleted.")
            : report.Recycled ? P(report.DeletedCount, "Moved {0:N0} element, {1}, to the Recycle Bin: the space is freed when you empty it.", "Moved {0:N0} elements, {1}, to the Recycle Bin: the space is freed when you empty it.", Format.Size(report.DeletedBytes))
            : P(report.DeletedCount, "Deleted {0:N0} element permanently: {1} freed.", "Deleted {0:N0} elements permanently: {1} freed.", Format.Size(report.DeletedBytes)), 15);
        var tree = Tree;
        await Task.Run(() => UpdateTreeAfterDelete(tree, report));
        AfterTreeChanged();
        if (_cleanup != null) ShowCleanup(fromUser: false);
        _duplicates?.Prune();
    }

    /// <summary>Lists again the parents of what was deleted, folders partly deleted (all of them) and this user's Recycle Bin folder.</summary>
    internal static void UpdateTreeAfterDelete(ScanTree tree, DeleteReport report)
    {
        var scanner = new DirectoryScanner(tree, CancellationToken.None);
        var folders = new List<(string Path, bool Deep)>();
        void Add(string? f, bool deep)
        {
            if (string.IsNullOrEmpty(f)) return;
            int i = folders.FindIndex(x => x.Path.Equals(f, OIC));
            if (i < 0) folders.Add((f, deep)); else if (deep) folders[i] = (f, true);
        }
        foreach (var r in report.Items)
        {
            Add(System.IO.Path.GetDirectoryName(r.Path), false);
            if (!r.Deleted) Add(r.Path, true);      // a folder partly deleted: what is left of it
        }
        if (report.Recycled)
            foreach (var root in report.Items.Where(i => i.Deleted).Select(i => System.IO.Path.GetPathRoot(i.Path)).Distinct(StringComparer.OrdinalIgnoreCase))
                if (root != null && RecycleBin.FolderOf(root) is { } bin) { Add(System.IO.Path.GetDirectoryName(bin), false); Add(bin, false); }
        foreach (var (path, deep) in folders.OrderBy(f => f.Path.Length))
        {
            try { RefreshFolder(scanner, tree, path, deep); }
            catch (Exception ex) { Log.Debug("After delete, " + path + ": " + ex.Message); }
        }
        if (tree.CountsLinksOnce) { try { tree.HardLinks.Apply(tree, CancellationToken.None); } catch (Exception ex) { Log.Debug("Hard links after delete: " + ex.Message); } }
        DataCache.RefreshDriveInfo(tree.Root);
    }

    private static void RefreshFolder(DirectoryScanner scanner, ScanTree tree, string path, bool deep)
    {
        if (tree.Find(path) is not ContainerNode node) return;
        bool listed;
        lock (tree.Sync) listed = node.Enumerated;
        if (!listed || !Directory.Exists(DirectoryScanner.IoPath(path))) return;   // not listed yet, or gone (its parent's listing removes it)
        if (deep)
        {
            tree.ResetScanFlags(node);
            scanner.ScanTree(node, path);
            return;
        }
        // sub folders a running scan has not listed yet stay its job; new ones (and replaced ones) are scanned here
        HashSet<ContainerNode> queued;
        lock (tree.Sync) queued = new HashSet<ContainerNode>(node.Children.OfType<ContainerNode>().Where(c => !c.Enumerated), ReferenceEqualityComparer.Instance);
        scanner.RefreshDirectory(node, path, recurseInto: false);
        List<(ContainerNode Node, string Path)> fresh;
        lock (tree.Sync) fresh = node.Children.OfType<ContainerNode>().Where(c => !c.Enumerated && !queued.Contains(c)).Select(c => (c, DeletePlan.Join(path, c.Name))).ToList();
        foreach (var (sub, subPath) in fresh) scanner.ScanTree(sub, subPath);
    }

    /// <summary>The map, the selection and the panels after elements left the tree.</summary>
    private void AfterTreeChanged()
    {
        bool Attached(FsNode n) { for (FsNode? p = n; p != null; p = p.Parent) if (ReferenceEquals(p, Tree.Root)) return true; return false; }
        if (Model.Selected is { } sel && !Attached(sel)) Model.Selected = null;
        if (!Attached(Model.ZoomRoot)) Model.ZoomTo(Model.ViewRoot);
        Map.MarkDirty();
        UpdateBreadcrumb(); UpdateDetails(); UpdateStatus();
        StateChanged?.Invoke();
    }

    /// <summary>Right click menu: "is it safe to delete?", then the delete commands (disabled with the reason when protected).</summary>
    private void AddDeleteItems(ContextMenu menu, FsNode node)
    {
        if (!Tree.IsLive || node.IsSynthetic) return;
        var ctx = SafetyContext.For(Tree);
        var a = DeleteSafety.Assess(node, new SafetyContext { Sync = ctx.Sync, Catalog = ctx.Catalog, HardLinks = ctx.HardLinks, ProtectSystem = ctx.ProtectSystem, ContentLimit = 20_000 });
        var safe = Item(T("Is it safe to delete?") + "  —  " + a.LevelText, "\uE83D", () => { Model.Selected = node; if (!DetailsVisible) ToggleDetails(); UpdateDetails(); MainWindow.Instance?.UpdateToolbar(); });
        safe.ToolTip = a.Headline + (a.Why.Length > 0 ? "\n" + a.Why : "");
        if (!ThemeManager.HighContrast) safe.Foreground = SafetyUi.LevelBrush(a.Level);
        menu.Items.Add(safe);
        bool recycleFirst = AppConfig.Current.DeleteToRecycleBin;
        bool root = ReferenceEquals(node, Tree.Root) || ReferenceEquals(node, Model.ViewRoot);
        string what = T(a.ContentOnly ? "Empty this folder" : "Move to the Recycle Bin");
        var recycle = Item(what + "…", "\uE74D", () => DeleteNodes(new[] { node }, false), recycleFirst ? "Del" : "Shift+Del");
        var forever = Item(T(a.ContentOnly ? "Empty this folder permanently" : "Delete permanently") + "…", "", () => DeleteNodes(new[] { node }, true), recycleFirst ? "Shift+Del" : "Del");
        foreach (var mi in new[] { recycle, forever })
        {
            if (a.Blocked || root)
            {
                mi.IsEnabled = false;
                mi.Header += "  (" + T(root ? "the folder this view shows" : "protected") + ")";
                mi.ToolTip = root ? T("Open its parent folder to delete it.") : a.Headline + "\n" + a.Why + (a.Cleanup.Length > 0 ? "\n" + T("The proper way:") + " " + a.Cleanup : "");
                ToolTipService.SetShowOnDisabled(mi, true);
            }
            menu.Items.Add(mi);
        }
    }

    private CancellationTokenSource? _safetyCts;

    /// <summary>"Is it safe to delete?" block of the details panel: verdict, warnings, and (checked in the background) what stands in the way.</summary>
    private void AddSafetyFacts(FsNode n)
    {
        if (n.IsSynthetic || !Tree.IsLive) return;
        var box = new Border { Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(10), CornerRadius = new CornerRadius(4), Background = (Brush)FindResource("Layer"), BorderBrush = (Brush)FindResource("Border"), BorderThickness = new Thickness(1) };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = T("Is it safe to delete?"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = T("Looking…"), Style = (Style)FindResource("Secondary") });
        sp.Children.Add(body);
        var access = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        sp.Children.Add(access);
        box.Child = sp;
        DetFacts.Children.Add(box);

        _safetyCts?.Cancel();
        var cts = _safetyCts = new CancellationTokenSource();
        var ctx = SafetyContext.For(Tree);
        var sync = Tree.Sync;
        bool known = Core.Knowledge.KnowledgeBase.Classify(n) != null;
        Task.Run(() => DeleteSafety.Assess(n, ctx), cts.Token).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (cts.IsCancellationRequested || t.Status != TaskStatus.RanToCompletion) return;
            var a = t.Result;
            body.Children.Clear();
            // the "What is this?" block above already says what a known location is and how to clean it
            body.Children.Add(SafetyUi.Verdict(a, why: !(known && a.Rule != null && !a.Blocked), cleanup: !(known && a.Rule != null)));
            if (a.Blocked) return;
            access.Text = T("Checking rights…");
            access.Foreground = (Brush)FindResource("TextSecondary");
            var del = new Button { Content = T(a.ContentOnly ? "Empty this folder…" : AppConfig.Current.DeleteToRecycleBin ? "Move to the Recycle Bin…" : "Delete…"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 8, 0, 0) };
            del.Click += (_, _) => DeleteNodes(new[] { n }, a.ContentOnly ? false : !AppConfig.Current.DeleteToRecycleBin);
            del.IsEnabled = !ReferenceEquals(n, Tree.Root) && !ReferenceEquals(n, Model.ViewRoot);
            sp.Children.Add(del);
            Task.Run(() => AccessProbe.ProbeTree(n, a.Path, sync, 300, cts.Token, includeRoot: !a.ContentOnly), cts.Token).ContinueWith(p => Dispatcher.BeginInvoke(() =>
            {
                if (cts.IsCancellationRequested || p.Status != TaskStatus.RanToCompletion) return;
                var (text, brush) = SafetyUi.ProbeLine(p.Result, AccessProbe.IsElevated);
                access.Text = text; access.Foreground = brush;
            }), TaskScheduler.Default);
        }), TaskScheduler.Default);
    }

    // ------------------------------------------------------------------ cleanup categories

    private CleanupWindow? _cleanup;
    private CancellationTokenSource? _cleanupCts;

    /// <summary>Command line "cleanup": opens the cleanup window once the scan is complete.</summary>
    public bool PendingCleanup
    {
        get => _pendingCleanup;
        set { _pendingCleanup = value; if (value && (!Tree.IsLive || Tree.Root.ScanComplete)) Dispatcher.BeginInvoke(RunPendingCleanup); }
    }
    private bool _pendingCleanup;

    /// <summary>Command line "cleanupreport &lt;file&gt;": the categories written to a text file once the view is scanned.</summary>
    public string? CleanupReportPath { get; set; }

    private void RunPendingCleanup()
    {
        if (!_pendingCleanup) return;
        _pendingCleanup = false;
        ShowCleanup(fromUser: false);
    }

    /// <summary>Everything the cleanup categories need, for a view root: installed programs, their footprint, Windows' record of program starts.</summary>
    internal static CleanupResult ComputeCleanup(ContainerNode root, ScanTree tree, CancellationToken token)
    {
        var catalog = AppCatalog.GetAsync().GetAwaiter().GetResult();
        var ctx = new SafetyContext { Sync = tree.Sync, Catalog = catalog, HardLinks = tree.CountsLinksOnce ? tree.HardLinks : null, ProtectSystem = AppConfig.Current.ProtectSystemFolders };
        var footprint = AppFootprint.Compute(root, catalog, n => n.DiskSize, token, tree.Sync);
        var starts = AppLastUse.FromRegistry();
        return CleanupFinder.Find(root, ctx, CleanupOptions.FromConfig(AppConfig.Current), footprint, starts, token);
    }

    /// <summary>Finds the cleanup categories of this view on a background thread and shows (or refreshes) the window.</summary>
    public async void ShowCleanup(bool fromUser = true)
    {
        if (fromUser) DisarmAuto();
        _cleanupCts?.Cancel();
        var cts = _cleanupCts = new CancellationTokenSource();
        var owner = Window.GetWindow(this);
        Cursor = Cursors.Wait;
        ShowNote(T("Looking for large, temporary and installation files and unused programs…"), 30);
        try
        {
            var root = Model.ViewRoot;
            var tree = Tree;
            var result = await Task.Run(() => ComputeCleanup(root, tree, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            Log.Debug($"Cleanup of {result.RootPath}: " + string.Join(", ", Enum.GetValues<CleanupCategory>().Select(c => CleanupFinder.Title(c) + " " + result.Of(c).Count + " / " + Format.Size(result.BytesOf(c)))) + $" ({result.Elapsed.TotalMilliseconds:0} ms)");
            ShowNote(F($"Cleanup: {Format.Size(Enum.GetValues<CleanupCategory>().Where(c => c != CleanupCategory.UnusedPrograms).Sum(c => result.BytesOf(c)))} in large, temporary and installation files"), 5);
            if (_cleanup == null)
            {
                _cleanup = new CleanupWindow(this) { Owner = owner };
                _cleanup.Closed += (_, _) => _cleanup = null;
                _cleanup.Show();
            }
            _cleanup.SetResult(result, Tree.IsMasterScanning || Model.IsSubScanning);
            if (fromUser) _cleanup.Activate();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Cleanup failed: " + ex.Message);
            ShowNote(T("Could not look for cleanup candidates:") + " " + ex.Message);
        }
        finally { if (ReferenceEquals(_cleanupCts, cts)) Cursor = null; }
    }

    /// <summary>Tests: runs instead of the uninstaller (gets its command line).</summary>
    internal static Action<string>? UninstallHook;

    /// <summary>Uninstalls a program the way Settings > Apps does: its own uninstaller (after a question), Store apps through Settings.</summary>
    internal void Uninstall(InstalledApp app, bool ask = true)
    {
        var cmd = app.UninstallCommand.Trim();
        if (app.Source == AppSource.Store || cmd.Length == 0) { MainWindow.OpenUrl("ms-settings:appsfeatures"); return; }
        if (ask && Dialogs.Show(Window.GetWindow(this), F($"Run the uninstaller of {app.Name}?") + "\n\n" + cmd + "\n\n" + T("This is the program's own uninstaller, the one Settings > Apps runs."), T("Uninstall"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (UninstallHook != null) { UninstallHook(cmd); return; }
        // running as administrator: an uninstaller a user without rights can replace (per-user programs keep it in their
        // own folder) would get those rights without Windows asking. Settings starts it as the user and asks itself.
        if (AccessProbe.IsElevated)
        {
            Dialogs.Show(Window.GetWindow(this), F($"{Branding.Name} is running as administrator, so it does not start uninstallers itself. Settings > Apps opens: uninstall {app.Name} there."), T("Uninstall"), MessageBoxButton.OK, MessageBoxImage.Information);
            MainWindow.OpenUrl("ms-settings:appsfeatures");
            return;
        }
        var (exe, args) = SplitCommand(cmd);
        // "MsiExec.exe /X{…}": Windows' own programs by their full path (never one lying in the current folder)
        if (SystemTools.Resolve(exe) is not { } full)
        {
            Dialogs.Show(Window.GetWindow(this), F($"The uninstaller of {app.Name} is not named with its folder:") + "\n\n" + cmd + "\n\n" + T("Use Settings > Apps instead."), T("Uninstall"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try { Dialogs.Start(new ProcessStartInfo(full, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(full) ?? "" }); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { }   // the UAC prompt was declined
        catch (Exception ex) { Dialogs.Show(Window.GetWindow(this), T("Could not start the uninstaller:") + " " + ex.Message + "\n\n" + T("Use Settings > Apps instead."), T("Uninstall"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    /// <summary>"\"C:\x\unins000.exe\" /SILENT" → exe and arguments; also unquoted "MsiExec.exe /X{...}".</summary>
    internal static (string Exe, string Args) SplitCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            if (end > 0) return (cmd.Substring(1, end - 1), cmd.Substring(end + 1).Trim());
        }
        int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return (cmd.Substring(0, exe + 4), cmd.Substring(exe + 4).Trim());
        return (cmd, "");
    }
}
