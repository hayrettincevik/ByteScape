using System.Diagnostics;
using System.Runtime.InteropServices;
using ByteScape.Core.Config;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using Xunit;
using Xunit.Abstractions;

namespace ByteScape.Tests;

/// <summary>Hard-linked files counted once: file ids from the listing and the MFT, grouping, ownership, option off.</summary>
[Collection("config")]
public class HardLinkTests
{
    private readonly ITestOutputHelper _out;
    public HardLinkTests(ITestOutputHelper o) => _out = o;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newName, string existing, IntPtr security);

    internal static readonly string Dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "hardlinks");

    /// <summary>links\a\target.bin with three more names (b\copy.bin, m.bin, z\zcopy.bin), a second file with two names, a plain file.</summary>
    internal static void BuildLayout()
    {
        if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        foreach (var d in new[] { "a", "b", "z", "other" }) Directory.CreateDirectory(Path.Combine(Dir, d));
        System.IO.File.WriteAllBytes(Path.Combine(Dir, @"a\target.bin"), new byte[100_000]);
        foreach (var name in new[] { @"b\copy.bin", "m.bin", @"z\zcopy.bin" })
            Assert.True(CreateHardLinkW(Path.Combine(Dir, name), Path.Combine(Dir, @"a\target.bin"), IntPtr.Zero), "CreateHardLink " + Marshal.GetLastWin32Error());
        System.IO.File.WriteAllBytes(Path.Combine(Dir, @"other\lib.dll"), new byte[30_000]);
        Assert.True(CreateHardLinkW(Path.Combine(Dir, @"a\lib.dll"), Path.Combine(Dir, @"other\lib.dll"), IntPtr.Zero));
        System.IO.File.WriteAllBytes(Path.Combine(Dir, @"other\plain.txt"), new byte[5000]);
    }

    internal static ScanTree Scan(bool once, string? path = null)
    {
        AppConfig.Replace(new AppConfig { WatchDog = false, CountHardLinksOnce = once });
        var tree = new ScanTree(DataCache.CreateRoot(path ?? Dir), live: true);
        tree.StartMasterScan();
        var sw = Stopwatch.StartNew();
        while (tree.IsMasterScanning && sw.ElapsedMilliseconds < 20000) Thread.Sleep(5);
        Assert.Equal(ScanState.Completed, tree.MasterState);
        return tree;
    }

    private static FileNode F(ScanTree t, string rel) => (FileNode)GameTests.Node(t.Root, rel);

    [Fact]
    public void HardLinkedFiles_AreCountedOnce_AtTheFirstName()
    {
        try
        {
            BuildLayout();
            var tree = Scan(once: true);
            Assert.True(tree.CountsLinksOnce);
            var target = F(tree, @"a\target.bin");
            Assert.NotEqual(0, target.FileId);
            Assert.Equal(target.FileId, F(tree, "m.bin").FileId);                 // the names share the id
            Assert.NotEqual(target.FileId, F(tree, @"other\plain.txt").FileId);
            Assert.Equal(102_400, target.DiskSize);                                 // the first name in path order carries the space
            foreach (var shadow in new[] { @"b\copy.bin", "m.bin", @"z\zcopy.bin" })
            {
                Assert.Equal(0, F(tree, shadow).DiskSize);
                Assert.Equal(100_000, F(tree, shadow).LogicalSize);                 // its length is unchanged
                Assert.True(F(tree, shadow).CountedElsewhere);
            }
            Assert.Equal(0, F(tree, @"other\lib.dll").DiskSize);                    // "a\lib.dll" comes first
            Assert.Equal(32_768, F(tree, @"a\lib.dll").DiskSize);
            Assert.Equal(102_400 + 32_768 + 8192, tree.Root.DiskSize);               // each file once
            var names = tree.HardLinks.GroupOf(F(tree, @"z\zcopy.bin"))!;
            Assert.Equal(4, names.Length);
            Assert.Same(target, names[0]);
            Assert.Null(tree.HardLinks.GroupOf(F(tree, @"other\plain.txt")));
            Assert.Equal(2, tree.HardLinks.Files);
            Assert.Equal(4, tree.HardLinks.ExtraNames);
            Assert.Equal(3 * 102_400 + 32_768, tree.HardLinks.SavedBytes);

            // a second scan of the same tree keeps the same picture
            tree.StartMasterScan();
            var sw = Stopwatch.StartNew();
            while (tree.IsMasterScanning && sw.ElapsedMilliseconds < 20000) Thread.Sleep(5);
            Assert.Equal(102_400 + 32_768 + 8192, tree.Root.DiskSize);
            // a folder listed again (as the file system watcher does) does not bring the space back to a shadow
            new DirectoryScanner(tree, CancellationToken.None).RefreshDirectory((ContainerNode)GameTests.Node(tree.Root, "z"), Path.Combine(Dir, "z"), recurseInto: false);
            Assert.Equal(0, F(tree, @"z\zcopy.bin").DiskSize);

            // the name that carried the space is deleted: the next one takes it over at the next scan
            System.IO.File.Delete(Path.Combine(Dir, @"a\target.bin"));
            tree.StartMasterScan();
            sw.Restart();
            while (tree.IsMasterScanning && sw.ElapsedMilliseconds < 20000) Thread.Sleep(5);
            Assert.Equal(102_400, F(tree, @"b\copy.bin").DiskSize);
            Assert.False(F(tree, @"b\copy.bin").CountedElsewhere);
            Assert.Equal(102_400 + 32_768 + 8192, tree.Root.DiskSize);
            Assert.Equal(3, tree.HardLinks.GroupOf(F(tree, "m.bin"))!.Length);

            // option off: once per name, like Explorer
            var every = Scan(once: false);
            Assert.False(every.CountsLinksOnce);
            Assert.Equal(0, F(every, "m.bin").FileId);
            Assert.Equal(3 * 102_400 + 2 * 32_768 + 8192, every.Root.DiskSize);
            Assert.Equal(0, every.HardLinks.Files);
        }
        finally { AppConfig.Replace(new AppConfig()); try { Directory.Delete(Dir, true); } catch { } }
    }

    [Fact]
    public void ListingWithFileIds_GivesTheSameTree_AsTheNormalListing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "idlisting");
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            var rnd = new Random(3);
            var folders = new List<string> { dir };
            for (int i = 0; i < 150; i++)
            {
                var f = Path.Combine(folders[rnd.Next(folders.Count)], "d" + i + (i % 4 == 0 ? " ünicode ç" : ""));
                Directory.CreateDirectory(f);
                folders.Add(f);
                for (int k = 0; k < 5; k++)
                {
                    var file = Path.Combine(f, $"f{k}_{i}.bin");
                    System.IO.File.WriteAllBytes(file, new byte[rnd.Next(0, 9000)]);
                    if (k == 0) System.IO.File.SetAttributes(file, FileAttributes.Hidden);
                }
            }
            Directory.CreateDirectory(@"\\?\" + Path.Combine(dir, "odd."));
            System.IO.File.WriteAllBytes(@"\\?\" + Path.Combine(dir, "odd.", "x.bin"), new byte[1234]);
            static List<string> Lines(ScanTree t) => t.Root.Descendants().Select(n => n.FullPath + "|" + n.LogicalSize + "|" + n.DiskSize + "|"
                + (n is FileNode f ? f.Attributes + "|" + f.Modified.Ticks + "|" + f.Created.Ticks : "")).OrderBy(s => s, StringComparer.Ordinal).ToList();
            var withIds = Lines(Scan(once: true, dir));
            var plain = Lines(Scan(once: false, dir));
            Assert.Equal(150 * 5 + 1, withIds.Count(l => l.Contains(".bin|")));
            Assert.Equal(plain, withIds);
        }
        finally { AppConfig.Replace(new AppConfig()); try { Directory.Delete(@"\\?\" + dir, true); } catch { } }
    }

    [Fact]
    public void Grouping_OnSeveralThreads_GivesTheSameSizes()
    {
        (ScanTree Tree, List<FileNode> Files) Make()
        {
            var root = new DriveNode(@"Q:\") { IsDrive = false, Enumerated = true, ScanComplete = true };
            var rnd = new Random(5);
            var files = new List<FileNode>();
            for (int i = 0; i < 20_000; i++)
            {
                var f = AppsTests.File(AppsTests.Path(root, $@"d{rnd.Next(300)}\s{rnd.Next(5)}"), $"f{i}.bin", 4096L * (1 + rnd.Next(10)));
                f.FileId = 1 + rnd.Next(12_000);                                   // collisions: groups of 1..6 names
                files.Add(f);
            }
            return (new ScanTree(root, live: true), files);
        }
        var (one, oneFiles) = Make();
        var (many, manyFiles) = Make();
        var sw = Stopwatch.StartNew();
        one.HardLinks.Apply(one, CancellationToken.None, threads: 1);
        long t1 = sw.ElapsedMilliseconds;
        sw.Restart();
        many.HardLinks.Apply(many, CancellationToken.None, threads: 8);
        _out.WriteLine($"1 thread {t1} ms, 8 threads {sw.ElapsedMilliseconds} ms; {one.HardLinks.Files} files with {one.HardLinks.ExtraNames} more names");
        Assert.Equal(8, many.HardLinks.Timing.Threads);
        Assert.True(one.HardLinks.Files > 1000);
        Assert.Equal(one.HardLinks.Files, many.HardLinks.Files);
        Assert.Equal(one.HardLinks.ExtraNames, many.HardLinks.ExtraNames);
        Assert.Equal(one.HardLinks.SavedBytes, many.HardLinks.SavedBytes);
        Assert.Equal(one.Root.DiskSize, many.Root.DiskSize);
        for (int i = 0; i < oneFiles.Count; i++)
        {
            Assert.Equal(oneFiles[i].DiskSize, manyFiles[i].DiskSize);
            Assert.Equal(oneFiles[i].CountedElsewhere, manyFiles[i].CountedElsewhere);
        }
        // the first name in path order carries the space, whatever thread found it
        var g = many.HardLinks.GroupOf(manyFiles.First(f => f.CountedElsewhere))!;
        Assert.Equal(g.Select(x => x.FullPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase), g.Select(x => x.FullPath));
        Assert.False(g[0].CountedElsewhere);
        // cancelled before it starts: nothing changes
        var (third, _) = Make();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => third.HardLinks.Apply(third, cts.Token, threads: 4));
        Assert.Equal(0, third.HardLinks.Files);
    }

    [Fact]
    public void Grouping_OfNodesWithTheSameId()
    {
        // what the MFT scan gives: record number and sequence make the id
        var ix = new Core.Scanning.Mft.MftIndex(100, 4096);
        ix.Seq[42] = 7;
        Assert.Equal(42L | (7L << 48), ix.FileIdOf(42));

        var root = new DriveNode(@"Q:\") { IsDrive = false, Enumerated = true, ScanComplete = true };
        var tree = new ScanTree(root, live: true);
        var sys = AppsTests.Path(root, @"Windows\System32");
        var sxs = AppsTests.Path(root, @"Windows\WinSxS\amd64_x");
        var a = AppsTests.File(sxs, "kernel.dll", 53_248); a.FileId = 99;
        var b = AppsTests.File(sys, "kernel.dll", 53_248); b.FileId = 99;
        var c = AppsTests.File(sys, "alone.dll", 1000); c.FileId = 100;
        Assert.Equal(2 * 53_248 + 1000, root.DiskSize);
        tree.HardLinks.Apply(tree, CancellationToken.None);
        Assert.Equal(53_248, b.DiskSize);                                           // System32 comes before WinSxS
        Assert.Equal(0, a.DiskSize);
        Assert.True(a.CountedElsewhere);
        Assert.Equal(53_248, a.LogicalSize);
        Assert.Equal(53_248 + 1000, root.DiskSize);                                 // the folders' totals follow
        Assert.Equal(0, sxs.DiskSize);
        Assert.Equal(53_248, tree.HardLinks.SavedBytes);
        // the name in System32 is gone: the WinSxS one counts its own space again at the next pass
        sys.RemoveChild(b);
        tree.HardLinks.Apply(tree, CancellationToken.None);
        Assert.Equal(53_248, a.DiskSize);
        Assert.False(a.CountedElsewhere);
        Assert.Equal(0, tree.HardLinks.Files);
    }
}
