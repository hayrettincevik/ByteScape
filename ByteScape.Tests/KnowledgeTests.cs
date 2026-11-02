using ByteScape.Core.Config;
using ByteScape.Core.Filtering;
using ByteScape.Core.Knowledge;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using ByteScape.Treemap;
using Xunit;

namespace ByteScape.Tests;

/// <summary>Knowledge base: "what is this folder?" classification, inheritance, reclaimable totals and the :reclaimable filter.</summary>
[Collection("config")]
public class KnowledgeTests
{
    private static FolderNode Folder(ContainerNode p, string name) { var f = new FolderNode(name) { Enumerated = true, ScanComplete = true }; p.AddChild(f); return f; }
    private static FileNode File(ContainerNode p, string name, long bytes) { var f = new FileNode(name) { LogicalSize = bytes, DiskSize = bytes }; p.AddChild(f); return f; }

    /// <summary>C:\ with a user profile, caches, a project and Windows folders.</summary>
    private static DriveNode Build()
    {
        var c = new DriveNode("C:\\") { IsDrive = true, Enumerated = true, ScanComplete = true, TotalBytes = 1L << 40, FreeBytes = 1L << 30 };
        var users = Folder(c, "Users"); var me = Folder(users, "Ayşe");
        var gradle = Folder(me, ".gradle"); var gcaches = Folder(gradle, "caches"); File(Folder(gcaches, "9.4.1"), "x.jar", 3_000_000);
        var cache = Folder(me, ".cache"); File(cache, "tool.bin", 1_000_000); var hf = Folder(cache, "huggingface"); File(Folder(hf, "hub"), "model.safetensors", 50_000_000);
        var proj = Folder(Folder(me, "src"), "app"); File(proj, "index.ts", 1000); var nm = Folder(proj, "node_modules"); File(Folder(nm, "react"), "index.js", 200_000);
        var local = Folder(Folder(me, "AppData"), "Local"); var temp = Folder(local, "Temp"); File(temp, "setup.tmp", 700_000);
        var chrome = Folder(Folder(Folder(Folder(local, "Google"), "Chrome"), "User Data"), "Default"); File(Folder(chrome, "Cache"), "f_0001", 400_000); File(chrome, "History", 5_000);
        var win = Folder(c, "Windows"); File(Folder(win, "WinSxS"), "a.dll", 9_000_000); File(Folder(win, "Temp"), "w.tmp", 100_000); File(Folder(win, "System32"), "ntdll.dll", 2_000_000);
        File(c, "pagefile.sys", 8_000_000); File(win, "MEMORY.DMP", 600_000);
        File(Folder(c, "Projects"), "notes.txt", 10);
        return c;
    }

