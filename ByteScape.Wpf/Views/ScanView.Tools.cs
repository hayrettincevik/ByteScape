using System.Windows;
using System.Windows.Input;
using ByteScape.Core.Compare;
using ByteScape.Core.Config;
using ByteScape.Core.Duplicates;
using ByteScape.Core.Forecast;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using ByteScape.Core.Util;
using ByteScape.Wpf.Windows;

namespace ByteScape.Wpf.Views;

/// <summary>Duplicate files, folder comparison and the folder history of this view.</summary>
public partial class ScanView
{
    // ------------------------------------------------------------------ duplicate files

    private DuplicatesWindow? _duplicates;
    private CancellationTokenSource? _duplicatesCts;

    /// <summary>Command line "duplicates": opens the duplicate files window once the scan is complete.</summary>
    public bool PendingDuplicates
    {
        get => _pendingDuplicates;
        set { _pendingDuplicates = value; if (value && (!Tree.IsLive || Tree.Root.ScanComplete)) Dispatcher.BeginInvoke(RunPendingDuplicates); }
    }
    private bool _pendingDuplicates;

    /// <summary>Command line "duplicatesreport &lt;file&gt;": the groups written once the view is scanned (.csv / .json / text by the name).</summary>
    public string? DuplicatesReportPath { get; set; }

    private void RunPendingDuplicates()
    {
        if (!_pendingDuplicates) return;
        _pendingDuplicates = false;
        ShowDuplicates(fromUser: false);
    }

    /// <summary>The report of a duplicate search in the format its file name asks for.</summary>
    internal static string DuplicatesReport(DuplicateResult r, string file) =>
        file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? DuplicateFinder.ToCsv(r, System.Globalization.CultureInfo.CurrentCulture.TextInfo.ListSeparator is { Length: 1 } s ? s[0] : ',')
        : file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? DuplicateFinder.ToJson(r)
        : DuplicateFinder.ToText(r);

