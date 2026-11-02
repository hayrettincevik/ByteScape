using System.Runtime.InteropServices;
using ByteScape.Core.Cloud;
using ByteScape.Core.Config;
using ByteScape.Core.Filtering;
using ByteScape.Core.Knowledge;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;

namespace ByteScape.Tests;

/// <summary>Cloud "Files On-Demand" awareness: states, sync roots, totals, filters, plain language, and a real Cloud Files sync root.</summary>
[Collection("config")]
public class CloudTests
{
    private readonly ITestOutputHelper _out;
    public CloudTests(ITestOutputHelper o) => _out = o;

    private const long MB = 1 << 20;
    private static FolderNode Folder(ContainerNode p, string name, FileAttributes attrs = FileAttributes.Directory) { var f = new FolderNode(name) { Enumerated = true, ScanComplete = true, Attributes = attrs }; p.AddChild(f); return f; }
    private static FileNode File(ContainerNode p, string name, long logical, long disk, FileAttributes attrs = FileAttributes.Archive) { var f = new FileNode(name) { LogicalSize = logical, DiskSize = disk, Attributes = attrs }; p.AddChild(f); return f; }

    /// <summary>C:\Users\Ali with a OneDrive folder (the sync root shows up as a reparse point), Documents and a plain folder.</summary>
    private static (DriveNode c, FolderNode oneDrive, FileNode local, FileNode pinned, FileNode online, FileNode plain) Build()
    {
        var c = new DriveNode("C:\\") { IsDrive = true, Enumerated = true, ScanComplete = true };
        var ali = Folder(Folder(c, "Users"), "Ali");
        var od = Folder(ali, "OneDrive", FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.ReadOnly);
        var docs = Folder(od, "Documents");
        var local = File(docs, "report.docx", 3 * MB, 3 * MB);
        var pinned = File(docs, "keep.pdf", 2 * MB, 2 * MB, FileAttributes.Archive | CloudFiles.Pinned);
        var online = File(Folder(od, "Photos"), "trip.mp4", 900 * MB, 0, FileAttributes.Archive | CloudFiles.RecallOnDataAccess | CloudFiles.Unpinned);
        var plain = File(Folder(ali, "Documents"), "notes.txt", 1 * MB, 1 * MB);
        return (c, od, local, pinned, online, plain);
    }

    private static void WithProviders(Func<string, string?> byPath, IReadOnlyList<SyncRoot> known, Action body)
    {
        var old = CloudFiles.ProviderByPath;
        CloudFiles.ProviderByPath = byPath;
        CloudFiles.SetKnownRoots(known);
        try { body(); }
        finally { CloudFiles.ProviderByPath = old; CloudFiles.SetKnownRoots(Array.Empty<SyncRoot>()); }
    }

    private static string? OneDriveApi(string path) => path.StartsWith(@"C:\Users\Ali\OneDrive", StringComparison.OrdinalIgnoreCase) ? "OneDrive" : null;

    [Fact]
    public void States_ComeFromAttributesAndTheSyncRoot()
    {
        WithProviders(OneDriveApi, Array.Empty<SyncRoot>(), () =>
        {
            var (c, od, local, pinned, online, plain) = Build();
            Assert.Equal(CloudState.LocalCopy, CloudFiles.StateOf(local));
            Assert.Equal(CloudState.AlwaysKept, CloudFiles.StateOf(pinned));
            Assert.Equal(CloudState.OnlineOnly, CloudFiles.StateOf(online));
            Assert.Equal(CloudState.None, CloudFiles.StateOf(plain));
            Assert.Equal(CloudState.None, CloudFiles.StateOf(od));                 // folders have no state
            Assert.Equal("OneDrive", CloudFiles.SyncRootOf(local)?.Provider);
            Assert.Equal(@"C:\Users\Ali\OneDrive", CloudFiles.SyncRootOf(od)?.Path);
            Assert.Null(CloudFiles.SyncRootOf(plain));
            Assert.Null(CloudFiles.SyncRootOf(c));
        });
    }

