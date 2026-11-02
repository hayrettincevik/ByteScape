using System.Diagnostics;
using System.Net;
using System.Text;
using ByteScape.Core.Ai;
using ByteScape.Core.Compare;
using ByteScape.Core.Config;
using ByteScape.Core.Filtering;
using ByteScape.Core.Knowledge;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using ByteScape.Core.Snapshot;
using ByteScape.Treemap;
using ByteScape.Wpf.Controls;
using Xunit;
using Xunit.Abstractions;

namespace ByteScape.Tests;

/// <summary>Edge cases and stress for the newer features: snapshot frames, odd input, model error answers, big trees.</summary>
[Collection("config")]
public class RobustnessTests
{
    private readonly ITestOutputHelper _out;
    public RobustnessTests(ITestOutputHelper o) => _out = o;

    private static FolderNode Folder(ContainerNode p, string name) { var f = new FolderNode(name) { Enumerated = true, ScanComplete = true }; p.AddChild(f); return f; }
    private static FileNode File(ContainerNode p, string name, long bytes) { var f = new FileNode(name) { LogicalSize = bytes, DiskSize = bytes }; p.AddChild(f); return f; }

    // ------------------------------------------------------------------ knowledge base

    [Fact]
    public void Knowledge_WorksInsideALoadedSnapshotFrame()
    {
        // a loaded .sns lives under a "[SNAPSHOT: file]" frame: full paths start with the frame name, rules must still match
        var content = new DriveNode("C:\\") { IsDrive = false, Enumerated = true, ScanComplete = true };
        var temp = Folder(Folder(Folder(Folder(Folder(content, "Users"), "Ali"), "AppData"), "Local"), "Temp");
        File(temp, "x.tmp", 5000);
        var nm = Folder(Folder(Folder(content, "src"), "app"), "node_modules"); File(nm, "a.js", 100);
        var frame = new DriveNode("[SNAPSHOT: C:\\snaps\\may.sns]") { IsDrive = false, IsSnapshotFrame = true, Enumerated = true, ScanComplete = true };
        frame.AddChild(content);
        Assert.StartsWith("[SNAPSHOT:", temp.FullPath);
        Assert.Equal("user-temp", KnowledgeBase.Classify(temp)?.Location.Id);
        Assert.Equal("node-modules", KnowledgeBase.Classify(nm)?.Location.Id);
        Assert.True(KnowledgeBase.IsReclaimable(temp.Children[0]));
        var s = KnowledgeBase.Summarize(frame, n => n.DiskSize, CancellationToken.None);
        Assert.Equal(5100, s.TotalBytes);
    }

