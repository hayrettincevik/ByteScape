// Hard links counted once: scan a path with the option on and off, compare, and check groups against Windows.
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ByteScape.Core.Config;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using Microsoft.Win32.SafeHandles;

namespace ByteScape.Diag;

internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleInfo
    {
        public uint Attributes; public long Created, Accessed, Written; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out ByHandleInfo info);

    private static void HardLinksCheck(string path)
    {
        Out("== HARD LINKS " + path);
        (ScanTree tree, double seconds) Run(bool once)
        {
            AppConfig.Replace(new AppConfig { WatchDog = false, CountHardLinksOnce = once, UseMftScan = false });
            var tree = new ScanTree(DataCache.CreateRoot(path), live: true);
            var sw = Stopwatch.StartNew();
            tree.StartMasterScan();
            while (tree.IsMasterScanning) Thread.Sleep(20);
            return (tree, sw.Elapsed.TotalSeconds);
        }
        var (off, tOff) = Run(false);
        var (on, tOn) = Run(true);
        Out($"   once per name : {tOff,6:0.00} s  {off.Root.FileCount:N0} files  disk {Fmt(off.Root.DiskSize)}");
        Out($"   counted once  : {tOn,6:0.00} s  {on.Root.FileCount:N0} files  disk {Fmt(on.Root.DiskSize)}  (grouping {on.HardLinks.Elapsed.TotalMilliseconds:0} ms)");
        Out($"   {on.HardLinks.Files:N0} files have {on.HardLinks.ExtraNames:N0} more names; counted once saves {Fmt(on.HardLinks.SavedBytes)}; difference of the totals {Fmt(off.Root.DiskSize - on.Root.DiskSize)}");
        // the grouping pass again on the same tree, on one thread and on every core (same result: sizes are already set)
        string T((TimeSpan Walk, TimeSpan Group, TimeSpan Apply, int Threads) t) =>
            $"{(t.Walk + t.Group + t.Apply).TotalMilliseconds,5:0} ms (walk {t.Walk.TotalMilliseconds:0}, group {t.Group.TotalMilliseconds:0}, apply {t.Apply.TotalMilliseconds:0}) on {t.Threads} thread(s)";
        long before = on.Root.DiskSize;
        for (int round = 1; round <= 3; round++)
        {
            on.HardLinks.Apply(on, CancellationToken.None, threads: 1);
            var single = on.HardLinks.Timing;
            on.HardLinks.Apply(on, CancellationToken.None);
            Out($"   grouping round {round}: {T(single)}  |  {T(on.HardLinks.Timing)}");
        }
        Out($"   same total after the extra passes: {on.Root.DiskSize == before}");
        foreach (var sub in new[] { @"Windows\WinSxS", @"Windows\System32", @"Program Files", @"Users" })
        {
            var a = off.Find(Path.Combine(path, sub)) as ContainerNode;
            var b = on.Find(Path.Combine(path, sub)) as ContainerNode;
            if (a != null && b != null) Out($"   {sub,-20} once per name {Fmt(a.DiskSize),10}   counted once {Fmt(b.DiskSize),10}");
        }
        // ask Windows about a sample of the groups: same file index, and at least as many links as names found
        var groups = on.Root.Descendants().OfType<FileNode>().Where(f => f.FileId != 0).Select(f => on.HardLinks.GroupOf(f)).Where(g => g != null).Distinct().Cast<FileNode[]>().ToList();
        var rnd = new Random(1);
        int ok = 0, bad = 0, gone = 0;
        foreach (var g in groups.OrderBy(_ => rnd.Next()).Take(300))
        {
            long? index = null; uint links = 0; bool same = true;
            foreach (var n in g)
            {
                try
                {
                    using var h = File.OpenHandle(n.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
                    if (!GetFileInformationByHandle(h, out var info)) { gone++; same = false; break; }
                    long idx = ((long)info.IndexHigh << 32) | info.IndexLow;
                    if (index == null) index = idx; else if (index != idx) same = false;
                    links = info.Links;
                }
                catch (UnauthorizedAccessException)
                {
                    // system files may refuse even read: attributes-only access is enough for the index
                    try
                    {
                        using var h2 = File.OpenHandle(n.FullPath, FileMode.Open, (FileAccess)0, FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
                        if (GetFileInformationByHandle(h2, out var info2)) { long idx = ((long)info2.IndexHigh << 32) | info2.IndexLow; if (index == null) index = idx; else if (index != idx) same = false; links = info2.Links; }
                    }
                    catch { gone++; same = false; break; }
                }
                catch { gone++; same = false; break; }
            }
            if (same && links >= g.Length) ok++;
            else if (same || gone == 0) { bad++; if (bad <= 10) Out($"   !!! group of {g.Length} ({links} links): " + string.Join(" | ", g.Select(x => x.FullPath))); }
        }
        Out($"   Windows check of {Math.Min(300, groups.Count)} random groups: {ok} confirmed (same file, enough links), {bad} wrong, {gone} unreadable");
        foreach (var g in groups.OrderByDescending(g => g[0].DiskSize).Take(8))
            Out($"   {Fmt(g[0].DiskSize),9} x{g.Length}: " + string.Join("  |  ", g.Select(x => x.FullPath)));
        // where the groups are: the first three folders of each name, as pairs
        static string Area(FileNode n)
        {
            var segs = n.FullPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return string.Join("\\", segs.Skip(1).Take(Math.Min(3, segs.Length - 2)));
        }
        Out("   groups by the folders of their names:");
        foreach (var a in groups.GroupBy(g => string.Join("  +  ", g.Select(Area).Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase))).OrderByDescending(x => x.Count()).Take(15))
            Out($"   {a.Count(),7:N0} groups {Fmt(a.Sum(g => g[0].DiskSize * (g.Length - 1))),9} saved: {a.Key}");
        var dump = Environment.GetEnvironmentVariable("HARDLINK_DUMP");
        if (!string.IsNullOrEmpty(dump))
            File.WriteAllLines(dump, groups.Select(g => string.Join("\t", g.Select(x => x.FullPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))).OrderBy(l => l, StringComparer.OrdinalIgnoreCase));
        DataCache.Release(off); DataCache.Release(on);
    }
}