    /// <summary>Looks for duplicate files in this view on a background thread and shows (or refreshes) the window.</summary>
    public async void ShowDuplicates(long minBytes = 1L << 20, bool includeWindows = false, bool fromUser = true)
    {
        if (fromUser) DisarmAuto();
        _duplicatesCts?.Cancel();
        var cts = _duplicatesCts = new CancellationTokenSource();
        if (_duplicates == null)
        {
            _duplicates = new DuplicatesWindow(this) { Owner = Window.GetWindow(this) };
            _duplicates.Closed += (_, _) => { _duplicates = null; _duplicatesCts?.Cancel(); };
            _duplicates.Show();
        }
        var w = _duplicates;
        var root = Model.ViewRoot;
        var sync = Tree.Sync;
        var progress = new Progress<DuplicateProgress>(p => { if (!cts.IsCancellationRequested) w.ShowProgress(p); });
        ShowNote(T("Looking for duplicate files…"), 30);
        try
        {
            var opt = new DuplicateOptions { MinBytes = minBytes, IncludeWindows = includeWindows };
            var result = await Task.Run(() => DuplicateFinder.Find(root, sync, opt, progress, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            Log.Debug($"Duplicates in {result.RootPath}: {result.Groups.Count} groups, {Format.Size(result.WastedBytes)} extra, {result.Candidates} compared, {Format.Size(result.BytesRead)} read ({result.Elapsed.TotalMilliseconds:0} ms)");
            ShowNote(P(result.Groups.Count, "{1} in extra copies of {0:N0} file", "{1} in extra copies of {0:N0} files", Format.Size(result.WastedBytes)), 5);
            w.SetResult(result);
            if (fromUser) w.Activate();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Duplicate search failed: " + ex.Message);
            ShowNote(T("Could not look for duplicates:") + " " + ex.Message);
        }
    }
    // ------------------------------------------------------------------ ready-made filters

    /// <summary>File types (the configured file classes and a few kinds), sizes and ages, as filter conditions.</summary>
    internal static List<(string Group, string Label, string Condition)> FilterPresetList()
    {
        var list = new List<(string, string, string)>();
        foreach (var c in AppConfig.Current.FileClasses) list.Add(("Type", c.DisplayName, ":class:" + c.Name));
        list.Add(("Type", T("Documents"), "*.pdf;*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx;*.odt;*.ods;*.txt;*.rtf;*.md;*.epub"));
        list.Add(("Type", T("Installation files"), "*.msi;*.msix;*.msixbundle;*.appx;*.iso;*setup*.exe;*install*.exe"));
        list.Add(("Type", T("Archives"), "*.zip;*.7z;*.rar;*.tar;*.gz;*.tgz;*.xz;*.zst"));
        list.Add(("Type", T("Temporary files"), "*.tmp;*.temp;~$*;*.crdownload;*.part;*.dmp"));
        list.Add(("Type", T("Virtual machine disks"), "*.vhd;*.vhdx;*.vmdk;*.vdi;*.qcow2"));
        list.Add(("Size", T("Over 10 MB"), ">10mb"));
        list.Add(("Size", T("Over 100 MB"), ">100mb"));
        list.Add(("Size", T("Over 1 GB"), ">1gb"));
        list.Add(("Size", T("Under 1 MB"), "<1mb"));
        list.Add(("Age", T("Changed in the last 7 days"), "<7days"));
        list.Add(("Age", T("Changed in the last month"), "<1month"));
        list.Add(("Age", T("Not changed for a year"), ">1year"));
        list.Add(("Age", T("Not changed for 3 years"), ">3years"));
        list.Add(("Special", T("Known caches and temporary areas"), ":reclaimable"));
        list.Add(("Special", T("Cloud: local copies that can be freed"), ":freeable"));
        list.Add(("Special", T("Cloud: online-only"), ":onlineonly"));
        return list;
    }

    /// <summary>Adds a ready-made condition to the filter (or starts one) and applies it.</summary>
    internal void AddFilterCondition(string condition)
    {
        var t = FilterBox.Text.Trim().TrimEnd(';');
        FilterBox.Text = t.Length == 0 ? condition : t + ";" + condition;
        ApplyFilter();
    }

    private void FilterPresets_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = FilterPresets, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var g in FilterPresetList().GroupBy(p => p.Group))
        {
            var sub = new System.Windows.Controls.MenuItem { Header = T(g.Key switch { "Type" => "File type", "Size" => "Size", "Age" => "Last change", _ => "Special" }) };
            foreach (var (_, label, cond) in g)
            {
                var mi = new System.Windows.Controls.MenuItem { Header = label, InputGestureText = cond.Length > 28 ? cond[..26] + "…" : cond, ToolTip = cond };
                mi.Click += (_, _) => AddFilterCondition(cond);
                sub.Items.Add(mi);
            }
            menu.Items.Add(sub);
        }
        menu.Items.Add(new System.Windows.Controls.Separator());
        var clear = new System.Windows.Controls.MenuItem { Header = T("Clear the filter"), IsEnabled = FilterBox.Text.Length > 0 };
        clear.Click += (_, _) => { FilterBox.Text = ""; ApplyFilter(); };
        menu.Items.Add(clear);
        Dialogs.OpenMenu(menu);
    }

    // ------------------------------------------------------------------ comparing two folders

    private FolderCompareWindow? _folderCompare;
    private CancellationTokenSource? _folderCompareCts;

    /// <summary>Command line "comparefolder &lt;path&gt;": compares this view with that folder once the scan is complete.</summary>
    public string? PendingCompareFolder
    {
        get => _pendingCompareFolder;
        set { _pendingCompareFolder = value; if (value != null && (!Tree.IsLive || Tree.Root.ScanComplete)) Dispatcher.BeginInvoke(RunPendingCompareFolder); }
    }
    private string? _pendingCompareFolder;

    /// <summary>Command line "comparefolderreport &lt;path&gt; &lt;file&gt;": the comparison written once the view is scanned (.csv or text).</summary>
    public (string Other, string File)? CompareFolderReport { get; set; }

    private void RunPendingCompareFolder()
    {
        var other = _pendingCompareFolder; _pendingCompareFolder = null;
        if (other != null) CompareWithFolder(other, fromUser: false);
    }

