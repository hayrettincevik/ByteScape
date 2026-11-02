using System.Diagnostics;
using System.Runtime.InteropServices;
using ByteScape.Core.Config;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using Xunit;
using Xunit.Abstractions;

namespace ByteScape.Tests;

/// <summary>Quick rescans from the NTFS change journal: in the same session, after ByteScape was closed, and when the journal cannot say.</summary>
[Collection("config")]
public class ChangeJournalTests
{
    private readonly ITestOutputHelper _out;
    public ChangeJournalTests(ITestOutputHelper o) => _out = o;

    internal static readonly string Dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "journal");
    internal static readonly string Outside = Path.Combine(Path.GetTempPath(), "bytescape-tests", "journal-outside");
    private static readonly string Store = Path.Combine(Path.GetTempPath(), "bytescape-tests", "journal-store");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newName, string existing, IntPtr security);

    private static string P(string rel) => Path.Combine(Dir, rel);

    /// <summary>
    /// keep\ (120 nested folders, untouched), and one folder per kind of change: a (a file added), b (a file grown), c (deleted
    /// with its sub folders), d (renamed), e (moved into f), g (moved away, another g with a sub folder of the same name made),
    /// h\owner.bin with a second name i\link.bin (the first name deleted: the other gets the space back), j (a file hidden);
    /// outside the tree n\n1 with a file (moved in).
    /// </summary>
    internal static void BuildLayout()
    {
        Clean();
        var rnd = new Random(5);
        var keep = new List<string> { P("keep") };
        Directory.CreateDirectory(keep[0]);
        for (int i = 0; i < 120; i++)
        {
            var f = Path.Combine(keep[rnd.Next(keep.Count)], "k" + i);
            Directory.CreateDirectory(f);
            keep.Add(f);
            System.IO.File.WriteAllBytes(Path.Combine(f, "k" + i + ".bin"), new byte[rnd.Next(1, 9000)]);
        }
        foreach (var d in new[] { "a", "b", @"c\c1", @"c\c2", @"d\d1", @"e\e1", "f", @"g\sub", "h", "i", "j" }) Directory.CreateDirectory(P(d));
        System.IO.File.WriteAllText(P(@"a\a.txt"), "a");
        System.IO.File.WriteAllBytes(P(@"b\grow.bin"), new byte[10_000]);
        System.IO.File.WriteAllBytes(P(@"c\c2\x.bin"), new byte[7000]);
        System.IO.File.WriteAllBytes(P(@"d\d1\y.bin"), new byte[6000]);
        System.IO.File.WriteAllBytes(P(@"e\e1\z.bin"), new byte[5000]);
        System.IO.File.WriteAllText(P(@"f\f.txt"), "f");
        System.IO.File.WriteAllBytes(P(@"g\sub\old.bin"), new byte[8000]);
        System.IO.File.WriteAllBytes(P(@"h\owner.bin"), new byte[50_000]);
        Assert.True(CreateHardLinkW(P(@"i\link.bin"), P(@"h\owner.bin"), IntPtr.Zero), "CreateHardLink " + Marshal.GetLastWin32Error());
        System.IO.File.WriteAllText(P(@"j\attr.txt"), "j");
        Directory.CreateDirectory(Path.Combine(Outside, @"n\n1"));
        System.IO.File.WriteAllBytes(Path.Combine(Outside, @"n\n1\n2.bin"), new byte[4000]);
    }

    /// <summary>Every kind of change of <see cref="BuildLayout"/>.</summary>
    internal static void Change()
    {
        System.IO.File.WriteAllText(P(@"a\new.txt"), "hello");
        using (var fs = new FileStream(P(@"b\grow.bin"), FileMode.Open)) fs.SetLength(200_000);
        Directory.Delete(P("c"), true);
        Directory.Move(P("d"), P("d-renamed"));
        Directory.Move(P("e"), P(@"f\e"));
        Directory.Move(P("g"), Path.Combine(Outside, "g-old"));
        Directory.CreateDirectory(P(@"g\sub"));
        System.IO.File.WriteAllBytes(P(@"g\sub\new.bin"), new byte[3000]);
        System.IO.File.Delete(P(@"h\owner.bin"));
        System.IO.File.SetAttributes(P(@"j\attr.txt"), FileAttributes.Hidden);
        Directory.Move(Path.Combine(Outside, "n"), P("n"));
    }

    internal static void Clean()
    {
        foreach (var d in new[] { Dir, Outside, Store })
            if (Directory.Exists(d))
            {
                foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)) System.IO.File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(d, true);
            }
    }

    internal static List<string> Lines(ScanTree t)
    {
        lock (t.Sync)
            return t.Root.Descendants().Select(n => n.FullPath + "|" + n.LogicalSize + "|" + n.DiskSize + "|" + ((n as FileNode)?.Attributes & FileAttributes.Hidden)).OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    private static void Wait(Func<bool> done, int ms = 20000)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < ms) Thread.Sleep(5);
        Assert.True(done(), "timed out");
    }

    internal static ScanTree FullScan()
    {
        var tree = new ScanTree(DataCache.CreateRoot(Dir), live: true);
        tree.StartMasterScan();
        Wait(() => !tree.IsMasterScanning);
        Assert.Equal(ScanState.Completed, tree.MasterState);
        return tree;
    }

    /// <summary>False (and says so) when the test drive keeps no change journal: nothing to test there.</summary>
    private bool HasJournal()
    {
        Directory.CreateDirectory(Path.GetTempPath());
        if (ChangeJournal.Mark(Path.GetTempPath()) != null) return true;
        _out.WriteLine("The drive of %TEMP% keeps no NTFS change journal: skipped");
        return false;
    }

    private static long IdOf(ScanTree t, string rel) => t.Find(P(rel)) is FolderNode f ? f.FileId : throw new Exception("no folder " + rel);

    [Fact]
    public void Journal_NamesTheFoldersThatChanged_AndTellsWhenItCannot()
    {
        if (!HasJournal()) return;
        try
        {
            BuildLayout();
            AppConfig.Replace(new AppConfig { WatchDog = false });
            var tree = FullScan();
            var mark = ChangeJournal.Mark(Dir)!;
            Assert.NotNull(mark);
            System.IO.File.WriteAllText(P(@"a\new.txt"), "hello");
            System.IO.File.SetAttributes(P(@"j\attr.txt"), FileAttributes.Hidden);
            Directory.CreateDirectory(P(@"f\made"));
            var ch = ChangeJournal.ReadSince(Dir, mark, default, out var why);
            Assert.True(ch != null, why);
            _out.WriteLine($"{ch!.Records} records, {ch.Folders.Count} folders on the drive, {ch.Elapsed.TotalMilliseconds:0} ms");
            Assert.Contains(IdOf(tree, "a"), ch.Folders);
            Assert.Contains(IdOf(tree, "j"), ch.Folders);
            Assert.Contains(IdOf(tree, "f"), ch.Folders);
            Assert.DoesNotContain(IdOf(tree, "b"), ch.Folders);
            Assert.DoesNotContain(IdOf(tree, @"keep\k0"), ch.Folders);
            Assert.True(ch.Next.Usn > mark.Usn);
            Assert.Equal(mark.JournalId, ch.Next.JournalId);

            // read in small pieces: the same folders
            var old = ChangeJournal.BufferBytes;
            try
            {
                ChangeJournal.BufferBytes = 4096;
                var small = ChangeJournal.ReadSince(Dir, mark, default, out _)!;
                Assert.Superset(new HashSet<long> { IdOf(tree, "a"), IdOf(tree, "j"), IdOf(tree, "f") }, small.Folders);
            }
            finally { ChangeJournal.BufferBytes = old; }

            // from the new mark on: nothing of ours
            var after = ChangeJournal.ReadSince(Dir, ch.Next, default, out _)!;
            Assert.DoesNotContain(IdOf(tree, "a"), after.Folders);

            // what the journal cannot say
            Assert.Null(ChangeJournal.ReadSince(Dir, mark with { JournalId = mark.JournalId + 1 }, default, out why));
            Assert.Contains("made again", why);
            Assert.Null(ChangeJournal.ReadSince(Dir, mark with { Volume = mark.Volume + 1 }, default, out why));
            Assert.Contains("another drive", why);
            Assert.Null(ChangeJournal.ReadSince(Dir, mark with { RootId = mark.RootId + 1 }, default, out why));
            Assert.Contains("replaced", why);
            Assert.False(ChangeJournal.IsCurrent(mark with { Usn = -1 }, Dir, out why));
            Assert.Contains("no longer holds", why);
            Assert.True(ChangeJournal.IsCurrent(mark, Dir, out _));
            // the folder deleted and made again: another folder
            Directory.Delete(Dir, true);
            Directory.CreateDirectory(Dir);
            Assert.False(ChangeJournal.IsCurrent(mark, Dir, out why));
            Assert.Contains("replaced", why);
        }
        finally { AppConfig.Replace(new AppConfig()); Clean(); }
    }

    [Fact]
    public void MarkChanged_ListsTheNamedFolders_AndWalksOnlyTheFoldersAbove()
    {
        var tree = new ScanTree(new DriveNode(@"X:\journal-test\"), live: true);
        FolderNode F(ContainerNode parent, string name, long id)
        {
            var f = new FolderNode(name) { FileId = id, Enumerated = true, ScanComplete = true };
            parent.AddChild(f);
            return f;
        }
        tree.Root.Enumerated = true;
        var a = F(tree.Root, "a", 10); var a1 = F(a, "a1", 11); var a2 = F(a, "a2", 12); var a21 = F(a2, "a21", 13);
        var b = F(tree.Root, "b", 20); var b1 = F(b, "b1", 21);
        var noId = F(tree.Root, "noid", 0);
        var unlisted = F(b1, "unlisted", 22); unlisted.Enumerated = false;

        int n = tree.MarkChanged(new HashSet<long> { 13, 999 }, rootId: 5);
        Assert.Equal(3, n);                                                        // a21, the folder without id, the unlisted one
        Assert.False(a21.Enumerated); Assert.False(a21.ScanComplete);
        Assert.True(a2.Enumerated); Assert.False(a2.ScanComplete);                 // above: walked, not listed
        Assert.True(a.Enumerated); Assert.False(a.ScanComplete);
        Assert.True(tree.Root.Enumerated); Assert.False(tree.Root.ScanComplete);
        Assert.True(a1.ScanComplete); Assert.True(a1.Enumerated);                  // beside: left alone
        Assert.False(noId.Enumerated);
        Assert.False(unlisted.Enumerated); Assert.False(b1.ScanComplete); Assert.False(b.ScanComplete);

        // the root named by its id
        foreach (var c in tree.Root.Descendants(true).OfType<ContainerNode>()) { c.Enumerated = true; c.ScanComplete = true; }
        noId.FileId = 30;
        Assert.Equal(1, tree.MarkChanged(new HashSet<long> { 5 }, rootId: 5));
        Assert.False(tree.Root.Enumerated);
        Assert.True(a.ScanComplete);
    }

    [Fact]
    public void QuickRescan_ListsOnlyWhatChanged_AndMatchesAFullScan()
    {
        if (!HasJournal()) return;
        try
        {
            BuildLayout();
            AppConfig.Replace(new AppConfig { WatchDog = false, ScanThreads = 1 });
            var tree = FullScan();
            Assert.NotNull(tree.ChangeMark);
            Assert.True(tree.CanRescanChanges);
            Assert.Equal(1, tree.HardLinks.Files);                                 // owner.bin and link.bin
            Change();
            var reference = Lines(FullScan());

            var listed = new List<string>();
            DirectoryScanner.Listed = n => { lock (listed) listed.Add(n.FullPath.Substring(Dir.Length).TrimStart('\\')); };
            var sw = Stopwatch.StartNew();
            tree.StartMasterScan(changesOnly: true);
            Assert.NotNull(tree.MasterScan!.Since);
            Wait(() => !tree.IsMasterScanning);
            DirectoryScanner.Listed = null;
            _out.WriteLine($"quick rescan {sw.ElapsedMilliseconds} ms, {tree.MasterScan.Journal}: listed {string.Join(", ", listed)}");
            Assert.Equal(ScanState.Completed, tree.MasterState);
            Assert.Equal("NTFS change journal", tree.MasterScan.Method);
            Assert.True(tree.Root.ScanComplete);
            Assert.Equal(reference, Lines(tree));
            Assert.DoesNotContain(listed, p => p.StartsWith("keep", StringComparison.OrdinalIgnoreCase));
            foreach (var must in new[] { "", "a", "b", "d-renamed", @"d-renamed\d1", "f", @"f\e", @"f\e\e1", "g", @"g\sub", "h", "j", "n", @"n\n1" })
                Assert.Contains(must, listed);
            Assert.Equal(listed.Count, listed.Distinct().Count());                 // no folder twice
            // the other name of the deleted file has its space back; the new g has only its own content
            Assert.True(tree.Find(P(@"i\link.bin"))!.DiskSize >= 50_000);
            Assert.Null(tree.Find(P(@"g\sub\old.bin")));
            Assert.Equal(0, tree.HardLinks.Files);

            // nothing changed since: nothing listed, and the mark moved on
            var mark = tree.ChangeMark!;
            listed.Clear();
            DirectoryScanner.Listed = n => { lock (listed) listed.Add(n.FullPath); };
            tree.StartMasterScan(changesOnly: true);
            Wait(() => !tree.IsMasterScanning);
            DirectoryScanner.Listed = null;
            Assert.Empty(listed);
            Assert.Equal(0, tree.MasterScan!.Journal!.ToList);
            Assert.True(tree.ChangeMark!.Usn >= mark.Usn);
            Assert.Equal(reference, Lines(tree));
        }
        finally { DirectoryScanner.Listed = null; AppConfig.Replace(new AppConfig()); Clean(); }
    }

    [Fact]
    public void CompleteScan_IsKept_AndBroughtUpToDateInANewTree()
    {
        if (!HasJournal()) return;
        ScanResume.DirOverride = Store;
        try
        {
            BuildLayout();
            AppConfig.Replace(new AppConfig { WatchDog = false, ScanThreads = 1 });
            var tree = FullScan();
            Assert.True(ScanResume.Save(tree));
            var info = ScanResume.Find(Dir)!;
            Assert.True(info.Complete);
            Assert.Equal(tree.ChangeMark, info.Mark);
            Assert.Equal(0, info.PendingFolders);

            // "ByteScape closed": the drive changes meanwhile
            Change();
            var reference = Lines(FullScan());
            Assert.True(ChangeJournal.IsCurrent(info.Mark!, Dir, out _));
            var again = new ScanTree(DataCache.CreateRoot(Dir), live: true);
            Assert.True(ScanResume.Restore(again, info));
            Assert.Equal(info.Mark, again.ChangeMark);
            var listed = new List<string>();
            DirectoryScanner.Listed = n => { lock (listed) listed.Add(n.FullPath); };
            again.StartMasterScan(changesOnly: true);
            Wait(() => !again.IsMasterScanning);
            DirectoryScanner.Listed = null;
            _out.WriteLine($"{again.MasterScan!.Journal}: {listed.Count} folders listed");
            Assert.Equal(ScanState.Completed, again.MasterState);
            Assert.Equal("NTFS change journal", again.MasterScan.Method);
            Assert.Equal(reference, Lines(again));                                 // the hard link's space came back too
            Assert.InRange(listed.Count, 10, 20);                                  // of 140 folders
        }
        finally
        {
            DirectoryScanner.Listed = null;
            ScanResume.DirOverride = TestSetup.KeptScans;
            AppConfig.Replace(new AppConfig());
            Clean();
        }
    }

    [Fact]
    public void QuickRescan_ListsEverything_WhenTheJournalCannotSay()
    {
        if (!HasJournal()) return;
        try
        {
            BuildLayout();
            AppConfig.Replace(new AppConfig { WatchDog = false, ScanThreads = 1 });
            var tree = FullScan();
            int folders = tree.Root.FolderCount + 1;
            Change();
            var reference = Lines(FullScan());
            var real = tree.ChangeMark!;
            tree.ChangeMark = real with { JournalId = real.JournalId + 1 };        // as if the journal had been made again
            Assert.True(tree.CanRescanChanges);
            int listed = 0;
            DirectoryScanner.Listed = _ => Interlocked.Increment(ref listed);
            tree.StartMasterScan(changesOnly: true);
            Wait(() => !tree.IsMasterScanning);
            DirectoryScanner.Listed = null;
            Assert.Equal(ScanState.Completed, tree.MasterState);
            Assert.Null(tree.MasterScan!.Journal);
            Assert.Equal("directory listing", tree.MasterScan.Method);
            Assert.Equal(reference, Lines(tree));
            Assert.True(listed >= folders - 10, $"{listed} of about {folders} folders listed");
            Assert.Equal(real.JournalId, tree.ChangeMark!.JournalId);              // a new, real mark for next time
        }
        finally { DirectoryScanner.Listed = null; AppConfig.Replace(new AppConfig()); Clean(); }
    }

    [Fact]
    public void QuickRescanOff_KeepsNoMark_AndACompleteScanIsNotKept()
    {
        ScanResume.DirOverride = Store;
        try
        {
            BuildLayout();
            AppConfig.Replace(new AppConfig { WatchDog = false, QuickRescan = false });
            var tree = FullScan();
            Assert.Null(tree.ChangeMark);
            Assert.False(tree.CanRescanChanges);
            Assert.False(ScanResume.Save(tree));
            // links followed: the journal of this drive does not cover their targets
            AppConfig.Replace(new AppConfig { WatchDog = false, FollowReparsePoints = true });
            Assert.False(FullScan().TracksChanges);
        }
        finally { ScanResume.DirOverride = TestSetup.KeptScans; AppConfig.Replace(new AppConfig()); Clean(); }
    }

    [Fact]
    public void Watchdog_ScansAFolderMovedIn_WithItsContent()
    {
        try
        {
            BuildLayout();
            AppConfig.Replace(new AppConfig { WatchDog = true });
            var tree = FullScan();
            tree.EnableWatchdog();
            Thread.Sleep(300);
            Directory.Move(Path.Combine(Outside, "n"), P("n"));                   // a folder with a sub folder and a file
            // the same name, another folder: its old content goes away, the new one comes in
            Directory.Move(P("g"), Path.Combine(Outside, "g-old"));
            Directory.CreateDirectory(P(@"g\sub"));
            System.IO.File.WriteAllBytes(P(@"g\sub\new.bin"), new byte[3000]);
            Wait(() => tree.Find(P(@"n\n1\n2.bin")) != null && tree.Find(P(@"g\sub\new.bin")) != null && tree.Find(P(@"g\sub\old.bin")) == null, 10000);
            Assert.Equal(4000, tree.Find(P(@"n\n1\n2.bin"))!.LogicalSize);
            tree.Dispose();
        }
        finally { AppConfig.Replace(new AppConfig()); Clean(); }
    }
}
