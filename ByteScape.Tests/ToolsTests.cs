using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ByteScape.Core;
using ByteScape.Core.Compare;
using ByteScape.Core.Config;
using ByteScape.Core.Duplicates;
using ByteScape.Core.Export;
using ByteScape.Core.Forecast;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using Xunit;
using Xunit.Abstractions;

namespace ByteScape.Tests;

/// <summary>Reports (CSV / JSON / HTML), duplicate files, folder comparison, folder history, Explorer's menu, ready-made filters.</summary>
[Collection("config")]
public class ToolsTests
{
    private readonly ITestOutputHelper _out;
    public ToolsTests(ITestOutputHelper o) => _out = o;

    internal static readonly string Dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "tools");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newName, string existing, IntPtr security);

    internal static void Clean()
    {
        if (!Directory.Exists(Dir)) return;
        foreach (var f in Directory.EnumerateFiles(Dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        try { Directory.Delete(Dir, true); } catch { }
    }

    private static string P(string rel) => Path.Combine(Dir, rel);

    private static void Write(string rel, byte[] data, DateTime? modified = null)
    {
        var p = P(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
        if (modified != null) File.SetLastWriteTimeUtc(p, modified.Value);
    }

    private static byte[] Data(int length, int seed) { var b = new byte[length]; new Random(seed).NextBytes(b); return b; }

    internal static ScanTree Scan(string path)
    {
        var t = new ScanTree(DataCache.CreateRoot(path), live: true);
        t.StartMasterScan();
        var sw = Stopwatch.StartNew();
        while (t.IsMasterScanning && sw.ElapsedMilliseconds < 20000) Thread.Sleep(10);
        Assert.Equal(ScanState.Completed, t.MasterState);
        return t;
    }

    // ------------------------------------------------------------------ reports

    [Fact]
    public void Reports_CsvJsonHtml_FollowTheFilter_AndEscapeWhatTheyMust()
    {
        try
        {
            Clean();
            AppConfig.Replace(new AppConfig { WatchDog = false });
            Write(@"a\big.bin", new byte[300_000]);
            Write(@"a\semi;colon, and comma.txt", new byte[1000]);
            Write(@"b\R&D's <notes>.html".Replace("<", "").Replace(">", ""), new byte[2000]);
            Write(@"b\keep.jpg", new byte[5000]);
            var tree = Scan(Dir);
            var model = new Treemap.ViewModel(tree, tree.Root);
            model.SetFilter("|*.jpg");                                          // everything but .jpg
            var sw = Stopwatch.StartNew();
            while ((model.IsFiltering || model.FilterResultStale) && sw.ElapsedMilliseconds < 10000) { if (!model.IsFiltering && model.FilterResultStale) model.RequestRefilter(); Thread.Sleep(10); }

            // CSV: a header, one line per element, quoted where the separator or a quote appears
            foreach (var sep in new[] { ',', ';' })
            {
                var w = new StringWriter();
                int rows = Reports.WriteCsv(tree.Root, tree.Sync, model.IsIncluded, model.SizeOf, w, CancellationToken.None, sep);
                var lines = w.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                _out.WriteLine(string.Join("\n", lines));
                Assert.StartsWith("Path" + sep + "Name" + sep + "Type", lines[0]);
                Assert.Equal(rows + 1, lines.Length);
                Assert.DoesNotContain(lines, l => l.Contains("keep.jpg"));
                Assert.Contains(lines, l => l.Contains("\"semi;colon, and comma.txt\""));
                Assert.Contains(lines, l => l.Contains(sep + "file" + sep + "bin" + sep + "300000" + sep));
            }

            // JSON: a tree with sizes, biggest first
            using (var ms = new MemoryStream())
            {
                Reports.WriteJson(tree.Root, tree.Sync, model.IsIncluded, model.SizeOf, ms, CancellationToken.None);
                using var doc = JsonDocument.Parse(ms.ToArray());
                var root = doc.RootElement.GetProperty("root");
                Assert.Equal("folder", root.GetProperty("type").GetString());
                var kids = root.GetProperty("children").EnumerateArray().ToList();
                Assert.Equal(new[] { "a", "b" }, kids.Select(k => k.GetProperty("name").GetString()));
                Assert.Equal(300_000, kids[0].GetProperty("children").EnumerateArray().First().GetProperty("size").GetInt64());
                Assert.DoesNotContain("keep.jpg", System.Text.Encoding.UTF8.GetString(ms.ToArray()));
                Assert.StartsWith("ByteScape", doc.RootElement.GetProperty("tool").GetString());
            }

            // deeper than readers like: the nesting stops, the totals stay
            var deep = Path.Combine(Dir, "deep");
            var cur = deep;
            for (int i = 0; i < 40; i++) cur = Path.Combine(cur, "d" + i);
            Directory.CreateDirectory(cur);
            File.WriteAllBytes(Path.Combine(cur, "leaf.bin"), new byte[10]);
            var deepTree = Scan(deep);
            using (var ms = new MemoryStream())
            {
                Reports.WriteJson(deepTree.Root, deepTree.Sync, _ => true, n => n.DiskSize, ms, CancellationToken.None, maxDepth: 10);
                using var doc = JsonDocument.Parse(ms.ToArray());                // compact and not deeper than 64: the default reader copes
                var n = doc.RootElement.GetProperty("root");
                for (int i = 0; i < 10; i++) n = n.GetProperty("children")[0];
                Assert.True(n.GetProperty("truncated").GetBoolean());
                Assert.Equal(1, n.GetProperty("files").GetInt32());
                Assert.DoesNotContain((byte)'\n', ms.ToArray());
            }

            // HTML: every section, names escaped
            var hw = new StringWriter();
            Reports.WriteHtml(tree.Root, tree.Sync, model.IsIncluded, model.SizeOf, hw, CancellationToken.None, "|*.jpg");
            var html = hw.ToString();
            foreach (var h in new[] { "<h2>Top level</h2>", "<h2>Largest folders</h2>", "<h2>Largest files</h2>", "<h2>File types</h2>", "<h2>By last change</h2>", "filter: |*.jpg" })
                Assert.Contains(h, html);
            Assert.Contains("R&amp;D&#39;s notes.html", html);
            Assert.DoesNotContain("R&D's", html);
            Assert.Contains(".bin", html);

            // files and the export dialog's configurations
            foreach (var cfg in ExportConfig.ReportConfigs)
            {
                var file = P("report" + cfg.DefaultExtension);
                Reports.WriteFile(cfg.Report!.Value, file, tree.Root, tree.Sync, model.IsIncluded, model.SizeOf, CancellationToken.None);
                Assert.True(new FileInfo(file).Length > 200, cfg.Description);
                Assert.Same(cfg, ExportConfig.FindByName(cfg.Description));
                Assert.False(cfg.IsBinarySnapshot);
                Assert.NotEmpty(Reports.Preview(cfg.Report.Value, tree.Root, tree.Sync, model.IsIncluded, model.SizeOf));
            }
            Assert.Equal(0xEF, File.ReadAllBytes(P("report.csv"))[0]);           // BOM: Excel reads the UTF-8 names
        }
        finally { AppConfig.Replace(new AppConfig()); Clean(); }
    }

    // ------------------------------------------------------------------ duplicate files

    [Fact]
    public void Duplicates_SameContentOnly_HardLinksAreNotCopies_OnlineFilesAreNotRead()
    {
        try
        {
            Clean();
            AppConfig.Replace(new AppConfig { WatchDog = false });
            var a = Data(3 << 20, 1);
            Write(@"one\photo.jpg", a, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Write(@"two\photo (copy).jpg", a, new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Write(@"three\deep\again.jpg", a, new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var middle = (byte[])a.Clone(); middle[1_500_000] ^= 0xFF;          // same length, same start and end, different middle
            Write(@"two\almost.jpg", middle);
            var b = Data(2 << 20, 2);
            Write(@"one\video.mp4", b);
            Assert.True(CreateHardLinkW(P(@"two\video-link.mp4"), P(@"one\video.mp4"), IntPtr.Zero));   // the same file, not a copy
            Write(@"one\small.txt", new byte[100]); Write(@"two\small.txt", new byte[100]);            // under the minimum
            Write(@"cloud\online.bin", a);
            var tree = Scan(Dir);
            ((FileNode)tree.Find(P(@"cloud\online.bin"))!).Attributes |= FileAttributes.Offline;      // as a cloud placeholder: never read
            var r = DuplicateFinder.Find(tree.Root, tree.Sync, new DuplicateOptions(), null, CancellationToken.None);
            _out.WriteLine(DuplicateFinder.ToText(r));
            var g = Assert.Single(r.Groups);
            Assert.Equal(3, g.Copies.Count);
            Assert.DoesNotContain(g.Copies, c => c.Path.EndsWith("almost.jpg"));
            Assert.Equal(2 * tree.Find(P(@"one\photo.jpg"))!.DiskSize, g.Wasted);
            Assert.Equal(1, r.SkippedOnline);
            Assert.True(r.HardLinkNames >= 1 || !tree.CountsLinksOnce);
            Assert.True(r.BytesRead < 5L * (3 << 20), "read " + r.BytesRead);   // the different middle was found reading whole files of 4 candidates at most

            // one copy always stays
            var keepOldest = DuplicateFinder.AllButOne(g, DuplicateFinder.KeepRule.Oldest);
            Assert.Equal(2, keepOldest.Count);
            Assert.DoesNotContain(keepOldest, c => c.Path.EndsWith(@"one\photo.jpg"));
            var keepNewest = DuplicateFinder.AllButOne(g, DuplicateFinder.KeepRule.Newest);
            Assert.DoesNotContain(keepNewest, c => c.Path.EndsWith("photo (copy).jpg"));
            var protectedOne = DuplicateFinder.AllButOne(g, DuplicateFinder.KeepRule.Oldest, c => c.Path.EndsWith("again.jpg"));
            Assert.Equal(2, protectedOne.Count);                                // the protected copy stays: the others may go
            Assert.DoesNotContain(protectedOne, c => c.Path.EndsWith("again.jpg"));

            // reports
            var csv = DuplicateFinder.ToCsv(r, ';');
            Assert.Equal(4, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
            using (var doc = JsonDocument.Parse(DuplicateFinder.ToJson(r))) Assert.Equal(3, doc.RootElement.GetProperty("groups")[0].GetProperty("copies").GetArrayLength());

            // a bigger minimum finds nothing; a file changed since the scan is not trusted
            Assert.Empty(DuplicateFinder.Find(tree.Root, tree.Sync, new DuplicateOptions { MinBytes = 10 << 20 }, null, CancellationToken.None).Groups);
            File.WriteAllBytes(P(@"two\photo (copy).jpg"), Data(1000, 3));
            var after = DuplicateFinder.Find(tree.Root, tree.Sync, new DuplicateOptions(), null, CancellationToken.None);
            Assert.Equal(2, after.Groups.Single().Copies.Count);
            Assert.Equal(1, after.SkippedUnreadable);
        }
        finally { AppConfig.Replace(new AppConfig()); Clean(); }
    }

    // ------------------------------------------------------------------ comparing two folders

    [Fact]
    public void FolderCompare_OnlyLeft_OnlyRight_Different_Same_AndContents()
    {
        try
        {
            Clean();
            AppConfig.Replace(new AppConfig { WatchDog = false });
            var t0 = new DateTime(2025, 5, 1, 10, 0, 0, DateTimeKind.Utc);
            var same = Data(5000, 1);
            Write(@"L\same.txt", same, t0); Write(@"R\same.txt", same, t0.AddSeconds(1));               // within 2 s: equal
            Write(@"L\shifted.txt", same, t0); Write(@"R\shifted.txt", same, t0.AddHours(1));           // summer time on FAT: equal
            Write(@"L\bigger.txt", Data(6000, 2), t0); Write(@"R\bigger.txt", Data(5000, 2), t0);
            Write(@"L\newer.txt", same, t0.AddDays(1)); Write(@"R\newer.txt", same, t0);
            var c1 = Data(4000, 5); var c2 = (byte[])c1.Clone(); c2[10] ^= 1;
            Write(@"L\silent.bin", c1, t0); Write(@"R\silent.bin", c2, t0);                             // same length and time, other content
            Write(@"L\onlyleft.txt", same, t0);
            Write(@"R\sub\onlyright1.txt", same, t0); Write(@"R\sub\onlyright2.txt", same, t0);
            Write(@"L\Photos\x.jpg", same, t0); Write(@"R\photos\X.JPG", same, t0);                    // names compare case-insensitively
            var left = Scan(P("L")); var right = Scan(P("R"));
            var r = FolderCompare.Compare(left.Root, left.Sync, right.Root, right.Sync);
            _out.WriteLine(FolderCompare.ToText(r));
            FolderDiffEntry E(string rel) => r.Entries.Single(e => e.RelativePath.Equals(rel, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(FolderDiffKind.Same, E("same.txt").Kind);
            Assert.Equal(FolderDiffKind.Same, E("shifted.txt").Kind);
            Assert.Equal(FolderDiffKind.Different, E("bigger.txt").Kind);
            Assert.Equal("bigger on the left", E("bigger.txt").Reason);
            Assert.Equal("newer on the left", E("newer.txt").Reason);
            Assert.Equal(FolderDiffKind.Same, E("silent.bin").Kind);                                    // by length and time
            Assert.Equal(FolderDiffKind.OnlyLeft, E("onlyleft.txt").Kind);
            var sub = E("sub");
            Assert.Equal(FolderDiffKind.OnlyRight, sub.Kind);
            Assert.True(sub.IsFolder);
            Assert.Equal("2 files inside", sub.Reason);
            Assert.Equal(FolderDiffKind.Same, E(@"Photos\x.jpg").Kind);
            Assert.Equal(2, r.Count(FolderDiffKind.Different));
            // with contents: the silent change is found
            var rc = FolderCompare.Compare(left.Root, left.Sync, right.Root, right.Sync, new FolderCompareOptions { CompareContents = true });
            Assert.Equal("same length, different content", rc.Entries.Single(e => e.RelativePath == "silent.bin").Reason);
            Assert.Equal(FolderDiffKind.Same, rc.Entries.Single(e => e.RelativePath == "same.txt").Kind);
            var csv = FolderCompare.ToCsv(rc);
            Assert.Contains("silent.bin,file,Different", csv);

            // from the view: the other folder inside the same tree, or scanned apart; never one inside the other
            var both = Scan(Dir);
            var viaTree = Wpf.Views.ScanView.CompareFolders(both, (ContainerNode)both.Find(P("L"))!, P("R"), false, CancellationToken.None);
            Assert.Equal(r.Entries.Count, viaTree.Entries.Count);
            var apart = Wpf.Views.ScanView.CompareFolders(left, left.Root, P("R"), false, CancellationToken.None);
            Assert.Equal(r.Count(FolderDiffKind.OnlyRight), apart.Count(FolderDiffKind.OnlyRight));
            Assert.Throws<InvalidOperationException>(() => Wpf.Views.ScanView.CompareFolders(both, both.Root, P("L"), false, CancellationToken.None));
        }
        finally { AppConfig.Replace(new AppConfig()); Clean(); }
    }

    // ------------------------------------------------------------------ folder history

    [Fact]
    public void FolderHistory_LinesAndChanges_FromTheScansRemembered()
    {
        try
        {
            Clean();
            Directory.CreateDirectory(Dir);
            var h = new DiskHistory(P("history.csv"));
            var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            h.SaveSummary("X:", t, new[] { ("Users", 50L << 30), ("Users\\ali\\Videos", 10L << 30), ("Games", 80L << 30), ("Old", 5L << 30) });
            h.SaveSummary("X:", t.AddDays(30), new[] { ("Users", 60L << 30), ("Users\\ali\\Videos", 20L << 30), ("Games", 70L << 30), ("Old", 5L << 30) });
            h.SaveSummary("X:", t.AddDays(60), new[] { ("Users", 65L << 30), ("Users\\ali\\Videos", 25L << 30), ("Games", 60L << 30), ("New", 1L << 30) });
            h.SaveSummary("Y:", t, new[] { ("Other", 1L << 30) });
            h.Record(new DiskSample(t, "X:", 500L << 30, 300L << 30), TimeSpan.Zero);
            h.Record(new DiskSample(t.AddDays(60), "X:", 500L << 30, 280L << 30), TimeSpan.Zero);
            var r = FolderHistory.Build(h, "X:");
            Assert.Equal(3, r.Scans.Count);
            Assert.Equal(2, r.Samples.Count);
            Assert.Equal(5, r.Folders.Count);
            Assert.Equal("Games", r.Folders[0].Path);                            // the biggest change: −20 GB
            Assert.Equal(-(20L << 30), r.Folders[0].Change);
            Assert.Equal(15L << 30, r.Find("users")!.Change);
            Assert.Null(r.Find("Old")!.Bytes[2]);                                // under the threshold at the last scan
            Assert.Equal(-(5L << 30), r.Find("Old")!.Change);
            Assert.Equal(1L << 30, r.Find("New")!.Change);
            Assert.Equal(3, r.Find("Users\\ali\\Videos")!.Depth);
            var csv = FolderHistory.ToCsv(r, ';');
            Assert.Contains("X:\\Old;5368709120;5368709120;;-5368709120", csv);
        }
        finally { Clean(); }
    }

    // ------------------------------------------------------------------ Explorer's right-click menu

    [Fact]
    public void ExplorerMenu_RegistryVerbs_InATestKey()
    {
        var old = ShellIntegration.ClassesRoot;
        ShellIntegration.ClassesRoot = @"Software\ByteScapeTests\Classes";      // Explorer never reads this key
        try
        {
            var exe = @"C:\Tools\ByteScape\ByteScape.exe";
            ShellIntegration.Uninstall();
            Assert.False(ShellIntegration.IsInstalled(exe));
            ShellIntegration.Install(exe);
            Assert.True(ShellIntegration.IsInstalled(exe));
            Assert.False(ShellIntegration.IsInstalled(@"D:\Other\ByteScape.exe"));
            Assert.True(ShellIntegration.IsAnyInstalled());
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ShellIntegration.ClassesRoot + @"\Drive\shell\ByteScape\command")!)
                Assert.Equal("\"C:\\Tools\\ByteScape\\ByteScape.exe\" scan \"%V\\.\"", k.GetValue(null));
            ShellIntegration.Uninstall();
            Assert.False(ShellIntegration.IsAnyInstalled());
            // what Explorer's command line becomes: "C:\." and "D:\Photos\." are the drive and the folder
            foreach (var (arg, expect) in new[] { (@"C:\.", @"C:\"), (@"C:""", @"C:\"), (@"D:\Photos\.", @"D:\Photos") })
            {
                var (req, _, _) = CommandLine.Parse(new[] { "scan", arg });
                Assert.Equal(expect, DataCache.NormalizePath(req.Single().Path));
            }
        }
        finally
        {
            ShellIntegration.Uninstall();
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\ByteScapeTests", false); } catch { }
            ShellIntegration.ClassesRoot = old;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out Wpf.Shell.IShellItem item);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHCreateShellItemArrayFromShellItem(Wpf.Shell.IShellItem item, ref Guid riid, out Wpf.Shell.IShellItemArray array);

    [Fact]
    public void ExplorerMenu_ComCommand_GivesItsTitle_AndOpensTheChosenFolder()
    {
        Directory.CreateDirectory(Dir);
        var launched = new List<string>();
        Wpf.Shell.ShellMenu.LaunchHook = p => launched.AddRange(p);
        try
        {
            var cmd = new Wpf.Shell.OpenInByteScapeCommand();
            Assert.Equal(0, cmd.GetTitle(null, out var title));
            Assert.Equal("Open in ByteScape", Marshal.PtrToStringUni(title));
            Marshal.FreeCoTaskMem(title);
            Assert.Equal(0, cmd.GetCanonicalName(out var id));
            Assert.Equal(Wpf.Shell.ShellMenu.Clsid, id);
            var iidItem = typeof(Wpf.Shell.IShellItem).GUID; var iidArray = typeof(Wpf.Shell.IShellItemArray).GUID;
            Assert.Equal(0, SHCreateItemFromParsingName(Dir, IntPtr.Zero, ref iidItem, out var item));
            Assert.Equal(0, SHCreateShellItemArrayFromShellItem(item, ref iidArray, out var array));
            Assert.Equal(0, cmd.GetState(array, false, out var state));
            Assert.Equal(0u, state);                                              // enabled for a folder
            Assert.Equal(0, cmd.Invoke(array, IntPtr.Zero));
            Assert.Equal(new[] { Dir.TrimEnd('\\') }, launched.Select(p => p.TrimEnd('\\')));
            // what Explorer gets from the class factory is the command
            var factory = (Wpf.Shell.IClassFactory)Activator.CreateInstance(typeof(Wpf.Shell.OpenInByteScapeCommand).Assembly.GetType("ByteScape.Wpf.Shell.OpenInByteScapeFactory")!)!;
            var iidCmd = typeof(Wpf.Shell.IExplorerCommand).GUID;
            Assert.Equal(0, factory.CreateInstance(IntPtr.Zero, ref iidCmd, out var ptr));
            Assert.NotEqual(IntPtr.Zero, ptr);
            Marshal.Release(ptr);
            Assert.True(Wpf.Shell.ShellMenu.IsServerStart(new[] { "-ShellMenu" }));
            Assert.True(Wpf.Shell.ShellMenu.IsServerStart(new[] { "-Embedding" }));
            Assert.False(Wpf.Shell.ShellMenu.IsServerStart(new[] { "scan", "C:\\" }));
        }
        finally { Wpf.Shell.ShellMenu.LaunchHook = null; }
    }

    // ------------------------------------------------------------------ ready-made filters and the command line

    [Fact]
    public void ReadyMadeFilters_AllParse_AndNewCommandsAreRead()
    {
        AppConfig.Replace(new AppConfig());
        foreach (var (group, label, cond) in Wpf.Views.ScanView.FilterPresetList())
        {
            var f = new Core.Filtering.Filter(cond);
            Assert.False(f.IsEmpty, label);
        }
        var (req, auto, _) = CommandLine.Parse(new[] { "scan", @"C:\data", "duplicatesreport", "d.csv", "comparefolderreport", @"E:\backup", "c.txt", "export", Reports.HtmlName, "r.html", "autoclose" });
        var r = req.Single();
        Assert.Equal("d.csv", r.DuplicatesReport);
        Assert.Equal(@"E:\backup", r.CompareFolderWith);
        Assert.Equal("c.txt", r.CompareFolderReport);
        Assert.Contains((Reports.HtmlName, "r.html"), r.Exports);
        Assert.True(auto);
        var (req2, _, _) = CommandLine.Parse(new[] { "scan", @"C:\data", "duplicates", "comparefolder", @"E:\b" });
        Assert.True(req2[0].ShowDuplicates);
        Assert.Equal(@"E:\b", req2[0].CompareFolderWith);
        Assert.Equal("", req2[0].CompareFolderReport);
    }
}
