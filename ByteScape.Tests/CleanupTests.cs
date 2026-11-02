using System.Diagnostics;
using ByteScape.Core.Apps;
using ByteScape.Core.Cleanup;
using ByteScape.Core.Config;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using Xunit;
using Xunit.Abstractions;

namespace ByteScape.Tests;

/// <summary>"Is it safe to delete?", protection, rights, deleting (Recycle Bin and permanent) and the cleanup categories.</summary>
[Collection("config")]
public class CleanupTests
{
    private readonly ITestOutputHelper _out;
    public CleanupTests(ITestOutputHelper o) => _out = o;

    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    internal static SafetyEnvironment Env => new()
    {
        WindowsDir = @"X:\Windows", ProgramFiles = @"X:\Program Files", ProgramFilesX86 = @"X:\Program Files (x86)", ProgramData = @"X:\ProgramData",
        UsersDir = @"X:\Users", OwnDirs = new[] { @"X:\Tools\ByteScape" },
    };

    private static AppCatalog Catalog() => AppCatalog.FromEntries(new[]
    {
        new UninstallEntry("foo", "Foo App") { Publisher = "Foo Inc", InstallLocation = @"X:\Program Files\Foo App", UninstallString = "\"X:\\Program Files\\Foo App\\unins000.exe\" /SILENT", InstallDate = "20200101" },
        new UninstallEntry("bar", "Bar Studio") { Publisher = "Bar Ltd", InstallLocation = @"X:\Program Files\Bar Studio", InstallDate = "20200101" },
        new UninstallEntry("baz", "Baz Tool") { InstallLocation = @"X:\Program Files\Baz Tool", InstallDate = Now.AddDays(-10).ToString("yyyyMMdd") },
        new UninstallEntry("qux", "Qux CLI") { InstallLocation = @"X:\Program Files\Qux CLI", InstallDate = "20190101" },
    }, Array.Empty<StorePackage>());

    private static SafetyContext Ctx(bool protect = true, AppCatalog? catalog = null) => new() { Environment = Env, NowUtc = Now, ProtectSystem = protect, Catalog = catalog };

    /// <summary>A tree in memory: nodes by relative path ("Windows\System32\"), folders end with a backslash.</summary>
    internal sealed class Fake
    {
        public readonly DriveNode Root = new(@"X:\") { IsDrive = true, Enumerated = true };
        private long _id = 100;

        public FolderNode Dir(string rel)
        {
            ContainerNode cur = Root;
            foreach (var part in rel.Trim('\\').Split('\\'))
            {
                var next = cur.FindChild(part) as FolderNode;
                if (next == null) { next = new FolderNode(part) { Enumerated = true, FileId = ++_id, Attributes = FileAttributes.Directory }; cur.AddChild(next); }
                cur = next;
            }
            return (FolderNode)cur;
        }

        public FileNode File(string rel, long size = 1000, DateTime? modified = null, FileAttributes attrs = FileAttributes.Archive)
        {
            int i = rel.LastIndexOf('\\');
            ContainerNode parent = i < 0 ? Root : Dir(rel.Substring(0, i));
            var f = new FileNode(rel.Substring(i + 1)) { LogicalSize = size, DiskSize = size, Modified = modified ?? Now.AddDays(-100), Created = Now.AddDays(-200), Attributes = attrs, FileId = ++_id };
            parent.AddChild(f);
            return f;
        }
    }

    private void Show(Assessment a) => _out.WriteLine($"{a.LevelText,-16} {a.Path}  —  {a.Headline}" + string.Concat(a.Warnings.Select(w => "\n                 [" + w.Level + "] " + w.Text)));

