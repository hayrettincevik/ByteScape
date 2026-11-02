// ByteScape diagnostic harness. Read-only with respect to the application: it only creates and
// removes its own test data under a dedicated folder and drives the public API / off-screen windows.
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ByteScape.Core.Config;
using ByteScape.Core.Export;
using ByteScape.Core.Filtering;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using ByteScape.Core.Snapshot;
using ByteScape.Treemap;
using ByteScape.Wpf;
using IOFile = System.IO.File;

namespace ByteScape.Diag;

internal static partial class Program
{
    private static readonly StringBuilder Report = new();

    private static void Out(string s = "")
    {
        Console.WriteLine(s);
        Report.AppendLine(s);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length == 0) { Console.WriteLine("commands: gen scan ui links sizes deep longpath denied hidden unicode layout leak export drives fat32 tools"); return 2; }
        AppConfig.Replace(new AppConfig());     // defaults, never the user's config file
        var sw = Stopwatch.StartNew();
        try
        {
            switch (args[0])
            {
                case "gen": Gen(args[1], int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4])); break;
                case "scan": Scan(args[1], args.Length > 2 ? int.Parse(args[2]) : 0, args.Length > 3 ? int.Parse(args[3]) : 1); break;
                case "ui": Ui(args[1], args.Length > 2 ? args[2] : null); break;
                case "links": Links(args[1]); break;
                case "sizes": Sizes(args[1]); break;
                case "deep": Deep(args[1], int.Parse(args[2])); break;
                case "longpath": LongPath(args[1]); break;
                case "denied": Denied(args.Skip(1).ToArray()); break;
                case "hidden": Hidden(args[1]); break;
                case "unicode": Unicode(args[1]); break;
                case "layout": Layout(); break;
                case "leak": Leak(args[1], args[2], int.Parse(args[3])); break;
                case "export": Export(args[1], args[2]); break;
                case "drives": Drives(); break;
                case "tools": Tools(); break;
                case "unc": Unc(args[1]); break;
                case "filterperf": FilterPerf(args[1]); break;
                case "apps": Apps(args.Skip(1).ToArray()); break;
                case "cloud": Cloud(args[1]); break;
                case "diskspeed": DiskSpeed(args.Skip(1).ToArray()); break;
                case "dev": Dev(args[1]); break;
                case "resume": ResumeCheck(args[1], args.Length > 2 ? int.Parse(args[2]) : 4000); break;
                case "diskorder": DiskOrder(args[1]); break;
                case "journal": JournalCheck(args[1], args.Length > 2 ? int.Parse(args[2]) : 20); break;
                case "cleanup": CleanupCheck(args[1]); break;
                case "duplicates": DuplicatesCheck(args[1], args.Length > 2 ? long.Parse(args[2]) : 1L << 20); break;
                case "reports": ReportsCheck(args[1], args[2]); break;
                case "comparefolders": CompareFoldersCheck(args[1], args[2], args.Length > 3 && args[3] == "contents"); break;
                case "hardlinks": HardLinksCheck(args[1]); break;
                case "games": Games(args.Length > 1 ? args[1] : @"C:\Program Files (x86)\Steam"); break;
                case "scanspeed": ScanSpeed(args[1], args.Length > 2 ? args[2].Split(',').Select(int.Parse).ToArray() : new[] { 1, 0 }); break;
                case "mftcheck": MftCheck(args.Length > 1 ? args[1] : "C:", args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "bytescape-mftcheck.txt"), args.Contains("ads")); break;
                case "mftdata": MftData(args[1], args.Length > 2 && args[2] == "clean"); break;
                case "scale": Scale(int.Parse(args[1]), args.Contains("ui")); break;
                case "crashtest": CrashTest(args.Length > 1 ? args[1] : ""); break;
                case "locstatus": LocStatus(); break;
                case "locbatch": LocBatch(args[1], int.Parse(args[2]), int.Parse(args[3]), args[4]); break;
                case "locmerge": LocMerge(args[1], args[2]); break;
                case "locreview": LocReview(args[1], args[2]); break;
                default: Console.WriteLine("unknown command"); return 2;
            }
        }
        catch (Exception ex)
        {
            Out("!!! HARNESS EXCEPTION: " + ex);
        }
        Out($"[{args[0]} finished in {sw.Elapsed.TotalSeconds:0.0}s]");
        var dir = Environment.GetEnvironmentVariable("DIAG_OUT") ?? Path.GetTempPath();
        IOFile.AppendAllText(Path.Combine(dir, "diag_" + args[0] + ".txt"), Report.ToString());
        return 0;
    }

    // ------------------------------------------------------------------ helpers

    private static readonly Process Me = Process.GetCurrentProcess();

    private static string Mem()
    {
        Me.Refresh();
        return $"ws={Me.WorkingSet64 / 1048576}MB private={Me.PrivateMemorySize64 / 1048576}MB gc={GC.GetTotalMemory(false) / 1048576}MB handles={Me.HandleCount} threads={Me.Threads.Count}";
    }

    private static long Peak() { Me.Refresh(); return Me.PeakWorkingSet64 / 1048576; }

    private static void SetInternal(object o, string prop, object value)
    {
        var p = o.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        p.SetValue(o, value);
    }

    private static FileNode SynthFile(ContainerNode p, string name, long bytes)
    {
        var f = new FileNode(name);
        SetInternal(f, "LogicalSize", bytes); SetInternal(f, "DiskSize", bytes);
        p.AddChild(f);
        return f;
    }

    private static FolderNode SynthFolder(ContainerNode p, string name)
    {
        var f = new FolderNode(name);
        SetInternal(f, "Enumerated", true); SetInternal(f, "ScanComplete", true);
        p.AddChild(f);
        return f;
    }

    private static DriveNode SynthRoot(string name = "X:\\")
    {
        var r = new DriveNode(name);
        SetInternal(r, "IsDrive", true); SetInternal(r, "TotalBytes", 1L << 40); SetInternal(r, "FreeBytes", 1L << 30);
        SetInternal(r, "Enumerated", true); SetInternal(r, "ScanComplete", true);
        return r;
    }

    private static void Run(string exe, string args, int timeoutMs = 60000)
    {
        var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
        string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
        p.WaitForExit(timeoutMs);
        if (p.ExitCode != 0) Out($"   [{exe} {args}] exit {p.ExitCode}: {(o + e).Trim()}");
    }

    private static (ScanTree tree, ContainerNode node, TimeSpan elapsed, long peakMB) ScanSync(string path, int timeoutSec = 3600, Action<ScanTree>? during = null)
    {
        var (tree, node) = DataCache.Acquire(path);
        var sw = Stopwatch.StartNew();
        if (!tree.IsMasterScanning && !tree.Root.ScanComplete) tree.StartMasterScan();
        while (tree.IsMasterScanning && sw.Elapsed.TotalSeconds < timeoutSec) { during?.Invoke(tree); Thread.Sleep(100); }
        if (tree.IsMasterScanning) { Out("   !!! scan timeout, stopping"); tree.StopMasterScan(); while (tree.IsMasterScanning) Thread.Sleep(50); }
        return (tree, node, sw.Elapsed, Peak());
    }

    private static string Fmt(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.00}GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0.00}MB" : b >= 1024 ? $"{b / 1024.0:0.0}KB" : b + "b";

    private static void SafeDelete(string path)
    {
        try { DeleteTree(path); }
        catch (Exception ex) { Out("   cleanup problem for " + path + ": " + ex.Message); }
    }

    /// <summary>
    /// Deletes a folder tree without ever entering a junction or symbolic link (a recursive enumeration would walk into
    /// them): links are removed as links, read-only / hidden / system entries and names ending in a dot are handled.
    /// </summary>
    private static void DeleteTree(string path)
    {
        var x = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + path;
        if (!Directory.Exists(x)) return;
        var one = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        foreach (var d in Directory.EnumerateDirectories(x, "*", one).ToList())
        {
            if ((IOFile.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) Directory.Delete(d);   // the link itself, never what it points to
            else DeleteTree(d);
        }
        foreach (var f in Directory.EnumerateFiles(x, "*", one).ToList())
        {
            IOFile.SetAttributes(f, FileAttributes.Normal);
            IOFile.Delete(f);
        }
        new DirectoryInfo(x).Attributes = FileAttributes.Normal;
        Directory.Delete(x);
    }

    // ------------------------------------------------------------------ dataset generation

    private sealed class Expected { public int Files; public int Folders; public long Logical; public long Disk; public int Cluster; public int MaxDepth; }

    private static void Gen(string root, int files, int folders, int depth)
    {
        Out($"== GEN {root}: {files} files, {folders} folders, depth {depth}");
        SafeDelete(root);
        Directory.CreateDirectory(root);
        int cluster = DataCache.CreateRoot(root).ClusterSize;
        var rnd = new Random(12345);
        // folder tree: a chain of `depth` levels first, then the rest spread as a wide tree under level 1..depth
        var dirs = new List<string>();
        string chain = root;
        for (int i = 1; i <= depth; i++) { chain = Path.Combine(chain, "d" + i); dirs.Add(chain); }
        var parents = new List<string>(dirs);
        int idx = 0;
        while (dirs.Count < folders)
        {
            var parent = parents[idx % parents.Count]; idx++;
            var d = Path.Combine(parent, "f" + dirs.Count);
            dirs.Add(d);
            if (dirs.Count % 7 == 0) parents.Add(d);   // some nesting
        }
        foreach (var d in dirs) Directory.CreateDirectory(d);
        // files: spread evenly, 2% get real content (1..64 KB), the rest are empty
        long logical = 0, disk = 0;
        int per = Math.Max(1, files / Math.Max(1, dirs.Count));
        var sizes = new long[files];
        for (int i = 0; i < files; i++) sizes[i] = i % 50 == 0 ? rnd.Next(1, 64) * 1024 : 0;
        var buf = new byte[64 * 1024];
        Parallel.For(0, dirs.Count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, di =>
        {
            int from = di * per, to = di == dirs.Count - 1 ? files : Math.Min(files, from + per);
            for (int i = from; i < to; i++)
            {
                var p = Path.Combine(dirs[di], "file" + i + (i % 3 == 0 ? ".txt" : i % 3 == 1 ? ".dat" : ".log"));
                using var fs = new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.None);
                if (sizes[i] > 0) fs.Write(buf, 0, (int)sizes[i]);
            }
        });
        for (int i = 0; i < files; i++) { logical += sizes[i]; disk += sizes[i] == 0 ? 0 : (sizes[i] + cluster - 1) / cluster * cluster; }
        var exp = new Expected { Files = files, Folders = dirs.Count, Logical = logical, Disk = disk, Cluster = cluster, MaxDepth = depth };
        IOFile.WriteAllText(Path.Combine(root, "..", Path.GetFileName(root) + ".expected.json"), JsonSerializer.Serialize(exp, new JsonSerializerOptions { IncludeFields = true }));
        Out($"   created: files={files} folders={dirs.Count} logical={Fmt(logical)} expectedDisk={Fmt(disk)} cluster={cluster}");
    }

    // ------------------------------------------------------------------ in-process scan metrics

    private static void Scan(string root, int cancelAfterMs, int repeat)
    {
        Out($"== SCAN {root} (cancelAfter={cancelAfterMs}ms, repeat={repeat})");
        Expected? exp = null;
        var expPath = Path.Combine(root, "..", Path.GetFileName(root) + ".expected.json");
        if (IOFile.Exists(expPath)) exp = JsonSerializer.Deserialize<Expected>(IOFile.ReadAllText(expPath), new JsonSerializerOptions { IncludeFields = true });
        Out("   before: " + Mem());
        for (int r = 1; r <= repeat; r++)
        {
            int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
            var cpu0 = Me.TotalProcessorTime;
            var (tree, node) = DataCache.Acquire(root);
            var sw = Stopwatch.StartNew();
            tree.StartMasterScan();
            long lastFiles = 0; var lastT = sw.Elapsed; bool cancelled = false; TimeSpan cancelAt = default, stoppedAt = default;
            int samples = 0; long peakHandles = 0, maxThreads = 0;
            while (tree.IsMasterScanning)
            {
                Thread.Sleep(250);
                samples++;
                Me.Refresh();
                peakHandles = Math.Max(peakHandles, Me.HandleCount); maxThreads = Math.Max(maxThreads, Me.Threads.Count);
                if (samples % 8 == 0)
                {
                    long f = tree.Root.FileCount;
                    Out($"   t={sw.Elapsed.TotalSeconds,6:0.0}s files={f,8} folders={tree.Root.FolderCount,6} rate={(f - lastFiles) / Math.Max(0.001, (sw.Elapsed - lastT).TotalSeconds),8:0} f/s  {Mem()}");
                    lastFiles = f; lastT = sw.Elapsed;
                }
                if (cancelAfterMs > 0 && !cancelled && sw.ElapsedMilliseconds >= cancelAfterMs)
                {
                    cancelled = true; cancelAt = sw.Elapsed;
                    tree.StopMasterScan();
                    var c = Stopwatch.StartNew();
                    while (tree.IsMasterScanning && c.ElapsedMilliseconds < 30000) Thread.Sleep(1);
                    stoppedAt = sw.Elapsed;
                    Out($"   CANCEL requested at {cancelAt.TotalSeconds:0.00}s -> state {tree.MasterState} after {(stoppedAt - cancelAt).TotalMilliseconds:0} ms; files so far {tree.Root.FileCount}");
                }
            }
            sw.Stop();
            var cpu = (Me.TotalProcessorTime - cpu0).TotalSeconds;
            long files = tree.Root.FileCount, folders = tree.Root.FolderCount;
            Out($"   run {r}: state={tree.MasterState} time={sw.Elapsed.TotalSeconds:0.00}s files={files} folders={folders} files/s={files / Math.Max(0.001, sw.Elapsed.TotalSeconds):0} dirs/s={folders / Math.Max(0.001, sw.Elapsed.TotalSeconds):0} cpu={cpu:0.0}s ({cpu / Math.Max(0.001, sw.Elapsed.TotalSeconds) * 100 / Environment.ProcessorCount:0}% of {Environment.ProcessorCount} cores) peakWS={Peak()}MB peakHandles={peakHandles} maxThreads={maxThreads} GC0/1/2={GC.CollectionCount(0) - gc0}/{GC.CollectionCount(1) - gc1}/{GC.CollectionCount(2) - gc2}");
            Out($"   tree: logical={Fmt(tree.Root.LogicalSize)} disk={Fmt(tree.Root.DiskSize)} denied={tree.Root.Descendants(true).OfType<ContainerNode>().Count(c => c.AccessDenied)}");
            var swk = Stopwatch.StartNew();
            var summary = Core.Knowledge.KnowledgeBase.Summarize(tree.Root, n => n.DiskSize, CancellationToken.None, tree.Sync);
            Out($"   knowledge summary: {swk.ElapsedMilliseconds} ms, reclaimable={Fmt(summary.TotalBytes)} in {summary.Items.Count} locations");
            if (exp != null && !cancelled)
            {
                Out($"   EXPECTED files={exp.Files} folders={exp.Folders} logical={Fmt(exp.Logical)} disk={Fmt(exp.Disk)} -> {(files == exp.Files && folders == exp.Folders && tree.Root.LogicalSize == exp.Logical && tree.Root.DiskSize == exp.Disk ? "MATCH" : "MISMATCH")}");
            }
            if (cancelled)
            {
                // is the scan thread really gone and is the tree quiet?
                long f1 = tree.Root.FileCount; Thread.Sleep(1500); long f2 = tree.Root.FileCount;
                var cpuA = Me.TotalProcessorTime; Thread.Sleep(1000); var cpuB = Me.TotalProcessorTime;
                Out($"   after cancel: files {f1} -> {f2} (still growing: {f2 != f1}), cpu in idle second = {(cpuB - cpuA).TotalMilliseconds:0} ms, scan threads alive = {Me.Threads.Count}");
                Out("   restart after cancel: " + (Restart(tree) ? "OK (second scan started and ran)" : "FAILED"));
            }
            DataCache.Release(tree);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Out($"   after release+GC: {Mem()}");
        }
    }

    private static bool Restart(ScanTree tree)
    {
        tree.StartMasterScan();
        var sw = Stopwatch.StartNew();
        bool started = tree.IsMasterScanning;
        while (tree.IsMasterScanning && sw.ElapsedMilliseconds < 120000) Thread.Sleep(50);
        return started && tree.MasterState == ScanState.Completed;
    }

    // ------------------------------------------------------------------ UI responsiveness (off-screen MainWindow)

    private static void Ui(string root, string? second)
    {
        Out($"== UI {root}" + (second != null ? " then " + second : ""));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/ByteScape;component/Themes/Theme.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/ByteScape;component/Themes/Styles.xaml") });
        ThemeManager.Initialize();
        var main = new MainWindow(new List<CommandLine.ViewRequest>(), false, openStartDialog: false)
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, Width = 1400, Height = 900 };
        main.Show();
        var lags = new List<double>();
        var lagTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(50) };
        var last = Stopwatch.StartNew();
        lagTimer.Tick += (_, _) => { lags.Add(last.Elapsed.TotalMilliseconds - 50); last.Restart(); };
        lagTimer.Start();
        var clock = Stopwatch.StartNew();
        var view = main.OpenScan(root)!;
        int navigations = 0; double maxNavMs = 0; string phase = "scan";
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        ByteScape.Wpf.Views.ScanView? view2 = null;
        poll.Tick += (_, _) =>
        {
            // navigate while the scan runs: zoom into the biggest folder and back
            var kids = view.Model.ZoomRoot.Children.OfType<ContainerNode>().OrderByDescending(c => c.DiskSize).ToList();
            var t = Stopwatch.StartNew();
            if (kids.Count > 0 && !view.CanGoUp) view.ZoomTo(kids[0]);
            else if (view.CanGoUp) view.GoUp();
            MapOf(view).MarkDirty();
            MapOf(view).UpdateLayout();
            navigations++; maxNavMs = Math.Max(maxNavMs, t.Elapsed.TotalMilliseconds);
            if (phase == "scan" && !view.Tree.IsMasterScanning)
            {
                phase = "done";
                Out($"   scan finished at {clock.Elapsed.TotalSeconds:0.0}s: files={view.Tree.Root.FileCount} folders={view.Tree.Root.FolderCount}  items in layout={MapOf(view).ItemCount}");
                Summ("during scan", lags); lags.Clear();
                if (second != null)
                {
                    phase = "second";
                    Out("   opening second scan while first tree stays open: " + second);
                    view2 = main.OpenScan(second)!;
                    Out($"   views={main.Views.Count()} first tree scanning={view.Tree.IsMasterScanning} second tree scanning={view2.Tree.IsMasterScanning} same tree={ReferenceEquals(view.Tree, view2.Tree)}");
                    // cancel the second scan after 1.5 s through the toolbar action
                    var cancelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                    cancelTimer.Tick += (_, _) =>
                    {
                        cancelTimer.Stop();
                        var c = Stopwatch.StartNew();
                        view2.ToggleMasterScan();
                        while (view2.Tree.IsMasterScanning && c.ElapsedMilliseconds < 10000) Thread.Sleep(1);
                        Out($"   toolbar stop on second view: state={view2.Tree.MasterState} after {c.ElapsedMilliseconds} ms, files={view2.Tree.Root.FileCount}");
                        var c2 = Stopwatch.StartNew();
                        view2.ToggleMasterScan();
                        Out($"   restarted: scanning={view2.Tree.IsMasterScanning}");
                        phase = "second-running";
                    };
                    cancelTimer.Start();
                }
                else app.Dispatcher.BeginInvoke(() => Finish(app, lags, view, navigations, maxNavMs));
            }
            else if (phase == "second-running" && !view2!.Tree.IsMasterScanning)
            {
                Out($"   second scan completed: files={view2.Tree.Root.FileCount} state={view2.Tree.MasterState}");
                Summ("second scan", lags);
                app.Dispatcher.BeginInvoke(() => Finish(app, lags, view, navigations, maxNavMs));
                phase = "end";
            }
        };
        poll.Start();
        Out("   " + Mem());
        app.Run();
    }

    private static void Finish(Application app, List<double> lags, ByteScape.Wpf.Views.ScanView view, int navigations, double maxNavMs)
    {
        // measure a full layout+render pass on the finished tree
        var t = Stopwatch.StartNew();
        view.GoHome(); MapOf(view).MarkDirty(); MapOf(view).UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(1400, 900, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(MapOf(view));
        var tl = Stopwatch.StartNew(); var lay = view.Model.BuildLayout(new RectangleF(0, 0, 1400, 900), 16, AppConfig.Current); var buildMs = tl.Elapsed.TotalMilliseconds; int cnt = 0; Walk(lay, _ => cnt++);
        Out($"   BuildLayout alone: {buildMs:0} ms for {cnt} items (children of zoom root: {view.Model.ZoomRoot.ChildCount})");
        Out($"   final layout+render of home view (cold): {t.Elapsed.TotalMilliseconds:0} ms, items={MapOf(view).ItemCount}; navigations during scan={navigations}, slowest navigation={maxNavMs:0} ms");
        for (int i = 0; i < 3; i++) { t.Restart(); MapOf(view).InvalidateVisual(); MapOf(view).UpdateLayout(); bmp.Render(MapOf(view)); Out($"   re-render {i + 1} (warm text cache, same layout): {t.Elapsed.TotalMilliseconds:0} ms"); }
        t.Restart(); MapOf(view).MarkDirty(); MapOf(view).UpdateLayout(); bmp.Render(MapOf(view)); Out($"   relayout+render (warm): {t.Elapsed.TotalMilliseconds:0} ms");
        for (int i = 0; i < 3; i++) { t.Restart(); MapOf(view).InvalidateVisual(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render); Out($"   OnRender pass only (UI thread, no rasterization) {i + 1}: {t.Elapsed.TotalMilliseconds:0} ms"); }
        Out("   " + Mem());
        app.Shutdown();
    }

    private static void Summ(string label, List<double> lags)
    {
        if (lags.Count == 0) { Out($"   UI lag {label}: no samples"); return; }
        var s = lags.OrderBy(x => x).ToList();
        Out($"   UI lag {label}: samples={s.Count} avg={s.Average():0.0}ms p95={s[(int)(s.Count * 0.95)]:0}ms max={s[^1]:0}ms  >100ms:{s.Count(x => x > 100)}  >500ms:{s.Count(x => x > 500)}  >2000ms:{s.Count(x => x > 2000)}");
    }

    // ------------------------------------------------------------------ links / reparse points

    private static void Links(string root)
    {
        Out("== LINKS " + root);
        SafeDelete(root); Directory.CreateDirectory(root);
        string a = Path.Combine(root, "A"), b = Path.Combine(a, "B"), c = Path.Combine(b, "C");
        Directory.CreateDirectory(c);
        IOFile.WriteAllBytes(Path.Combine(a, "a.bin"), new byte[100_000]);
        IOFile.WriteAllBytes(Path.Combine(b, "b.bin"), new byte[200_000]);
        IOFile.WriteAllBytes(Path.Combine(c, "c.bin"), new byte[300_000]);
        Run("cmd.exe", $"/c mklink /J \"{Path.Combine(b, "JunctionToA")}\" \"{a}\"");
        Run("cmd.exe", $"/c mklink /J \"{Path.Combine(c, "JunctionToB")}\" \"{b}\"");
        Run("cmd.exe", $"/c mklink /D \"{Path.Combine(root, "SymDirToA")}\" \"{a}\"");
        Run("cmd.exe", $"/c mklink \"{Path.Combine(root, "SymFileToA")}\" \"{Path.Combine(a, "a.bin")}\"");
        Run("cmd.exe", $"/c mklink /H \"{Path.Combine(root, "HardToC")}\" \"{Path.Combine(c, "c.bin")}\"");
        foreach (var e in Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, MaxRecursionDepth = 3 }))
            Out($"   fs: {e.Substring(root.Length)}  attrs={IOFile.GetAttributes(e)}");
        long realBytes = 600_000, cluster = DataCache.CreateRoot(root).ClusterSize;
        long RoundUp(long v) => (v + cluster - 1) / cluster * cluster;
        Out($"   physical unique content: 600000 bytes (3 files), hard link adds 0, cluster={cluster}");

        foreach (bool follow in new[] { false, true })
        {
            AppConfig.Current.FollowReparsePoints = follow;
            Out($"-- FollowReparsePoints={follow}");
            var started = Stopwatch.StartNew();
            int maxDepth = 0;
            var (tree, node, elapsed, peak) = ScanSync(root, timeoutSec: 30, during: t => { lock (t.Sync) maxDepth = Math.Max(maxDepth, t.Root.Descendants().Select(n => n.Level).DefaultIfEmpty(0).Max()); });
            lock (tree.Sync)
            {
                var all = tree.Root.Descendants().ToList();
                Out($"   state={tree.MasterState} time={elapsed.TotalSeconds:0.00}s nodes={all.Count} files={tree.Root.FileCount} folders={tree.Root.FolderCount} maxDepth={all.Select(n => n.Level).DefaultIfEmpty(0).Max()} disk={tree.Root.DiskSize} logical={tree.Root.LogicalSize} (unique content disk={RoundUp(100000) + RoundUp(200000) + RoundUp(300000)})");
                foreach (var n in all.Where(n => n.Level <= 3)) Out($"     L{n.Level} {n.Kind,-6} {n.Name,-14} disk={n.DiskSize,8} logical={n.LogicalSize,8}");
                var names = all.Select(n => n.Name).ToList();
                Out($"   'a.bin' occurrences in tree: {names.Count(n => n == "a.bin")}   'JunctionToA' folders: {names.Count(n => n == "JunctionToA")}   HardToC present: {names.Contains("HardToC")}   SymFileToA present: {names.Contains("SymFileToA")}  SymDirToA present: {names.Contains("SymDirToA")}");
            }
            DataCache.Release(tree);
        }
        AppConfig.Current.FollowReparsePoints = false;
        SafeDelete(root);
    }

    // ------------------------------------------------------------------ folder size correctness

    private static void Sizes(string root)
    {
        Out("== SIZES " + root);
        SafeDelete(root); Directory.CreateDirectory(root);
        string fa = Path.Combine(root, "FolderA"), fb = Path.Combine(root, "FolderB");
        Directory.CreateDirectory(fa); Directory.CreateDirectory(fb);
        void Big(string p, int mb) { using var fs = new FileStream(p, FileMode.Create); fs.SetLength(mb * 1024L * 1024); fs.Seek(-1, SeekOrigin.End); fs.WriteByte(1); }
        Big(Path.Combine(fa, "100MB.bin"), 100); Big(Path.Combine(fb, "200MB.bin"), 200); Big(Path.Combine(fb, "500MB.bin"), 500);
        IOFile.WriteAllBytes(Path.Combine(fa, "dup1.bin"), new byte[50_000]); IOFile.Copy(Path.Combine(fa, "dup1.bin"), Path.Combine(fb, "dup2.bin"));
        Run("cmd.exe", $"/c mklink /J \"{Path.Combine(root, "LinkFolder")}\" \"{fa}\"");
        Run("cmd.exe", $"/c mklink /H \"{Path.Combine(root, "HardLinkTo100MB.bin")}\" \"{Path.Combine(fa, "100MB.bin")}\"");
        IOFile.WriteAllBytes(Path.Combine(root, "hidden.bin"), new byte[10_000]); IOFile.SetAttributes(Path.Combine(root, "hidden.bin"), FileAttributes.Hidden);
        IOFile.WriteAllBytes(Path.Combine(root, "system.bin"), new byte[10_000]); IOFile.SetAttributes(Path.Combine(root, "system.bin"), FileAttributes.System | FileAttributes.Hidden);
        IOFile.WriteAllBytes(Path.Combine(root, "tiny.txt"), new byte[10]);
        IOFile.WriteAllBytes(Path.Combine(root, "zero.txt"), Array.Empty<byte>());
        // sparse file: 64 MB logical, ~0 allocated
        string sparse = Path.Combine(root, "sparse.bin");
        Run("cmd.exe", $"/c fsutil file createnew \"{sparse}\" 0 && fsutil sparse setflag \"{sparse}\" && fsutil sparse setrange \"{sparse}\" 0 67108864");
        using (var fs = new FileStream(sparse, FileMode.Open)) { fs.SetLength(64 * 1024 * 1024); }
        long cluster = DataCache.CreateRoot(root).ClusterSize;
        long R(long v) => v <= 0 ? 0 : (v + cluster - 1) / cluster * cluster;
        var expected = new Dictionary<string, (long logical, long disk)>
        {
            ["FolderA"] = (100 * 1048576L + 50_000, R(100 * 1048576L) + R(50_000)),
            ["FolderB"] = (700 * 1048576L + 50_000, R(700 * 1048576L) + R(50_000)),
            ["hidden.bin"] = (10_000, R(10_000)), ["system.bin"] = (10_000, R(10_000)), ["tiny.txt"] = (10, R(10)), ["zero.txt"] = (0, 0),
            // a second name of FolderA\100MB.bin: counted once (at FolderA, first in path order) unless the option is off
            ["sparse.bin"] = (64 * 1048576L, R(0)), ["HardLinkTo100MB.bin"] = (100 * 1048576L, AppConfig.Current.CountHardLinksOnce ? 0 : R(100 * 1048576L)),
        };
        var (tree, node, elapsed, _) = ScanSync(root, 120);
        Out($"   scan {tree.MasterState} in {elapsed.TotalSeconds:0.0}s, cluster={cluster}");
        Out("   Name                 | Expected logical | Expected disk | App logical | App disk | Result");
        lock (tree.Sync)
        {
            foreach (var (name, (el, ed)) in expected)
            {
                var n = tree.Root.FindChild(name);
                string res = n == null ? "MISSING" : (n.LogicalSize == el && n.DiskSize == ed ? "OK" : "DIFF");
                Out($"   {name,-20} | {el,16} | {ed,13} | {(n?.LogicalSize.ToString() ?? "-"),11} | {(n?.DiskSize.ToString() ?? "-"),8} | {res}");
            }
            Out($"   LinkFolder (junction) present: {tree.Root.FindChild("LinkFolder") != null} (default FollowReparsePoints=false -> expected absent)");
            long sumChildren = tree.Root.Children.Sum(c => c.DiskSize);
            Out($"   root disk={tree.Root.DiskSize} sum(children)={sumChildren} consistent={tree.Root.DiskSize == sumChildren}; root logical={tree.Root.LogicalSize}");
            var du = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).Select(f => new FileInfo(f).Length).Sum();
            Out($"   OS logical sum (incl. junction content, hard link twice): {du}; app logical={tree.Root.LogicalSize}");
            var hard = tree.Root.FindChild("HardLinkTo100MB.bin");
            Out($"   hard link counted as separate 100MB: {hard != null && hard.DiskSize > 0} (count hard links once: {AppConfig.Current.CountHardLinksOnce}; off = every name counted)");
        }
        DataCache.Release(tree);
        SafeDelete(root);
    }

    // ------------------------------------------------------------------ depth / long paths

    private static void Deep(string root, int levels)
    {
        Out($"== DEEP {root} levels={levels}");
        SafeDelete(root); Directory.CreateDirectory(root);
        string p = root;
        for (int i = 1; i <= levels; i++) { p = Path.Combine(p, "L" + i); Directory.CreateDirectory(@"\\?\" + p); }
        IOFile.WriteAllBytes(@"\\?\" + Path.Combine(p, "bottom.txt"), new byte[12_345]);
        Out($"   deepest path length = {Path.Combine(p, "bottom.txt").Length} chars");
        var (tree, node, elapsed, _) = ScanSync(root, 120);
        lock (tree.Sync)
        {
            var bottom = tree.Root.Descendants().FirstOrDefault(n => n.Name == "bottom.txt");
            Out($"   state={tree.MasterState} time={elapsed.TotalSeconds:0.00}s folders={tree.Root.FolderCount} files={tree.Root.FileCount} bottom found={bottom != null} level={bottom?.Level} fullPathLen={bottom?.FullPath.Length} disk={tree.Root.DiskSize} logical={tree.Root.LogicalSize}");
            Out($"   denied folders: {tree.Root.Descendants().OfType<ContainerNode>().Count(c => c.AccessDenied)}");
            // exercise the recursive pieces of the code on this depth
            var vm = new ViewModel(tree, tree.Root) { DetailLevel = 3 };
            try { var lay = vm.BuildLayout(new RectangleF(0, 0, 1200, 800), 16, AppConfig.Current); Out("   BuildLayout OK"); } catch (Exception ex) { Out("   BuildLayout FAILED: " + ex.GetType().Name + " " + ex.Message); }
        }
        {
            var vm = new ViewModel(tree, tree.Root) { DetailLevel = 3 };
            try { vm.SetFilter("*.txt"); WaitFilter(vm, 20000); Out($"   Filter OK: filtered size={vm.SizeOf(tree.Root)}"); } catch (Exception ex) { Out("   Filter FAILED: " + ex.Message); }
            try { var ex2 = new Exporter(ExportConfig.FindByName("File paths")!, tree.Root, vm.IsIncluded, tree.Sync, vm.SizeOf); var w = new StringWriter(); ex2.ExportText(w, CancellationToken.None); Out($"   Export OK: {w.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} lines, longest line {w.ToString().Split('\n').Max(l => l.Length)} chars"); } catch (Exception ex) { Out("   Export FAILED: " + ex.Message); }
            try { var sns = Path.Combine(root, "..", "deep.sns"); SnsFormat.Write(sns, tree.Root, _ => true); var back = SnsFormat.Read(sns); Out($"   Snapshot OK: {back.Descendants().Count()} nodes"); IOFile.Delete(sns); } catch (Exception ex) { Out("   Snapshot FAILED: " + ex.GetType().Name + " " + ex.Message); }
            vm.Dispose();
        }
        DataCache.Release(tree);
        try { Directory.Delete(@"\\?\" + root, true); } catch (Exception ex) { Out("   cleanup: " + ex.Message); }
    }

    private static void LongPath(string root)
    {
        Out("== LONGPATH " + root);
        SafeDelete(root); Directory.CreateDirectory(root);
        string seg = new string('x', 100);
        string d1 = Path.Combine(root, seg, seg);                      // ~ 250-270 chars with file
        string d2 = Path.Combine(root, seg, seg, seg, seg, seg);      // 500+
        Directory.CreateDirectory(@"\\?\" + d1); Directory.CreateDirectory(@"\\?\" + d2);
        string f1 = Path.Combine(d1, "near260.txt"), f2 = Path.Combine(d2, "over500.txt");
        IOFile.WriteAllBytes(@"\\?\" + f1, new byte[1000]); IOFile.WriteAllBytes(@"\\?\" + f2, new byte[2000]);
        Out($"   f1 length={f1.Length}  f2 length={f2.Length}  (LongPathsEnabled registry = {Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", null)})");
        var (tree, node, elapsed, _) = ScanSync(root, 60);
        lock (tree.Sync)
        {
            var n1 = tree.Root.Descendants().FirstOrDefault(n => n.Name == "near260.txt");
            var n2 = tree.Root.Descendants().FirstOrDefault(n => n.Name == "over500.txt");
            Out($"   state={tree.MasterState} files={tree.Root.FileCount} near260 found={n1 != null} over500 found={n2 != null} denied folders={tree.Root.Descendants().OfType<ContainerNode>().Count(c => c.AccessDenied)}");
            Out($"   full paths resolve: {n2?.FullPath.Length} chars; Directory.Exists(plain)={Directory.Exists(d2)} File.Exists(plain)={IOFile.Exists(f2)}");
        }
        DataCache.Release(tree);
        try { Directory.Delete(@"\\?\" + root, true); } catch (Exception ex) { Out("   cleanup: " + ex.Message); }
    }

    // ------------------------------------------------------------------ access denied

    private static void Denied(string[] paths)
    {
        Out("== DENIED " + string.Join(" | ", paths));
        var msgs = new List<string>();
        Core.Util.Log.Added += e => { if (e.Level is Core.Util.LogLevel.Warn or Core.Util.LogLevel.Error) msgs.Add(e.Message); };
        foreach (var p in paths)
        {
            msgs.Clear();
            Out("-- " + p);
            try
            {
                var (tree, node, elapsed, _) = ScanSync(p, 900);
                lock (tree.Sync)
                {
                    var denied = tree.Root.Descendants(true).OfType<ContainerNode>().Where(c => c.AccessDenied).ToList();
                    Out($"   state={tree.MasterState} time={elapsed.TotalSeconds:0.0}s files={tree.Root.FileCount} folders={tree.Root.FolderCount} disk={Fmt(tree.Root.DiskSize)} deniedFolders={denied.Count} rootDenied={tree.Root.AccessDenied} warnings={msgs.Count}");
                    foreach (var d in denied.Take(5)) Out("     denied: " + d.FullPath);
                    foreach (var m in msgs.Take(3)) Out("     log: " + m);
                    if (tree.Root.IsDrive) Out($"   drive: total={Fmt(tree.Root.TotalBytes)} free={Fmt(tree.Root.FreeBytes)} scanned={Fmt(tree.Root.DiskSize)} unknown={Fmt(tree.Root.UnknownBytes)}");
                }
                DataCache.Release(tree);
            }
            catch (Exception ex) { Out("   EXCEPTION escaped to caller: " + ex.GetType().Name + " " + ex.Message); }
        }
    }

    // ------------------------------------------------------------------ hidden / system

    private static void Hidden(string root)
    {
        Out("== HIDDEN " + root);
        SafeDelete(root); Directory.CreateDirectory(root);
        IOFile.WriteAllBytes(Path.Combine(root, "visible.bin"), new byte[40_000]);
        IOFile.WriteAllBytes(Path.Combine(root, "hidden.bin"), new byte[30_000]); IOFile.SetAttributes(Path.Combine(root, "hidden.bin"), FileAttributes.Hidden);
        IOFile.WriteAllBytes(Path.Combine(root, "system.bin"), new byte[20_000]); IOFile.SetAttributes(Path.Combine(root, "system.bin"), FileAttributes.System);
        IOFile.WriteAllBytes(Path.Combine(root, "hs.bin"), new byte[10_000]); IOFile.SetAttributes(Path.Combine(root, "hs.bin"), FileAttributes.System | FileAttributes.Hidden);
        var hd = Path.Combine(root, "HiddenDir"); Directory.CreateDirectory(hd); IOFile.WriteAllBytes(Path.Combine(hd, "inside.bin"), new byte[50_000]); IOFile.SetAttributes(hd, FileAttributes.Hidden | FileAttributes.Directory);
        var (tree, node, _, _) = ScanSync(root, 60);
        using var vm = new ViewModel(tree, tree.Root);
        lock (tree.Sync)
        {
            foreach (var n in tree.Root.Descendants()) Out($"   {n.Name,-12} kind={n.Kind,-6} disk={n.DiskSize,6} attrs={(n is FileNode f ? f.Attributes : (n as FolderNode)?.Attributes)}");
            Out($"   root disk={tree.Root.DiskSize} sum(children)={tree.Root.Children.Sum(c => c.DiskSize)} files={tree.Root.FileCount} (expected 5)");
        }
        foreach (var flt in new[] { ":attr:h", ":attr:s", "|:attr:h", ":attr:h-s" })
        {
            vm.SetFilter(flt); WaitFilter(vm, 5000);
            Out($"   filter '{flt}': size={vm.SizeOf(tree.Root)} matching files={tree.Root.Descendants().OfType<FileNode>().Count(vm.IsIncluded)}");
        }
        DataCache.Release(tree);
        SafeDelete(root);
    }

    // ------------------------------------------------------------------ unicode

    private static void Unicode(string root)
    {
        Out("== UNICODE " + root);
        SafeDelete(root); Directory.CreateDirectory(root);
        var names = new[] { "çğışöüÇĞİŞÖÜ.txt", "İstanbul-rapor.docx", "ışık.txt", "emoji-😀🚀📁.log", "日本語ファイル.dat", "Ünlü Şarkı.mp3", "kılavuz.pdf", "ﬁle-ligature.txt", "a\u0301ccent-combining.txt", "trailing space .txt" };
        var dir = Path.Combine(root, "Klasör Şüpheli 😀"); Directory.CreateDirectory(dir);
        int i = 1; foreach (var n in names) { IOFile.WriteAllBytes(Path.Combine(dir, n), new byte[1000 * i++]); }
        var (tree, node, _, _) = ScanSync(root, 60);
        using var vm = new ViewModel(tree, tree.Root);
        lock (tree.Sync)
        {
            var d = tree.Root.Children.OfType<FolderNode>().First();
            Out($"   folder '{d.Name}' files={d.FileCount} (expected {names.Length})");
            foreach (var n in d.Children) Out($"     {n.Name}  disk={n.DiskSize}  roundtrip-exists={IOFile.Exists(n.FullPath)}");
        }
        foreach (var flt in new[] { "*ış*", "*İST*", "*ist*", "*😀*", "*.MP3", "\\klasör*", "*日本*" })
        {
            vm.SetFilter(flt); WaitFilter(vm, 5000);
            Out($"   filter '{flt}': {tree.Root.Descendants().OfType<FileNode>().Count(vm.IsIncluded)} files, size={vm.SizeOf(tree.Root)}");
        }
        vm.SetFilter("");
        var exp = new Exporter(ExportConfig.FindByName("File paths")!, tree.Root, vm.IsIncluded, tree.Sync, vm.SizeOf);
        var w = new StringWriter(); exp.ExportText(w, CancellationToken.None);
        var outFile = Path.Combine(root, "..", "unicode_export.txt");
        IOFile.WriteAllText(outFile, w.ToString(), new UTF8Encoding(false));
        var back = IOFile.ReadAllText(outFile, Encoding.UTF8);
        Out($"   export: {w.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} lines; every name round-trips through UTF-8 file: {names.All(n => back.Contains(n))}");
        var sns = Path.Combine(root, "..", "unicode.sns"); SnsFormat.Write(sns, tree.Root, _ => true); var rd = SnsFormat.Read(sns);
        Out($"   snapshot: names preserved={names.All(n => rd.Descendants().Any(x => x.Name == n))}");
        IOFile.Delete(outFile); IOFile.Delete(sns);
        // name ordering used by the export (ordinal ignore case) vs. Turkish culture
        var sorted = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var tr = names.OrderBy(n => n, StringComparer.Create(new CultureInfo("tr-TR"), true)).ToList();
        Out($"   name sort ordinal: {string.Join(" < ", sorted.Take(4))}");
        Out($"   name sort tr-TR : {string.Join(" < ", tr.Take(4))}  (differs={!sorted.SequenceEqual(tr)})");
        DataCache.Release(tree);
        SafeDelete(root);
    }

    // ------------------------------------------------------------------ treemap layout stress

    private static void Layout()
    {
        Out("== LAYOUT");
        var cfg = AppConfig.Current;
        var cases = new List<(string name, DriveNode root)>();
        { var r = SynthRoot(); SynthFile(r, "big", 1L << 30); for (int i = 0; i < 100; i++) SynthFile(r, "s" + i, 1000 + i); cases.Add(("1 big + 100 small", r)); }
        { var r = SynthRoot(); for (int i = 0; i < 10; i++) SynthFile(r, "big" + i, (1L << 28) + i); for (int i = 0; i < 10000; i++) SynthFile(r, "s" + i, 100 + i % 7); cases.Add(("10 big + 10000 small", r)); }
        { var r = SynthRoot(); for (int i = 0; i < 400; i++) SynthFile(r, "eq" + i, 1_000_000); cases.Add(("400 equal", r)); }
        { var r = SynthRoot(); SynthFile(r, "huge", 1L << 40); SynthFile(r, "one", 1); for (int i = 0; i < 50; i++) SynthFile(r, "t" + i, 10); cases.Add(("extreme imbalance 1TB vs 1 byte", r)); }
        { var r = SynthRoot(); for (int i = 0; i < 200; i++) SynthFile(r, "z" + i, 0); SynthFile(r, "one", 5000); cases.Add(("200 zero-byte + 1", r)); }
        { var r = SynthRoot(); var big = SynthFolder(r, "bigfolder"); for (int i = 0; i < 2000; i++) SynthFile(big, "b" + i, 1_000_000); for (int i = 0; i < 3000; i++) { var f = SynthFolder(r, "small" + i); SynthFile(f, "x", 1000 + i); } cases.Add(("1 huge folder + 3000 small folders", r)); }
        { var r = SynthRoot(); ContainerNode c = r; for (int i = 0; i < 60; i++) { c = SynthFolder(c, "n" + i); SynthFile(c, "f", 1 << 20); } cases.Add(("60 levels nested", r)); }
        { var r = SynthRoot(); cases.Add(("empty root", r)); }
        var sizesToTest = new[] { new SizeF(1200, 800), new SizeF(300, 2000), new SizeF(2000, 120), new SizeF(40, 40), new SizeF(1, 1), new SizeF(0, 0), new SizeF(3840, 2160) };
        foreach (var (name, root) in cases)
        {
            // drive totals consistent with the content: free = 10% of used, nothing unknown
            SetInternal(root, "FreeBytes", Math.Max(1, root.DiskSize / 10)); SetInternal(root, "TotalBytes", root.DiskSize + Math.Max(1, root.DiskSize / 10));
            var tree = new ScanTree(root, live: false);
            using var vm = new ViewModel(tree, root) { DetailLevel = 3, ShowFreeSpace = true, ShowUnknownSpace = true };
            foreach (var sz in sizesToTest)
            {
                var sw = Stopwatch.StartNew();
                TreemapItem lay;
                try { lay = vm.BuildLayout(new RectangleF(0, 0, sz.Width, sz.Height), 16, cfg); }
                catch (Exception ex) { Out($"   {name} @{sz.Width}x{sz.Height}: EXCEPTION {ex.GetType().Name}: {ex.Message}"); continue; }
                var ms = sw.Elapsed.TotalMilliseconds;
                var problems = Check(lay, cfg.MinElementSizePx);
                int items = 0; Walk(lay, _ => items++);
                Out($"   {name,-36} @{sz.Width,4}x{sz.Height,-4} items={items,5} {ms,7:0.00} ms  {(problems.Count == 0 ? "OK" : "PROBLEMS: " + string.Join("; ", problems.Take(4)))}");
            }
            // resize sequence stability: 50 consecutive sizes must all be valid
            int bad = 0; var swr = Stopwatch.StartNew();
            for (int i = 0; i < 50; i++) { var lay = vm.BuildLayout(new RectangleF(0, 0, 600 + i * 17, 400 + (i % 5) * 33), 16, cfg); if (Check(lay, cfg.MinElementSizePx).Count > 0) bad++; }
            Out($"   {name,-36} resize x50: {swr.Elapsed.TotalMilliseconds / 50:0.00} ms avg, invalid layouts={bad}");
        }
        // engine level: zero / NaN / negative inputs go straight to the layout engine
        try
        {
            var vals = new double[] { 0, double.NaN, -5, double.PositiveInfinity, 3, 7 };
            var rects = new Treemap.Layout.RectangleD[vals.Length];
            Treemap.Layout.Squarify.Layout(vals, new Treemap.Layout.RectangleD(0, 0, 100, 100), 1.0, rects);
            Out("   engine with [0, NaN, -5, +Inf, 3, 7]: " + string.Join(" ", rects.Select(r => $"[{r.X:0.#},{r.Y:0.#},{r.Width:0.#},{r.Height:0.#}]")));
        }
        catch (Exception ex) { Out("   engine with bad values: EXCEPTION " + ex.GetType().Name + " " + ex.Message); }
    }

    /// <summary>Like the view: re-requests the filter while the tree version moves, then waits for the result.</summary>
    private static void WaitFilter(ViewModel vm, int ms)
    {
        var sw = Stopwatch.StartNew();
        while ((vm.IsFiltering || vm.FilterResultStale) && sw.ElapsedMilliseconds < ms) { if (!vm.IsFiltering && vm.FilterResultStale) vm.RequestRefilter(); Thread.Sleep(10); }
        if (vm.IsFiltering || vm.FilterResultStale) Out("   (filter wait timed out)");
    }

    private static void Walk(TreemapItem it, Action<TreemapItem> a) { a(it); foreach (var c in it.Children) Walk(c, a); }

    private static List<string> Check(TreemapItem root, int min)
    {
        var problems = new List<string>();
        Walk(root, it =>
        {
            var r = it.Rect;
            if (float.IsNaN(r.X) || float.IsNaN(r.Y) || float.IsNaN(r.Width) || float.IsNaN(r.Height) || float.IsInfinity(r.Width) || float.IsInfinity(r.Height)) problems.Add("NaN/Inf " + it.Node.Name);
            if (r.Width < 0 || r.Height < 0) problems.Add("negative " + it.Node.Name);
            if (it.Parent != null && (r.Width > 0 && r.Height > 0) && !Contains(it.Parent.Rect, r, 0.01f)) problems.Add("outside parent " + it.Node.Name);
            if (it.Parent != null && r.Width > 0 && r.Height > 0 && (r.Width < min - 0.01f || r.Height < min - 0.01f)) problems.Add($"below min {it.Node.Name} {r.Width:0.#}x{r.Height:0.#}");
            var kids = it.Children.Where(k => k.Rect.Width > 0 && k.Rect.Height > 0).ToList();
            for (int i = 0; i < kids.Count; i++)
                for (int j = i + 1; j < kids.Count; j++)
                {
                    var a = kids[i].Rect; var b = kids[j].Rect;
                    float ox = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left), oy = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
                    if (ox > 0.01f && oy > 0.01f) { problems.Add($"overlap {kids[i].Node.Name}/{kids[j].Node.Name} {ox:0.##}x{oy:0.##}"); }
                }
        });
        return problems;
    }

    private static bool Contains(RectangleF p, RectangleF c, float eps) => c.Left >= p.Left - eps && c.Top >= p.Top - eps && c.Right <= p.Right + eps && c.Bottom <= p.Bottom + eps;

    // ------------------------------------------------------------------ leak cycle (open / navigate / close / rescan / switch)

    private static void Leak(string a, string b, int cycles)
    {
        Out($"== LEAK {a} <-> {b} x{cycles}");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/ByteScape;component/Themes/Theme.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/ByteScape;component/Themes/Styles.xaml") });
        ThemeManager.Initialize();
        var main = new MainWindow(new List<CommandLine.ViewRequest>(), false, openStartDialog: false)
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, Width = 1200, Height = 800 };
        main.Show();
        Pump();
        int cycle = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            string path = cycle % 2 == 0 ? a : b;
            var view = main.OpenScan(path)!;
            var sw = Stopwatch.StartNew();
            while (view.Tree.IsMasterScanning && sw.ElapsedMilliseconds < 120000) { Pump(); Thread.Sleep(20); }
            // navigate: zoom into biggest folder, render, back, home
            var kids = view.Model.ZoomRoot.Children.OfType<ContainerNode>().OrderByDescending(c => c.DiskSize).Take(3).ToList();
            foreach (var k in kids) { view.ZoomTo(k); MapOf(view).MarkDirty(); Pump(); view.GoUp(); Pump(); }
            MapOf(view).MarkDirty(); Pump();
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(1200, 800, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bmp.Render(MapOf(view));
            // rescan the same tree, then close the view (drops the tree from the cache)
            view.ToggleMasterScan(); sw.Restart(); while (view.Tree.IsMasterScanning && sw.ElapsedMilliseconds < 120000) { Pump(); Thread.Sleep(20); }
            var tab = ((System.Windows.Controls.TabControl)main.GetType().GetField("Tabs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!).Items.OfType<System.Windows.Controls.TabItem>().First(t => ReferenceEquals(t.Content, view));
            main.GetType().GetMethod("CloseView", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, new object[] { tab });
            Pump();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Out($"   cycle {++cycle,2} ({Path.GetFileName(path)}): {Mem()}  views={main.Views.Count()} cacheTrees={CacheCount()}");
            if (cycle < cycles) timer.Start(); else app.Shutdown();
        };
        timer.Start();
        Out("   start: " + Mem());
        app.Run();
    }

    private static ByteScape.Wpf.Controls.TreemapView MapOf(ByteScape.Wpf.Views.ScanView v) => (ByteScape.Wpf.Controls.TreemapView)v.GetType().GetField("Map", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(v)!;

    private static int CacheCount()
    {
        var f = typeof(DataCache).GetField("_trees", BindingFlags.NonPublic | BindingFlags.Static)!;
        return ((System.Collections.ICollection)f.GetValue(null)!).Count;
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    // ------------------------------------------------------------------ export on a big tree

    private static void Export(string root, string outDir)
    {
        Out("== EXPORT " + root);
        Directory.CreateDirectory(outDir);
        var (tree, node, elapsed, _) = ScanSync(root, 3600);
        using var vm = new ViewModel(tree, tree.Root);
        Out($"   scanned in {elapsed.TotalSeconds:0.0}s: files={tree.Root.FileCount} folders={tree.Root.FolderCount}");
        foreach (var name in new[] { "File paths", "Folders and their files", "Totals" })
        {
            var sw = Stopwatch.StartNew();
            var path = Path.Combine(outDir, name.Replace(' ', '_') + ".txt");
            var ex = new Exporter(ExportConfig.FindByName(name)!, tree.Root, vm.IsIncluded, tree.Sync, vm.SizeOf);
            using (var w = new StreamWriter(path, false, new UTF8Encoding(false))) ex.ExportText(w, CancellationToken.None);
            var lines = IOFile.ReadLines(path).Count();
            Out($"   {name,-18}: {sw.Elapsed.TotalSeconds:0.00}s, {new FileInfo(path).Length / 1048576.0:0.0} MB, {lines} lines, peakWS={Peak()}MB");
            if (name == "File paths") Out($"     lines == files: {lines == tree.Root.FileCount}; distinct lines == lines: {IOFile.ReadLines(path).Distinct().Count() == lines}");
            IOFile.Delete(path);
        }
        var sns = Path.Combine(outDir, "big.sns");
        var s2 = Stopwatch.StartNew(); SnsFormat.Write(sns, tree.Root, _ => true); var wt = s2.Elapsed; s2.Restart(); var back = SnsFormat.Read(sns);
        Out($"   snapshot: write {wt.TotalSeconds:0.00}s ({new FileInfo(sns).Length / 1048576.0:0.0} MB), read {s2.Elapsed.TotalSeconds:0.00}s, files back={back.FileCount} folders back={back.FolderCount} disk equal={back.DiskSize == tree.Root.DiskSize}");
        IOFile.Delete(sns);
        DataCache.Release(tree);
    }

    // ------------------------------------------------------------------ drives

    private static void Drives()
    {
        Out("== DRIVES");
        foreach (var d in DriveInfo.GetDrives())
        {
            string info;
            try { info = d.IsReady ? $"ready fs={d.DriveFormat} total={Fmt(d.TotalSize)} free={Fmt(d.AvailableFreeSpace)}" : "NOT READY"; } catch (Exception ex) { info = "error " + ex.Message; }
            Out($"   {d.Name} type={d.DriveType} {info}");
            var root = DataCache.CreateRoot(d.Name);
            Out($"     app root: isDrive={root.IsDrive} cluster={root.ClusterSize} total={Fmt(root.TotalBytes)} free={Fmt(root.FreeBytes)}");
            var (kind, product) = DriveProfile.Detect(d.Name);
            var st = DriveProfile.For(d.Name);
            Out($"     profile: {ScanStrategy.KindName(kind)}{(product.Length > 0 ? " (" + product + ")" : "")} -> {st.Short}, MFT readers {st.MftReaders}, listing buffer {(st.ListBufferBytes > 0 ? Fmt(st.ListBufferBytes) : "default")}, full scan waits for rescans: {st.YieldToSubScans}");
        }
        foreach (var d in DriveInfo.GetDrives().Where(x => !x.IsReady))
        {
            Out("-- scanning not-ready drive " + d.Name);
            try
            {
                var (tree, node, elapsed, _) = ScanSync(d.Name, 30);
                Out($"   state={tree.MasterState} time={elapsed.TotalSeconds:0.0}s files={tree.Root.FileCount} rootDenied={tree.Root.AccessDenied}");
                DataCache.Release(tree);
            }
            catch (Exception ex) { Out("   EXCEPTION: " + ex.GetType().Name + " " + ex.Message); }
        }
    }


    // ------------------------------------------------------------------ unreachable UNC path (what the UI thread would wait for)

    private static void Unc(string path)
    {
        Out("== UNC " + path);
        var sw = Stopwatch.StartNew();
        bool exists = Directory.Exists(path);
        Out($"   Directory.Exists = {exists} in {sw.ElapsedMilliseconds} ms   (MainWindow.OpenScan calls this on the UI thread before acquiring the tree)");
        sw.Restart();
        var root = DataCache.CreateRoot(path);
        Out($"   DataCache.CreateRoot (GetDiskFreeSpace on the share) in {sw.ElapsedMilliseconds} ms  isDrive={root.IsDrive} cluster={root.ClusterSize}");
        sw.Restart();
        try
        {
            var (tree, node, elapsed, _) = ScanSync(path, 120);
            Out($"   scan state={tree.MasterState} in {elapsed.TotalSeconds:0.0}s files={tree.Root.FileCount} rootDenied={tree.Root.AccessDenied} watchdog={(tree.Watchdog != null ? "on" : "off")}");
            DataCache.Release(tree);
        }
        catch (Exception ex) { Out("   EXCEPTION: " + ex.GetType().Name + " " + ex.Message); }
    }

    // ------------------------------------------------------------------ filter performance on a big tree

    // ------------------------------------------------------------------ cloud files (Files On-Demand placeholders)

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct CfRegistration
    {
        public uint StructSize;
        public string ProviderName;
        public string ProviderVersion;
        public IntPtr SyncRootIdentity; public uint SyncRootIdentityLength;
        public IntPtr FileIdentity; public uint FileIdentityLength;
        public Guid ProviderId;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CfPolicies
    {
        public uint StructSize;
        public ushort HydrationPrimary, HydrationModifier;
        public ushort PopulationPrimary, PopulationModifier;
        public uint InSync, HardLink, PlaceholderManagement;
    }

    [System.Runtime.InteropServices.DllImport("cldapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int CfRegisterSyncRoot(string path, ref CfRegistration reg, ref CfPolicies pol, uint flags);
    [System.Runtime.InteropServices.DllImport("cldapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int CfUnregisterSyncRoot(string path);
    [System.Runtime.InteropServices.DllImport("cldapi.dll")]
    private static extern int CfConvertToPlaceholder(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte[]? identity, uint identityLength, uint flags, out long usn, IntPtr overlapped);
    [System.Runtime.InteropServices.DllImport("cldapi.dll")]
    private static extern int CfSetPinState(Microsoft.Win32.SafeHandles.SafeFileHandle h, int pinState, uint flags, IntPtr overlapped);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string name, out uint high);
    [System.Runtime.InteropServices.DllImport("cldapi.dll")]
    private static extern int CfDehydratePlaceholder(Microsoft.Win32.SafeHandles.SafeFileHandle h, long offset, long length, uint flags, IntPtr overlapped);

    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenForCf(string path, bool dir)
    {
        // GENERIC_READ | GENERIC_WRITE, share all, OPEN_EXISTING, BACKUP_SEMANTICS for folders
        var h = CreateFileW(path, 0x80000000 | 0x40000000, 7, IntPtr.Zero, 3, dir ? 0x02000000u : 0u, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException("open " + path + " failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        return h;
    }

    /// <summary>
    /// Registers a private Cloud Files sync root under %TEMP%, turns files into placeholders in every state OneDrive
    /// uses (in sync + local, pinned "always keep", dehydrated "online-only", folders as placeholders), prints their
    /// attributes and scans them with ByteScape. Unregisters and deletes everything afterwards.
    /// </summary>
    private static void Cloud(string baseDir)
    {
        var root = Path.Combine(baseDir, "CloudRoot");
        Out("== CLOUD test sync root " + root);
        SafeDelete(baseDir);
        Directory.CreateDirectory(root);
        var reg = new CfRegistration { ProviderName = "ByteScapeTest", ProviderVersion = "1.0", ProviderId = new Guid("6d1a1b2c-3e4f-4a5b-9c8d-7e6f5a4b3c2d") };
        reg.StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<CfRegistration>();
        var pol = new CfPolicies { HydrationPrimary = 2 /*FULL*/, PopulationPrimary = 3 /*ALWAYS_FULL*/ };
        pol.StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<CfPolicies>();
        int hr = CfRegisterSyncRoot(root, ref reg, ref pol, 0);
        Out($"   CfRegisterSyncRoot: 0x{hr:X8}");
        if (hr != 0) return;
        try
        {
            var docs = Directory.CreateDirectory(Path.Combine(root, "Documents")).FullName;
            var photos = Directory.CreateDirectory(Path.Combine(root, "Photos")).FullName;
            var plain = Directory.CreateDirectory(Path.Combine(root, "NotYetSynced")).FullName;
            void Make(string p, int mb) { using var fs = IOFile.Create(p); fs.Write(new byte[mb << 20]); }
            Make(Path.Combine(docs, "local.bin"), 3);
            Make(Path.Combine(docs, "pinned.bin"), 2);
            Make(Path.Combine(photos, "online.bin"), 5);
            Make(Path.Combine(photos, "freed.bin"), 4);
            Make(Path.Combine(plain, "new.bin"), 1);
            var id = Encoding.UTF8.GetBytes("id");
            void Convert(string p, bool dir, uint flags)
            {
                using var h = OpenForCf(p, dir);
                int r = CfConvertToPlaceholder(h, dir ? null : id, dir ? 0u : (uint)id.Length, flags, out _, IntPtr.Zero);
                if (r != 0) Out($"   convert {Path.GetFileName(p)}: 0x{r:X8}");
            }
            void Pin(string p, bool dir, int state)
            {
                using var h = OpenForCf(p, dir);
                int r = CfSetPinState(h, state, 0, IntPtr.Zero);
                if (r != 0) Out($"   pin {Path.GetFileName(p)}={state}: 0x{r:X8}");
            }
            Convert(docs, true, 1); Convert(photos, true, 1);
            Convert(Path.Combine(docs, "local.bin"), false, 1);                 // in sync, hydrated: "available on this device"
            Convert(Path.Combine(docs, "pinned.bin"), false, 1);
            Pin(Path.Combine(docs, "pinned.bin"), false, 1);                      // PINNED: "always keep on this device"
            Convert(Path.Combine(photos, "online.bin"), false, 1 | 2);          // MARK_IN_SYNC | DEHYDRATE: "online-only"
            Convert(Path.Combine(photos, "freed.bin"), false, 1 | 2);           // what "Free up space" leaves behind:
            Pin(Path.Combine(photos, "freed.bin"), false, 2);                     // dehydrated + UNPINNED
            foreach (var p in new[] { root, docs, photos, plain }.Concat(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)))
            {
                var a = (int)IOFile.GetAttributes(p);
                uint low = GetCompressedFileSizeW(p, out uint high);
                Out($"   {p.Substring(root.Length).PadRight(24)} attrs=0x{a:X8} allocated={(((long)high << 32) | low)}");
            }
            foreach (var fi in new DirectoryInfo(baseDir).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                Out($"   enum {fi.FullName.Substring(baseDir.Length).PadRight(34)} attrs=0x{(int)fi.Attributes:X8}");
            int logFrom = ByteScape.Core.Util.Log.Snapshot().Count;
            var (tree, node, elapsed, _) = ScanSync(baseDir);
            Out($"   scan state {tree.MasterState} in {elapsed.TotalMilliseconds:0} ms");
            foreach (var le in ByteScape.Core.Util.Log.Snapshot().Skip(logFrom)) Out("   log: " + le.Level + " " + le.Message);
            Out($"   scanned {baseDir}: {tree.Root.FileCount} files, {tree.Root.FolderCount} folders, logical {Fmt(tree.Root.LogicalSize)}, disk {Fmt(tree.Root.DiskSize)}");
            foreach (var n in tree.Root.Descendants())
                Out($"      {n.FullPath.Substring(baseDir.Length).PadRight(34)} {n.Kind,-6} logical={Fmt(n.LogicalSize),-8} disk={Fmt(n.DiskSize),-8} attrs=0x{(int)(n is FileNode f ? f.Attributes : ((FolderNode)n).Attributes):X8} cloud={Core.Cloud.CloudFiles.StateOf(n)} root={Core.Cloud.CloudFiles.SyncRootOf(n)?.Provider}");
            var cs = Core.Cloud.CloudFiles.Summarize(tree.Root, CancellationToken.None, tree.Sync);
            foreach (var r in cs.Roots) Out($"   summary {r.Root.Provider} {r.Node.FullPath}: local {Fmt(r.LocalBytes)} ({r.LocalFiles}), kept {Fmt(r.KeptBytes)} ({r.KeptFiles}), online-only {Fmt(r.OnlineBytes)} ({r.OnlineFiles})");
            foreach (var flt in new[] { ":freeable", ":onlineonly", ":alwayskept", ":cloud", "|:cloud" })
            {
                var fl = new Filter(flt);
                var hits = tree.Root.Descendants().OfType<FileNode>().Where(x => fl.Matches(x, Filter.FolderMaskState.Root, DateTime.UtcNow)).Select(x => x.Name);
                Out($"   filter {flt,-12} -> {string.Join(", ", hits)}");
            }
            Out("   provider by path (Documents): " + (Core.Cloud.CloudFiles.ProviderOf(docs) ?? "null") + ", (temp): " + (Core.Cloud.CloudFiles.ProviderOf(Path.GetTempPath()) ?? "null"));
            DataCache.Release(tree);
        }
        finally
        {
            // the registration lives on the folder: unregister first, then delete the (now ordinary) placeholders
            Out($"   CfUnregisterSyncRoot: 0x{CfUnregisterSyncRoot(root):X8}");
            SafeDelete(baseDir);
            Out("   cleaned: " + !Directory.Exists(baseDir));
        }
    }

    // ------------------------------------------------------------------ parallel folder listing

    /// <summary>Full scans of <paramref name="path"/> with 1 worker and with other worker counts, alternating (warm cache after the first).</summary>
    private static void ScanSpeed(string path, int[] threads)
    {
        path = DataCache.NormalizePath(path);
        Out($"== SCAN SPEED {path}  (auto = {DirectoryScanner.WorkerCount(path)} workers, {Environment.ProcessorCount} cores)");
        var results = new Dictionary<int, List<double>>();
        string? reference = null;
        for (int round = 0; round < 2; round++)
            foreach (var t in threads)
            {
                AppConfig.Replace(new AppConfig { ScanThreads = t, WatchDog = false });
                var tree = new ScanTree(DataCache.CreateRoot(path), live: true);
                var sw = Stopwatch.StartNew();
                tree.StartMasterScan();
                while (tree.IsMasterScanning) Thread.Sleep(10);
                sw.Stop();
                var sig = $"{tree.Root.FileCount}/{tree.Root.FolderCount}";
                reference ??= sig;
                Out($"   round {round + 1}, {(t == 0 ? "auto" : t.ToString()),4} worker(s): {sw.Elapsed.TotalSeconds,6:0.00} s  files/folders {sig}  disk {Fmt(tree.Root.DiskSize)}");
                if (round > 0 || threads.Length == 1) { if (!results.TryGetValue(t, out var l)) results[t] = l = new(); l.Add(sw.Elapsed.TotalSeconds); }
                tree.Dispose();
                GC.Collect();
            }
        double baseT = results.TryGetValue(1, out var b) ? b.Average() : 0;
        foreach (var (t, l) in results) Out($"   {(t == 0 ? "auto" : t.ToString()),4} worker(s): {l.Average():0.00} s" + (baseT > 0 && t != 1 ? $"  ({baseT / l.Average():0.0}x)" : ""));
        AppConfig.Replace(new AppConfig());
    }

    // ------------------------------------------------------------------ game library

    /// <summary>Reads the machine's game library, scans <paramref name="path"/> and prints the audit.</summary>
    private static void Games(string path)
    {
        var sw = Stopwatch.StartNew();
        var lib = Core.Games.GameLibrary.Load();
        Out($"== GAMES library: {lib.Games.Count} games in {sw.ElapsedMilliseconds} ms; Steam at {lib.SteamPath ?? "-"}, libraries: {string.Join(" | ", lib.SteamLibraries)}; Epic libraries: {string.Join(" | ", lib.EpicLibraries)}; played but not installed: {lib.SteamPlayedNotInstalled.Count}");
        foreach (var g in lib.Games) Out($"   {g.StoreText,-5} {g.Id,-10} {g.Name,-40} tool={g.IsTool,-5} reported={Fmt(g.ReportedBytes),-9} played={g.LastPlayed?.ToLocalTime().ToString("yyyy-MM-dd") ?? "never"} time={(g.Playtime is { } t ? Core.Games.GameAudit.PlaytimeText(t) : "-")} updated={g.LastUpdated?.ToLocalTime().ToString("yyyy-MM-dd")}");
        var (tree, node, elapsed, _) = ScanSync(path);
        var swa = Stopwatch.StartNew();
        var r = Core.Games.GameAudit.Compute(tree.Root, lib, n => n.DiskSize, CancellationToken.None, tree.Sync);
        Out($"   audit of {path}: {swa.ElapsedMilliseconds} ms (scan {elapsed.TotalSeconds:0.0} s)");
        foreach (var line in Core.Games.GameAudit.ToText(r, DateTime.UtcNow).Split('\n')) Out("   | " + line.TrimEnd('\r'));
        foreach (var n in tree.Root.Descendants().OfType<ContainerNode>())
            if (lib.LabelFor(n) is { } label) Out("   label: " + n.FullPath + "  ->  " + label);
        DataCache.Release(tree);
    }

    /// <summary>Scans a path and lists the projects with rebuildable folders (developer leftovers).</summary>
    private static void Dev(string path)
    {
        var (tree, node, elapsed, _) = ScanSync(path);
        var sw = Stopwatch.StartNew();
        var r = Core.Dev.DevProjects.Compute(tree.Root, n => n.DiskSize, CancellationToken.None, tree.Sync);
        Out($"== DEV {path}: {r.Projects.Count} projects, {Fmt(r.ArtifactBytes)} rebuildable, in {sw.ElapsedMilliseconds} ms (scan {elapsed.TotalSeconds:0.0} s, {tree.Root.FileCount:N0} files)");
        foreach (var line in Core.Dev.DevProjects.ToText(r, DateTime.UtcNow).Split('\n')) Out("   | " + line.TrimEnd('\r'));
        DataCache.Release(tree);
    }

    // ------------------------------------------------------------------ raw disk speed (no administrator rights)

    /// <summary>Reads files unbuffered (straight from the disk, read only) to learn the drive's real sequential speed.</summary>
    private static unsafe void DiskSpeed(string[] files)
    {
        Out("== DISK SPEED (unbuffered sequential reads)");
        foreach (var f in files)
            foreach (int chunk in new[] { 4 << 20, 1 << 20 })
            {
                using var h = File.OpenHandle(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, (FileOptions)0x20000000 /*NO_BUFFERING*/);
                void* p = System.Runtime.InteropServices.NativeMemory.AlignedAlloc((nuint)chunk, 4096);
                try
                {
                    long total = 0;
                    var sw = Stopwatch.StartNew();
                    while (true)
                    {
                        int n = RandomAccess.Read(h, new Span<byte>(p, chunk), total);
                        if (n <= 0) break;
                        total += n;
                        if (n < chunk) break;
                    }
                    Out($"   {Path.GetFileName(f)}: {total >> 20:N0} MB in {sw.ElapsedMilliseconds:N0} ms = {(total >> 20) / sw.Elapsed.TotalSeconds:N0} MB/s ({chunk >> 10} KB reads)");
                }
                finally { System.Runtime.InteropServices.NativeMemory.AlignedFree(p); }
            }
        // 4 threads reading 1 MB pieces: one shared synchronous handle (the I/O manager serializes it) vs one handle each
        foreach (var f in files)
            foreach (bool shared in new[] { true, false })
            {
                long len = new FileInfo(f).Length / (1 << 20) * (1 << 20);
                long next = -1;
                var sharedHandle = shared ? File.OpenHandle(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, (FileOptions)0x20000000) : null;
                var sw = Stopwatch.StartNew();
                var threads = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
                {
                    var h = sharedHandle ?? File.OpenHandle(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, (FileOptions)0x20000000);
                    void* p = System.Runtime.InteropServices.NativeMemory.AlignedAlloc(1 << 20, 4096);
                    try
                    {
                        long i;
                        while ((i = Interlocked.Increment(ref next)) * (1 << 20) < len) RandomAccess.Read(h, new Span<byte>(p, 1 << 20), i * (1 << 20));
                    }
                    finally { System.Runtime.InteropServices.NativeMemory.AlignedFree(p); if (sharedHandle == null) h.Dispose(); }
                })).ToList();
                threads.ForEach(t => t.Start());
                threads.ForEach(t => t.Join());
                sharedHandle?.Dispose();
                Out($"   {Path.GetFileName(f)}: 4 threads, 1 MB pieces, {(shared ? "one shared handle " : "a handle per thread")}: {(len >> 20) / sw.Elapsed.TotalSeconds:N0} MB/s");
            }
    }

    // ------------------------------------------------------------------ program footprint

    /// <summary>Scans each path, then times the installed program catalog and the footprint walk; the full report goes to diag_apps_&lt;n&gt;.txt.</summary>
    private static void Apps(string[] roots)
    {
        var swc = Stopwatch.StartNew();
        var catalog = Core.Apps.AppCatalog.LoadFromSystem();
        Out($"== APPS catalog: {catalog.Apps.Count} programs ({catalog.Apps.Count(a => a.Source == Core.Apps.AppSource.Store)} Store, {catalog.Apps.Count(a => a.IsComponent)} components, {catalog.Apps.Sum(a => a.InstallDirs.Count)} install folders) in {swc.ElapsedMilliseconds} ms, max match depth {catalog.MaxMatchDepth}");
        int n = 0;
        foreach (var root in roots)
        {
            n++;
            var (tree, node, elapsed, peak) = ScanSync(root);
            Out($"== {root}: scanned {tree.Root.FileCount:N0} files / {tree.Root.FolderCount:N0} folders, {Fmt(tree.Root.DiskSize)} in {elapsed.TotalSeconds:0.0}s (peak {peak} MB)");
            Core.Apps.FootprintResult r = null!;
            var times = new List<double>();
            for (int i = 0; i < 3; i++)
            {
                var sw = Stopwatch.StartNew();
                r = Core.Apps.AppFootprint.Compute(tree.Root, catalog, x => x.DiskSize, CancellationToken.None, tree.Sync);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            var swc2 = Stopwatch.StartNew();
            var cloud = Core.Cloud.CloudFiles.Summarize(tree.Root, CancellationToken.None, tree.Sync);
            Out($"   cloud summary: {swc2.ElapsedMilliseconds} ms, {cloud.Roots.Count} cloud folders");
            Out($"   footprint: {string.Join(" / ", times.Select(t => t.ToString("0")))} ms; {r.Apps.Count} programs = {Fmt(r.AppBytes)} ({100.0 * r.AppBytes / Math.Max(1, r.ScannedBytes):0.0}%), unlinked {r.Unlinked.Count} folders = {Fmt(r.UnlinkedBytes)}");
            foreach (var u in r.Apps.Take(15)) Out($"      {Fmt(u.Bytes),10}  {u.App.Name}  [{string.Join(", ", u.Pieces.Select(p => p.Kind + " " + Fmt(p.Bytes)))}]");
            var file = Path.Combine(Environment.GetEnvironmentVariable("DIAG_OUT") ?? Path.GetTempPath(), $"diag_apps_{n}.txt");
            IOFile.WriteAllText(file, Core.Apps.AppFootprint.ToText(r), new UTF8Encoding(false));
            Out("   report: " + file);
            DataCache.Release(tree);
        }
    }

    private static void FilterPerf(string root)
    {
        Out("== FILTERPERF " + root);
        var (tree, node, elapsed, _) = ScanSync(root, 3600);
        Out($"   scanned in {elapsed.TotalSeconds:0.0}s: files={tree.Root.FileCount} folders={tree.Root.FolderCount} disk={tree.Root.DiskSize}");
        using var vm = new ViewModel(tree, tree.Root);
        foreach (var flt in new[] { "*.txt", "*.txt;*.dat", ">10kb", "<1kb", "<1day", ">1year", ":attr:a", "\\d3*", "*file1*", "*.txt;|*file1*;>0", ":red" })
        {
            var sw = Stopwatch.StartNew();
            vm.SetFilter(flt); WaitFilter(vm, 120000);
            long size = vm.SizeOf(tree.Root);
            int files = 0; lock (tree.Sync) { files = tree.Root.Descendants().OfType<FileNode>().Count(vm.IsIncluded); }
            // reference count computed independently
            int expect = -1;
            if (flt == "*.txt") lock (tree.Sync) { expect = tree.Root.Descendants().OfType<FileNode>().Count(f => f.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)); }
            if (flt == ">10kb") lock (tree.Sync) { expect = tree.Root.Descendants().OfType<FileNode>().Count(f => f.DiskSize > 10 * 1024); }
            long expectSize = -1;
            if (flt == "*.txt") lock (tree.Sync) { expectSize = tree.Root.Descendants().OfType<FileNode>().Where(f => f.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).Sum(f => f.DiskSize); }
            Out($"   {flt,-24} {sw.Elapsed.TotalMilliseconds,7:0} ms  files={files,7} size={size,12}" + (expect >= 0 ? $"  expected files={expect} {(expect == files ? "OK" : "MISMATCH")}" : "") + (expectSize >= 0 ? $"  expected size={expectSize} {(expectSize == size ? "OK" : "MISMATCH")}" : ""));
        }
        vm.SetFilter("");
        Out($"   empty filter restores: size={vm.SizeOf(tree.Root)} == {tree.Root.DiskSize}: {vm.SizeOf(tree.Root) == tree.Root.DiskSize}");
        DataCache.Release(tree);
    }
    private static void Tools()
    {
        Out("== TOOLS: mklink capability check (needs cmd)");
        var tmp = Path.Combine(Path.GetTempPath(), "bytescape-diag-tools"); SafeDelete(tmp); Directory.CreateDirectory(Path.Combine(tmp, "t"));
        Run("cmd.exe", $"/c mklink /J \"{Path.Combine(tmp, "j")}\" \"{Path.Combine(tmp, "t")}\"");
        Run("cmd.exe", $"/c mklink /D \"{Path.Combine(tmp, "s")}\" \"{Path.Combine(tmp, "t")}\"");
        Out($"   junction={Directory.Exists(Path.Combine(tmp, "j"))} symlinkdir={Directory.Exists(Path.Combine(tmp, "s"))}");
        SafeDelete(tmp);
    }
}
