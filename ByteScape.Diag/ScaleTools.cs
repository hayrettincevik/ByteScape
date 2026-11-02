// Scale test: millions of files without creating them. A synthetic tree shaped like a real disk (random folder tree, skewed
// file counts per folder, log-normal sizes, varied names and dates) is built in memory with the model's own classes; then
// every tree-wide operation is timed on it: layout, filters, reports, cleanup categories, snapshot write / read, and finally
// the real window opening that snapshot. Only the snapshot file is written (to %TEMP%\bytescape-scale) and it is deleted.
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ByteScape.Core.Cleanup;
using ByteScape.Core.Config;
using ByteScape.Core.Export;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using ByteScape.Core.Snapshot;
using ByteScape.Treemap;
using ByteScape.Wpf;

namespace ByteScape.Diag;

internal static partial class Program
{
    private static readonly string[] Words =
    {
        "cache", "data", "temp", "logs", "assets", "images", "photos", "music", "video", "docs", "build", "src", "lib", "bin", "obj",
        "packages", "modules", "resources", "config", "backup", "archive", "projects", "games", "steam", "downloads", "shared",
        "Microsoft", "Google", "Adobe", "Windows", "System32", "WinSxS", "node_modules", "site-packages", "icons", "fonts", "themes",
        "report", "invoice", "holiday", "IMG", "DSC", "Screenshot", "setup", "update", "manifest", "index", "chunk", "segment", "part",
    };

    private static readonly string[] Extensions =
    {
        ".dll", ".dll", ".dll", ".exe", ".txt", ".log", ".log", ".json", ".xml", ".png", ".png", ".jpg", ".jpg", ".jpg", ".js", ".js",
        ".ts", ".cs", ".py", ".pyc", ".h", ".cpp", ".mp3", ".mp4", ".mkv", ".zip", ".7z", ".pdf", ".docx", ".xlsx", ".ini", ".dat",
        ".bin", ".pak", ".tmp", ".cache", ".mui", ".cat", ".manifest", ".svg", ".css", ".html", "", ".sys", ".msi",
    };

    private static string NewName(Random r, bool file)
    {
        var sb = new StringBuilder(40);
        sb.Append(Words[r.Next(Words.Length)]);
        switch (r.Next(4))
        {
            case 0: sb.Append('_').Append(r.Next(100000)); break;
            case 1: sb.Append('-').Append(Words[r.Next(Words.Length)]).Append(r.Next(100)); break;
            case 2: sb.Append(' ').Append('(').Append(r.Next(1, 30)).Append(')'); break;
            default: for (int i = r.Next(4, 20); i > 0; i--) sb.Append((char)('a' + r.Next(26))); break;
        }
        if (file) sb.Append(Extensions[r.Next(Extensions.Length)]);
        return sb.ToString(); // a new string for every node, as a listing of the disk gives
    }

    private static double Gauss(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

    /// <summary>About nine files per folder (the ratio of a Windows system drive), folders hung under random earlier folders
    /// (depth grows like a real tree's), files crowded into some folders: the square of a uniform pick favours the first.</summary>
    internal static DriveNode BuildScaleTree(int files, int seed, out int folderCount)
    {
        var r = new Random(seed);
        var root = new DriveNode("X:\\") { IsDrive = true, Enumerated = true, ScanComplete = true };
        folderCount = Math.Max(1, files / 9);
        var folders = new List<ContainerNode>(folderCount + 1) { root };
        var now = DateTime.UtcNow;
        long id = 1000;
        for (int i = 0; i < folderCount; i++)
        {
            var parent = folders[r.Next(folders.Count)];
            var modified = now.AddDays(-r.NextDouble() * 2000);
            var f = new FolderNode(NewName(r, false)) { Enumerated = true, ScanComplete = true, FileId = id++, Modified = modified, Created = modified.AddDays(-r.NextDouble() * 100), Accessed = modified, Attributes = FileAttributes.Directory };
            parent.AddChild(f);
            folders.Add(f);
        }
        for (int i = 0; i < files; i++)
        {
            var parent = folders[(int)(folders.Count * Math.Pow(r.NextDouble(), 2))];
            long len = r.Next(20) == 0 ? 0 : (long)Math.Min(Math.Exp(8.5 + 2.4 * Gauss(r)), 64e9);
            var modified = now.AddDays(-Math.Pow(r.NextDouble(), 2) * 2000);
            var f = new FileNode(NewName(r, true))
            {
                LogicalSize = len, DiskSize = (len + 4095) / 4096 * 4096, FileId = id++,
                Modified = modified, Created = modified.AddDays(-r.NextDouble() * 30), Accessed = modified, Attributes = FileAttributes.Archive,
            };
            parent.AddChild(f);
        }
        root.TotalBytes = root.DiskSize + root.DiskSize / 5;
        root.FreeBytes = root.DiskSize / 10;
        return root;
    }

    private static long Managed() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }

    private static void Scale(int files, bool ui)
    {
        Out($"== SCALE {files:N0} files (synthetic, in memory){(ui ? " + window" : "")}");
        var cfg = AppConfig.Current;
        long before = Managed();
        var sw = Stopwatch.StartNew();
        var root = BuildScaleTree(files, 42, out int folders);
        var build = sw.Elapsed;
        long model = Managed() - before;
        long nodes = (long)files + folders;
        Out($"   built {files:N0} files + {folders:N0} folders in {build.TotalSeconds:0.0} s; model {model / 1048576.0:N0} MB managed = {model / (double)nodes:0} bytes per element; {Mem()}");
        int maxDepth = 0; long depthSum = 0;
        sw.Restart();
        foreach (var n in root.Descendants()) { int d = n.Level; depthSum += d; if (d > maxDepth) maxDepth = d; }
        Out($"   walk with depth: {sw.Elapsed.TotalSeconds:0.00} s; depth max {maxDepth}, mean {depthSum / (double)nodes:0.0}; largest folder {root.Descendants().OfType<ContainerNode>().Max(c => c.ChildCount):N0} entries");

        var tree = new ScanTree(root, live: false);
        var vm = new ViewModel(tree, root) { DetailLevel = 3, ShowFreeSpace = true, ShowUnknownSpace = true };
        foreach (var pass in new[] { "first", "second" })
        {
            sw.Restart();
            var lay = vm.BuildLayout(new RectangleF(0, 0, 1400, 860), 16, cfg);
            int items = 0; Walk(lay, _ => items++);
            Out($"   layout 1400x860 ({pass}): {sw.Elapsed.TotalMilliseconds:0} ms, {items:N0} rectangles");
        }
        foreach (var flt in new[] { "*.log", ">100mb", "<30days", "*cache*", "files over 1 GB" })
        {
            sw.Restart();
            vm.SetFilter(flt); WaitFilter(vm, 600000);
            var t = sw.Elapsed;
            Out($"   filter {flt,-16} {t.TotalMilliseconds,7:0} ms  shown {vm.SizeOf(root) / 1073741824.0,8:N1} GB  {Mem()}");
        }
        vm.SetFilter(""); WaitFilter(vm, 600000);
        Out($"   filter cleared: all back = {vm.SizeOf(root) == root.DiskSize}; {Mem()}");

        sw.Restart();
        int rows;
        using (var w = new StreamWriter(Stream.Null, new UTF8Encoding(false), 1 << 16)) rows = Reports.WriteCsv(root, tree.Sync, vm.IsIncluded, vm.SizeOf, w, CancellationToken.None);
        Out($"   CSV report: {sw.Elapsed.TotalSeconds:0.0} s, {rows:N0} rows");
        sw.Restart();
        Reports.WriteJson(root, tree.Sync, vm.IsIncluded, vm.SizeOf, Stream.Null, CancellationToken.None);
        Out($"   JSON report: {sw.Elapsed.TotalSeconds:0.0} s");
        sw.Restart();
        Reports.WriteHtml(root, tree.Sync, vm.IsIncluded, vm.SizeOf, TextWriter.Null, CancellationToken.None);
        Out($"   HTML report: {sw.Elapsed.TotalSeconds:0.0} s");
        sw.Restart();
        long alloc = GC.GetTotalAllocatedBytes(true);
        try
        {
            var found = CleanupFinder.Find(root, SafetyContext.For(tree), new CleanupOptions(), null, null, CancellationToken.None);
            Out($"   cleanup categories: {sw.Elapsed.TotalSeconds:0.0} s, {found.Items.Count:N0} items; {Mem()}; allocated {(GC.GetTotalAllocatedBytes(true) - alloc) / 1048576.0:N0} MB in all; after a full collection {Managed() / 1048576.0:N0} MB managed");
        }
        catch (Exception ex) { Out($"   cleanup categories: {ex.GetType().Name}: {ex.Message}"); }

        var dir = Path.Combine(Path.GetTempPath(), "bytescape-scale");
        Directory.CreateDirectory(dir);
        var sns = Path.Combine(dir, $"scale-{files}.sns");
        sw.Restart();
        alloc = GC.GetTotalAllocatedBytes(true);
        SnsFormat.Write(sns, root, _ => true);
        Out($"   snapshot write: {sw.Elapsed.TotalSeconds:0.0} s, {new FileInfo(sns).Length / 1048576.0:N0} MB, allocated {(GC.GetTotalAllocatedBytes(true) - alloc) / 1048576.0:N0} MB; {Mem()}");
        vm.Dispose();
        vm = null; tree = null; root = null;
        long afterRelease = Managed();
        Out($"   model released: {afterRelease / 1048576.0:N0} MB managed left; {Mem()}");

        sw.Restart();
        long beforeRead = Managed();
        var back = SnsFormat.Read(sns);
        var read = sw.Elapsed;
        long readMem = Managed() - beforeRead;
        Out($"   snapshot read: {read.TotalSeconds:0.0} s, files {back.FileCount:N0} folders {back.FolderCount:N0}, {readMem / 1048576.0:N0} MB; {Mem()}");
        GC.KeepAlive(back);
        back = null;
        Managed();

        if (ui) ScaleWindow(sns);
        try { File.Delete(sns); Directory.Delete(dir); } catch { }
        Out($"   peak working set {Peak():N0} MB");
    }