    [Fact]
    public void Protection_KeepsWhatWindowsNeeds_AndExplainsTheRest()
    {
        var t = new Fake();
        var cat = Catalog();
        Assessment A(FsNode n, bool protect = true) { var a = DeleteSafety.Assess(n, Ctx(protect, cat)); Show(a); return a; }

        // always protected, whatever the settings say
        foreach (var n in new FsNode[]
                 {
                     t.Root, t.Dir(@"Windows"), t.File(@"Windows\System32\kernel32.dll"), t.Dir(@"Program Files"), t.Dir(@"ProgramData"), t.Dir(@"Users"), t.Dir(@"Users\ali"),
                     t.File("pagefile.sys", 4L << 30), t.Dir("System Volume Information"), t.File(@"$Recycle.Bin\S-1-5-21-1\$R1.txt"), t.Dir(@"Program Files\WindowsApps\Foo_1.0_x64"),
                     t.Dir(@"Tools\ByteScape"), t.Dir(@"Tools"),
                 })
        {
            var a = A(n, protect: false);
            Assert.True(a.Blocked && a.AlwaysProtected, a.Path);
        }
        Assert.False(A(t.File(@"Tools\notes.txt")).Blocked);

        // Windows' own cleanup areas inside the Windows folder are not locked: Temp keeps its folder, gives its content
        var winTemp = A(t.Dir(@"Windows\Temp"));
        Assert.False(winTemp.Blocked);
        Assert.True(winTemp.ContentOnly);
        Assert.Equal(SafetyLevel.Safe, winTemp.Level);
        Assert.False(A(t.Dir(@"Windows\SoftwareDistribution\Download")).Blocked);
        Assert.True(A(t.Dir(@"Windows\WinSxS")).Blocked);

        // protected unless the settings say otherwise
        var docs = t.Dir(@"Users\ali\Documents");
        Assert.True(A(docs).Blocked);
        var unlocked = A(docs, protect: false);
        Assert.False(unlocked.Blocked);
        Assert.Contains(unlocked.Warnings, w => w.Level == WarningLevel.Serious);
        Assert.True(A(t.Dir(@"Users\ali\AppData\Local")).Blocked);
        Assert.True(A(t.Dir(@"ProgramData\Microsoft\Windows\Start Menu")).Blocked);
        // ...but where the knowledge base knows the place (crash reports), its verdict counts
        Assert.Equal(SafetyLevel.Safe, A(t.Dir(@"ProgramData\Microsoft\Windows\WER")).Level);
        Assert.True(A(t.File(@"Data\desktop.ini", 100, null, FileAttributes.Hidden | FileAttributes.System)).Blocked);
        var oldWin = t.Dir(@"Old\Windows"); t.Dir(@"Old\Windows\System32");
        Assert.True(A(oldWin).Blocked);
        var program = A(t.Dir(@"Program Files\Foo App"));
        Assert.True(program.Blocked && program.IsProgramFolder);
        Assert.Contains("Uninstall", program.Cleanup);
        // a folder holding a protected location
        var backup = t.Dir(@"Backup\Win"); t.Dir(@"Backup\Win\Windows\WinSxS");
        Assert.Equal(SafetyLevel.Protected, A(t.Dir(@"Backup")).Level);

        // explained, not blocked
        var dll = A(t.File(@"Program Files\Foo App\foo.dll"));
        Assert.Equal(SafetyLevel.Caution, dll.Level);
        Assert.Contains("Foo App", dll.Headline);
        Assert.Equal(SafetyLevel.Caution, A(t.Dir(@"Program Files\Leftover")).Level);
        var roaming = A(t.Dir(@"Users\ali\AppData\Roaming\Foo App"));
        Assert.Equal(SafetyLevel.Caution, roaming.Level);
        Assert.Contains("Foo App", roaming.Headline);
        Assert.Equal(SafetyLevel.Personal, A(t.File(@"Users\ali\Documents\report.docx")).Level);
        var temp = A(t.Dir(@"Users\ali\AppData\Local\Temp"));
        Assert.True(temp.ContentOnly && temp.Level == SafetyLevel.Safe);
        Assert.Equal(SafetyLevel.Safe, A(t.File(@"Users\ali\AppData\Local\Temp\x.tmp")).Level);
        Assert.Equal(SafetyLevel.SafeWhenClosed, A(t.Dir(@"Users\ali\AppData\Local\Google\Chrome\User Data\Default\Cache")).Level);
        Assert.Equal(SafetyLevel.Safe, A(t.File(@"Users\ali\Downloads\python-3.12.1-amd64.exe")).Level);
        Assert.Equal(SafetyLevel.Safe, A(t.File(@"Users\ali\Downloads\movie.mp4.crdownload")).Level);
        Assert.Equal(SafetyLevel.Caution, A(t.File(@"Users\ali\Downloads\contract.pdf")).Level);

        // what a file is
        var wallet = A(t.File(@"Data\wallet.dat"));
        Assert.Equal(SafetyLevel.Personal, wallet.Level);
        Assert.Contains(wallet.Warnings, w => w.Level == WarningLevel.Serious);
        Assert.True(wallet.NeedsConfirmation);
        Assert.Contains(A(t.File(@"Data\disk.vhdx", 10L << 30)).Warnings, w => w.Level == WarningLevel.Serious);
        Assert.Contains(A(t.File(@"Data\live.log", 10, Now.AddHours(-1))).Warnings, w => w.Text.Contains("still be using it"));
        Assert.Equal(SafetyLevel.Unknown, A(t.File(@"Data\thing.xyz")).Level);

        // what a folder holds
        t.File(@"Data\Photos\a.jpg"); t.File(@"Data\Photos\b.jpg"); t.File(@"Data\Photos\c.png");
        var photos = A(t.Dir(@"Data\Photos"));
        Assert.Equal(SafetyLevel.Personal, photos.Level);
        Assert.Contains(photos.Warnings, w => w.Text.Contains("3 photos"));
        t.File(@"Data\app\app.exe"); t.File(@"Data\app\lib.dll");
        Assert.Contains("program", A(t.Dir(@"Data\app")).Headline);
        t.File(@"Data\junk\a.tmp"); t.File(@"Data\junk\b.log");
        Assert.Equal(SafetyLevel.Safe, A(t.Dir(@"Data\junk")).Level);

        // hard links: another name holds the space
        var owner = t.File(@"Data\links\one.bin", 50_000);
        var shadow = t.File(@"Data\links\two.bin", 50_000);
        shadow.FileId = owner.FileId; shadow.CountedElsewhere = true;
        Assert.Contains(A(shadow).Warnings, w => w.Text.Contains("frees no space"));
    }