    [Fact]
    public void Knowledge_ClassifiesAScanRootThatIsAKnownLocation()
    {
        // scanning a cache folder directly: the root is named by its full path, its own rule must still apply
        var root = new DriveNode(@"C:\Users\Ali\.gradle\caches\") { IsDrive = false, Enumerated = true, ScanComplete = true };
        File(Folder(root, "9.4.1"), "x.jar", 4000);
        var m = KnowledgeBase.Classify(root);
        Assert.Equal("gradle-caches", m?.Location.Id);
        Assert.False(m!.Inherited);
        Assert.Equal(4000, KnowledgeBase.Summarize(root, n => n.DiskSize, CancellationToken.None).TotalBytes);
    }

    [Fact]
    public void Knowledge_HandlesUncAndOddPaths()
    {
        var unc = new DriveNode(@"\\server\share\") { IsDrive = false, Enumerated = true, ScanComplete = true };
        var nm = Folder(Folder(unc, "proj"), "node_modules");
        Assert.Equal("node-modules", KnowledgeBase.Classify(nm)?.Location.Id);
        Assert.Null(KnowledgeBase.Classify(unc));
        var weird = new DriveNode("X:\\") { IsDrive = true, Enumerated = true, ScanComplete = true };
        var odd = Folder(weird, "node_modules.bak");                   // similar name, no match
        Assert.Null(KnowledgeBase.Classify(odd));
        var caseFolder = Folder(Folder(Folder(Folder(weird, "USERS"), "me"), ".GRADLE"), "CACHES");   // case insensitive
        Assert.Equal("gradle-caches", KnowledgeBase.Classify(caseFolder)?.Location.Id);
        Assert.Null(KnowledgeBase.Classify(new FreeSpaceNode()));
    }

    [Fact]
    public void Knowledge_SummarizeIsFastOnALargeTree()
    {
        // 50k folders / 200k files with a few known locations sprinkled in
        var root = new DriveNode("C:\\") { IsDrive = true, Enumerated = true, ScanComplete = true };
        var users = Folder(root, "Users"); var me = Folder(users, "Ayşe");
        for (int i = 0; i < 500; i++)
        {
            var d = Folder(me, "proj" + i);
            for (int j = 0; j < 100; j++) { var s = Folder(d, "s" + j); for (int k = 0; k < 4; k++) File(s, "f" + k + ".txt", 1000); }
            if (i % 50 == 0) { var nm = Folder(d, "node_modules"); File(nm, "index.js", 1_000_000); }
        }
        var sw = Stopwatch.StartNew();
        var s1 = KnowledgeBase.Summarize(root, n => n.DiskSize, CancellationToken.None);
        var t1 = sw.ElapsedMilliseconds;
        sw.Restart();
        int classified = 0;
        foreach (var n in root.Descendants()) if (n is ContainerNode && KnowledgeBase.Classify(n) != null) classified++;
        var t2 = sw.ElapsedMilliseconds;
        _out.WriteLine($"Summarize: {t1} ms, classify all {root.FolderCount} folders: {t2} ms");
        Assert.Equal(10 * 1_000_000, s1.TotalBytes);
        Assert.Equal(10, classified);
        Assert.True(t1 < 3000, "summarize took " + t1 + " ms");
        Assert.True(t2 < 5000, "classify took " + t2 + " ms");
    }

    // ------------------------------------------------------------------ natural language filter

    [Fact]
    public void NaturalFilter_NeverThrows_OnGarbage()
    {
        AppConfig.Replace(new AppConfig());
        var rnd = new Random(7);
        var alphabet = "abcçdefgğhıijklmnoöprsştuüvyz0123456789 .,;:*?<>|\\/-_'\"()%!+";
        var samples = new List<string> { "", " ", "…", "\t", "1", "MB", "büyük", "büyük büyük büyük", "ve", "hariç", "2 gb 3 mb 4 kb", "klasör", "in", "folder", "videos except", "except", ">", "*", "a".PadRight(500, 'a'), "🙂 🚀", "İİİ ııı", "son son son gün" };
        for (int i = 0; i < 300; i++)
        {
            int len = rnd.Next(1, 40);
            var sb = new StringBuilder();
            for (int j = 0; j < len; j++) sb.Append(alphabet[rnd.Next(alphabet.Length)]);
            samples.Add(sb.ToString());
        }
        foreach (var s in samples)
        {
            var natural = NaturalFilter.LooksNatural(s);
            var r = NaturalFilter.Translate(s);
            Assert.NotNull(r.Filter);
            if (!r.IsEmpty) new Filter(r.Filter);     // whatever is produced parses
        }
    }

    [Theory]
    [InlineData("100 mb", ">100mb")]                                       // bare size: treated as "larger than"
    [InlineData("under 100 mb", "<100mb")]
    [InlineData("en fazla 100 mb", "<100mb")]
    [InlineData("100 MB'dan küçük", "<100mb")]
    [InlineData("2.5 gb üstü", ">2.5gb")]
    [InlineData("1,5 gb üstü", ">1.5gb")]
    [InlineData("iki hafta içinde değişen", "modify<2weeks")]
    [InlineData("older than three years", "modify>3years")]
    [InlineData("yarım yıldır açılmamış", "access>0.5years")]
    [InlineData("indirilenler klasöründeki isolar", "\\Downloads*")]
    [InlineData("indirilenler klasöründeki isolar", "*.iso")]
    [InlineData("mavi etiketli", ":blue")]
    [InlineData("etiketli dosyalar", ":tags:r+y+g+b")]
    [InlineData("salt okunur dosyalar", ":attr:r")]
    [InlineData("gereksiz dosyalar hariç videolar", "|:reclaimable")]
    public void NaturalFilter_MoreShapes(string text, string expectedPart)
    {
        var parts = NaturalFilter.Translate(text).Filter.Split(';');
        Assert.Contains(expectedPart, parts);
    }

    // ------------------------------------------------------------------ comparison

    [Fact]
    public void Diff_HandlesTypeChangeAndCaseOnlyNames()
    {
        var before = new DriveNode("C:\\") { IsDrive = true }; File(before, "Thing", 10); Folder(before, "Docs"); File(before, "Same.txt", 5);
        var after = new DriveNode("C:\\") { IsDrive = true }; var thing = Folder(after, "thing"); File(thing, "inner", 20); Folder(after, "DOCS"); File(after, "same.TXT", 5);
        var diff = TreeDiff.Compare(before, "b", DateTime.UtcNow, after, null);
        // "Thing" file became a folder: reported as removed file + added folder, never crashes
        Assert.Contains(diff.Entries, e => e.Kind == ChangeKind.Removed && e.Name == "Thing" && !e.IsFolder);
        Assert.Contains(diff.Entries, e => e.Kind == ChangeKind.Added && e.Name == "thing" && e.IsFolder);
        // case-only differences are the same element
        Assert.DoesNotContain(diff.Entries, e => e.Name.Equals("DOCS", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(diff.Entries, e => e.Name.Equals("same.TXT", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(10, diff.Delta);
    }

    [Fact]
    public void Diff_IsFastOnLargeTrees()
    {
        DriveNode Build(int seed)
        {
            var r = new DriveNode("C:\\") { IsDrive = true };
            for (int i = 0; i < 200; i++) { var d = Folder(r, "d" + i); for (int j = 0; j < 500; j++) File(d, "f" + j, 1000 + ((i * 500 + j) % 7 == seed ? 1 : 0)); }
            return r;
        }
        var a = Build(0); var b = Build(1);
        var sw = Stopwatch.StartNew();
        var diff = TreeDiff.Compare(a, "a", DateTime.UtcNow, b, null);
        _out.WriteLine($"diff of 2 x 100k files: {sw.ElapsedMilliseconds} ms, {diff.Entries.Count} entries");
        Assert.True(sw.ElapsedMilliseconds < 3000);
        Assert.True(diff.Entries.Count > 10000);
    }

    [Fact]
    public void Diff_SnapshotViewAgainstItsOwnFile_IsEmpty()
    {
        // comparing a loaded snapshot view with the very file it came from: aligned through the frame, no changes
        var tree = SampleTree.Create();
        var dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "compare"); Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "self.sns");
        SnsFormat.Write(file, tree.Root, _ => true);
        var loaded = SnsFormat.Read(file);
        var frame = new DriveNode("[SNAPSHOT: " + file + "]") { IsDrive = false, IsSnapshotFrame = true, Enumerated = true, ScanComplete = true };
        frame.AddChild(loaded);
        var inner = (ContainerNode)frame.Children[0];
        var baseline = SnsFormat.Read(file);
        var aligned = TreeDiff.Align(baseline, inner.FullPath.Substring(inner.FullPath.IndexOf(']') + 2));
        Assert.NotNull(aligned);
        var diff = TreeDiff.Compare(aligned!, file, DateTime.UtcNow, inner, null);
        Assert.Empty(diff.Entries);
    }

    // ------------------------------------------------------------------ local model: error shapes

    private sealed class OneShot : IDisposable
    {
        private readonly HttpListener _l = new();
        public string Prefix { get; }
        public OneShot(int status, string body)
        {
            int port = 19400 + Random.Shared.Next(1000);
            Prefix = $"http://127.0.0.1:{port}/";
            _l.Prefixes.Add(Prefix); _l.Start();
            _ = Task.Run(async () =>
            {
                while (_l.IsListening)
                {
                    HttpListenerContext ctx; try { ctx = await _l.GetContextAsync(); } catch { return; }
                    var bytes = Encoding.UTF8.GetBytes(body);
                    ctx.Response.StatusCode = status; ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close();
                }
            });
        }
        public void Dispose() { try { _l.Stop(); } catch { } }
    }

    [Fact]
    public async Task Model_ErrorAnswers_BecomeExceptions_NotCrashes()
    {
        using (var s = new OneShot(404, """{"error":"model 'x' not found, try pulling it first"}"""))
        {
            var ex = await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() => new LocalModel(s.Prefix, "x").CompleteAsync("a", "b"));
            Assert.Contains("404", ex.Message); Assert.Contains("not found", ex.Message);
        }
        using (var s = new OneShot(200, "this is not json"))
            await Assert.ThrowsAnyAsync<Exception>(() => new LocalModel(s.Prefix, "x").CompleteAsync("a", "b"));
        using (var s = new OneShot(200, """{"message":{"role":"assistant","content":""}}"""))
        {
            Assert.Equal("", await new LocalModel(s.Prefix, "x").CompleteAsync("a", "b"));
            Assert.Null(await Assistant.TranslateFilterAsync(new LocalModel(s.Prefix, "x"), "anything", CancellationToken.None));
        }
        using (var s = new OneShot(200, """{"models":"oops"}"""))
            await Assert.ThrowsAnyAsync<Exception>(() => new LocalModel(s.Prefix, "x").ListModelsAsync());
    }

    [Fact]
    public void ExtractFilter_IgnoresProse_KeepsSyntax()
    {
        Assert.Equal("*.mp4;>1gb", Assistant.ExtractFilter("*.mp4;>1gb"));
        Assert.Equal("*.mp4;>1gb", Assistant.ExtractFilter("Filter: *.mp4;>1gb"));
        Assert.Equal(">100mb", Assistant.ExtractFilter("The user wants big files.\n>100mb\nHope this helps!"));
        Assert.Null(Assistant.ExtractFilter("I am not able to do that."));
        Assert.Null(Assistant.ExtractFilter(""));
        Assert.Equal("access>1year", Assistant.ExtractFilter("`access>1year`"));
    }

    // ------------------------------------------------------------------ config path

    [Fact]
    public void ConfigPath_IsPortableNextToExe_WhenNotPackaged()
    {
        Assert.False(AppConfig.IsPackaged());
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "ByteScapeConfig.xml"), AppConfig.ConfigPath);
    }

    // ------------------------------------------------------------------ parallel folder listing

    private static List<string> Snapshot(ContainerNode root) =>
        root.Descendants().Select(n => n.FullPath + "|" + n.LogicalSize + "|" + n.DiskSize + "|" + (n is ContainerNode c ? string.Join(",", c.Children.Select(k => k.Name)) : "")).OrderBy(s => s, StringComparer.Ordinal).ToList();

    private static (ScanState state, List<string> tree, long ms) ScanWith(string dir, int threads)
    {
        AppConfig.Replace(new AppConfig { ScanThreads = threads, WatchDog = false });
        var tree = new ScanTree(DataCache.CreateRoot(dir), live: true);
        var sw = Stopwatch.StartNew();
        tree.StartMasterScan();
        while (tree.IsMasterScanning) Thread.Sleep(5);
        return (tree.MasterState, Snapshot(tree.Root), sw.ElapsedMilliseconds);
    }

    [Fact]
    public void ParallelScan_GivesTheSameTree_AndStops()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "parallel");
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            var rnd = new Random(7);
            var folders = new List<string> { dir };
            for (int i = 0; i < 400; i++)
            {
                var parent = folders[rnd.Next(folders.Count)];
                var f = Path.Combine(parent, "d" + i + (i % 5 == 0 ? " ünicode ç" : ""));
                Directory.CreateDirectory(f);
                folders.Add(f);
                for (int k = 0; k < 6; k++) System.IO.File.WriteAllBytes(Path.Combine(f, $"f{k}_{i}.bin"), new byte[rnd.Next(0, 9000)]);
            }
            var (s1, one, ms1) = ScanWith(dir, 1);
            var (s6, six, ms6) = ScanWith(dir, 6);
            _out.WriteLine($"1 worker: {ms1} ms, 6 workers: {ms6} ms, {one.Count} nodes");
            Assert.Equal(ScanState.Completed, s1);
            Assert.Equal(ScanState.Completed, s6);
            Assert.Equal(400 + 2400, one.Count);
            Assert.Equal(one, six);                      // same nodes, sizes and child order in every folder

