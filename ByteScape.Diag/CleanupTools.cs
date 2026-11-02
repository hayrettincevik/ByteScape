// "Is it safe to delete?" and the cleanup categories on a real drive. Read only: nothing is deleted.
using System.Diagnostics;
using System.IO;
using ByteScape.Core.Apps;
using ByteScape.Core.Cleanup;
using ByteScape.Core.Config;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;

namespace ByteScape.Diag;

internal static partial class Program
{
    private static void CleanupCheck(string path)
    {
        Out($"== CLEANUP {path} (read only)");
        AppConfig.Replace(new AppConfig { WatchDog = false });
        var tree = new ScanTree(DataCache.CreateRoot(path), live: true);
        var sw = Stopwatch.StartNew();
        tree.StartMasterScan();
        while (tree.IsMasterScanning) Thread.Sleep(10);
        Out($"   scan           : {sw.Elapsed.TotalSeconds:0.00} s, {tree.Root.FileCount:N0} files, {tree.Root.FolderCount:N0} folders");

        sw.Restart();
        var catalog = AppCatalog.LoadFromSystem();
        var catalogMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var footprint = AppFootprint.Compute(tree.Root, catalog, n => n.DiskSize, CancellationToken.None, tree.Sync);
        var footMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var starts = AppLastUse.FromRegistry();
        var startsMs = sw.ElapsedMilliseconds;
        var ctx = new SafetyContext { Sync = tree.Sync, Catalog = catalog, HardLinks = tree.CountsLinksOnce ? tree.HardLinks : null };
        sw.Restart();
        var r = CleanupFinder.Find(tree.Root, ctx, CleanupOptions.FromConfig(AppConfig.Current), footprint, starts, CancellationToken.None);
        Out($"   categories     : {sw.ElapsedMilliseconds} ms (programs read {catalogMs} ms, footprint {footMs} ms, program starts {startsMs} ms: {starts.Entries} remembered)");
        foreach (var c in Enum.GetValues<CleanupCategory>())
        {
            var items = r.Of(c);
            Out($"   {CleanupFinder.Title(c),-24}: {items.Count,4} item(s), {Fmt(r.BytesOf(c)),9}, safe {items.Count(i => i.Level == SafetyLevel.Safe)}, ticked {items.Count(i => i.Preselected)}, protected {items.Count(i => i.Assessment?.Blocked == true)}");
            foreach (var i in items.Take(12)) Out($"      {Fmt(i.Bytes),9}  [{DeleteSafety.LevelText(i.Level)}{(i.Assessment?.Blocked == true ? ", blocked: " + i.Assessment.Headline : "")}] {i.Title}  {i.WhenText}  {i.Path}");
        }
        foreach (var n in r.Notes) Out("   note: " + n);

        // the verdict for places everybody has, and what trying the rights finds (as this process: not administrator)
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var probes = new List<string>
        {
            @"C:\", @"C:\Windows", @"C:\Windows\System32", @"C:\Windows\Temp", @"C:\Windows\SoftwareDistribution\Download", @"C:\Windows\WinSxS",
            @"C:\Program Files", @"C:\ProgramData", @"C:\ProgramData\Package Cache", @"C:\Users", profile,
            profile + @"\Documents", profile + @"\Downloads", profile + @"\AppData\Local", profile + @"\AppData\Local\Temp",
            profile + @"\AppData\Local\Google\Chrome\User Data\Default\Cache", @"C:\pagefile.sys", @"C:\hiberfil.sys", @"C:\$Recycle.Bin", @"C:\System Volume Information",
        };
        var app = catalog.Apps.FirstOrDefault(a => !a.IsComponent && a.Source == AppSource.Desktop && a.InstallDirs.Any(d => d.StartsWith(@"C:\Program Files", StringComparison.OrdinalIgnoreCase) && tree.Find(d) != null));
        if (app != null)
        {
            var dir = app.InstallDirs.First(d => tree.Find(d) != null);
            probes.Add(dir);
            if (tree.Find(dir) is ContainerNode c)
                lock (tree.Sync) if (c.Children.OfType<FileNode>().FirstOrDefault() is { } f) probes.Add(f.FullPath);
        }
        Out("   verdicts (and rights, first 2,000 elements):");
        foreach (var p in probes)
        {
            if (tree.Find(p) is not { } node) { Out($"      {"(not in tree)",-18} {p}"); continue; }
            sw.Restart();
            var a = DeleteSafety.Assess(node, ctx);
            var assessMs = sw.Elapsed.TotalMilliseconds;
            string rights = "";
            if (!a.Blocked)
            {
                var s = AccessProbe.ProbeTree(node, a.Path, tree.Sync, 2000, CancellationToken.None, includeRoot: !a.ContentOnly);
                rights = $"  rights: {s.Checked:N0} checked{(s.Complete ? "" : "+")}, admin {s.NeedsAdminCount}, Windows {s.ProtectedCount}, in use {s.InUseCount}";
            }
            Out($"      {a.LevelText,-18} {p}  —  {a.Headline}{(a.ContentOnly ? " [content only]" : "")}  ({assessMs:0} ms){rights}");
        }

        // how long the verdict takes for every folder of the drive (tooltips and lists compute it on the fly)
        List<FsNode> folders;
        lock (tree.Sync) folders = tree.Root.Descendants().Where(n => n is ContainerNode).ToList();
        var sample = folders.Where((_, i) => i % Math.Max(1, folders.Count / 2000) == 0).Take(2000).ToList();
        var quick = new SafetyContext { Sync = tree.Sync, Catalog = catalog, HardLinks = ctx.HardLinks, ContentLimit = 20_000 };
        sw.Restart();
        var levels = sample.Select(n => DeleteSafety.Assess(n, quick).Level).GroupBy(l => l).OrderBy(g => g.Key).Select(g => $"{DeleteSafety.LevelText(g.Key)} {g.Count()}").ToList();
        Out($"   {sample.Count:N0} folders assessed in {sw.ElapsedMilliseconds} ms ({(double)sw.ElapsedMilliseconds / Math.Max(1, sample.Count):0.00} ms each): {string.Join(", ", levels)}");
        DataCache.Release(tree);
        AppConfig.Replace(new AppConfig());
    }
}
