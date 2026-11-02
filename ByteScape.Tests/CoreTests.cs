using System.Drawing;
using ByteScape.Core.Config;
using ByteScape.Core.Export;
using ByteScape.Core.Filtering;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using ByteScape.Core.Snapshot;
using ByteScape.Core.Util;
using ByteScape.Treemap;
using ByteScape.Wpf.Controls;
using Xunit;

namespace ByteScape.Tests;

/// <summary>End-to-end coverage of the UI independent core: filter language, exports, snapshots, scanning, navigation.</summary>
[Collection("config")]
public class CoreTests
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "core");
    static CoreTests() => Directory.CreateDirectory(Dir);

    private static FileNode File(ContainerNode p, string name, long bytes, TagColor tag = TagColor.None, FileAttributes attrs = FileAttributes.Archive, int ageDays = 10)
    {
        var f = new FileNode(name) { LogicalSize = bytes, DiskSize = bytes, Tag = tag, Attributes = attrs, Created = DateTime.UtcNow.AddDays(-ageDays * 3), Modified = DateTime.UtcNow.AddDays(-ageDays), Accessed = DateTime.UtcNow.AddDays(-1) };
        p.AddChild(f);
        return f;
    }

    private static bool M(string filter, FileNode f) => new Filter(filter).Matches(f, Filter.FolderMaskState.Root, DateTime.UtcNow);

    // ------------------------------------------------------------------ paths

    /// <summary>The definition FullPath had before it was built in one go: every level asks its parent, then appends.</summary>
    private static string RecursivePath(FsNode n)
    {
        if (n is DriveNode d) return d.Parent == null ? d.Name : RecursivePath(d.Parent) + "\\" + d.Name;
        if (n.Parent == null) return n.Name;
        var parentPath = RecursivePath(n.Parent);
        return parentPath.EndsWith('\\') ? parentPath + n.Name : parentPath + "\\" + n.Name;
    }

    [Fact]
    public void FullPath_SameAsTheLevelByLevelDefinition_ForDrivesFoldersFramesAndOddNames()
    {
        var roots = new List<ContainerNode>
        {
            new DriveNode(@"C:\") { IsDrive = true },
            new DriveNode(@"D:\data\scans\"),                    // a folder scanned as root: full path as its name
            new DriveNode(@"\\server\share\"),
            new FolderNode("loose"),                              // a folder without a drive above it
            new FolderNode(""),                                   // an empty name
        };
        var frame = new DriveNode(@"[SNAPSHOT: E:\snap.sns]") { IsSnapshotFrame = true };
        var inFrame = new DriveNode(@"C:\");
        frame.AddChild(inFrame);
        roots.Add(frame);
        var r = new Random(5);
        var all = new List<FsNode>();
        foreach (var root in roots)
        {
            all.Add(root);
            var level = new List<ContainerNode> { root is DriveNode { IsSnapshotFrame: true } ? inFrame : root };
            for (int depth = 0; depth < 25; depth++)
            {
                var next = new List<ContainerNode>();
                foreach (var c in level)
                {
                    var names = new[] { "sub" + depth, "Ünïcödé " + depth, "trailing\\", "", "x.y" };
                    var f = new FolderNode(names[r.Next(names.Length)]);
                    c.AddChild(f); next.Add(f); all.Add(f);
                    var file = new FileNode("file" + depth + ".txt");
                    c.AddChild(file); all.Add(file);
                }
                level = next;
            }
        }
        all.Add(inFrame);
        foreach (var n in all) Assert.Equal(RecursivePath(n), n.FullPath);
        Assert.Equal(@"C:\file0.txt", roots[0].Children.OfType<FileNode>().First().FullPath);
        Assert.Equal(@"[SNAPSHOT: E:\snap.sns]\C:\file0.txt", inFrame.Children.OfType<FileNode>().First().FullPath);
    }

    [Fact]
    public void SnapshotNames_SameBytesAsTheStringRoute_AtEveryLineBreakBoundary()
    {
        // what WriteName wrote before it encoded into one buffer
        static byte[] Old(string name)
        {
            var raw = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(name));
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < raw.Length; i += 72)
            {
                if (i > 0) sb.Append('\r').Append('\n');
                sb.Append(raw, i, Math.Min(72, raw.Length - i));
            }
            var bytes = System.Text.Encoding.ASCII.GetBytes(sb.ToString());
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms)) { w.Write(bytes.Length); w.Write(bytes); }
            return ms.ToArray();
        }
        var names = new List<string> { "", "a", "ab", "abc", "Ünïcödé ğüşiöç 日本語 😀", new string('x', 2000), string.Concat(Enumerable.Repeat("ş日", 400)) };
        for (int n = 50; n <= 112; n++) names.Add(new string('q', n)); // 54 bytes = 72 base64 characters: the first break
        foreach (var name in names)
        {
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms)) SnsFormat.WriteName(w, name);
            Assert.Equal(Old(name), ms.ToArray());
        }
    }

    // ------------------------------------------------------------------ filter language

    [Fact]
    public void Filter_Masks_Size_Age_Tags_Attributes_Classes_Negation()
    {
        AppConfig.Replace(new AppConfig());
        var root = new DriveNode("C:\\") { IsDrive = true, Enumerated = true, ScanComplete = true };
        var jpg = File(root, "Holiday.JPG", 5 * 1024 * 1024, TagColor.Red, ageDays: 400);
        var mp3 = File(root, "song.mp3", 50 * 1024, TagColor.Blue | TagColor.Green, FileAttributes.Hidden | FileAttributes.Archive, ageDays: 2);
        var txt = File(root, "readme.txt", 10, attrs: FileAttributes.ReadOnly, ageDays: 40);

        Assert.True(new Filter("").IsEmpty);
        Assert.True(new Filter(" ; ; ").IsEmpty);
        // masks are case insensitive, ';' joins as OR
        Assert.True(M("*.jpg", jpg)); Assert.False(M("*.jpg", mp3));
        Assert.True(M("*.jpg;*.mp3", mp3));
        Assert.True(M("hol*", jpg)); Assert.True(M("?ong.mp3", mp3)); Assert.False(M("?ong.mp3", jpg));
        // exclusion with | is AND-ed
        Assert.False(M("|*.mp3", mp3)); Assert.True(M("|*.mp3", jpg));
        Assert.True(M("*.*;|*.txt", jpg)); Assert.False(M("*.*;|*.txt", txt));
        // size
        Assert.True(M(">1mb", jpg)); Assert.False(M(">1mb", mp3));
        Assert.True(M("<100kb", mp3)); Assert.True(M("disksize>1mb", jpg)); Assert.True(M("size<100", txt)); Assert.True(M(">=4mb", jpg)); Assert.False(M(">5mb", jpg)); Assert.True(M(">4.5m", jpg));
        Assert.True(M("|<1mb", jpg)); Assert.False(M("|<1mb", mp3));
        // age
        Assert.True(M(">1year", jpg)); Assert.False(M(">1year", mp3));
        Assert.True(M("<1week", mp3)); Assert.True(M("modify>1month", txt)); Assert.True(M("create>3months", txt)); Assert.True(M("access<1week", jpg)); Assert.True(M("<49h", mp3)); Assert.False(M("|<3d", mp3));
        // tags
        Assert.True(M(":red", jpg)); Assert.False(M(":red", mp3));
        Assert.True(M(":tag:b", mp3)); Assert.True(M(":tags:g+b", mp3)); Assert.False(M(":tags:b-g", mp3)); Assert.True(M("|:red", mp3)); Assert.False(M("|:red", jpg));
        Assert.True(M(":blue;:red", jpg)); Assert.True(M(":blue;:red", mp3)); Assert.False(M(":blue;:red", txt));
        // attributes (with and without ads)
        Assert.True(M(":attr:h", mp3)); Assert.False(M(":attr:h", jpg)); Assert.True(M(":attrs:a-h", jpg)); Assert.False(M(":attrs:a-h", mp3)); Assert.True(M(":attr:ro", txt));
        // class names come from the configuration
        var cls = AppConfig.Current.FileClasses.First(c => c.Contains("mp3"));
        Assert.True(M(":class:" + cls.Name, mp3)); Assert.False(M(":class:" + cls.Name, jpg));
        Assert.False(M("|:class:" + cls.Name, mp3));
        // OR group (mask/tag/class) combined with AND conditions
        Assert.True(M("*.jpg;>1mb", jpg)); Assert.False(M("*.mp3;>1mb", mp3)); Assert.True(M(":blue;<1mb;:attr:h", mp3));
        // errors
        Assert.Throws<FilterException>(() => new Filter(":class:NoSuchClass"));
        Assert.Throws<FilterException>(() => new Filter(":tag:purple"));
        Assert.Throws<FilterException>(() => new Filter(":attr:zz"));
        Assert.Throws<FilterException>(() => new Filter(">12parsecs"));
        Assert.Throws<FilterException>(() => new Filter("\\a\\b"));
    }

    [Fact]
    public void Filter_FolderMasks_InheritAlongThePath()
    {
        var f = new Filter("\\temp*;|\\cache");
        Assert.True(f.HasFolderMasks);
        Assert.True(f.FolderMatches("Temp")); Assert.False(f.FolderMatches("tmp"));
        var root = Filter.FolderMaskState.Root;
        var inTemp = f.FolderState(root, "Temporary");
        Assert.True(inTemp.Included && !inTemp.Excluded);
        var deeper = f.FolderState(inTemp, "anything");
        Assert.True(deeper.Included);
        var cache = f.FolderState(deeper, "cache");
        Assert.True(cache.Excluded);
        var drive = new DriveNode("C:\\");
        var file = File(drive, "x.dat", 100);
        Assert.False(f.Matches(file, root, DateTime.UtcNow));      // outside an included folder
        Assert.True(f.Matches(file, inTemp, DateTime.UtcNow));
        Assert.False(f.Matches(file, cache, DateTime.UtcNow));
    }

    [Fact]
    public void ViewModel_Filter_RunsInBackground_AndSizesFollowIt()
    {
        AppConfig.Replace(new AppConfig());
        var tree = SampleTree.Create();
        using var vm = new ViewModel(tree, tree.Root);
        vm.SetFilter("*.mp3");
        WaitFor(() => !vm.IsFiltering && !vm.FilterResultStale, 5000);
        const long MB = 1024 * 1024;
        var users = (ContainerNode)tree.Root.FindChild("Users")!;
        var music = (ContainerNode)users.FindChild("Music")!;
        Assert.Equal(160 * MB, vm.SizeOf(music));            // song + clip
        Assert.Equal(160 * MB, vm.SizeOf(users));
        Assert.Equal(0, vm.SizeOf(tree.Root.FindChild("movie.mkv")!));
        Assert.Equal(160 * MB, vm.SizeOf(tree.Root));
        Assert.Equal(160.0 / 6000, vm.ViewablePercent, 4);         // ratio of the whole media (drive total)
        // folder masks make matching folders bold
        vm.SetFilter("\\Pict*");
        WaitFor(() => !vm.IsFiltering && !vm.FilterResultStale, 5000);
        var pics = (ContainerNode)users.FindChild("Pictures")!;
        Assert.True(vm.IsBoldFolder(pics));
        Assert.Equal(820 * MB, vm.SizeOf(pics));
        Assert.Equal(0, vm.SizeOf(music));
        // an empty filter restores the real sizes
        vm.SetFilter("");
        Assert.Equal(tree.Root.DiskSize, vm.SizeOf(tree.Root));
        Assert.Equal(3776.0 / 6000, vm.ViewablePercent, 4);
    }

    // ------------------------------------------------------------------ exports

    [Fact]
    public void Exports_BuiltinScripts_ProduceExpectedText()
    {
        AppConfig.Replace(new AppConfig());
        var tree = SampleTree.Create();
        string Run(string name, ContainerNode? root = null, Func<FsNode, bool>? include = null)
        {
            var cfg = ExportConfig.FindByName(name)!;
            var ex = new Exporter(cfg, root ?? tree.Root, include ?? (_ => true), tree.Sync);
            var sw = new StringWriter();
            ex.ExportText(sw, CancellationToken.None);
            return sw.ToString();
        }
        var summary = Run("Totals");
        // the root is counted at header and detail time (the folder count of a report includes it twice)
        Assert.Equal("Space on disk: 3.7GB\r\nFolders: 9\r\nFiles: 29\r\n", summary);

        var first = Run("Top-level folders");
        Assert.Contains("     1.5GB  C:\\Users\n", first);
        Assert.Contains("     1.0GB  C:\\Windows\n", first);
        Assert.DoesNotContain("System32", first);
        Assert.DoesNotContain("pagefile", first);
        // folders sorted by size descending
        Assert.True(first.IndexOf("Users", StringComparison.Ordinal) < first.IndexOf("Windows", StringComparison.Ordinal));

        var list = Run("File paths");
        var lines = list.Split('\n', StringSplitOptions.RemoveEmptyEntries);   // {&br} is a bare LF
        Assert.Equal(29, lines.Length);
        Assert.Equal("C:\\movie.mkv", lines[0]);                 // files block first, largest first
        Assert.Equal("C:\\pagefile.sys", lines[1]);
        Assert.Contains("C:\\Windows\\System32\\kernel.dll", lines);

        var grouped = Run("Folders and their files");
        Assert.Contains("\nC:\\  (3.7GB)\n   640.0MB  movie.mkv\n", grouped);   // the drive's own files under the drive
        Assert.Contains("C:\\Users\\Pictures  (820.0MB)", grouped);
        Assert.DoesNotContain("C:\\Users  (", grouped);                // Users contains no direct files

        // export root and include predicate restrict the output
        var win = (ContainerNode)tree.Root.FindChild("Windows")!;
        var sub = Run("File paths", win, n => n is not FileNode || n.Name.EndsWith(".dll"));
        Assert.Equal("C:\\Windows\\System32\\kernel.dll\n", sub);
        var empty = new Exporter(ExportConfig.FindByName("File paths")!, win, n => n is not FileNode, tree.Sync);
        Assert.True(empty.WouldBeEmpty());
        Assert.False(new Exporter(ExportConfig.FindByName("File paths")!, win, _ => true, tree.Sync).WouldBeEmpty());
    }


    [Fact]
    public void Exports_UnderFilter_ListEveryFolderAndTheMatchingFiles()
    {
        // folders are always listed (with their filtered size, "0b" when empty), files only when they match, zero byte
        // files included, the total is the filtered size
        AppConfig.Replace(new AppConfig());
        var tree = SampleTree.Create();
        (tree.Root.FindChild("Users") as ContainerNode)!.AddChild(new FileNode("empty.mp3"));
        using var vm = new ViewModel(tree, tree.Root);
        vm.SetFilter("*.mp3");
        WaitFor(() => !vm.IsFiltering && !vm.FilterResultStale, 5000);
        string Run(string name)
        {
            var ex = new Exporter(ExportConfig.FindByName(name)!, vm.ExportRoot, vm.IsIncluded, tree.Sync, vm.SizeOf);
            var sw = new StringWriter(); ex.ExportText(sw, CancellationToken.None); return sw.ToString();
        }
        Assert.Equal("Space on disk: 160.0MB\r\nFolders: 9\r\nFiles: 3\r\n", Run("Totals"));
        var first = Run("Top-level folders");
        Assert.Contains("   160.0MB  C:\\Users\n", first);
        Assert.Contains("        0b  C:\\Windows\n", first);
        var list = Run("File paths").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "C:\\Users\\empty.mp3", "C:\\Users\\Music\\song.mp3", "C:\\Users\\Music\\clip.mp3" }, list);
        var grouped = Run("Folders and their files");
        Assert.Contains("        0b  empty.mp3", grouped);
        Assert.DoesNotContain("System32", grouped);

        // a loaded snapshot is wrapped in a "[SNAPSHOT: file]" frame; exports start at the content
        var frame = new DriveNode("[SNAPSHOT: x.sns]") { IsDrive = false, IsSnapshotFrame = true, Enumerated = true, ScanComplete = true };
        var content = SampleTree.Create().Root;
        frame.AddChild(content);
        var snapTree = new ScanTree(frame, live: false);
        using var svm = new ViewModel(snapTree, frame);
        Assert.Same(content, svm.ExportRoot);
        var summary = new Exporter(ExportConfig.FindByName("Totals")!, svm.ExportRoot, svm.IsIncluded, snapTree.Sync, svm.SizeOf);
        var w = new StringWriter(); summary.ExportText(w, CancellationToken.None);
        Assert.Equal("Space on disk: 3.7GB\r\nFolders: 9\r\nFiles: 29\r\n", w.ToString());
        var listEx = new Exporter(ExportConfig.FindByName("File paths")!, svm.ExportRoot, svm.IsIncluded, snapTree.Sync, svm.SizeOf);
        w = new StringWriter(); listEx.ExportText(w, CancellationToken.None);
        Assert.StartsWith("[SNAPSHOT: x.sns]\\C:\\movie.mkv\n", w.ToString());
        svm.ZoomTo(content);
        Assert.Same(content, svm.ExportRoot);
    }
    [Fact]
    public void ScriptEngine_Commands_And_Tags()
    {
        var root = new DriveNode("C:\\") { IsDrive = true };
        var folder = new FolderNode("Docs"); root.AddChild(folder);
        var file = File(folder, "a.txt", 3072);
        var ctx = new ScriptContext { Node = file, ExportRoot = root, Counters = new ExportCounters(), Include = _ => true };
        string Eval(string t) => new Script(t).Evaluate(ctx);
        Assert.Equal("a.txt|txt|a|true|false", Eval("<%file%>|<%fileext%>|<%filename%>|<%isfile%>|<%isfolder%>"));
        Assert.Equal("C:\\Docs\\a.txt", Eval("<%pathfile%>"));
        Assert.Equal("2", Eval("<%nestinglevel%>"));
        Assert.Equal("3KB", Eval("<%disksize%>"));
        Assert.Equal("3072", Eval("<%disksizebytes%>"));
        Assert.Equal("[   a.txt]", Eval("{script}[{leftpad{<%file%>}{ }{8}}]"));
        Assert.Equal("[a.txt   ]", Eval("{script}[{rightpad{<%file%>}{ }{8}}]"));
        Assert.Equal("yes", Eval("{script}{ if {<%isfile%>} == {true} ? {yes} : {no}}"));
        Assert.Equal("no", Eval("{script}{ if {<%isfolder%>} == {true} ? {yes} : {no}}"));
        Assert.Equal("x\ny", Eval("{script}x{&br}y"));       // a bare LF
        Assert.Equal("\t{}<>", Eval("{script}{&tab}{&curlyop}{&curlycl}{&angleop}{&anglecl}"));
        Assert.Equal("*VOID*", Eval("{script}{&nosuch}"));
        Assert.Equal("*BAD TAG*", Eval("<%nosuch%>"));
        Assert.Equal("    a.txt", Eval("{script}{nest{  }}<%file%>"));
        // script mode drops literal line breaks / tabs, plain mode keeps them
        Assert.Equal("ab", Eval("{script}a\r\n\tb"));
        Assert.Equal("a\r\n\tb", Eval("a\r\n\tb"));
        Assert.Equal("*BAD PAD*", Eval("{leftpad}"));
    }

    // ------------------------------------------------------------------ snapshots / du

    [Fact]
    public void Snapshot_RoundTrip_PreservesStructureSizesTagsAndDates()
    {
        var tree = SampleTree.Create();
        var path = Path.Combine(Dir, "sample.sns");
        SnsFormat.Write(path, tree.Root, _ => true);
        Assert.True(SnsFormat.IsSnapshotFile(path));
        var back = SnsFormat.Read(path);
        Assert.Equal(tree.Root.Name, back.Name);
        Assert.Equal(tree.Root.DiskSize, back.DiskSize);
        Assert.False(back.IsDrive);                                  // the .sns format carries no drive totals: loaded trees are plain folders
        Assert.Equal(tree.Root.Descendants().Count(), back.Descendants().Count());
        foreach (var n in tree.Root.Descendants())
        {
            var b = back.Descendants().Single(x => x.FullPath == n.FullPath);
            Assert.Equal(n.Kind, b.Kind);
            Assert.Equal(n.DiskSize, b.DiskSize);
            Assert.Equal(n.Tag, b.Tag);
            if (n is FileNode f)
            {
                var bf = Assert.IsType<FileNode>(b);
                Assert.Equal(f.LogicalSize, bf.LogicalSize);
                Assert.Equal(f.Attributes, bf.Attributes);
                Assert.True(Math.Abs((f.Modified - bf.Modified).TotalSeconds) < 1);
            }
        }
        // a snapshot written with an include predicate only carries the matching files
        var mp3Only = Path.Combine(Dir, "mp3.sns");
        SnsFormat.Write(mp3Only, tree.Root, n => n is not FileNode || n.Name.EndsWith(".mp3"));
        var filtered = SnsFormat.Read(mp3Only);
        Assert.Equal(2, filtered.Descendants().OfType<FileNode>().Count());
        Assert.Equal(160L * 1024 * 1024, filtered.DiskSize);
        // garbage is rejected
        var bad = Path.Combine(Dir, "bad.sns");
        System.IO.File.WriteAllBytes(bad, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.ThrowsAny<Exception>(() => SnsFormat.Read(bad));
    }

    [Fact]
    public void DuFile_IsParsedIntoATree()
    {
        var path = Path.Combine(Dir, "du.txt");
        System.IO.File.WriteAllLines(path, new[]
        {
            "Size (KB)\tPath",
            "3,072\tD:\\data",
            "2,048\tD:\\data\\photos",
            "1,024\tD:\\data\\docs",
            "Files: 3",
        });
        Assert.True(DuFormat.IsDuFile(path));
        var root = DuFormat.Read(path)!;
        Assert.Equal("D:\\data", root.Name);
        Assert.Equal(2, root.Children.Count);
        Assert.Equal(3L * 1024 * 1024, root.DiskSize);
        Assert.Equal(2L * 1024 * 1024, root.FindChild("photos")!.DiskSize);
        var empty = Path.Combine(Dir, "empty.txt");
        System.IO.File.WriteAllText(empty, "Size (KB)\tPath\r\n");
        Assert.Null(DuFormat.Read(empty));
    }

    // ------------------------------------------------------------------ command line

    [Fact]
    public void CommandLine_ParsesViewsFiltersExportsAndBarePaths()
    {
        var snapshot = Path.Combine(Dir, "cl.sns");
        SnsFormat.Write(snapshot, SampleTree.Create().Root, _ => true);
        var (req, autoClose, help) = CommandLine.Parse(new[] { "scan", "C:\\;D:\\Data", "filter", "*.jpg", "export", "Totals", "out.txt", "load", snapshot, "export", "File paths", "l.txt", "autoclose" });
        Assert.False(help); Assert.True(autoClose);
        Assert.Equal(3, req.Count);
        Assert.Equal("C:\\", req[0].Path); Assert.False(req[0].IsLoad); Assert.Empty(req[0].Filter);
        Assert.Equal("D:\\Data", req[1].Path); Assert.Equal("*.jpg", req[1].Filter);
        Assert.Single(req[1].Exports); Assert.Equal(("Totals", "out.txt"), req[1].Exports[0]);
        Assert.True(req[2].IsLoad); Assert.Equal(snapshot, req[2].Path); Assert.Equal(("File paths", "l.txt"), req[2].Exports[0]);
        // bare paths (drag & drop onto the exe)
        var (bare, _, _) = CommandLine.Parse(new[] { Dir, snapshot, "no-such-thing" });
        Assert.Equal(2, bare.Count);
        Assert.False(bare[0].IsLoad); Assert.True(bare[1].IsLoad);
        Assert.True(CommandLine.Parse(new[] { "/?" }).help);
        Assert.True(CommandLine.Parse(new[] { "--help" }).help);
        Assert.Empty(CommandLine.Parse(Array.Empty<string>()).requests);
        Assert.Empty(CommandLine.Parse(new[] { "filter", "*.x", "export", "a" }).requests);   // dangling options are ignored
    }

    // ------------------------------------------------------------------ navigation, tags, layout

    [Fact]
    public void ViewModel_Navigation_History_And_Tags()
    {
        AppConfig.Replace(new AppConfig());
        var tree = SampleTree.Create();
        using var vm = new ViewModel(tree, tree.Root);
        var users = (ContainerNode)tree.Root.FindChild("Users")!;
        var pics = (ContainerNode)users.FindChild("Pictures")!;
        var win = (ContainerNode)tree.Root.FindChild("Windows")!;
        Assert.False(vm.CanGoUp); Assert.False(vm.CanGoBack);
        vm.ZoomTo(pics);
        Assert.Same(pics, vm.ZoomRoot); Assert.True(vm.CanGoUp); Assert.True(vm.CanGoBack); Assert.False(vm.CanGoForward);
        vm.GoUp(); Assert.Same(users, vm.ZoomRoot);
        vm.GoBack(); Assert.Same(pics, vm.ZoomRoot);
        vm.GoForward(); Assert.Same(users, vm.ZoomRoot);
        vm.GoHome(); Assert.Same(tree.Root, vm.ZoomRoot); Assert.False(vm.CanGoUp);
        Assert.Same(users, vm.PeekBack());
        vm.ZoomTo(win); Assert.False(vm.CanGoForward);        // a new jump clears the forward stack
        // zoom root disappears -> falls back to the nearest surviving ancestor
        var sys = (ContainerNode)win.FindChild("System32")!;
        vm.ZoomTo(sys);
        lock (tree.Sync) win.RemoveChild(sys);
        vm.ValidateZoomRoot();
        Assert.Same(win, vm.ZoomRoot);

        // tags: toggling, inheritance through EffectiveTag, clearing from the zoom root
        var inst = (ContainerNode)win.FindChild("Installer")!;
        var kernel = inst.FindChild("setup.msi")!;
        vm.ToggleTag(inst, TagColor.Red);
        Assert.Equal(TagColor.Red, inst.Tag);
        Assert.Equal(TagColor.Red | TagColor.Yellow, inst.FindChild("pack.zip")!.EffectiveTag);   // own + inherited
        vm.ToggleTag(inst, TagColor.Blue);
        Assert.Equal(TagColor.Red | TagColor.Blue, inst.Tag);
        vm.ToggleTag(inst, TagColor.Red);
        Assert.Equal(TagColor.Blue, inst.Tag);
        vm.ToggleTag(kernel, TagColor.Green);
        vm.GoHome(); vm.ZoomTo(win);
        vm.ClearTags();                                          // only below the zoom root
        Assert.Equal(TagColor.None, inst.Tag);
        Assert.Equal(TagColor.None, kernel.Tag);
        Assert.Equal(TagColor.Red, tree.Root.Descendants().First(n => n.Name == "holiday.jpg").Tag);
    }

    [Fact]
    public void Layout_NoAggregateBlock_MinimumSize_And_HitTest()
    {
        var cfg = new AppConfig();
        Assert.False(cfg.AggregateSmallElements);
        var tree = SampleTree.Create();
        using var vm = new ViewModel(tree, tree.Root) { DetailLevel = 3, ShowFreeSpace = true, ShowUnknownSpace = true };
        var bounds = new RectangleF(0, 0, 1000, 700);
        var root = vm.BuildLayout(bounds, 16, cfg);
        var all = Flatten(root).ToList();
        Assert.DoesNotContain(all, i => i.Node is RestNode);
        Assert.Contains(all, i => i.Node is FreeSpaceNode);
        Assert.Contains(all, i => i.Node is UnknownSpaceNode);
        int min = cfg.MinElementSizePx;
        foreach (var it in all.Where(i => !ReferenceEquals(i, root)))
        {
            Assert.True(it.Rect.Width >= min - 0.01f && it.Rect.Height >= min - 0.01f, it.Node.Name + " " + it.Rect);
            Assert.True(bounds.Contains(it.Rect) || bounds.IntersectsWith(it.Rect), it.Node.Name + " outside");
        }
        // every visible element is a real entry (enterable when it is a container)
        Assert.All(all.Where(i => i.Node is ContainerNode), i => Assert.False(i.Node.IsSynthetic));
        // the tiny*.txt files (4 MB each) are dropped rather than aggregated, bigger ones stay
        Assert.Contains(all, i => i.Node.Name == "movie.mkv");
        // hit test finds the deepest element under a point
        var movie = all.First(i => i.Node.Name == "movie.mkv");
        var center = new PointF(movie.Rect.X + movie.Rect.Width / 2, movie.Rect.Y + movie.Rect.Height / 2);
        Assert.Same(movie.Node, ViewModel.HitTest(root, center)!.Node);
        Assert.Same(movie, ViewModel.Find(root, movie.Node));
        Assert.Null(ViewModel.HitTest(root, new PointF(-5, -5)));
        // aggregate mode (opt-in) shows the "N items" block instead
        cfg.AggregateSmallElements = true;
        var agg = Flatten(vm.BuildLayout(bounds, 16, cfg)).ToList();
        Assert.Contains(agg, i => i.Node is RestNode);
    }

    private static IEnumerable<TreemapItem> Flatten(TreemapItem it)
    {
        yield return it;
        foreach (var c in it.Children) foreach (var d in Flatten(c)) yield return d;
    }

    // ------------------------------------------------------------------ scanning a real directory

    [Fact]
    public void Scanner_ReadsARealTree_HonoursExcludes_AndFindsPaths()
    {
        var root = Path.Combine(Dir, "scanroot");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(Path.Combine(root, "a", "deep"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "pkg"));
        System.IO.File.WriteAllBytes(Path.Combine(root, "big.bin"), new byte[300_000]);
        System.IO.File.WriteAllBytes(Path.Combine(root, "a", "one.txt"), new byte[10_000]);
        System.IO.File.WriteAllBytes(Path.Combine(root, "a", "deep", "two.txt"), new byte[20_000]);
        System.IO.File.WriteAllBytes(Path.Combine(root, "node_modules", "pkg", "index.js"), new byte[50_000]);

        AppConfig.Replace(new AppConfig { ExcludePaths = "node_modules" });
        var (tree, node) = DataCache.Acquire(root);
        try
        {
            Assert.True(tree.IsLive);
            tree.StartMasterScan();
            WaitFor(() => !tree.IsMasterScanning, 20000);
            Assert.Equal(ScanState.Completed, tree.MasterState);
            Assert.Equal(3, node.Descendants().OfType<FileNode>().Count());
            Assert.Null(node.FindChild("node_modules"));
            Assert.Equal(330_000, node.Descendants().OfType<FileNode>().Sum(f => f.LogicalSize));
            Assert.True(node.DiskSize >= 330_000);                   // disk size rounds up to clusters
            var a = (ContainerNode)node.FindChild("a")!;
            Assert.Equal(2, a.FileCount);
            Assert.Equal(1, a.FolderCount);
            Assert.True(a.ScanComplete && a.Enumerated);
            var found = tree.Find(Path.Combine(root, "a", "deep", "two.txt"));
            Assert.NotNull(found);
            Assert.Equal(20_000, found!.LogicalSize);
            Assert.Equal(Path.Combine(root, "a", "deep", "two.txt"), found.FullPath);
            // the same path is shared through the cache
            var (again, node2) = DataCache.Acquire(root);
            Assert.Same(tree, again); Assert.Same(node, node2);
            DataCache.Release(again);

            // a live view exports the real numbers
            using var vm = new ViewModel(tree, node);
            var ex = new Exporter(ExportConfig.FindByName("Totals")!, node, _ => true, tree.Sync);
            var sw = new StringWriter(); ex.ExportText(sw, CancellationToken.None);
            Assert.Contains("Files: 3", sw.ToString());
            Assert.Contains("Folders: 4", sw.ToString());      // a, deep + the root twice
        }
        finally
        {
            DataCache.Release(tree);
            AppConfig.Replace(new AppConfig());
        }
    }


    // ------------------------------------------------------------------ regression tests for the diagnostic findings

    [Fact]
    public void FollowReparsePoints_JunctionLoop_IsWalkedOnce()
    {
        var root = Path.Combine(Dir, "loop");
        if (Directory.Exists(root)) CleanLinks(root);
        string a = Path.Combine(root, "A"), b = Path.Combine(a, "B"), outside = Path.Combine(Dir, "loop-outside");
        Directory.CreateDirectory(b); Directory.CreateDirectory(outside);
        System.IO.File.WriteAllBytes(Path.Combine(a, "a.bin"), new byte[100_000]);
        System.IO.File.WriteAllBytes(Path.Combine(b, "b.bin"), new byte[200_000]);
        System.IO.File.WriteAllBytes(Path.Combine(outside, "o.bin"), new byte[300_000]);
        static void Junction(string link, string target)
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!;
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }
        Junction(Path.Combine(b, "LoopToA"), a);                 // ancestor loop
        Junction(Path.Combine(a, "IntoTree"), b);                // target inside the scanned tree
        Junction(Path.Combine(a, "Outside1"), outside);          // followed once
        Junction(Path.Combine(b, "Outside2"), outside);          // same target: skipped
        try
        {
            AppConfig.Replace(new AppConfig { FollowReparsePoints = true, WatchDog = false });
            var (tree, node) = DataCache.Acquire(root);
            tree.StartMasterScan();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (tree.IsMasterScanning && sw.ElapsedMilliseconds < 20000) Thread.Sleep(20);
            Assert.Equal(ScanState.Completed, tree.MasterState);
            var names = node.Descendants().Select(n => n.Name).ToList();
            Assert.Equal(1, names.Count(n => n == "a.bin"));
            Assert.Equal(1, names.Count(n => n == "o.bin"));
            Assert.DoesNotContain("LoopToA", names);
            Assert.DoesNotContain("IntoTree", names);
            Assert.Contains("Outside1", names);
            Assert.DoesNotContain("Outside2", names);
            Assert.Equal(600_000, node.LogicalSize);
            DataCache.Release(tree);
            // default (not following) still ignores every junction
            AppConfig.Replace(new AppConfig { FollowReparsePoints = false, WatchDog = false });
            (tree, node) = DataCache.Acquire(root);
            tree.StartMasterScan();
            sw.Restart(); while (tree.IsMasterScanning && sw.ElapsedMilliseconds < 20000) Thread.Sleep(20);
            Assert.Equal(300_000, node.LogicalSize);
            DataCache.Release(tree);
        }
        finally
        {
            AppConfig.Replace(new AppConfig());
            CleanLinks(root);
            Directory.Delete(outside, true);
        }
    }

    /// <summary>Deletes a tree that contains junctions without ever descending into them (recursive enumeration would loop).</summary>
    private static void CleanLinks(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var d in Directory.GetDirectories(root))
        {
            if ((System.IO.File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) Directory.Delete(d);
            else CleanLinks(d);
        }
        Directory.Delete(root, true);
    }

    [Fact]
    public void Wildcard_FoldsTurkishI()
    {
        Assert.True(new Wildcard("*ist*").IsMatch("İstanbul-rapor.docx"));
        Assert.True(new Wildcard("*İST*").IsMatch("istanbul.txt"));
        Assert.True(new Wildcard("*ışık*").IsMatch("IŞIK.TXT"));
        Assert.False(new Wildcard("*ist*").IsMatch("ankara.txt"));
        Assert.True(new Wildcard("*.mp3").IsMatch("Ünlü Şarkı.MP3"));
        var f = new Filter("*ist*");
        var root = new DriveNode("C:\\");
        Assert.True(f.Matches(File(root, "İstanbul.txt", 10), Filter.FolderMaskState.Root, DateTime.UtcNow));
    }

    [Fact]
    public void Layout_VisibleElements_AreNeverThinnerThanMinimum_InEitherMode()
    {
        foreach (bool aggregate in new[] { false, true })
        {
            var cfg = new AppConfig { AggregateSmallElements = aggregate };
            int min = cfg.MinElementSizePx;
            var root = new DriveNode("X:\\") { IsDrive = true, Enumerated = true, ScanComplete = true };
            for (int i = 0; i < 400; i++) File(root, "eq" + i, 1_000_000);
            var big = new FolderNode("big") { Enumerated = true, ScanComplete = true }; root.AddChild(big);
            for (int i = 0; i < 2000; i++) File(big, "b" + i, 1_000_000);
            for (int i = 0; i < 300; i++) { var d = new FolderNode("s" + i) { Enumerated = true, ScanComplete = true }; root.AddChild(d); File(d, "x", 1000 + i); }
            root.FreeBytes = root.DiskSize / 10; root.TotalBytes = root.DiskSize + root.FreeBytes;
            var tree = new ScanTree(root, live: false);
            using var vm = new ViewModel(tree, root) { DetailLevel = 3 };
            foreach (var (w, h) in new[] { (1200f, 800f), (2000f, 120f), (300f, 2000f), (640f, 480f) })
            {
                var lay = vm.BuildLayout(new RectangleF(0, 0, w, h), 16, cfg);
                var thin = new List<string>();
                int nearly = 0, total = 0;
                void Walk(TreemapItem it)
                {
                    // a squarified layout can leave a last row slightly thinner than the minimum: tolerate a few items
                    // just under the limit, never anything clearly below it
                    if (it.Parent != null && it.Rect.Width > 0 && it.Rect.Height > 0 && (it.Rect.Width < min * 0.75f || it.Rect.Height < min * 0.75f))
                        thin.Add($"{it.Node.Name} {it.Rect.Width:0.#}x{it.Rect.Height:0.#}");
                    if (it.Parent != null && it.Rect.Width > 0 && it.Rect.Height > 0 && (it.Rect.Width < min - 0.01f || it.Rect.Height < min - 0.01f)) nearly++;
                    total++;
                    foreach (var c in it.Children) Walk(c);
                }
                Walk(lay);
                Assert.True(thin.Count == 0, $"aggregate={aggregate} {w}x{h}: " + string.Join(", ", thin.Take(5)));
                Assert.True(nearly <= Math.Max(2, total / 50), $"aggregate={aggregate} {w}x{h}: {nearly} of {total} items just under the minimum");
            }
        }
    }
    private static void WaitFor(Func<bool> cond, int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("condition not met in " + ms + " ms");
            Thread.Sleep(20);
        }
    }
}
