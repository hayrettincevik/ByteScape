using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using ByteScape.Core.Apps;
using ByteScape.Core.Cleanup;
using ByteScape.Core.Cloud;
using ByteScape.Core.Compare;
using ByteScape.Core.Config;
using ByteScape.Core.Dev;
using ByteScape.Core.Export;
using ByteScape.Core.Forecast;
using ByteScape.Core.Knowledge;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using ByteScape.Core.Snapshot;
using ByteScape.Core.Util;
using ByteScape.Treemap;
using ByteScape.Wpf.Windows;
using Xunit;

namespace ByteScape.Tests;

/// <summary>The security review's findings, kept fixed: deletes that stay where they were checked, untrusted snapshots, deep folder chains, reports, launching programs.</summary>
[Collection("config")]
public class SecurityTests
{
    internal static readonly string Dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "security");

    /// <summary>C:\x\L1\L2\…\L{levels} with a file at the bottom, in memory only.</summary>
    private static (DriveNode Root, ContainerNode Bottom) Chain(int levels, string rootPath = @"C:\bytescape-depth\")
    {
        var root = new DriveNode(rootPath) { IsDrive = false, Enumerated = true, ScanComplete = true };
        ContainerNode c = root;
        for (int i = 1; i <= levels; i++)
        {
            var f = new FolderNode("L" + i) { Enumerated = true, ScanComplete = true, Modified = DateTime.UtcNow.AddDays(-3) };
            c.AddChild(f);
            c = f;
        }
        c.AddChild(new FileNode("bottom.txt") { LogicalSize = 5000, DiskSize = 8192, Modified = DateTime.UtcNow.AddDays(-2) });
        return (root, c);
    }

    /// <summary>Runs <paramref name="a"/> on a thread with half the stack of a program's main (interface) thread (1 MB): room is left for what is already on it.</summary>
    private static void OnSmallStack(Action a)
    {
        Exception? error = null;
        var t = new Thread(() => { try { a(); } catch (Exception ex) { error = ex; } }, 512 << 10);
        t.Start();
        t.Join();
        if (error != null) throw new Xunit.Sdk.XunitException("failed on the 1 MB thread: " + error);
    }

    private static void Clean()
    {
        // what the tests made, junctions included (.NET removes a junction without going into it)
        if (!Directory.Exists(Dir)) return;
        foreach (var f in Directory.EnumerateFiles(Dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        SafeFs.DeleteTree(Dir);
    }

    /// <summary>A junction (no administrator rights needed, unlike a symbolic link), made by Windows' own mklink.</summary>
    private static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "junction not made: " + p.StandardError.ReadToEnd());
    }

    /// <summary>The "junction swap": a folder on the way replaced by a link after the check must not lead the delete elsewhere.</summary>
    [Fact]
    public void Delete_ThroughAFolderSwappedForAJunction_IsRefused_AndNothingElsewhereGoes()
    {
        Clean();
        try
        {
            var root = Path.Combine(Dir, "scan");
            var outside = Path.Combine(Dir, "outside");
            Directory.CreateDirectory(Path.Combine(root, "a", "b"));
            File.WriteAllText(Path.Combine(root, "a", "b", "x.tmp"), "scanned");
            Directory.CreateDirectory(Path.Combine(outside, "b"));
            File.WriteAllText(Path.Combine(outside, "b", "x.tmp"), "precious");
            var guard = new DeleteGuard { ScanRoot = root, RootId = SafeFs.Identity(root), Strict = true };
            var target = Path.Combine(root, "a", "b", "x.tmp");

            // held: the folders on the way cannot be renamed (or swapped) while the delete runs
            using (var hold = guard.Hold(target, out var none))
            {
                Assert.NotNull(hold);
                Assert.Equal("", none);
                Assert.ThrowsAny<IOException>(() => Directory.Move(Path.Combine(root, "a"), Path.Combine(root, "a2")));
            }

            // the swap between the check and the delete
            Directory.Move(Path.Combine(root, "a"), Path.Combine(root, "a-old"));
            Junction(Path.Combine(root, "a"), outside);
            Assert.Null(guard.Hold(target, out var why));
            Assert.Contains("link", why);
            var report = Deleter.Run(new[] { (target, 7L), (Path.Combine(root, "a", "b"), 7L) }, recycle: false, IntPtr.Zero, null, CancellationToken.None, guard);
            Assert.All(report.Items, i => Assert.False(i.Deleted));
            Assert.Equal("precious", File.ReadAllText(Path.Combine(outside, "b", "x.tmp")));
            // the control: without the guard the very same delete reaches the file outside (what the guard prevents)
            var unguarded = Deleter.Run(new[] { (target, 7L) }, recycle: false, IntPtr.Zero, null, CancellationToken.None);
            Assert.True(unguarded.Items[0].Deleted);
            Assert.False(File.Exists(Path.Combine(outside, "b", "x.tmp")));

            // the scanned folder itself replaced by another one of the same name
            var fresh = Path.Combine(Dir, "scan2");
            Directory.CreateDirectory(Path.Combine(fresh, "c"));
            File.WriteAllText(Path.Combine(fresh, "c", "y.tmp"), "new");
            var guard2 = new DeleteGuard { ScanRoot = fresh, RootId = SafeFs.Identity(fresh), Strict = true };
            Directory.Move(fresh, fresh + "-old");
            Directory.CreateDirectory(Path.Combine(fresh, "c"));
            File.WriteAllText(Path.Combine(fresh, "c", "y.tmp"), "other");
            Assert.Null(guard2.Hold(Path.Combine(fresh, "c", "y.tmp"), out var why2));
            Assert.Contains("not the folder that was scanned", why2);

            // nothing swapped: the same delete goes through
            var ok = Path.Combine(root, "a-old", "b", "x.tmp");
            var done = Deleter.Run(new[] { (ok, 7L) }, recycle: false, IntPtr.Zero, null, CancellationToken.None, guard);
            Assert.True(done.Items[0].Deleted, done.Items[0].Error);
            Assert.False(File.Exists(ok));
        }
        finally { Clean(); }
    }

    [Fact]
    public void PermanentDelete_RemovesLinksWithoutFollowing_ReadOnlyFiles_DeepChains_AndOddNames()
    {
        Clean();
        try
        {
            var victim = Path.Combine(Dir, "victim");
            Directory.CreateDirectory(victim);
            File.WriteAllText(Path.Combine(victim, "keep.txt"), "keep");
            var tree = Path.Combine(Dir, "tree");
            Directory.CreateDirectory(Path.Combine(tree, "sub"));
            var ro = Path.Combine(tree, "sub", "ro.txt");
            File.WriteAllText(ro, "x");
            File.SetAttributes(ro, FileAttributes.ReadOnly);
            Junction(Path.Combine(tree, "link"), victim);
            // deeper than a scan goes, and longer than MAX_PATH
            var deep = Path.Combine(tree, "deep");
            for (int i = 0; i < FsNode.MaxDepth + 40; i++) deep = Path.Combine(deep, "d");
            Directory.CreateDirectory(@"\\?\" + deep);
            File.WriteAllText(@"\\?\" + Path.Combine(deep, "bottom.txt"), "b");
            Directory.CreateDirectory(@"\\?\" + Path.Combine(tree, "ends with a dot."));
            File.WriteAllText(@"\\?\" + Path.Combine(tree, "ends with a dot.", "f.txt"), "f");

            Assert.Equal("", SafeFs.DeleteTree(tree));
            Assert.False(Directory.Exists(tree));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(victim, "keep.txt")));   // the junction went, not what it points to
        }
        finally { Clean(); }
    }

    [Fact]
    public void Scan_StopsAtTheDepthLimit_WithoutCrashing()
    {
        Clean();
        try
        {
            var root = Path.Combine(Dir, "chain");
            var p = root;
            for (int i = 0; i < FsNode.MaxDepth + 20; i++) p = Path.Combine(p, "d");
            Directory.CreateDirectory(@"\\?\" + p);
            File.WriteAllText(@"\\?\" + Path.Combine(p, "bottom.txt"), "b");
            var tree = HardLinkTests.Scan(once: false, root);
            lock (tree.Sync)
            {
                var folders = tree.Root.Descendants().OfType<ContainerNode>().ToList();
                var deepest = folders.OrderByDescending(f => f.Level).First();
                Assert.Equal(FsNode.MaxDepth, deepest.Level);
                Assert.True(deepest.AccessDenied);                       // shown as not listed
                Assert.DoesNotContain(tree.Root.Descendants(), n => n.Name == "bottom.txt");
            }
            tree.Dispose();
        }
        finally { AppConfig.Replace(new AppConfig()); Clean(); }
    }

    [Fact]
    public void Snapshot_TooDeepOrDamaged_IsRefused_NeverCrashes()
    {
        Directory.CreateDirectory(Dir);
        var file = Path.Combine(Dir, "crafted.sns");
        try
        {
            SnsFormat.Write(file, Chain(FsNode.MaxDepth + 5).Root, _ => true);
            var ex = Assert.Throws<InvalidDataException>(() => SnsFormat.Read(file));
            Assert.Contains("levels", ex.Message);

            // damaged in many ways: always an exception, never a crash, a hang or a runaway allocation
            var sample = Wpf.Controls.SampleTree.Create();
            SnsFormat.Write(file, sample.Root, _ => true);
            var good = File.ReadAllBytes(file);
            var rnd = new Random(1234);
            for (int round = 0; round < 400; round++)
            {
                var bytes = (byte[])good.Clone();
                int changes = 1 + rnd.Next(8);
                for (int c = 0; c < changes; c++) bytes[rnd.Next(bytes.Length)] = (byte)rnd.Next(256);
                if (round % 5 == 0) Array.Resize(ref bytes, rnd.Next(bytes.Length));
                File.WriteAllBytes(file, bytes);
                var sw = Stopwatch.StartNew();
                try { SnsFormat.Read(file); } catch (Exception) { }
                Assert.True(sw.ElapsedMilliseconds < 2000, "round " + round + " took " + sw.ElapsedMilliseconds + " ms");
            }
        }
        finally { File.Delete(file); }
    }

    /// <summary>A snapshot someone sent with a network path in it: Windows is never asked about it (it would connect and offer the user's sign-in).</summary>
    [Fact]
    public void Snapshot_WithANetworkPath_IsNeverLookedUpOnTheNetwork()
    {
        var asked = new List<string>();
        var old = CloudFiles.ProviderByPath;
        CloudFiles.ProviderByPath = p => { lock (asked) asked.Add(p); return null; };
        try
        {
            var (root, bottom) = Chain(3, @"\\attacker.example\share\");
            ((FolderNode)root.Children[0]).Attributes = FileAttributes.Directory | FileAttributes.ReparsePoint;
            CloudFiles.Summarize(root, CancellationToken.None);
            CloudFiles.SyncRootOf(bottom);
            Assert.DoesNotContain(asked, p => p.StartsWith(@"\\", StringComparison.Ordinal));
        }
        finally { CloudFiles.ProviderByPath = old; }
    }

    /// <summary>A kept scan's description whose key names a path elsewhere ("..\\", a full path) is ignored: nothing outside its folder is read or deleted.</summary>
    [Fact]
    public void KeptScan_WithAKeyThatNamesAnotherPath_IsIgnored()
    {
        Directory.CreateDirectory(Dir);
        Directory.CreateDirectory(ScanResume.Dir);
        var victim = Path.Combine(Dir, "victim.json");
        File.WriteAllText(victim, "{}");
        var rel = Path.GetRelativePath(ScanResume.Dir, Path.Combine(Dir, "victim"));
        var forged = new[]
        {
            (Path.Combine(ScanResume.Dir, "0123456789ABCDEF.json"), rel),
            (Path.Combine(ScanResume.Dir, "FEDCBA9876543210.json"), Path.Combine(Dir, "victim")),
            (Path.Combine(ScanResume.Dir, "AAAAAAAAAAAAAAAA.json"), "BBBBBBBBBBBBBBBB"),   // a key that is not its own file
        };
        try
        {
            foreach (var (file, key) in forged)
                File.WriteAllText(file, JsonSerializer.Serialize(new ResumeInfo(@"C:\x", DateTime.UtcNow, 1, 1, 1, 0, 1) { Key = key }));
            var all = ScanResume.All(includeOld: true);
            Assert.DoesNotContain(all, i => forged.Any(f => f.Item2 == i.Key));
            Assert.True(File.Exists(victim));
            Assert.True(ScanResume.IsKey("0123456789ABCDEF"));
            Assert.False(ScanResume.IsKey(@"..\x"));
            Assert.False(ScanResume.IsKey("0123456789abcdef"));
        }
        finally
        {
            foreach (var (file, _) in forged) File.Delete(file);
            File.Delete(victim);
        }
    }

    [Fact]
    public void CsvCells_ThatASpreadsheetWouldRun_AreDefused()
    {
        Assert.Equal("'=WEBSERVICE(1)", Csv.Cell("=WEBSERVICE(1)", ','));
        Assert.Equal("'+1+2", Csv.Cell("+1+2", ','));
        Assert.Equal("'@SUM(1)", Csv.Cell("@SUM(1)", ';'));
        Assert.Equal("'-cmd", Csv.Cell("-cmd", ','));
        Assert.Equal("-5", Csv.Cell("-5", ','));                      // numbers stay numbers
        Assert.Equal("\"a,b\"", Csv.Cell("a,b", ','));
        Assert.Equal("\"'=a;b\"", Csv.Cell("=a;b", ';'));
        // in a report: a file named like a formula
        var root = new DriveNode(@"C:\r\") { Enumerated = true, ScanComplete = true };
        root.AddChild(new FileNode("=1+2.txt") { LogicalSize = 1, DiskSize = 1 });
        var w = new StringWriter();
        Reports.WriteCsv(root, null, _ => true, n => n.DiskSize, w, CancellationToken.None, ',');
        Assert.Contains(",'=1+2.txt,", w.ToString());
    }

    [Fact]
    public void WindowsPrograms_AreStartedByTheirFullPath_OnlyFromWindowsFolders()
    {
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), SystemTools.Resolve("MsiExec.exe"), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "rundll32.exe"), SystemTools.Resolve("rundll32"), StringComparer.OrdinalIgnoreCase);
        Assert.Null(SystemTools.Resolve("unins000.exe"));                 // not Windows' own: never looked for in the current folder
        Assert.Null(SystemTools.Resolve(@"..\unins000.exe"));
        Assert.Equal(@"C:\Program Files\App\unins000.exe", SystemTools.Resolve(@"C:\Program Files\App\unins000.exe"));
        Assert.True(File.Exists(SystemTools.Explorer));
        Assert.True(Path.IsPathFullyQualified(SystemTools.Explorer));
        var (exe, args) = Wpf.Views.ScanView.SplitCommand("MsiExec.exe /X{12345678-ABCD}");
        Assert.Equal("MsiExec.exe", exe);
        Assert.Equal("/X{12345678-ABCD}", args);
    }

    [Fact]
    public void Wildcard_WithManyStars_MatchesInLinearTime()
    {
        var w = new Wildcard("*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*b");
        var sw = Stopwatch.StartNew();
        Assert.False(w.IsMatch(new string('a', 250)));
        Assert.True(w.IsMatch("xaaaaaaaaaaaaaaaab"));
        Assert.True(sw.ElapsedMilliseconds < 2000, sw.ElapsedMilliseconds + " ms");
        Assert.True(new Wildcard("*.mp4").IsMatch("film.MP4"));
    }

    [Fact]
    public void NativeCalls_LoadWindowsDllsFromSystem32Only()
    {
        foreach (var asm in new[] { typeof(Secrets).Assembly, typeof(Wpf.Localizer).Assembly })
        {
            var a = asm.GetCustomAttributes(typeof(DefaultDllImportSearchPathsAttribute), false).OfType<DefaultDllImportSearchPathsAttribute>().SingleOrDefault();
            Assert.NotNull(a);
            Assert.Equal(DllImportSearchPath.System32, a!.Paths);
        }
    }

    /// <summary>A settings profile passed around could switch the local AI on with an address of its own: importing it asks first.</summary>
    [Fact]
    public void SettingsProfile_ThatSwitchesTheLocalAiOn_AsksBeforeTakingItOver() => WpfHost.Run(() =>
    {
        const string endpoint = "http://collector.example:11434";
        Directory.CreateDirectory(Dir);
        var file = Path.Combine(Dir, "profile.xml");
        var asked = new List<string>();
        try
        {
            AppConfig.Replace(new AppConfig());
            AppConfig.Save(new AppConfig { AiEnabled = true, AiEndpoint = endpoint, AiModel = "any", FontSize = 13 }, file);
            ConfigWindow.ConfirmAiHook = e => { asked.Add(e); return false; };
            var w = new ConfigWindow();
            w.ImportFrom(file);
            Assert.Equal(new[] { endpoint }, asked);
            Assert.False(w.Vm.Model.AiEnabled);
            Assert.NotEqual(endpoint, w.Vm.Model.AiEndpoint);
            Assert.Equal(13, w.Vm.Model.FontSize);                  // the rest of the profile is taken over
            ConfigWindow.ConfirmAiHook = _ => true;
            w.ImportFrom(file);
            Assert.True(w.Vm.Model.AiEnabled);
            Assert.Equal(endpoint, w.Vm.Model.AiEndpoint);
            // the same address again: no question
            asked.Clear();
            ConfigWindow.ConfirmAiHook = e => { asked.Add(e); return false; };
            w.ImportFrom(file);
            Assert.Empty(asked);
            w.Close();
        }
        finally
        {
            ConfigWindow.ConfirmAiHook = null;
            AppConfig.Replace(new AppConfig());
            File.Delete(file);
        }
    });

    [Fact]
    public void TreeAtTheDepthLimit_EveryWalkFitsInHalfTheStackOfTheInterfaceThread()
    {
        Directory.CreateDirectory(Dir);
        var (root, bottom) = Chain(FsNode.MaxDepth);
        var tree = new ScanTree(root, live: false);
        var sync = tree.Sync;
        long SizeOf(FsNode n) => n.DiskSize;
        var token = CancellationToken.None;
        var sns = Path.Combine(Dir, "deep.sns");
        try
        {
            OnSmallStack(() =>
            {
                SnsFormat.Write(sns, root, _ => true);
                var back = SnsFormat.Read(sns);
                Assert.Equal(FsNode.MaxDepth + 1, back.Descendants().OfType<FileNode>().Single().Level);
                KnowledgeBase.Classify(bottom);
                KnowledgeBase.Summarize(root, SizeOf, token, sync);
                CloudFiles.Summarize(root, token, sync);
                DevProjects.Compute(root, SizeOf, token, sync);
                AppFootprint.Compute(root, AppsTests.Catalog(), SizeOf, token, sync);
                TreeDiff.Compare(back, "deep.sns", DateTime.UtcNow, root, sync, token);
                FolderCompare.Compare((ContainerNode)root.Children[0], sync, (ContainerNode)back.Children[0], null, null, token);
                DiskForecast.RecentlyAdded(root, TimeSpan.FromDays(30), DateTime.UtcNow, sync);
                Reports.WriteCsv(root, sync, _ => true, SizeOf, new StringWriter(), token);
                Reports.WriteJson(root, sync, _ => true, SizeOf, new MemoryStream(), token);
                Reports.WriteHtml(root, sync, _ => true, SizeOf, new StringWriter(), token);
                var ctx = new SafetyContext { Sync = sync };
                DeletePlan.Build(new FsNode[] { root.Children[0] }, ctx);
                CleanupFinder.Find(root, ctx, new CleanupOptions(), null, null, token);
                var vm = new ViewModel(tree, root) { DetailLevel = 3 };
                var layout = vm.BuildLayout(new RectangleF(0, 0, 1200, 800), 16, AppConfig.Current);
                Assert.NotNull(layout);
                vm.Dispose();
            });
        }
        finally { File.Delete(sns); }
    }
}
