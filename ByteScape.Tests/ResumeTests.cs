using System.Diagnostics;
using System.Runtime.InteropServices;
using ByteScape.Core.Config;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using Xunit;
using Xunit.Abstractions;

namespace ByteScape.Tests;

/// <summary>Pausing, stopping and continuing scans, also after ByteScape was closed (the unfinished scan kept on disk).</summary>
[Collection("config")]
public class ResumeTests
{
    private readonly ITestOutputHelper _out;
    public ResumeTests(ITestOutputHelper o) => _out = o;

    internal static readonly string Dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "resume");
    internal static readonly string Store = Path.Combine(Path.GetTempPath(), "bytescape-tests", "resume-store");

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, IntPtr input, int inSize, IntPtr output, int outSize, out int returned, IntPtr overlapped);

    /// <summary>300 folders with a file each, and a sparse file at the top (its size on disk is not in the snapshot format).</summary>
    internal static void BuildLayout()
    {
        if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        var rnd = new Random(21);
        var folders = new List<string> { Dir };
        Directory.CreateDirectory(Dir);
        for (int i = 0; i < 300; i++)
        {
            var f = Path.Combine(folders[rnd.Next(folders.Count)], "d" + i);
            Directory.CreateDirectory(f);
            folders.Add(f);
            System.IO.File.WriteAllBytes(Path.Combine(f, "x" + i + ".bin"), new byte[rnd.Next(1, 20_000)]);
        }
        using var fs = new FileStream(Path.Combine(Dir, "big.sparse"), FileMode.Create, FileAccess.ReadWrite);
        Assert.True(DeviceIoControl(fs.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero));
        fs.SetLength(30L << 20);
        fs.Position = 1 << 20;
        var data = new byte[8192];
        rnd.NextBytes(data);
        fs.Write(data);
        fs.Flush(true);
    }

    internal static List<string> Lines(ScanTree t)
    {
        lock (t.Sync)
            return t.Root.Descendants().Select(n => n.FullPath + "|" + n.LogicalSize + "|" + n.DiskSize).OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    /// <summary>What the snapshot format did not keep: folder times and attributes, file times and ids.</summary>
    private static List<string> Details(ScanTree t)
    {
        lock (t.Sync)
            return t.Root.Descendants().Select(n => n switch
            {
                FolderNode f => $"{f.FullPath}|{f.Attributes}|{f.Created.Ticks}|{f.Modified.Ticks}|{f.FileId}|{f.Enumerated}|{f.AccessDenied}",
                FileNode f => $"{f.FullPath}|{f.Attributes}|{f.Created.Ticks}|{f.Accessed.Ticks}|{f.Modified.Ticks}|{f.FileId}",
                _ => n.FullPath,
            }).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private static void Wait(Func<bool> done, int ms = 20000)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < ms) Thread.Sleep(5);
        Assert.True(done(), "timed out");
    }

    internal static List<string> Reference()
    {
        var tree = new ScanTree(DataCache.CreateRoot(Dir), live: true);
        tree.StartMasterScan();
        Wait(() => !tree.IsMasterScanning);
        Assert.Equal(ScanState.Completed, tree.MasterState);
        return Lines(tree);
    }

    [Fact]
    public void Pause_HoldsTheScan_AndItsClock_ThenItFinishes()
    {
        try
        {
            BuildLayout();
            AppConfig.Replace(new AppConfig { WatchDog = false, ScanThreads = 1 });
            var reference = Reference();
            int listed = 0;
            ScanTree? tree = null;
            DirectoryScanner.Listed = _ => { if (Interlocked.Increment(ref listed) == 30) tree!.PauseMasterScan(); };
            tree = new ScanTree(DataCache.CreateRoot(Dir), live: true);
            tree.StartMasterScan();
            Wait(() => tree.IsMasterPaused);
            Thread.Sleep(300);
            int at = listed;
            var clock = tree.MasterScan!.Elapsed;
            Thread.Sleep(300);
            Assert.Equal(30, at);                                              // nothing listed while paused
            Assert.Equal(at, listed);
            Assert.Equal(clock, tree.MasterScan.Elapsed);                       // and the time does not run
            Assert.True(tree.IsMasterScanning);
            tree.ContinueMasterScan();
            Wait(() => !tree.IsMasterScanning);
            DirectoryScanner.Listed = null;
            Assert.Equal(ScanState.Completed, tree.MasterState);
            Assert.Equal(reference, Lines(tree));

            // several folders at once: all of them hold
            AppConfig.Replace(new AppConfig { WatchDog = false, ScanThreads = 4 });
            var par = new ScanTree(DataCache.CreateRoot(Dir), live: true);
            par.StartMasterScan();
            par.PauseMasterScan();
            Thread.Sleep(300);
            int c1; lock (par.Sync) c1 = par.Root.FileCount;
            Thread.Sleep(300);
            int c2; lock (par.Sync) c2 = par.Root.FileCount;
            Assert.Equal(c1, c2);
            par.ContinueMasterScan();
            Wait(() => !par.IsMasterScanning);
            Assert.Equal(reference, Lines(par));

            // stopping a paused scan ends it
            var stop = new ScanTree(DataCache.CreateRoot(Dir), live: true);
            stop.StartMasterScan();
            stop.PauseMasterScan();
            stop.StopMasterScan();
            Wait(() => !stop.IsMasterScanning, 3000);
            Assert.Equal(ScanState.Aborted, stop.MasterState);
        }
        finally { DirectoryScanner.Listed = null; AppConfig.Replace(new AppConfig()); try { Directory.Delete(Dir, true); } catch { } }
    }

    /// <summary>Scans one folder at a time and stops after <paramref name="count"/> folders; the paths listed.</summary>
    internal static (ScanTree Tree, List<string> Listed) StopAfter(int count)
    {
        AppConfig.Replace(new AppConfig { WatchDog = false, ScanThreads = 1 });
        var listed = new List<string>();
        ScanTree? tree = null;
        DirectoryScanner.Listed = n => { lock (listed) { listed.Add(n.FullPath); if (listed.Count == count) tree!.StopMasterScan(); } };
        tree = new ScanTree(DataCache.CreateRoot(Dir), live: true);
        tree.StartMasterScan();
        Wait(() => !tree.IsMasterScanning);
        DirectoryScanner.Listed = null;
        Assert.Equal(ScanState.Aborted, tree.MasterState);
        return (tree, listed);
    }

    [Fact]
    public void StoppedScan_ContinuesWithTheFoldersItHadNotReached()
    {
        try
        {
            BuildLayout();
            AppConfig.Replace(new AppConfig { WatchDog = false });
            var reference = Reference();
            var (tree, first) = StopAfter(40);
            Assert.Equal(40, first.Count);
            Assert.True(tree.CanResume);
            var second = new List<string>();
            DirectoryScanner.Listed = n => { lock (second) second.Add(n.FullPath); };
            tree.StartMasterScan(resume: true);
            Assert.True(tree.MasterScan!.Resume);
            Wait(() => !tree.IsMasterScanning);
            DirectoryScanner.Listed = null;
            Assert.Equal(ScanState.Completed, tree.MasterState);
            Assert.False(tree.CanResume);
            Assert.Empty(first.Intersect(second));                             // nothing listed twice
            Assert.Equal(301, first.Count + second.Count);                     // every folder once (the root too)
            Assert.Equal(reference, Lines(tree));
        }
        finally { DirectoryScanner.Listed = null; AppConfig.Replace(new AppConfig()); try { Directory.Delete(Dir, true); } catch { } }
    }

    [Fact]
    public void UnfinishedScan_IsKept_AndContinuesInANewTree()
    {
        ScanResume.DirOverride = Store;
        try
        {
            if (Directory.Exists(Store)) Directory.Delete(Store, true);
            BuildLayout();
            AppConfig.Replace(new AppConfig { WatchDog = false });
            var reference = Reference();
            var (tree, first) = StopAfter(40);
            var partial = Lines(tree);
            List<string> ListedFolders(ScanTree t) { lock (t.Sync) return t.Root.Descendants(true).OfType<ContainerNode>().Where(c => c.Enumerated).Select(c => c.FullPath).OrderBy(p => p).ToList(); }
            var listedBefore = ListedFolders(tree);
            Assert.True(ScanResume.Save(tree));
            var info = ScanResume.Find(Dir + "\\")!;                            // the same path, written either way
            Assert.NotNull(info);
            Assert.Equal(tree.Root.FileCount, info.Files);
            Assert.True(info.PendingFolders > 0);
            _out.WriteLine($"kept: {info.Files} files, {info.Folders} folders, {info.PendingFolders} to go, {info.Nodes} elements");

            // "ByteScape is started again": a new, empty live tree of the same path
            var again = new ScanTree(DataCache.CreateRoot(Dir), live: true);
            Assert.True(ScanResume.Restore(again, info));
            Assert.Equal(partial, Lines(again));                               // sizes on disk too (the sparse file)
            Assert.Equal(Details(tree), Details(again));                       // folder times and attributes, file ids
            Assert.Equal(listedBefore, ListedFolders(again));
            Assert.True(again.CanResume);
            var second = new List<string>();
            DirectoryScanner.Listed = n => { lock (second) second.Add(n.FullPath); };
            again.StartMasterScan(resume: true);
            Wait(() => !again.IsMasterScanning);
            DirectoryScanner.Listed = null;
            Assert.Equal(ScanState.Completed, again.MasterState);
            Assert.Equal(301 - 40, second.Count);                              // only what was not reached then
            Assert.Empty(first.Intersect(second));
            Assert.Equal(reference, Lines(again));

            // forgotten on request, and when too old
            ScanResume.Delete(Dir);
            Assert.Null(ScanResume.Find(Dir));
            Assert.True(ScanResume.Save(tree));
            var old = ScanResume.MaxAge;
            try { ScanResume.MaxAge = TimeSpan.Zero; Assert.Null(ScanResume.Find(Dir)); }
            finally { ScanResume.MaxAge = old; }
            // a cut file: the tree stays empty and the scan starts over
            var st = Directory.GetFiles(Store, "*.tree").Single();
            var bytes = System.IO.File.ReadAllBytes(st);
            System.IO.File.WriteAllBytes(st, bytes.AsSpan(0, bytes.Length / 2).ToArray());
            var broken = new ScanTree(DataCache.CreateRoot(Dir), live: true);
            Assert.False(ScanResume.Restore(broken, ScanResume.Find(Dir)!));
            Assert.Equal(0, broken.Root.ChildCount);
            // a scan kept in the first format (snapshot + state file): not offered, its files go
            System.IO.File.WriteAllText(Path.Combine(Store, "0123456789ABCDEF.json"), "{\"Path\":\"X:/old\",\"SavedUtc\":\"" + DateTime.UtcNow.ToString("o") + "\",\"Key\":\"0123456789ABCDEF\",\"Nodes\":1}");
            System.IO.File.WriteAllText(Path.Combine(Store, "0123456789ABCDEF.sns"), "x");
            System.IO.File.WriteAllText(Path.Combine(Store, "0123456789ABCDEF.state"), "x");
            Assert.DoesNotContain(ScanResume.All(includeOld: true), i => i.Key == "0123456789ABCDEF");
            Assert.Empty(Directory.GetFiles(Store, "0123456789ABCDEF.*"));
            // a complete scan is kept only for quick rescans (with the change journal's position)
            Assert.NotNull(again.ChangeMark);
            AppConfig.Replace(new AppConfig { WatchDog = false, QuickRescan = false });
            Assert.False(ScanResume.Save(again));
        }
        finally
        {
            DirectoryScanner.Listed = null;
            ScanResume.DirOverride = TestSetup.KeptScans;
            AppConfig.Replace(new AppConfig());
            try { Directory.Delete(Dir, true); } catch { }
            try { Directory.Delete(Store, true); } catch { }
        }
    }
}