    /// <summary>The real window opens the snapshot: how long until the map shows, how the window answers while it is up.</summary>
    private static void ScaleWindow(string sns)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/ByteScape;component/Themes/Theme.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/ByteScape;component/Themes/Styles.xaml") });
        ThemeManager.Initialize();
        var main = new MainWindow(new List<CommandLine.ViewRequest>(), false, openStartDialog: false)
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, Width = 1400, Height = 900 };
        main.Show();
        Pump();
        AppConfig.Current.ZoomTimeMs = 0;   // the zoom animation (400 ms by default) would be timed otherwise, not the work
        long baseManaged = Managed();
        var lags = new List<double>();
        var lagTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(50) };
        var last = Stopwatch.StartNew();
        lagTimer.Tick += (_, _) => { lags.Add(last.Elapsed.TotalMilliseconds - 50); last.Restart(); };
        lagTimer.Start();

        lags.Clear();
        var sw = Stopwatch.StartNew();
        var view = main.OpenSnapshot(sns)!;
        var opened = sw.Elapsed;
        var openLags = lags.ToList();
        var map = MapOf(view);
        map.MarkDirty(); map.UpdateLayout(); Pump();
        var shown = sw.Elapsed;
        Out($"   window: OpenSnapshot {opened.TotalSeconds:0.0} s, map drawn at {shown.TotalSeconds:0.0} s; while opening the window's timer ran {openLags.Count} times, longest gap {(openLags.Count > 0 ? openLags.Max() + 50 : opened.TotalMilliseconds):0} ms; tree + view {(Managed() - baseManaged) / 1048576.0:N0} MB managed, {Mem()}");

        var content = (ContainerNode)view.Model.ZoomRoot.Children.OfType<ContainerNode>().First();
        view.ZoomTo(content); map.MarkDirty(); map.UpdateLayout(); Pump();
        var nav = new List<double>();
        var kids = content.Children.OfType<ContainerNode>().OrderByDescending(c => c.DiskSize).Take(5).ToList();
        foreach (var k in kids)
        {
            var t = Stopwatch.StartNew(); view.ZoomTo(k); map.MarkDirty(); map.UpdateLayout(); Pump(); nav.Add(t.Elapsed.TotalMilliseconds);
            t.Restart(); view.GoUp(); map.MarkDirty(); map.UpdateLayout(); Pump(); nav.Add(t.Elapsed.TotalMilliseconds);
        }
        Out($"   zoom in / out x{nav.Count}: median {Median(nav):0} ms, worst {nav.Max():0} ms");
        foreach (var flt in new[] { "*.log", ">100mb" })
        {
            var t = Stopwatch.StartNew();
            view.Model.SetFilter(flt); WaitFilter(view.Model, 600000); map.MarkDirty(); map.UpdateLayout(); Pump();
            Out($"   window filter {flt,-8} {t.Elapsed.TotalMilliseconds:0} ms");
        }
        view.Model.SetFilter(""); WaitFilter(view.Model, 600000); Pump();
        lags.Clear();
        var idle = Stopwatch.StartNew();
        while (idle.ElapsedMilliseconds < 3000) { Pump(); Thread.Sleep(5); }
        lagTimer.Stop();
        var sorted = lags.OrderBy(x => x).ToList();
        Out($"   idle window with the tree: timer lag p95 {(sorted.Count > 0 ? sorted[(int)(sorted.Count * 0.95)] : 0):0} ms over {sorted.Count} ticks; {Mem()}");
        main.Close();
        Pump();
        app.Shutdown();
    }

    private static long WsMb() { Me.Refresh(); return Me.WorkingSet64 / 1048576; }
    private static double Median(List<double> v) { var s = v.OrderBy(x => x).ToList(); return s.Count == 0 ? 0 : s[s.Count / 2]; }
}