            // stopping a parallel scan ends every worker quickly
            AppConfig.Replace(new AppConfig { ScanThreads = 6, WatchDog = false });
            var tree = new ScanTree(DataCache.CreateRoot(dir), live: true);
            tree.StartMasterScan();
            tree.StopMasterScan();
            var sw = Stopwatch.StartNew();
            while (tree.IsMasterScanning && sw.ElapsedMilliseconds < 5000) Thread.Sleep(5);
            Assert.False(tree.IsMasterScanning);
            Assert.Contains(tree.MasterState, new[] { ScanState.Aborted, ScanState.Completed });
        }
        finally
        {
            AppConfig.Replace(new AppConfig());
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void WorkerCount_ByConfigurationAndDriveType()
    {
        try
        {
            AppConfig.Replace(new AppConfig { ScanThreads = 3 });
            Assert.Equal(3, DirectoryScanner.WorkerCount(@"C:\x"));
            AppConfig.Replace(new AppConfig());
            Assert.Equal(8, DirectoryScanner.WorkerCount(@"\\server\share"));
            int auto = DirectoryScanner.WorkerCount(@"C:\");
            var penalty = ByteScape.Interop.Native.IncursSeekPenalty("C:");     // no administrator rights needed
            _out.WriteLine($"C: seek penalty = {penalty?.ToString() ?? "unknown"}, workers = {auto}");
            Assert.Equal(DriveProfile.StrategyFor(DriveProfile.Detect("C:").Kind, Environment.ProcessorCount).Workers, auto);
            if (penalty == true) Assert.Equal(1, auto);
            Assert.Null(ByteScape.Interop.Native.IncursSeekPenalty(@"\\server\share"));
        }
        finally { AppConfig.Replace(new AppConfig()); }
    }
}
