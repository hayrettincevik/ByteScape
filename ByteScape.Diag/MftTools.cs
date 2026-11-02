// NTFS master file table checks: a dataset of NTFS corner cases (mftdata) and the comparison of the fast MFT scan
// with the normal folder scan on a real drive or folder (mftcheck, administrator).
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ByteScape.Core.Config;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using Microsoft.Win32.SafeHandles;
using IOFile = System.IO.File;

namespace ByteScape.Diag;

internal static partial class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref ushort input, int inSize, IntPtr output, int outSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr input, int inSize, IntPtr output, int outSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newName, string existing, IntPtr security);
    private const uint FsctlSetSparse = 0x000900C4, FsctlSetCompression = 0x0009C040;

    /// <summary>
    /// Builds NTFS corner cases under <paramref name="root"/>: resident and cluster-edge sizes, deep and long paths, Unicode
    /// and trailing dot / space names, hidden and system entries, alternate data streams (on files, a folder, a file with
    /// 60 streams), sparse files (one with hundreds of runs), NTFS-compressed files, hard links (one file with 150 names:
    /// an attribute list with extension records), 10,000 files in one folder, a junction loop and empty folders.
    /// Only its own files; removed with <c>mftdata &lt;root&gt; clean</c>.
    /// </summary>
    private static void MftData(string root, bool clean)
    {
        Out("== MFT DATA " + root + (clean ? " (clean)" : ""));
        string X(string p) => @"\\?\" + p;
        try { DeleteTree(root); } catch (Exception ex) { Out("   cleanup problem: " + ex.Message); if (!clean) return; }
        if (clean) { Out("   removed: " + !Directory.Exists(root)); return; }
        Directory.CreateDirectory(root);
        var rnd = new Random(42);
        int files = 0;
        string P(string rel) => Path.Combine(root, rel);
        void Bytes(string path, byte[] data) { Directory.CreateDirectory(X(Path.GetDirectoryName(path)!)); IOFile.WriteAllBytes(X(path), data); files++; }
        byte[] Rnd(int n) { var b = new byte[n]; rnd.NextBytes(b); return b; }
        void W(string rel, int n) => Bytes(P(rel), Rnd(n));

        // sizes around the resident limit and the cluster edges
        foreach (var (name, n) in new[] { ("zero.bin", 0), ("one.bin", 1), ("resident400.bin", 400), ("resident700.bin", 700), ("almost1k.bin", 1000),
                                          ("cluster.bin", 4096), ("cluster+1.bin", 4097), ("mega.bin", 1 << 20), ("mega+1.bin", (1 << 20) + 1) })
            W(@"sizes\" + name, n);
        // deep nesting and a path far beyond MAX_PATH
        var deep = P("deep");
        for (int i = 1; i <= 60; i++) deep = Path.Combine(deep, "d" + i);
        Bytes(Path.Combine(deep, "bottom.txt"), Rnd(5000));
        var lng = P("longpath");
        for (int i = 0; i < 12; i++) lng = Path.Combine(lng, $"folder number {i:00} with quite a long name to pass MAX_PATH");
        Bytes(Path.Combine(lng, "at the end of a very long path.txt"), Rnd(7000));
        Out($"   long path: {lng.Length + 32} characters");
        // names
        foreach (var name in new[] { "Türkçe çğıİöşü ÇĞIÖŞÜ.txt", "emoji 😀🎮.txt", "日本語のファイル.txt", "العربية.txt", "Ελληνικά.txt", "a.b.c.d.e", "no extension",
                                     "name with  two  spaces.txt", "UPPER.TXT", "lower.txt", "Long File Name That Gets An 8dot3 Alias.txt", "x" + new string('y', 240) })
            W(@"names\" + name, 1234);
        Bytes(P(@"names\trailing dot."), Rnd(111));        // only reachable with \\?\ paths
        Bytes(P(@"names\trailing space "), Rnd(222));
        Directory.CreateDirectory(X(P(@"names\folder with dot.")));
        W(@"names\Ünicode klasör\içerik.txt", 3333);
        // hidden and system
        W(@"attrs\hidden.txt", 2000); IOFile.SetAttributes(P(@"attrs\hidden.txt"), FileAttributes.Hidden);
        W(@"attrs\system.txt", 2000); IOFile.SetAttributes(P(@"attrs\system.txt"), FileAttributes.System);
        W(@"attrs\hidden folder\inside.txt", 2000); new DirectoryInfo(P(@"attrs\hidden folder")).Attributes |= FileAttributes.Hidden | FileAttributes.System;
        W(@"attrs\readonly.txt", 2000); IOFile.SetAttributes(P(@"attrs\readonly.txt"), FileAttributes.ReadOnly);
        // alternate data streams: on a file, on an empty file, on a folder, and 60 of them on one file (attribute list)
        W(@"ads\host.txt", 1000); IOFile.WriteAllBytes(P(@"ads\host.txt") + ":secret", Rnd(100_000));
        W(@"ads\only streams.bin", 0); IOFile.WriteAllBytes(P(@"ads\only streams.bin") + ":s", Rnd(5000));
        Directory.CreateDirectory(P(@"ads\folder")); IOFile.WriteAllBytes(P(@"ads\folder") + ":folderstream", Rnd(3000));
        W(@"ads\many streams.bin", 500);
        for (int i = 0; i < 60; i++) IOFile.WriteAllBytes(P(@"ads\many streams.bin") + ":stream" + i, Rnd(200));
        // sparse: a big hole, and hundreds of separate allocated pieces (a long run list in extension records)
        void Sparse(string rel, long length, IEnumerable<long> offsets, int piece)
        {
            var path = P(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
            if (!DeviceIoControl(fs.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero)) Out("   FSCTL_SET_SPARSE failed: " + Marshal.GetLastWin32Error());
            fs.SetLength(length);
            foreach (var o in offsets) { fs.Position = o; fs.Write(Rnd(piece)); }
            files++;
        }
        Sparse(@"sparse\one piece.bin", 100L << 20, new[] { 50L << 20 }, 64 << 10);
        Sparse(@"sparse\many pieces.bin", 600L * (256 << 10), Enumerable.Range(0, 600).Select(i => (long)i * (256 << 10)), 4096);
        Sparse(@"sparse\empty sparse.bin", 10L << 20, Array.Empty<long>(), 0);
        // NTFS compression: set on the empty file, then written (text compresses well, random data does not)
        void Compressed(string rel, byte[] data)
        {
            var path = P(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
            ushort format = 1;   // COMPRESSION_FORMAT_DEFAULT
            if (!DeviceIoControl(fs.SafeFileHandle, FsctlSetCompression, ref format, 2, IntPtr.Zero, 0, out _, IntPtr.Zero)) Out("   FSCTL_SET_COMPRESSION failed: " + Marshal.GetLastWin32Error());
            fs.Write(data);
            files++;
        }
        var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 60000).Select(i => "line " + (i % 97) + " of a very repetitive text file\n")));
        Compressed(@"compressed\text.txt", text);
        Compressed(@"compressed\random.bin", Rnd(300_000));
        Compressed(@"compressed\small.txt", text.AsSpan(0, 3000).ToArray());
        // hard links: three names for one file in two folders, and one file with 150 names in one folder
        W(@"links\target.bin", 50_000);
        Directory.CreateDirectory(P(@"links\other"));
        if (!CreateHardLinkW(P(@"links\other\link1.bin"), P(@"links\target.bin"), IntPtr.Zero)) Out("   hard link failed: " + Marshal.GetLastWin32Error());
        CreateHardLinkW(P(@"links\link2.bin"), P(@"links\target.bin"), IntPtr.Zero);
        W(@"manylinks\t.bin", 3000);
        int made = 0;
        for (int i = 0; i < 150; i++) if (CreateHardLinkW(P($@"manylinks\l{i:000} with a fairly long name so the names fill the record.bin"), P(@"manylinks\t.bin"), IntPtr.Zero)) made++;
        Out($"   hard links in one folder: {made}");
        // a big folder (index allocation), with 8.3 aliases where the drive makes them
        for (int i = 0; i < 10_000; i++) W($@"bigdir\Long File Name Number {i:00000}.txt", rnd.Next(0, 5000));
        // links that are not followed: a junction loop, a folder symbolic link, a file symbolic link (may need developer mode)
        Directory.CreateDirectory(P(@"loop\A\B"));
        W(@"loop\A\a.bin", 10_000);
        Run("cmd.exe", $"/c mklink /J \"{P(@"loop\A\B\JunctionToA")}\" \"{P(@"loop\A")}\"");
        Run("cmd.exe", $"/c mklink /D \"{P(@"loop\SymDirToA")}\" \"{P(@"loop\A")}\"");
        Run("cmd.exe", $"/c mklink \"{P(@"loop\SymFile.bin")}\" \"{P(@"loop\A\a.bin")}\"");
        // empty folders
        Directory.CreateDirectory(P(@"empty\e1\e2\e3"));
        Directory.CreateDirectory(P(@"empty\sibling"));
        Out($"   created {files:N0} files under {root}");
    }


    /// <summary>
    /// Scans <paramref name="path"/> (a drive or a folder) twice through the application's master scan - once reading the NTFS
    /// master file table, once listing folders - and compares the two trees entry by entry; Windows is asked who is right
    /// where they differ. Folders are also compared with alternate data streams counted. Needs administrator rights.
    /// </summary>
    private static void MftCheck(string path, string report, bool withAds)
    {
        path = DataCache.NormalizePath(path);
        bool folder = path.TrimEnd('\\').Length > 2;
        Out($"== MFT CHECK {path}  (administrator: {Environment.IsPrivilegedProcess}, {DateTime.Now:yyyy-MM-dd HH:mm})");
        try
        {
            if (!Environment.IsPrivilegedProcess) { Out("   needs administrator rights"); return; }
            var swr = Stopwatch.StartNew();
            var rstats = new Core.Scanning.Mft.MftReader.Stats();
            var ix = Core.Scanning.Mft.MftScan.ReadVolume(path, CancellationToken.None, adsSizes: false, stats: rstats);
            Out($"   MFT read alone: {swr.ElapsedMilliseconds} ms, {ix.Count:N0} records, {ix.InUseCount:N0} in use");
            Out($"   MFT read detail: {Core.Scanning.Mft.MftScan.LastReadInfo}");
            ix = null!;
            GC.Collect();
            Core.Scanning.Mft.MftScan.ForFolders = folder;   // the application reads the table for whole drives only
            CheckPass(path, ads: false, folder);
            if (folder || withAds) CheckPass(path, ads: true, folder);
        }
        catch (Exception ex) { Out("   !!! " + ex); }
        finally
        {
            AppConfig.Replace(new AppConfig());
            try { IOFile.WriteAllText(report, Report.ToString(), new UTF8Encoding(true)); } catch { }
        }
    }

    private static void CheckPass(string path, bool ads, bool detail)
    {
        Out("");
        Out(ads ? "   ---- with alternate data streams counted (\"scan ADS\" on)" : "   ---- default settings");
        (ScanTree tree, ScanJob job) Run(bool mft)
        {
            AppConfig.Replace(new AppConfig { UseMftScan = mft, WatchDog = false, ScanADS = ads });
            var tree = new ScanTree(DataCache.CreateRoot(path), live: true);
            tree.StartMasterScan();
            while (tree.IsMasterScanning) Thread.Sleep(20);
            return (tree, tree.MasterScan!);
        }
        var (a, ja) = Run(mft: true);
        Out($"   MFT scan     : {ja.Method}, {ja.Elapsed.TotalSeconds:0.00} s, {a.Root.FileCount:N0} files, {a.Root.FolderCount:N0} folders, logical {Fmt(a.Root.LogicalSize)}, disk {Fmt(a.Root.DiskSize)}  ({Mem()})");
        Out($"   (its read    : {Core.Scanning.Mft.MftScan.LastReadInfo})");
        if (ja.Method != "NTFS MFT") Out("   !!! the MFT scan was not used (see the log): the comparison below is listing against listing");
        var (b, jb) = Run(mft: false);
        int denied = b.Root.Descendants().OfType<ContainerNode>().Count(c => c.AccessDenied);
        Out($"   Folder scan  : {jb.Method}, {jb.Elapsed.TotalSeconds:0.00} s, {b.Root.FileCount:N0} files, {b.Root.FolderCount:N0} folders, logical {Fmt(b.Root.LogicalSize)}, disk {Fmt(b.Root.DiskSize)}, {denied} folders denied");
        Out($"   speed-up     : {jb.Elapsed.TotalSeconds / Math.Max(0.001, ja.Elapsed.TotalSeconds):0.0}x");
        Out($"   hard links   : MFT scan {a.HardLinks.Files:N0} files with {a.HardLinks.ExtraNames:N0} more names, {Fmt(a.HardLinks.SavedBytes)} counted once; folder scan {b.HardLinks.Files:N0} / {b.HardLinks.ExtraNames:N0} / {Fmt(b.HardLinks.SavedBytes)}");

        int onlyMft = 0, onlyMftUnderDenied = 0, onlyList = 0, sizeDiff = 0, diskDiff = 0, kindDiff = 0, same = 0, idDiff = 0;
        var lenPairs = new List<(string Path, long Mft, long List)>();
        var diskPairs = new List<(string Path, long Mft, long List)>();
        var detailRows = new List<string>();
        long onlyMftFilesUnderDenied = 0;
        var samples = new Dictionary<string, List<string>>();
        void Sample(string k, string s) { if (!samples.TryGetValue(k, out var l)) samples[k] = l = new(); if (l.Count < 25) l.Add(s); }
        void Cmp(ContainerNode x, ContainerNode y, string p)
        {
            var mx = x.Children.Where(n => !n.IsSynthetic).GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var my = y.Children.Where(n => !n.IsSynthetic).GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var (name, nx) in mx)
            {
                var q = p + "\\" + name;
                if (!my.TryGetValue(name, out var ny))
                {
                    if (y.AccessDenied) { onlyMftUnderDenied++; onlyMftFilesUnderDenied += nx is ContainerNode dc ? dc.FileCount : 1; }
                    else { onlyMft++; Sample("only in MFT scan", q + (nx is ContainerNode c1 ? $"\\  ({c1.FileCount:N0} files, {Fmt(c1.DiskSize)})" : $"  {Fmt(nx.DiskSize)}")); }
                    continue;
                }
                if (nx is ContainerNode cx && ny is ContainerNode cy)
                {
                    same++;
                    if (detail && (cx.FileCount != cy.FileCount || cx.FolderCount != cy.FolderCount)) Sample("folder counts differ", $"{q}  MFT {cx.FileCount}/{cx.FolderCount}, listing {cy.FileCount}/{cy.FolderCount}");
                    Cmp(cx, cy, q);
                    continue;
                }
                if (nx is FileNode fx && ny is FileNode fy)
                {
                    // the ids must agree: after an MFT scan the file system watcher re-lists folders, and a different id would undo the grouping
                    if (fx.FileId != fy.FileId) { idDiff++; Sample("file id differs (MFT / listing)", $"{q}  {fx.FileId:X16} / {fy.FileId:X16}"); }
                    if (fx.LogicalSize != fy.LogicalSize) { sizeDiff++; lenPairs.Add((q, fx.LogicalSize, fy.LogicalSize)); }
                    else if (fx.DiskSize != fy.DiskSize) { diskDiff++; diskPairs.Add((q, fx.DiskSize, fy.DiskSize)); }
                    else same++;
                    if (detail && !q.Contains(@"\bigdir\") && !q.Contains(@"\manylinks\l"))
                        detailRows.Add($"{Path.GetRelativePath(path, q),-60} MFT {fx.LogicalSize,11:N0} / {fx.DiskSize,11:N0}   listing {fy.LogicalSize,11:N0} / {fy.DiskSize,11:N0}{(fx.LogicalSize == fy.LogicalSize && fx.DiskSize == fy.DiskSize ? "" : "   <-- differ")}   attr {fx.Attributes}");
                    continue;
                }
                kindDiff++; Sample("file in one, folder in the other", q);
            }
            foreach (var (name, ny) in my)
                if (!mx.ContainsKey(name)) { onlyList++; Sample("only in folder scan", p + "\\" + name + (ny is ContainerNode c2 ? $"\\  ({c2.FileCount:N0} files, {Fmt(c2.DiskSize)})" : $"  {Fmt(ny.DiskSize)}")); }
        }
        Cmp(a.Root, b.Root, path.TrimEnd('\\'));
        Out($"   identical entries        : {same:N0}");
        Out($"   only in MFT scan         : {onlyMft:N0}  (+ {onlyMftUnderDenied:N0} entries / {onlyMftFilesUnderDenied:N0} files inside folders the folder scan could not open: expected)");
        Out($"   only in folder scan      : {onlyList:N0}");
        Out($"   different length         : {sizeDiff:N0}");
        Out($"   different size on disk   : {diskDiff:N0}");
        Out($"   file vs folder           : {kindDiff:N0}");
        Out($"   different file id        : {idDiff:N0}");
        // who is right? ask Windows now: the file's length and what it really occupies (compressed size)
        long cluster = Math.Max(512, a.Root.ClusterSize);
        long Round(long v) => v <= 0 ? 0 : (v + cluster - 1) / cluster * cluster;
        void Judge(string what, List<(string Path, long Mft, long List)> pairs, Func<string, long?> truth)
        {
            int mftRight = 0, listRight = 0, neither = 0, gone = 0;
            foreach (var (p, m, l) in pairs)
            {
                long? t;
                try { t = truth(p); } catch { t = null; }
                if (t == null) { gone++; continue; }
                if (t == m) mftRight++;
                else if (t == l) { listRight++; Sample(what + ": the folder scan was right", $"{p}  MFT {m:N0} / listing {l:N0} / now {t:N0}"); }
                else { neither++; Sample(what + ": neither matches now (file changed)", $"{p}  MFT {m:N0} / listing {l:N0} / now {t:N0}"); }
                if (t == m) Sample(what + ": the MFT scan was right", $"{p}  MFT {m:N0} / listing {l:N0} / now {t:N0}");
            }
            Out($"   {what,-24}: MFT right {mftRight:N0}, folder scan right {listRight:N0}, changed since {neither:N0}, gone {gone:N0}");
        }
        string Long(string p) => p.Length >= 250 && !p.StartsWith(@"\\") ? @"\\?\" + p : p;
        // both scans keep the main stream's length as the length and add alternate streams to the size on disk
        Judge("length", lenPairs, p => IOFile.Exists(Long(p)) ? new FileInfo(Long(p)).Length : null);
        Judge("size on disk", diskPairs, p =>
        {
            if (!IOFile.Exists(Long(p))) return null;
            uint low = GetCompressedFileSizeW(Long(p), out uint high);
            if (low == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0) return null;
            long streams = ads && NtfsAds.HasAlternateStreams(Long(p), out long s) ? Round(s) : 0;
            return Round(((long)high << 32) | low) + streams;
        });
        foreach (var (k, l) in samples) { Out("   -- " + k + ":"); foreach (var s in l.Take(12)) Out("      " + s); }
        if (detail && detailRows.Count > 0)
        {
            Out("   -- every file (length / size on disk), big folder and hard link names left out:");
            foreach (var r in detailRows.OrderBy(r => r, StringComparer.OrdinalIgnoreCase).Take(200)) Out("      " + r);
        }
        Out("   (files being written during the two scans - logs, caches, the page file - legitimately differ)");
    }
}
