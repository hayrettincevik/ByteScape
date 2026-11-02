using System.Diagnostics;
using ByteScape.Core.Apps;
using ByteScape.Core.Model;
using ByteScape.Treemap;
using Xunit;
using Xunit.Abstractions;

namespace ByteScape.Tests;

/// <summary>"Programs on disk": installed program catalog, folder ownership and the footprint walk.</summary>
[Collection("config")]
public class AppsTests
{
    private readonly ITestOutputHelper _out;
    public AppsTests(ITestOutputHelper o) => _out = o;

    private const long MB = 1 << 20;
    private static readonly DateTime Old = new(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Recent = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static FolderNode Folder(ContainerNode p, string name) { var f = new FolderNode(name) { Enumerated = true, ScanComplete = true }; p.AddChild(f); return f; }
    internal static FileNode File(ContainerNode p, string name, long bytes, DateTime? modified = null) { var f = new FileNode(name) { LogicalSize = bytes, DiskSize = bytes, Modified = modified ?? Recent }; p.AddChild(f); return f; }
    internal static FolderNode Path(ContainerNode root, string rel) { ContainerNode c = root; foreach (var s in rel.Split('\\')) c = (c.FindChild(s) as FolderNode) ?? Folder(c, s); return (FolderNode)c; }

    private static UninstallEntry E(string name, string publisher = "", string loc = "", string icon = "", string uninstall = "", bool component = false) =>
        new(name.Replace(' ', '_'), name) { Publisher = publisher, InstallLocation = loc, DisplayIcon = icon, UninstallString = uninstall, SystemComponent = component };

    internal static AppCatalog Catalog() => AppCatalog.FromEntries(new[]
    {
        E("Spotify", "Spotify AB", loc: @"C:\Users\Ali\AppData\Roaming\Spotify"),
        E("Google Chrome", "Google LLC", uninstall: "MsiExec.exe /X{FBFEB552-5ABD-358B-8B8D-6620F20966C7}"),
        // a web app shortcut: its uninstaller is chrome.exe, Chrome's folder must not become the web app's
        E("Telegram Web", @"Google\Chrome", icon: @"C:\Users\Ali\AppData\Local\Google\Chrome\User Data\Default\Web Applications\_crx_ib\Telegram Web.ico",
          uninstall: "\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" --profile-directory=Default --uninstall-app-id=ib"),
        E("Steam", "Valve Corporation", icon: @"C:\Program Files (x86)\Steam\uninstall.exe", uninstall: @"C:\Program Files (x86)\Steam\uninstall.exe"),
        E("Alba: A Wildlife Adventure", "ustwo games", loc: @"C:\Program Files (x86)\Steam\steamapps\common\Alba"),
        E("Microsoft Edge", "Microsoft Corporation", loc: @"C:\Program Files (x86)\Microsoft\Edge\Application"),
        E("Microsoft Visual Studio Code (User)", "Microsoft Corporation", loc: @"C:\Users\Ali\AppData\Local\Programs\Microsoft VS Code\"),
        E("Readest", "bilingify", loc: "\"C:\\Program Files\\Readest\""),
        E("Readest", "bilingify", loc: @"C:\Users\Ali\AppData\Local\Readest"),
        E("Cherry Studio", "support@cherry-ai.com", icon: @"C:\Users\Ali\AppData\Local\Programs\Cherry Studio\Cherry Studio.exe,0"),
        E("HL-2130", "Brother Industries, Ltd.", loc: @"C:\Program Files (x86)\Brother\Brmfl10d"),
        E("Python 3.14.3 (64-bit)", "Python Software Foundation", icon: @"C:\Users\Ali\AppData\Local\Package Cache\{d91d5a08}\python-3.14.3-amd64.exe,0"),
        E("Python 3.14.3 Core Interpreter (64-bit)", "Python Software Foundation", component: true),
        E("Microsoft Visual C++ 2015 Redistributable (x64) - 14.0.1", "Microsoft Corporation"),
        E("Microsoft Visual C++ 2015 Redistributable (x86) - 14.0.1", "Microsoft Corporation"),
        E("Microsoft Edge WebView2 Runtime", "Microsoft Corporation", loc: @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application", component: true),
        E("Microsoft Office MUI (English) 2016", "Microsoft Corporation", loc: @"C:\Program Files\Microsoft Office\", component: true),
        E("Microsoft Office Professional Plus 2016", "Microsoft Corporation", loc: @"C:\Program Files\Microsoft Office"),
        E("NVIDIA PhysX", "NVIDIA Corporation", loc: @"C:\Program Files (x86)\NVIDIA Corporation\PhysX"),
        E("Deep Tool", "Deep Tools Ltd", loc: @"D:\a\b\c\d\e\f\g\h\i\Deep Tool"),
        E("Security Update KB123", "Microsoft Corporation") with { IsUpdate = true },
    }, new[]
    {
        new StorePackage("Claude_2.2553.13.0_x64__pzs8sxrjxfjjc", "Claude") { RootFolder = @"C:\Program Files\WindowsApps\Claude_2.2553.13.0_x64__pzs8sxrjxfjjc" },
        new StorePackage("Microsoft.MicrosoftEdge.Stable_153.0.1.0_neutral__8wekyb3d8bbwe", "Microsoft Edge") { RootFolder = @"C:\Program Files (x86)\Microsoft\Edge\Application" },
        new StorePackage("Microsoft.WindowsNotepad_11.0.0.0_x64__8wekyb3d8bbwe", "Notepad") { RootFolder = @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_11.0.0.0_x64__8wekyb3d8bbwe" },
        new StorePackage("1527c705-839a-4832-9118-54d4Bd6a0c89_10.0.19041.4239_neutral_neutral_cw5n1h2txyewy", "@{1527c705?ms-resource://FilePicker/Resources/Name}")
            { RootFolder = @"C:\Windows\SystemApps\Microsoft.Windows.FilePicker_cw5n1h2txyewy" },
    });

    private static InstalledApp App(AppCatalog c, string name) => c.Apps.Single(a => a.Name == name);

    [Fact]
    public void Names_AreCleanedAndNormalised()
    {
        Assert.Equal("Python", AppCatalog.CleanName("Python 3.14.3 (64-bit)"));
        Assert.Equal("Microsoft Visual C++ 2010 Redistributable", AppCatalog.CleanName("Microsoft Visual C++ 2010  x64 Redistributable - 10.0.40219"));
        Assert.Equal("SoulseekQt", AppCatalog.CleanName("SoulseekQt 2024.6.30 sürümü"));
        Assert.Equal("Unity", AppCatalog.CleanName("Unity 2022.3.10f1"));
        Assert.Equal("WinRAR", AppCatalog.CleanName("WinRAR 7.01 (64 bit)"));
        Assert.Equal("7-Zip", AppCatalog.CleanName("7-Zip 23.01 (x64)"));
        Assert.Equal("Java 8 Update 491", AppCatalog.CleanName("Java 8 Update 491"));
        Assert.Equal("akiskartizlemearaci", AppCatalog.Key("Akis Kart İzleme Aracı"));
        Assert.Equal("obsstudio", AppCatalog.Key("obs-studio"));
        Assert.Equal("Chrome", AppCatalog.WithoutPublisher("Google Chrome", "Google LLC"));
        Assert.Null(AppCatalog.WithoutPublisher("Spotify", "Spotify AB"));
        Assert.Equal(new[] { "brotherindustries", "brother" }, AppCatalog.PublisherKeysOf("Brother Industries, Ltd.").ToArray());
        Assert.Equal("Claude_pzs8sxrjxfjjc", AppCatalog.FamilyOf("Claude_2.2553.13.0_x64__pzs8sxrjxfjjc"));
        Assert.Null(AppCatalog.FamilyOf("ActiveSync"));
        Assert.Equal("WhatsAppDesktop", AppCatalog.PrettyPackageName("5319275A.WhatsAppDesktop_cv1g1gvanyjgm"));
        Assert.Equal(@"C:\x\a.exe", AppCatalog.ExePath("\"C:\\x\\a.exe\" --uninstall"));
        Assert.Equal(@"C:\x\a.exe", AppCatalog.ExePath(@"C:\x\a.exe,0"));
        Assert.Null(AppCatalog.ExePath("MsiExec.exe /X{1}"));
        Assert.Null(AppCatalog.ExePath(@"C:\Windows\System32\rundll32.exe x.dll,Uninstall"));
    }

    [Fact]
    public void Catalog_MergesEntriesAndFindsInstallFolders()
    {
        var c = Catalog();
        Assert.Equal(new[] { @"C:\Program Files\Readest", @"C:\Users\Ali\AppData\Local\Readest" }, App(c, "Readest").InstallDirs);
        Assert.Single(c.Apps, a => a.Name.StartsWith("Microsoft Visual C++ 2015"));
        Assert.Equal("Microsoft Visual C++ 2015 Redistributable", c.Apps.Single(a => a.Name.StartsWith("Microsoft Visual C++ 2015")).Name);
        Assert.DoesNotContain(c.Apps, a => a.Name.Contains("KB123"));
        Assert.Empty(App(c, "Telegram Web").InstallDirs);                                            // chrome.exe is not the web app's folder
        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam" }, App(c, "Steam").InstallDirs);          // from the uninstaller
        Assert.Equal(new[] { @"C:\Program Files (x86)\Microsoft\Edge" }, App(c, "Microsoft Edge").InstallDirs);   // "Application" climbed
        Assert.Equal(new[] { @"C:\Users\Ali\AppData\Local\Programs\Cherry Studio" }, App(c, "Cherry Studio").InstallDirs);
        Assert.Empty(App(c, "Python 3.14.3 (64-bit)").InstallDirs);                                  // an installer cache is not the program
        Assert.True(App(c, "Python 3.14.3 Core Interpreter (64-bit)").IsComponent);
        // Store: third party app, Edge's identity package folded into desktop Edge, Windows' own package named from its folder
        Assert.Equal(AppSource.Store, App(c, "Claude").Source);
        Assert.Single(c.Apps, a => a.Name == "Microsoft Edge");
        var picker = c.Apps.Single(a => a.PackageFamily?.StartsWith("1527c705") == true);
        Assert.True(picker.IsComponent);
        Assert.Equal("Windows", picker.TypeText);
        Assert.True(c.MaxMatchDepth >= 11);                                                           // the deep install folder is reachable
    }

    [Fact]
    public void Match_FindsTheOwningProgram()
    {
        var c = Catalog();
        string? Owner(string path) => c.Match(path)?.App.Name;
        PieceKind? Kind(string path) => c.Match(path)?.Kind;

        Assert.Equal("Steam", Owner(@"C:\Program Files (x86)\Steam"));
        Assert.Equal("Alba: A Wildlife Adventure", Owner(@"C:\Program Files (x86)\Steam\steamapps\common\Alba"));
        Assert.Equal(("Alba: A Wildlife Adventure", @"C:\Program Files (x86)\Steam\steamapps\common\Alba"),
            c.OwnerOf(@"C:\Program Files (x86)\Steam\steamapps\common\Alba\data\level1.pak") is { } o ? (o.Claim.App.Name, o.Folder) : default);
        Assert.Equal("Steam", c.OwnerOf(@"C:\Program Files (x86)\Steam\steamapps\workshop\x.bin")?.Claim.App.Name);
        // vendor folders: the sub folder named like the vendor's program
        Assert.Equal("Google Chrome", Owner(@"C:\Users\Ali\AppData\Local\Google\Chrome"));
        Assert.Equal(PieceKind.LocalData, Kind(@"C:\Users\Ali\AppData\Local\Google\Chrome"));
        Assert.Equal("Google Chrome", Owner(@"C:\Program Files\Google\Chrome"));
        Assert.Equal(PieceKind.Program, Kind(@"C:\Program Files\Google\Chrome"));
        Assert.Null(Owner(@"C:\Program Files\Google"));                                            // a big vendor's folder is nobody's
        Assert.Equal("Microsoft Edge", Owner(@"C:\Users\Ali\AppData\Local\Microsoft\Edge"));
        Assert.Null(Owner(@"C:\Users\Ali\AppData\Local\Microsoft\Windows"));
        // by name, in every profile
        Assert.Equal("Spotify", Owner(@"C:\Users\Bob\AppData\Roaming\Spotify"));
        Assert.Equal(PieceKind.LocalData, Kind(@"C:\Users\Ali\AppData\Local\Spotify"));
        Assert.Equal("Readest", Owner(@"C:\Users\Ali\AppData\Local\com.bilingify.readest"));      // reverse DNS
        Assert.Equal("Cherry Studio", Owner(@"C:\Users\Ali\AppData\Local\cherrystudio-updater"));  // updater folder
        Assert.Equal("Cherry Studio", Owner(@"C:\Users\Ali\AppData\Roaming\CherryStudio"));
        Assert.Equal("HL-2130", Owner(@"C:\ProgramData\Brother"));                                  // a small vendor's only program
        Assert.Null(Owner(@"C:\ProgramData\NVIDIA Corporation"));
        Assert.Equal("Microsoft Office Professional Plus 2016", Owner(@"C:\Program Files\Microsoft Office"));   // the visible entry, not its MUI component
        Assert.Equal("Microsoft Office Professional Plus 2016", Owner(@"C:\Program Files (x86)\Microsoft Office"));
        Assert.Equal("Microsoft Office Professional Plus 2016", Owner(@"C:\Users\Ali\AppData\Roaming\Microsoft\Word"));   // alias
        Assert.Equal("Microsoft Edge WebView2 Runtime", Owner(@"C:\Program Files (x86)\Microsoft\EdgeWebView"));
        // aliases
        Assert.Equal("Microsoft Visual Studio Code (User)", Owner(@"C:\Users\Ali\AppData\Roaming\Code"));
        Assert.Equal(PieceKind.Profile, Kind(@"C:\Users\Ali\.vscode"));
        Assert.Equal("Python 3.14.3 (64-bit)", Owner(@"C:\Users\Ali\AppData\Local\pip"));
        // Store packages
        Assert.Equal("Claude", Owner(@"C:\Users\Ali\AppData\Local\Packages\Claude_pzs8sxrjxfjjc"));
        Assert.Equal(PieceKind.StoreData, Kind(@"C:\Users\Ali\AppData\Local\Packages\Claude_pzs8sxrjxfjjc"));
        Assert.Equal("Claude", Owner(@"C:\Users\Ali\AppData\Roaming\Claude"));                      // third party Store apps match by name too
        Assert.Equal("Microsoft Edge", Owner(@"C:\Users\Ali\AppData\Local\Packages\Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe"));
        Assert.Equal("Some.Tool", Owner(@"C:\Users\Ali\AppData\Local\Packages\Some.Tool_abcdefghjkmnp"));   // not registered for this user
        Assert.Null(Owner(@"C:\Users\Ali\AppData\Roaming\Notepad"));                                // Microsoft's own packages never match by name
        Assert.Null(Owner(@"C:\Users\Ali\AppData\Local\Packages\cr.sb.cdm3e4d1a"));
        // never
        Assert.Null(Owner(@"C:\Users\Ali\AppData\Local\Temp"));
        Assert.Null(Owner(@"C:\Users\Ali\Documents\Spotify"));
        Assert.Null(Owner(@"C:\Windows\System32"));
        Assert.Equal("Deep Tool", Owner(@"D:\a\b\c\d\e\f\g\h\i\Deep Tool"));
        // snapshot paths carry a frame
        Assert.Equal("Spotify", Owner(@"[SNAPSHOT: C:\snaps\may.sns]\C:\Users\Ali\AppData\Roaming\Spotify"));
    }

    /// <summary>C:\ with programs, their data and caches, a Store app, leftovers and Windows.</summary>
    internal static DriveNode BuildDrive()
    {
        var c = new DriveNode("C:\\") { IsDrive = true, Enumerated = true, ScanComplete = true, TotalBytes = 1L << 40, FreeBytes = 1L << 30 };
        var steam = Path(c, @"Program Files (x86)\Steam");
        File(steam, "steam.exe", 100 * MB, Old);
        File(Path(steam, @"steamapps\common\Alba"), "alba.pak", 600 * MB, Old);
        File(Path(steam, @"steamapps\workshop"), "w.bin", 50 * MB, Old);
        File(Path(c, @"Program Files\Google\Chrome\Application"), "chrome.exe", 300 * MB, Old);
        var profile = Path(c, @"Users\Ali\AppData");
        File(Path(profile, @"Local\Google\Chrome\User Data\Default\Cache"), "f_000001", 200 * MB);
        File(Path(profile, @"Local\Google\Chrome\User Data\Default"), "History", 5 * MB, new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc));
        File(Path(profile, @"Roaming\Spotify"), "Spotify.exe", 80 * MB, Old);
        File(Path(profile, @"Local\Spotify\Storage"), "blob", 400 * MB);
        File(Path(profile, @"Local\Packages\Claude_pzs8sxrjxfjjc\LocalCache"), "x", 50 * MB);
        File(Path(profile, @"Roaming\Claude"), "y", 20 * MB);
        File(Path(profile, @"Roaming\Notion"), "z", 30 * MB, Old);
        File(Path(profile, @"Local\Android\Sdk\platforms"), "android.jar", 90 * MB);
        File(Path(profile, @"Local\tiny"), "t", 100 * 1024);
        File(Path(profile, @"Local\Temp"), "setup.tmp", 70 * MB);
        File(Path(c, @"Windows\System32"), "big.dll", 1024 * MB);
        return c;
    }

    [Fact]
    public void Footprint_GroupsProgramFoldersCarvesNestedOnesAndCountsCaches()
    {
        var c = BuildDrive();
        var r = AppFootprint.Compute(c, Catalog(), n => n.DiskSize, CancellationToken.None);
        foreach (var u in r.Apps) _out.WriteLine($"{u.App.Name}: {u.Bytes / MB} MB [{string.Join(", ", u.Pieces.Select(p => p.Kind + " " + p.Bytes / MB))}] reclaim {u.ReclaimableBytes / MB}");
        AppUsage U(string name) => r.Apps.Single(a => a.App.Name == name);

        Assert.True(r.IsWholeDrive);
        Assert.Equal("C:", r.RootPath);
        var steam = U("Steam");
        Assert.Equal(150 * MB, steam.Bytes);                          // the game is carved out
        var alba = U("Alba: A Wildlife Adventure");
        Assert.Equal(600 * MB, alba.Bytes);
        Assert.Same(steam.Pieces[0], alba.Pieces[0].Parent);
        var chrome = U("Google Chrome");
        Assert.Equal(505 * MB, chrome.Bytes);
        Assert.Equal(300 * MB, chrome.ProgramBytes);
        Assert.Equal(200 * MB, chrome.ReclaimableBytes);             // the knowledge base's Chrome cache
        Assert.Equal(new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc), chrome.LastDataWrite);
        var spotify = U("Spotify");
        Assert.Equal(480 * MB, spotify.Bytes);
        Assert.Equal(80 * MB, spotify.ProgramBytes);                 // installed per user into Roaming: still its program folder
        Assert.Equal(400 * MB, spotify.ReclaimableBytes);
        Assert.Equal(70 * MB, U("Claude").Bytes);
        Assert.Equal(2, U("Claude").Pieces.Count);
        Assert.Equal(r.Apps.Sum(a => a.Bytes), r.AppBytes);
        Assert.Equal(r.Apps.OrderByDescending(a => a.Bytes).Select(a => a.App.Name), r.Apps.Select(a => a.App.Name));
        // not linked: leftovers above the threshold with what is known about them; Temp and Windows are no candidates
        Assert.Equal(new[] { @"C:\Users\Ali\AppData\Local\Android", @"C:\Users\Ali\AppData\Roaming\Notion" }, r.Unlinked.Select(p => p.Path));
        Assert.Equal("Contains Android SDK", r.Unlinked[0].Note);
        Assert.Equal(Old, r.Unlinked[1].LastWrite);
        Assert.Equal(120 * MB, r.UnlinkedBytes);
        // map highlight: Steam's folder in, the game carved out of it out
        var spot = r.SpotlightFor(steam.Pieces);
        Assert.True(spot[steam.Pieces[0].Node]);
        Assert.False(spot[alba.Pieces[0].Node]);
        var text = AppFootprint.ToText(r);
        Assert.Contains("Steam\tValve Corporation\tDesktop\t150.0MB", text);
        Assert.Contains("Not linked to an installed program", text);
        Assert.Contains("Contains Android SDK", text);
    }

    [Fact]
    public void Footprint_ViewInsideAProgramFolder_AndSnapshotFrame()
    {
        // a view opened on a cache folder belongs to its program as a whole
        var storage = new DriveNode(@"C:\Users\Ali\AppData\Local\Spotify\Storage\") { Enumerated = true, ScanComplete = true };
        File(storage, "blob", 10 * MB);
        var r = AppFootprint.Compute(storage, Catalog(), n => n.DiskSize, CancellationToken.None);
        var u = Assert.Single(r.Apps);
        Assert.Equal("Spotify", u.App.Name);
        Assert.Equal(PieceKind.LocalData, u.Pieces[0].Kind);
        Assert.Equal(10 * MB, u.ReclaimableBytes);
        Assert.False(r.IsWholeDrive);

        // a loaded snapshot: paths start after the "[SNAPSHOT: file]" frame
        var content = BuildDrive();
        content.IsDrive = false;
        var frame = new DriveNode("[SNAPSHOT: C:\\snaps\\may.sns]") { IsSnapshotFrame = true, Enumerated = true, ScanComplete = true };
        frame.AddChild(content);
        var rf = AppFootprint.Compute(frame, Catalog(), n => n.DiskSize, CancellationToken.None);
        Assert.Equal("C:", rf.RootPath);
        Assert.True(rf.IsWholeDrive);
        Assert.Equal(150 * MB, rf.Apps.Single(a => a.App.Name == "Steam").Bytes);
        Assert.Equal(2, rf.Unlinked.Count);
    }

    [Fact]
    public void ViewModel_Spotlight_FollowsNearestAncestor()
    {
        var c = BuildDrive();
        var tree = new Core.Scanning.ScanTree(c, live: false);
        var vm = new ViewModel(tree, c);
        var r = AppFootprint.Compute(c, Catalog(), n => n.DiskSize, CancellationToken.None);
        var steam = r.Apps.Single(a => a.App.Name == "Steam");
        Assert.True(vm.InSpotlight(c));                               // no spotlight: everything is in
        vm.Spotlight = r.SpotlightFor(steam.Pieces);
        var steamNode = (ContainerNode)steam.Pieces[0].Node;
        Assert.True(vm.InSpotlight(steamNode.FindChild("steam.exe")!));
        Assert.True(vm.InSpotlight(Path(steamNode, @"steamapps\workshop")));
        Assert.False(vm.InSpotlight(Path(steamNode, @"steamapps\common\Alba")));
        Assert.False(vm.InSpotlight(Path(c, @"Windows\System32")));
        vm.Spotlight = null;
        Assert.True(vm.InSpotlight(Path(c, @"Windows\System32")));
    }

    [Fact]
    public void Footprint_IsFastAndSkipsUnownedDepths()
    {
        var c = new DriveNode("C:\\") { IsDrive = true, Enumerated = true, ScanComplete = true };
        var local = Path(c, @"Users\Ali\AppData\Local");
        for (int a = 0; a < 1500; a++)
        {
            var app = Folder(local, a % 3 == 0 ? "Spotify" + (a == 0 ? "" : a.ToString()) : "tool" + a);
            for (int s = 0; s < 20; s++) { var sub = Folder(app, "s" + s); for (int f = 0; f < 5; f++) File(sub, "f" + f, 20_000); }
        }
        // a huge unowned tree: nothing can be claimed below the match depth, it must not be walked file by file
        var win = Folder(c, "Windows");
        for (int d = 0; d < 400; d++) { var x = Path(win, $@"WinSxS\p{d}\a\b\c\d\e\f"); for (int f = 0; f < 100; f++) File(x, "f" + f, 10); }
        int nodes = c.FileCount + c.FolderCount;
        var catalog = Catalog();
        AppFootprint.Compute(c, catalog, n => n.DiskSize, CancellationToken.None);   // warm up
        var sw = Stopwatch.StartNew();
        var r = AppFootprint.Compute(c, catalog, n => n.DiskSize, CancellationToken.None, sync: new object());
        sw.Stop();
        _out.WriteLine($"{nodes:N0} nodes: {sw.ElapsedMilliseconds} ms, {r.Apps.Count} programs, {r.Unlinked.Count} unlinked");
        Assert.Equal("Spotify", Assert.Single(r.Apps).App.Name);
        Assert.Equal(1499, r.Unlinked.Count);                         // "toolN" and "SpotifyN" folders: the latter are not Spotify's
        Assert.True(sw.ElapsedMilliseconds < 3000, "footprint took " + sw.ElapsedMilliseconds + " ms");
        // cancellation is honoured
        var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => AppFootprint.Compute(c, catalog, n => n.DiskSize, cts.Token));
    }

    [Fact]
    public void MachineCatalog_LoadsWithoutErrors()
    {
        var sw = Stopwatch.StartNew();
        var c = AppCatalog.LoadFromSystem();
        _out.WriteLine($"{c.Apps.Count} programs ({c.Apps.Count(a => a.Source == AppSource.Store)} Store, {c.Apps.Count(a => a.IsComponent)} components) in {sw.ElapsedMilliseconds} ms");
        Assert.All(c.Apps, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
        Assert.All(c.Apps.SelectMany(a => a.InstallDirs), d => Assert.False(d.EndsWith('\\') || d.StartsWith('"')));
        // everything the matcher needs works on the real catalog
        Assert.Null(c.Match(@"C:\Windows\System32"));
        Assert.Null(c.OwnerOf(@"C:\Users\Nobody\AppData\Local\Temp\x.tmp"));
    }

    [Fact]
    public void CommandLine_AppsOptions()
    {
        var (req, _, _) = CommandLine.Parse(new[] { "scan", "C:\\", "apps", "appsreport", "r.txt", "scan", "D:\\" });
        Assert.True(req[0].ShowApps);
        Assert.Equal("r.txt", req[0].AppsReport);
        Assert.False(req[1].ShowApps);
        Assert.Equal("", req[1].AppsReport);
    }
}