    /// <summary>Asks for the other folder, then compares <paramref name="left"/> (the zoomed folder by default) with it.</summary>
    public void CompareWithFolder(ContainerNode? left = null)
    {
        left ??= Model.ZoomRoot;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = F($"Compare {left.RealPath()} with…") };
        if (Dialogs.Pick(dlg, Window.GetWindow(this)) != true) return;
        CompareWithFolder(dlg.FolderName, left);
    }

    /// <summary>
    /// The other folder: from this tree when it lies in it and the scan is complete, else scanned on its own (not shown as a
    /// view). A folder inside the other one cannot be compared with it.
    /// </summary>
    internal static FolderCompareResult CompareFolders(ScanTree tree, ContainerNode left, string otherPath, bool contents, CancellationToken token)
    {
        otherPath = DataCache.NormalizePath(otherPath);
        string leftPath = left.RealPath().TrimEnd('\\');
        string other = otherPath.TrimEnd('\\');
        if (other.Equals(leftPath, StringComparison.OrdinalIgnoreCase) || other.StartsWith(leftPath + "\\", StringComparison.OrdinalIgnoreCase) || leftPath.StartsWith(other + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(F($"One folder lies inside the other: choose a folder outside {leftPath}."));
        if (!Directory.Exists(otherPath)) throw new DirectoryNotFoundException(F($"{otherPath} does not exist (or is not reachable)."));
        var opt = new FolderCompareOptions { CompareContents = contents };
        if (tree.IsLive && tree.Root.ScanComplete && tree.Find(otherPath) is ContainerNode inTree)
            return FolderCompare.Compare(left, tree.Sync, inTree, tree.Sync, opt, token);
        var t = new ScanTree(DataCache.CreateRoot(otherPath), live: true);
        t.StartMasterScan();
        try
        {
            while (t.IsMasterScanning) { token.ThrowIfCancellationRequested(); Thread.Sleep(20); }
        }
        catch (OperationCanceledException) { t.StopMasterScan(); throw; }
        return FolderCompare.Compare(left, tree.Sync, t.Root, t.Sync, opt, token);
    }

    public async void CompareWithFolder(string otherPath, ContainerNode? left = null, bool fromUser = true)
    {
        if (fromUser) DisarmAuto();
        left ??= Model.ZoomRoot;
        await RunFolderCompare(otherPath, left, contents: false);
    }

    private async Task RunFolderCompare(string otherPath, ContainerNode left, bool contents)
    {
        _folderCompareCts?.Cancel();
        var cts = _folderCompareCts = new CancellationTokenSource();
        if (_folderCompare == null)
        {
            _folderCompare = new FolderCompareWindow(this, c => RunFolderCompare(otherPath, left, c)) { Owner = Window.GetWindow(this) };
            _folderCompare.Closed += (_, _) => { _folderCompare = null; _folderCompareCts?.Cancel(); };
            _folderCompare.Show();
        }
        var w = _folderCompare;
        w.ShowWorking(contents ? T("Reading and comparing the files of both folders…") : F($"Scanning {otherPath} and comparing…"));
        var tree = Tree;
        try
        {
            var result = await Task.Run(() => CompareFolders(tree, left, otherPath, contents, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            Log.Debug($"Compared {result.LeftPath} with {result.RightPath}: {result.Count(FolderDiffKind.OnlyLeft)} only left, {result.Count(FolderDiffKind.OnlyRight)} only right, {result.Count(FolderDiffKind.Different)} different ({result.Elapsed.TotalMilliseconds:0} ms)");
            w.SetResult(result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Folder comparison failed: " + ex.Message);
            w.ShowWorking(T("Could not compare:") + " " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ folder history

    private HistoryWindow? _history;

    /// <summary>How this drive's folders changed over the complete scans remembered for the forecast, as charts.</summary>
    public void ShowHistory()
    {
        DisarmAuto();
        if (DiskHistory.Current is not { } history || Tree.Root.FullPath.Length < 2 || Tree.Root.FullPath[1] != ':')
        {
            ShowNote(T("Disk usage history is kept for local drives when \"Measure the drives' free space\" is on (Settings > Behavior)."));
            return;
        }
        var drive = Tree.Root.FullPath.Substring(0, 2).ToUpperInvariant();
        var h = FolderHistory.Build(history, drive);
        if (_history == null)
        {
            _history = new HistoryWindow(this) { Owner = Window.GetWindow(this) };
            _history.Closed += (_, _) => _history = null;
            _history.Show();
        }
        _history.SetResult(h);
        _history.Activate();
    }
}