    [Fact]
    public void SyncRoots_FromRegistry_TopOfView_AndSnapshotsOfOtherMachines()
    {
        // registered root (no reparse attribute needed), the API unavailable
        WithProviders(_ => null, new[] { new SyncRoot(@"C:\Users\Ali\OneDrive", "OneDrive - Personal") }, () =>
        {
            var (_, _, local, _, _, plain) = Build();
            Assert.Equal("OneDrive - Personal", CloudFiles.SyncRootOf(local)?.Provider);
            Assert.Null(CloudFiles.SyncRootOf(plain));
            // a view opened inside the cloud folder: the path of its root decides
            var inner = new DriveNode(@"C:\Users\Ali\OneDrive\Photos\") { Enumerated = true, ScanComplete = true };
            var f = File(inner, "a.jpg", MB, MB);
            Assert.Equal(CloudState.LocalCopy, CloudFiles.StateOf(f));
        });
        // snapshot of another machine: no registry, no API; the reparse folder's name tells the provider
        WithProviders(_ => null, Array.Empty<SyncRoot>(), () =>
        {
            var content = new DriveNode("D:\\") { Enumerated = true, ScanComplete = true };
            var db = Folder(Folder(Folder(content, "Users"), "Bob"), "Dropbox", FileAttributes.Directory | FileAttributes.ReparsePoint);
            var f = File(db, "x.zip", MB, MB);
            var notCloud = Folder(content, "Dropbox");                              // same name, not a reparse point
            var g = File(notCloud, "y.zip", MB, MB);
            var frame = new DriveNode("[SNAPSHOT: C:\\s.sns]") { IsSnapshotFrame = true, Enumerated = true, ScanComplete = true };
            frame.AddChild(content);
            Assert.Equal("Dropbox", CloudFiles.SyncRootOf(f)?.Provider);
            Assert.Equal(CloudState.LocalCopy, CloudFiles.StateOf(f));
            Assert.Equal(CloudState.None, CloudFiles.StateOf(g));
            Assert.Null(CloudFiles.SyncRootOf(frame));
        });
    }

    [Fact]
    public void Summary_TotalsPerCloudFolder()
    {
        WithProviders(OneDriveApi, Array.Empty<SyncRoot>(), () =>
        {
            var (c, od, _, _, _, _) = Build();
            var s = CloudFiles.Summarize(c, CancellationToken.None, new object());
            var r = Assert.Single(s.Roots);
            Assert.Same(od, r.Node);
            Assert.Equal((3 * MB, 1), (r.LocalBytes, r.LocalFiles));
            Assert.Equal((2 * MB, 1), (r.KeptBytes, r.KeptFiles));
            Assert.Equal((900 * MB, 1), (r.OnlineBytes, r.OnlineFiles));
            Assert.Equal("OneDrive", s.Providers);
            var docs = (ContainerNode)od.FindChild("Documents")!;
            var d = CloudFiles.CountIn(docs, r.Root, CancellationToken.None);
            Assert.Equal(3 * MB, d.LocalBytes);
            Assert.Equal(0, d.OnlineFiles);
            var plainDocs = (ContainerNode)((ContainerNode)((ContainerNode)c.FindChild("Users")!).FindChild("Ali")!).FindChild("Documents")!;
            Assert.Empty(CloudFiles.Summarize(plainDocs, CancellationToken.None).Roots);
        });
    }

    [Fact]
    public void FilterTokens_AndPlainLanguage()
    {
        WithProviders(OneDriveApi, Array.Empty<SyncRoot>(), () =>
        {
            var (_, _, local, pinned, online, plain) = Build();
            var all = new[] { local, pinned, online, plain };
            string[] Hits(string f) { var fl = new Filter(f); return all.Where(x => fl.Matches(x, Filter.FolderMaskState.Root, DateTime.UtcNow)).Select(x => x.Name).ToArray(); }
            Assert.Equal(new[] { "report.docx" }, Hits(":freeable"));
            Assert.Equal(new[] { "trip.mp4" }, Hits(":onlineonly"));
            Assert.Equal(new[] { "keep.pdf" }, Hits(":alwayskept"));
            Assert.Equal(new[] { "report.docx", "keep.pdf", "trip.mp4" }, Hits(":cloud"));
            Assert.Equal(new[] { "notes.txt" }, Hits("|:cloud"));
            Assert.Equal(new[] { "report.docx" }, Hits("*.docx;:cloud"));
            Assert.False(new Filter(":cloud").IsEmpty);
        });
        Assert.Equal(":freeable", NaturalFilter.Translate("OneDrive'da yer açılabilecek dosyalar").Filter);
        Assert.Equal(":freeable", NaturalFilter.Translate("cloud files I can free up").Filter);
        Assert.Equal(":onlineonly", NaturalFilter.Translate("sadece çevrimiçi dosyalar").Filter);
        Assert.Equal(":alwayskept", NaturalFilter.Translate("OneDrive'da her zaman cihazda tutulanlar").Filter);
        Assert.Equal(":cloud", NaturalFilter.Translate("buluttaki dosyalar").Filter);
        var videos = NaturalFilter.Translate("OneDrive'daki 1 GB üstü videolar").Filter;
        Assert.Contains(":cloud", videos);
        Assert.Contains("*.mp4", videos);
        Assert.Contains(">1gb", videos);
        Assert.True(NaturalFilter.LooksNatural("onedrive"));
        foreach (var t in new[] { ":freeable", ":onlineonly", ":alwayskept", ":cloud" }) Assert.Null(Record.Exception(() => new Filter(t)));
    }

    [Fact]
    public void KnowledgeBase_KnowsTheCloudFolders()
    {
        var c = new DriveNode("C:\\") { IsDrive = true, Enumerated = true, ScanComplete = true };
        var ali = Folder(Folder(c, "Users"), "Ali");
        Assert.Equal("dropbox", KnowledgeBase.Classify(Folder(ali, "Dropbox"))?.Location.Id);
        Assert.Equal("dropbox", KnowledgeBase.Classify(Folder(ali, "Dropbox (Contoso)"))?.Location.Id);
        Assert.Equal("icloud-drive", KnowledgeBase.Classify(Folder(ali, "iCloudDrive"))?.Location.Id);
        Assert.Equal("google-drive", KnowledgeBase.Classify(Folder(ali, "My Drive"))?.Location.Id);
        Assert.Equal(LocationCategory.Cloud, KnowledgeBase.Classify(Folder(ali, "OneDrive - Contoso"))?.Location.Category);
        Assert.False(KnowledgeBase.IsReclaimable(File(Folder(ali, "iCloudDrive"), "a.txt", 1, 1)));   // cloud files are never "junk"
    }

    // ------------------------------------------------------------------ a real Cloud Files sync root

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CfRegistration
    {
        public uint StructSize; public string ProviderName; public string ProviderVersion;
        public IntPtr SyncRootIdentity; public uint SyncRootIdentityLength;
        public IntPtr FileIdentity; public uint FileIdentityLength; public Guid ProviderId;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct CfPolicies { public uint StructSize; public ushort HydrationPrimary, HydrationModifier, PopulationPrimary, PopulationModifier; public uint InSync, HardLink, PlaceholderManagement; }
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)] private static extern int CfRegisterSyncRoot(string path, ref CfRegistration reg, ref CfPolicies pol, uint flags);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)] private static extern int CfUnregisterSyncRoot(string path);
    [DllImport("cldapi.dll")] private static extern int CfConvertToPlaceholder(SafeFileHandle h, byte[]? identity, uint identityLength, uint flags, out long usn, IntPtr overlapped);
    [DllImport("cldapi.dll")] private static extern int CfSetPinState(SafeFileHandle h, int state, uint flags, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    private static SafeFileHandle Open(string path, bool dir)
    {
        var h = CreateFileW(path, 0x80000000 | 0x40000000, 7, IntPtr.Zero, 3, dir ? 0x02000000u : 0u, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException("open failed: " + path);
        return h;
    }

    /// <summary>
    /// Registers a private sync root (no admin needed), makes placeholders in OneDrive's states and scans them: the root
    /// itself is a reparse point and must be walked (it used to be skipped with the whole folder), online-only files
    /// take no disk space, junctions are still skipped. Everything is unregistered and deleted afterwards.
    /// </summary>
    [Fact]
    public void RealSyncRoot_IsScanned_AndStatesAreRead()
    {
        AppConfig.Replace(new AppConfig { WatchDog = false });   // no file system watcher holding the folder at cleanup
        var baseDir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "cloud-" + Guid.NewGuid().ToString("N")[..8]);
        var root = Path.Combine(baseDir, "CloudRoot");
        Directory.CreateDirectory(root);
        var reg = new CfRegistration { ProviderName = "ByteScapeTest", ProviderVersion = "1.0", ProviderId = Guid.NewGuid() };
        reg.StructSize = (uint)Marshal.SizeOf<CfRegistration>();
        var pol = new CfPolicies { HydrationPrimary = 2, PopulationPrimary = 3 };
        pol.StructSize = (uint)Marshal.SizeOf<CfPolicies>();
        int hr;
        try { hr = CfRegisterSyncRoot(root, ref reg, ref pol, 0); }
        catch (DllNotFoundException) { _out.WriteLine("no Cloud Files API on this Windows: skipped"); Directory.Delete(baseDir, true); return; }
        if (hr != 0) { _out.WriteLine($"CfRegisterSyncRoot failed 0x{hr:X8}: skipped"); Directory.Delete(baseDir, true); return; }
        try
        {
            var docs = Directory.CreateDirectory(Path.Combine(root, "Docs")).FullName;
            void Make(string name, int mb) { using var fs = System.IO.File.Create(Path.Combine(docs, name)); fs.Write(new byte[mb << 20]); }
            Make("local.bin", 3); Make("pinned.bin", 2); Make("online.bin", 5);
            var id = new byte[] { 1, 2 };
            void Convert(string p, bool dir, uint flags) { using var h = Open(p, dir); Assert.Equal(0, CfConvertToPlaceholder(h, dir ? null : id, dir ? 0u : 2u, flags, out _, IntPtr.Zero)); }
            Convert(docs, true, 1);
            Convert(Path.Combine(docs, "local.bin"), false, 1);
            Convert(Path.Combine(docs, "pinned.bin"), false, 1);
            using (var h = Open(Path.Combine(docs, "pinned.bin"), false)) Assert.Equal(0, CfSetPinState(h, 1, 0, IntPtr.Zero));
            Convert(Path.Combine(docs, "online.bin"), false, 1 | 2);   // in sync + dehydrated = online-only
            // a junction next to it must still be skipped by default
            var link = Path.Combine(baseDir, "link");
            using (var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + docs + "\"") { CreateNoWindow = true, UseShellExecute = false }))
                mk!.WaitForExit(10000);
            Assert.True(Directory.Exists(link), "junction was not created");
            var (tree, _) = DataCache.Acquire(baseDir);
            try
            {
                tree.StartMasterScan();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (tree.IsMasterScanning && sw.ElapsedMilliseconds < 20000) Thread.Sleep(20);
                var cloudRoot = tree.Root.FindChild("CloudRoot") as ContainerNode;
                Assert.NotNull(cloudRoot);                                               // the sync root is walked
                var d = (ContainerNode)cloudRoot!.FindChild("Docs")!;
                var local = d.FindChild("local.bin")!; var pinned = d.FindChild("pinned.bin")!; var online = d.FindChild("online.bin")!;
                Assert.Equal(5 * MB, online.LogicalSize);
                Assert.Equal(0, online.DiskSize);                                        // no space on this disk
                Assert.Equal(3 * MB, local.DiskSize);
                Assert.Equal(CloudState.OnlineOnly, CloudFiles.StateOf(online));
                Assert.Equal(CloudState.AlwaysKept, CloudFiles.StateOf(pinned));
                Assert.Equal(CloudState.LocalCopy, CloudFiles.StateOf(local));
                Assert.Equal("ByteScapeTest", CloudFiles.SyncRootOf(local)?.Provider);
                Assert.Null(tree.Root.FindChild("link"));                                // junctions are not followed
                var s = CloudFiles.Summarize(tree.Root, CancellationToken.None, tree.Sync);
                var r = Assert.Single(s.Roots);
                Assert.Equal((3 * MB, 2 * MB, 5 * MB), (r.LocalBytes, r.KeptBytes, r.OnlineBytes));
            }
            finally { DataCache.Release(tree); }
        }
        finally
        {
            CfUnregisterSyncRoot(root);
            for (int attempt = 0; attempt < 10 && Directory.Exists(baseDir); attempt++)
            {
                try
                {
                    var j = Path.Combine(baseDir, "link");
                    if (Directory.Exists(j)) Directory.Delete(j);   // the junction itself, never its target
                    Directory.Delete(baseDir, true);
                }
                catch (Exception ex) { _out.WriteLine("cleanup: " + ex.Message); Thread.Sleep(200); }
            }
            AppConfig.Replace(new AppConfig());
        }
        Assert.False(Directory.Exists(baseDir));
    }
}