    private static FsNode Find(ContainerNode root, string path) => root.Descendants().First(n => n.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Classifies_KnownLocations_AndInherits()
    {
        var c = Build();
        Assert.Equal("gradle-caches", KnowledgeBase.Classify(Find(c, @"C:\Users\Ayşe\.gradle\caches"))!.Location.Id);
        var inherited = KnowledgeBase.Classify(Find(c, @"C:\Users\Ayşe\.gradle\caches\9.4.1\x.jar"))!;
        Assert.Equal("gradle-caches", inherited.Location.Id); Assert.True(inherited.Inherited); Assert.Equal("caches", inherited.MatchedNode.Name);
        Assert.Equal("node-modules", KnowledgeBase.Classify(Find(c, @"C:\Users\Ayşe\src\app\node_modules"))!.Location.Id);
        Assert.Null(KnowledgeBase.Classify(Find(c, @"C:\Users\Ayşe\src\app\index.ts")));
        Assert.Equal("user-temp", KnowledgeBase.Classify(Find(c, @"C:\Users\Ayşe\AppData\Local\Temp"))!.Location.Id);
        Assert.Equal("chrome-cache", KnowledgeBase.Classify(Find(c, @"C:\Users\Ayşe\AppData\Local\Google\Chrome\User Data\Default\Cache"))!.Location.Id);
        Assert.Null(KnowledgeBase.Classify(Find(c, @"C:\Users\Ayşe\AppData\Local\Google\Chrome\User Data\Default\History")));
        // a wildcard segment never spans folders: children of a matched "AndroidStudio*" folder inherit instead of matching again
        var stray = Folder(Folder(Folder(c, "Users2"), "Google"), "AndroidStudio2024.1");
        Assert.Null(KnowledgeBase.Classify(stray));   // not under Users\<name>\AppData\Local
        var local2 = Folder(Folder(Folder((ContainerNode)Find(c, @"C:\Users"), "Ali"), "AppData"), "Local");
        var studio = Folder(Folder(local2, "Google"), "AndroidStudio2024.1"); var index = Folder(studio, "index");
        Assert.False(KnowledgeBase.Classify(studio)!.Inherited);
        Assert.True(KnowledgeBase.Classify(index)!.Inherited);
        // the more specific rule wins at its own node; Windows itself is protected and its children inherit that
        Assert.Equal("winsxs", KnowledgeBase.Classify(Find(c, @"C:\Windows\WinSxS"))!.Location.Id);
        Assert.Equal("user-temp", KnowledgeBase.Classify(Find(c, @"C:\Windows\Temp"))!.Location.Id);
        Assert.Equal("windows-dir", KnowledgeBase.Classify(Find(c, @"C:\Windows\System32"))!.Location.Id);
        Assert.Equal(Safety.System, KnowledgeBase.Classify(Find(c, @"C:\Windows\System32\ntdll.dll"))!.Location.Safety);
        // file rules
        Assert.Equal("pagefile", KnowledgeBase.Classify(Find(c, @"C:\pagefile.sys"))!.Location.Id);
        Assert.Equal("crash-dumps", KnowledgeBase.Classify(Find(c, @"C:\Windows\MEMORY.DMP"))!.Location.Id);
        Assert.Null(KnowledgeBase.Classify(Find(c, @"C:\Projects\notes.txt")));
        Assert.Null(KnowledgeBase.Classify(c));
        // nested: ~/.cache is reclaimable, its huggingface sub folder is not
        Assert.True(KnowledgeBase.IsReclaimable(Find(c, @"C:\Users\Ayşe\.cache\tool.bin")));
        Assert.False(KnowledgeBase.IsReclaimable(Find(c, @"C:\Users\Ayşe\.cache\huggingface\hub\model.safetensors")));
        // every rule compiles and has the documentation fields filled
        foreach (var r in KnowledgeBase.Rules)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Title)); Assert.False(string.IsNullOrWhiteSpace(r.Description)); Assert.False(string.IsNullOrWhiteSpace(r.Consequence));
            Assert.NotEmpty(r.Patterns);
        }
        Assert.Equal(KnowledgeBase.Rules.Count, KnowledgeBase.Rules.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void Summarize_CountsEachReclaimableLocationOnce_WithoutProtectedNesting()
    {
        var c = Build();
        var s = KnowledgeBase.Summarize(c, n => n.DiskSize, CancellationToken.None);
        var byId = s.Items.ToDictionary(i => i.Location.Id + "|" + i.Node.FullPath, i => i.Bytes);
        Assert.Equal(3_000_000, byId[@"gradle-caches|C:\Users\Ayşe\.gradle\caches"]);
        Assert.Equal(1_000_000, byId[@"generic-cache|C:\Users\Ayşe\.cache"]);          // huggingface (Caution) excluded
        Assert.Equal(200_000, byId[@"node-modules|C:\Users\Ayşe\src\app\node_modules"]);
        Assert.Equal(700_000, byId[@"user-temp|C:\Users\Ayşe\AppData\Local\Temp"]);
        Assert.Equal(100_000, byId[@"user-temp|C:\Windows\Temp"]);                      // found below the protected Windows folder
        Assert.Equal(400_000, byId[@"chrome-cache|C:\Users\Ayşe\AppData\Local\Google\Chrome\User Data\Default\Cache"]);
        Assert.Equal(600_000, byId[@"crash-dumps|C:\Windows\MEMORY.DMP"]);
        Assert.DoesNotContain(s.Items, i => i.Location.Id is "winsxs" or "pagefile" or "hf-models");
        Assert.Equal(3_000_000 + 1_000_000 + 200_000 + 700_000 + 100_000 + 400_000 + 600_000, s.TotalBytes);
        Assert.Equal(s.Items.OrderByDescending(i => i.Bytes).Select(i => i.Bytes), s.Items.Select(i => i.Bytes));
        // scanning a cache folder directly: the root itself counts
        var direct = KnowledgeBase.Summarize((ContainerNode)Find(c, @"C:\Users\Ayşe\.gradle\caches"), n => n.DiskSize, CancellationToken.None);
        Assert.Equal(3_000_000, direct.TotalBytes);
    }

    [Fact]
    public void Filter_Reclaimable_SelectsCacheFiles()
    {
        AppConfig.Replace(new AppConfig());
        var c = Build();
        var tree = new ScanTree(c, live: false);
        using var vm = new ViewModel(tree, c);
        var f = new Filter(":reclaimable");
        Assert.False(f.IsEmpty);
        Assert.True(f.Matches((FileNode)Find(c, @"C:\Users\Ayşe\.gradle\caches\9.4.1\x.jar"), Filter.FolderMaskState.Root, DateTime.UtcNow));
        Assert.False(f.Matches((FileNode)Find(c, @"C:\Users\Ayşe\src\app\index.ts"), Filter.FolderMaskState.Root, DateTime.UtcNow));
        Assert.False(f.Matches((FileNode)Find(c, @"C:\Windows\WinSxS\a.dll"), Filter.FolderMaskState.Root, DateTime.UtcNow));
        var not = new Filter("|:cache");
        Assert.False(not.Matches((FileNode)Find(c, @"C:\Users\Ayşe\.gradle\caches\9.4.1\x.jar"), Filter.FolderMaskState.Root, DateTime.UtcNow));
        Assert.True(not.Matches((FileNode)Find(c, @"C:\Users\Ayşe\src\app\index.ts"), Filter.FolderMaskState.Root, DateTime.UtcNow));
        vm.SetFilter("*.js;:reclaimable");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while ((vm.IsFiltering || vm.FilterResultStale) && sw.ElapsedMilliseconds < 5000) { if (!vm.IsFiltering && vm.FilterResultStale) vm.RequestRefilter(); Thread.Sleep(10); }
        Assert.Equal(200_000, vm.SizeOf(c));
        // the background summary of the view model
        vm.RequestReclaimSummary(force: true);
        sw.Restart(); while (vm.IsSummarizing && sw.ElapsedMilliseconds < 5000) Thread.Sleep(10);
        Assert.Equal(6_000_000, vm.Reclaim.TotalBytes);
    }
}