    [Fact]
    public void Plan_DropsNestedChoices_EmptiesTemp_KeepsRecentFiles_AndLeavesProtectedOut()
    {
        var t = new Fake();
        var temp = t.Dir(@"Users\ali\AppData\Local\Temp");
        var oldFile = t.File(@"Users\ali\AppData\Local\Temp\old.tmp", 5000, Now.AddDays(-5));
        t.File(@"Users\ali\AppData\Local\Temp\fresh.tmp", 7000, Now.AddHours(-2));
        t.File(@"Users\ali\AppData\Local\Temp\sub\older.log", 3000, Now.AddDays(-9));
        t.File(@"Users\ali\AppData\Local\Temp\busy\new.dat", 3000, Now.AddMinutes(-5));
        var doc = t.File(@"Data\a.docx", 1234);
        var plan = DeletePlan.Build(new FsNode[] { temp, oldFile, t.Dir("Windows"), doc, doc }, Ctx());
        Assert.Equal(3, plan.Items.Count);                                     // old.tmp goes with Temp, the document once
        var tempItem = plan.Items.Single(i => ReferenceEquals(i.Node, temp));
        Assert.Equal(new[] { "old.tmp", "sub" }, tempItem.Targets.Select(x => x.Node.Name).OrderBy(x => x));
        Assert.Equal(2, tempItem.KeptRecent);
        Assert.Equal(8000, tempItem.Bytes);
        Assert.Single(plan.Protected);
        Assert.Equal(2, plan.Deletable.Count());
        Assert.True(plan.NeedsConfirmation);                                   // the document is "your own file"
        Assert.DoesNotContain(plan.TargetsOf(), x => x.Path.StartsWith(@"X:\Windows", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ real files (test data in %TEMP% only)

    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "cleanup");
    private static readonly string Outside = Path.Combine(Path.GetTempPath(), "bytescape-tests", "cleanup-outside");

    private static void Clean()
    {
        foreach (var d in new[] { Dir, Outside })
        {
            if (!Directory.Exists(d)) continue;
            foreach (var f in Directory.EnumerateFiles(d, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            try { Directory.Delete(d, true); } catch { }
        }
    }

    private static void Junction(string link, string target)
    {
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "junction");
    }

    [Fact]
    public void Permanent_DeletesReadOnlyFiles_LinksWithoutTheirTarget_LongPaths_AndSaysWhatIsOpen()
    {
        try
        {
            Clean();
            Directory.CreateDirectory(Dir);
            Directory.CreateDirectory(Outside);
            File.WriteAllText(Path.Combine(Outside, "keep.txt"), "must survive");
            var ro = Path.Combine(Dir, "folder", "readonly.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(ro)!);
            File.WriteAllText(ro, "x");
            File.SetAttributes(ro, FileAttributes.ReadOnly);
            Junction(Path.Combine(Dir, "folder", "link"), Outside);
            var deep = Path.Combine(Dir, "long", string.Join("\\", Enumerable.Repeat(new string('d', 60), 5)));
            Directory.CreateDirectory(deep);
            File.WriteAllText(Path.Combine(deep, "file.txt"), "long");
            Assert.True(Path.Combine(deep, "file.txt").Length > 300);
            var open = Path.Combine(Dir, "open.txt");
            File.WriteAllText(open, "busy");
            using (var hold = new FileStream(open, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Equal(AccessState.InUse, AccessProbe.Probe(open));
                var r = Deleter.RunAsync(new[] { (Path.Combine(Dir, "folder"), 1L), (Path.Combine(Dir, "long"), 2L), (open, 3L) }, recycle: false, IntPtr.Zero, null, CancellationToken.None).Result;
                foreach (var i in r.Items) _out.WriteLine($"{i.Deleted} {i.Path} {i.Error}");
                Assert.True(r.Items[0].Deleted);
                Assert.True(r.Items[1].Deleted);
                Assert.False(r.Items[2].Deleted);
                Assert.Equal("open in a program", r.Items[2].Error);
                Assert.Equal(3, r.DeletedBytes);
            }
            Assert.True(File.Exists(Path.Combine(Outside, "keep.txt")));      // the junction's target is untouched
            Assert.False(Directory.Exists(Path.Combine(Dir, "folder")));
            Assert.Equal(AccessState.Ok, AccessProbe.Probe(open));
            Assert.Equal(AccessState.Missing, AccessProbe.Probe(Path.Combine(Dir, "nothing-here")));
        }
        finally { Clean(); }
    }

    [Fact]
    public void AccessProbe_TellsAdministratorRights_FromWhatWindowsProtects()
    {
        // a Windows file: owned by TrustedInstaller, even administrators may not delete it
        var kernel = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");
        Assert.Equal(AccessState.ProtectedByWindows, AccessProbe.Probe(kernel));
        Assert.True(AccessProbe.OwnerIsTrustedInstaller(kernel));
        if (AccessProbe.IsElevated) return;
        // a file only administrators may delete: the parent denies deleting children, the file denies DELETE, both for this user
        try
        {
            Clean();
            var locked = Path.Combine(Dir, "locked");
            Directory.CreateDirectory(locked);
            var f = Path.Combine(locked, "admin-only.txt");
            File.WriteAllText(f, "x");
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            try
            {
                Icacls($"\"{f}\" /deny *{sid}:(D)");
                Icacls($"\"{locked}\" /deny *{sid}:(DC)");
                Assert.Equal(AccessState.NeedsAdmin, AccessProbe.Probe(f));
                // the same found by looking at the folder's content (a scanned tree)
                var tree = new ScanTree(DataCache.CreateRoot(Dir), live: true);
                tree.StartMasterScan();
                var sw = Stopwatch.StartNew();
                while (tree.IsMasterScanning && sw.ElapsedMilliseconds < 10000) Thread.Sleep(10);
                var s = AccessProbe.ProbeTree(tree.Root, Dir, tree.Sync, 100, CancellationToken.None);
                Assert.True(s.Complete);
                Assert.Equal(1, s.NeedsAdminCount);
                Assert.Equal(f, s.NeedsAdmin.Single());
            }
            finally
            {
                Icacls($"\"{locked}\" /remove:d *{sid}", check: false);
                Icacls($"\"{f}\" /remove:d *{sid}", check: false);
            }
        }
        finally { Clean(); }

        static void Icacls(string args, bool check = true)
        {
            var p = Process.Start(new ProcessStartInfo("icacls.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (check) Assert.Equal(0, p.ExitCode);
        }
    }

    [Fact]
    public void Recycle_MovesAFileToTheRecycleBin()
    {
        var bin = RecycleBin.For(Path.GetTempPath());
        _out.WriteLine($"Recycle Bin of the temp drive: {bin}");
        if (!bin.Available) return;
        Assert.True(bin.MaxBytes > 0);
        var binFolder = RecycleBin.FolderOf(Path.GetTempPath())!;
        string? info = null;
        try
        {
            Clean();
            Directory.CreateDirectory(Dir);
            var f = Path.Combine(Dir, "bytescape-recycle-test-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(f, "a test file ByteScape's tests put in the Recycle Bin and take out again");
            var r = Deleter.RunAsync(new[] { (f, 100L) }, recycle: true, IntPtr.Zero, null, CancellationToken.None).Result;
            Assert.True(r.Items[0].Deleted, r.Items[0].Error);
            Assert.False(File.Exists(f));
            // Windows' record of it ($I file: version, size, time, then the original path)
            info = Directory.EnumerateFiles(binFolder, "$I*").FirstOrDefault(i => { try { return System.Text.Encoding.Unicode.GetString(File.ReadAllBytes(i)).Contains(Path.GetFileName(f)); } catch { return false; } });
            Assert.NotNull(info);
        }
        finally
        {
            // take our own test file out of the Recycle Bin again (its $I record and its $R data)
            if (info != null)
            {
                try { File.Delete(Path.Combine(binFolder, "$R" + Path.GetFileName(info).Substring(2))); } catch { }
                try { File.Delete(info); } catch { }
            }
            Clean();
        }
    }

    // ------------------------------------------------------------------ cleanup categories

    [Fact]
    public void Categories_FindLargeTemporaryAndInstallationFiles_AndUnusedPrograms()
    {
        var t = new Fake();
        t.File(@"Users\ali\Videos\holiday.mkv", 3L << 30);
        t.File("pagefile.sys", 8L << 30);
        t.File(@"Users\ali\AppData\Local\Temp\old1.tmp", 200 << 20, Now.AddDays(-4));
        t.File(@"Users\ali\AppData\Local\Temp\fresh.tmp", 50 << 20, Now.AddHours(-1));
        t.File(@"Data\stray.tmp", 3 << 20, Now.AddDays(-20));
        t.File(@"Data\sys.tmp", 5 << 20, Now.AddDays(-20), FileAttributes.Hidden | FileAttributes.System);   // protected: not offered
        t.File(@"Users\ali\Documents\proj\.venv\Scripts\pywin32_postinstall.exe", 1 << 20, Now.AddDays(-90));   // part of a tool, not a download
        t.File(@"Data\half.crdownload", 4 << 20, Now.AddDays(-20));
        t.File(@"Data\new.tmp", 4 << 20, Now.AddHours(-3));
        t.File(@"Users\ali\Downloads\setup-old.exe", 90 << 20, Now.AddDays(-60));
        t.File(@"Users\ali\Downloads\tool-new.msi", 20 << 20, Now.AddDays(-5));
        t.File(@"Users\ali\Downloads\win.iso", 700L << 20, Now.AddDays(-90));
        t.File(@"$WINDOWS.~BT\Sources\install.esd", 2L << 30);
        t.File(@"ProgramData\Package Cache\{x}\vs.msi", 300 << 20);
        t.File(@"NVIDIA\DisplayDriver\setup.exe", 600 << 20);
        t.File(@"$Recycle.Bin\S-1-5-21-1\$R1.bin", 1L << 30);
        t.File(@"Program Files\Foo App\foo.exe", 80 << 20);
        t.File(@"Users\ali\AppData\Roaming\Foo App\settings.json", 1 << 20, Now.AddDays(-400));
        t.File(@"Program Files\Bar Studio\bar.exe", 90 << 20);
        t.File(@"Program Files\Baz Tool\baz.exe", 10 << 20);
        t.File(@"Program Files\Qux CLI\qux.exe", 30 << 20);                   // a command line tool: no record of use, not "unused"
        var catalog = Catalog();
        var footprint = AppFootprint.Compute(t.Root, catalog, n => n.DiskSize, CancellationToken.None);
        static byte[] Rec(int count, DateTime last) { var b = new byte[72]; BitConverter.GetBytes(count).CopyTo(b, 4); BitConverter.GetBytes(last.ToFileTimeUtc()).CopyTo(b, 60); return b; }
        var starts = AppLastUse.FromValues(new[]
        {
            (@"X:\Program Files\Foo App\foo.exe", Rec(3, Now.AddDays(-300))),
            (@"X:\Program Files\Bar Studio\bar.exe", Rec(40, Now.AddDays(-2))),
        });
        var r = CleanupFinder.Find(t.Root, Ctx(catalog: catalog), new CleanupOptions { NowUtc = Now }, footprint, starts, CancellationToken.None);
        _out.WriteLine(CleanupFinder.ToText(r));

        var large = r.Of(CleanupCategory.LargeFiles);
        Assert.Contains(large, i => i.Title == "holiday.mkv" && i.Level == SafetyLevel.Personal && !i.Preselected);
        Assert.DoesNotContain(large, i => i.Title == "pagefile.sys");
        Assert.Contains(r.Notes, n => n.Contains("page file"));

        var temp = r.Of(CleanupCategory.TemporaryFiles);
        var tempFolder = temp.Single(i => i.Title == "Temporary files");
        Assert.Equal(200L << 20, tempFolder.Bytes);                           // the fresh one stays
        Assert.True(tempFolder.Preselected);
        Assert.Contains("changed in the last day", tempFolder.Detail);
        var stray = temp.Single(i => i.Title == "Leftover temporary files");
        Assert.Equal(3L << 20, stray.Bytes);
        Assert.Single(stray.Nodes);
        Assert.Contains(temp, i => i.Title == "Unfinished downloads" && i.Bytes == 4L << 20);
        var recycled = temp.Single(i => i.Title == "Recycle Bin");
        Assert.False(recycled.CanDelete);
        Assert.Equal("shell:RecycleBinFolder", recycled.WindowsAction);

        var inst = r.Of(CleanupCategory.OldInstallers);
        Assert.Contains(inst, i => i.Title == "setup-old.exe" && i.Level == SafetyLevel.Safe && i.Preselected);
        Assert.DoesNotContain(inst, i => i.Title == "tool-new.msi");           // five days old
        Assert.Contains(inst, i => i.Title == "win.iso");
        Assert.DoesNotContain(inst, i => i.Title == "pywin32_postinstall.exe");
        Assert.Contains(inst, i => i.Path == @"X:\$WINDOWS.~BT" && i.WindowsAction == "ms-settings:storagesense" && !i.Preselected);
        Assert.Contains(inst, i => i.Title == "Installer package cache" && i.Level == SafetyLevel.Caution && !i.Preselected);
        Assert.Contains(inst, i => i.Title == "NVIDIA driver installer");

        var progs = r.Of(CleanupCategory.UnusedPrograms);
        var foo = Assert.Single(progs);
        Assert.Equal("Foo App", foo.Title);
        Assert.Contains("last started", foo.WhenText);
        Assert.Equal((80L << 20) + (1L << 20), foo.Bytes);                    // the program and its data
        Assert.False(foo.CanDelete);                                           // uninstalled, never deleted
    }

    [Fact]
    public void LastUse_DecodesWindowsRecordOfProgramStarts()
    {
        Assert.Equal("Zvpebfbsg", AppLastUse.Rot13("Microsoft"));
        Assert.Equal("Microsoft", AppLastUse.Rot13(AppLastUse.Rot13("Microsoft")));
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.Equal(pf + @"\Mozilla Firefox\firefox.exe", AppLastUse.ExpandKnownFolder(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Mozilla Firefox\firefox.exe"));
        Assert.Equal("not a path", AppLastUse.ExpandKnownFolder("not a path"));
        static byte[] Rec(DateTime last) { var b = new byte[72]; BitConverter.GetBytes(1).CopyTo(b, 4); BitConverter.GetBytes(last.ToFileTimeUtc()).CopyTo(b, 60); return b; }
        var u = AppLastUse.FromValues(new[]
        {
            (@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Foo App.lnk", Rec(Now.AddDays(-7))),
            ("Contoso.Notes_4a5b6c7d8e9f0!App", Rec(Now.AddDays(-3))),
            ("UEME_CTLSESSION", new byte[1612]),
        });
        Assert.Equal(2, u.Entries);
        var catalog = AppCatalog.FromEntries(new[] { new UninstallEntry("foo", "Foo App") { InstallLocation = @"X:\Program Files\Foo App" } },
            new[] { new StorePackage("Contoso.Notes_1.0.0.0_x64__4a5b6c7d8e9f0", "Contoso Notes") { RootFolder = @"C:\Program Files\WindowsApps\Contoso.Notes_1.0.0.0_x64__4a5b6c7d8e9f0" } });
        Assert.Equal(Now.AddDays(-7), u.LastRun(catalog.Apps.Single(a => a.Name == "Foo App"))!.Value.When);
        Assert.Equal(Now.AddDays(-3), u.LastRun(catalog.Apps.Single(a => a.Name == "Contoso Notes"))!.Value.When);
        // the real record of this account can be read (it may be empty when app tracking is off)
        _out.WriteLine("Program starts remembered by Windows here: " + AppLastUse.FromRegistry().Entries);
    }

    [Fact]
    public void Uninstaller_CommandLines_AreSplitIntoProgramAndArguments()
    {
        Assert.Equal((@"C:\Program Files\Foo\unins000.exe", "/SILENT"), Wpf.Views.ScanView.SplitCommand("\"C:\\Program Files\\Foo\\unins000.exe\" /SILENT"));
        Assert.Equal(("MsiExec.exe", "/X{1234}"), Wpf.Views.ScanView.SplitCommand("MsiExec.exe /X{1234}"));
        Assert.Equal((@"C:\Tools\uninst.exe", ""), Wpf.Views.ScanView.SplitCommand(@"C:\Tools\uninst.exe"));
    }

    [Fact]
    public void Config_KeepsTheDeleteAndCleanupSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), "bytescape-tests", "cleanup-config.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var c = new AppConfig { DeleteToRecycleBin = false, ProtectSystemFolders = false, LargeFileMB = 250, OldInstallerDays = 60, UnusedProgramDays = 365 };
        Assert.True(AppConfig.Save(c, path));
        var old = AppConfig.Current;
        try
        {
            AppConfig.Load(path);
            var l = AppConfig.Current;
            Assert.False(l.DeleteToRecycleBin); Assert.False(l.ProtectSystemFolders);
            Assert.Equal(250, l.LargeFileMB); Assert.Equal(60, l.OldInstallerDays); Assert.Equal(365, l.UnusedProgramDays);
            var o = CleanupOptions.FromConfig(l);
            Assert.Equal(250L << 20, o.LargeFileBytes);
        }
        finally { AppConfig.Replace(old); File.Delete(path); }
    }
}
