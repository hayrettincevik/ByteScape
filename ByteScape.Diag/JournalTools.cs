// Quick rescans from the NTFS change journal on a real drive: what a full scan costs, what the quick rescan costs in the
// same session and after a "restart" (the kept scan restored), and how far back the journal reaches.
using System.Diagnostics;
using System.IO;
using ByteScape.Core.Config;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;

namespace ByteScape.Diag;

internal static partial class Program
{
    private static void JournalCheck(string path, int waitSeconds)
    {
        Out($"== JOURNAL {path} (changes collected for {waitSeconds} s)");
        var store = Path.Combine(Path.GetTempPath(), "bytescape-diag-journal");
        ScanResume.DirOverride = store;
        try
        {
            AppConfig.Replace(new AppConfig { WatchDog = false });
            if (ChangeJournal.Oldest(path) is { } oldest)
                Out($"   journal        : id {oldest.Mark.JournalId:X}, first change kept {oldest.FirstChangeUtc.ToLocalTime():g} ({(DateTime.UtcNow - oldest.FirstChangeUtc).TotalDays:0.0} days back)");
            else { Out("   no NTFS change journal on this drive: nothing to measure"); return; }

            var tree = new ScanTree(DataCache.CreateRoot(path), live: true);
            var sw = Stopwatch.StartNew();
            tree.StartMasterScan();
            while (tree.IsMasterScanning) Thread.Sleep(10);
            Out($"   full scan      : {sw.Elapsed.TotalSeconds,6:0.00} s  {tree.Root.FileCount:N0} files  {tree.Root.FolderCount:N0} folders  {Fmt(tree.Root.DiskSize)}  ({tree.MasterScan!.Method}, mark {(tree.ChangeMark != null ? "taken" : "NONE")})");
            if (tree.ChangeMark == null) return;

            sw.Restart();
            bool saved = ScanResume.Save(tree);
            long bytes = Directory.EnumerateFiles(store).Sum(f => new FileInfo(f).Length);
            Out($"   kept           : {saved} in {sw.ElapsedMilliseconds} ms, {Fmt(bytes)} on disk");
            var info = ScanResume.Find(path)!;

            Thread.Sleep(waitSeconds * 1000);

            // the same session: the scan button
            int listed = 0;
            DirectoryScanner.Listed = _ => Interlocked.Increment(ref listed);
            sw.Restart();
            tree.StartMasterScan(changesOnly: true);
            while (tree.IsMasterScanning) Thread.Sleep(5);
            var quick = sw.Elapsed;
            DirectoryScanner.Listed = null;
            var js = tree.MasterScan!.Journal;
            Out($"   quick rescan   : {quick.TotalSeconds,6:0.00} s  ({tree.MasterScan.Method}; {js?.Records:N0} changes on the drive in {js?.Read.TotalMilliseconds:0} ms, {js?.Folders:N0} folders, {js?.ToList:N0} here, {listed:N0} listed; hard links {tree.HardLinks.Elapsed.TotalMilliseconds:0} ms)");

            // what a full scan finds now
            var fresh = new ScanTree(DataCache.CreateRoot(path), live: true);
            sw.Restart();
            fresh.StartMasterScan();
            while (fresh.IsMasterScanning) Thread.Sleep(10);
            Out($"   full scan now  : {sw.Elapsed.TotalSeconds,6:0.00} s  {fresh.Root.FileCount:N0} files  {fresh.Root.FolderCount:N0} folders  {Fmt(fresh.Root.DiskSize)}");
            Compare("quick rescan vs full scan", tree, fresh);

            // "ByteScape started again": the kept scan brought up to date
            var again = new ScanTree(DataCache.CreateRoot(path), live: true);
            sw.Restart();
            bool restored = ScanResume.Restore(again, info);
            var restore = sw.Elapsed;
            listed = 0;
            DirectoryScanner.Listed = _ => Interlocked.Increment(ref listed);
            sw.Restart();
            again.StartMasterScan(changesOnly: true);
            while (again.IsMasterScanning) Thread.Sleep(5);
            DirectoryScanner.Listed = null;
            var js2 = again.MasterScan!.Journal;
            Out($"   after restart  : restore {restore.TotalSeconds:0.00} s ({restored}) + update {sw.Elapsed.TotalSeconds:0.00} s = {(restore + sw.Elapsed).TotalSeconds:0.00} s  ({again.MasterScan.Method}; {js2?.Records:N0} changes, {js2?.ToList:N0} folders here, {listed:N0} listed)");
            Compare("restarted vs full scan", again, fresh);

            // the oldest mark the journal still covers: the most a quick rescan can have to list
            var far = new ScanTree(DataCache.CreateRoot(path), live: true);
            Assert(ScanResume.Restore(far, info), "restore for the far test");
            far.ChangeMark = oldest.Mark with { RootId = info.Mark!.RootId };
            sw.Restart();
            far.StartMasterScan(changesOnly: true);
            while (far.IsMasterScanning) Thread.Sleep(10);
            var js3 = far.MasterScan!.Journal;
            Out($"   whole journal  : {sw.Elapsed.TotalSeconds,6:0.00} s  ({js3?.Records:N0} changes over {(DateTime.UtcNow - oldest.FirstChangeUtc).TotalDays:0.0} days read in {js3?.Read.TotalMilliseconds:0} ms, {js3?.Folders:N0} folders, {js3?.ToList:N0} here listed again)");
            DataCache.Release(tree); DataCache.Release(fresh); DataCache.Release(again); DataCache.Release(far);
        }
        finally
        {
            DirectoryScanner.Listed = null;
            ScanResume.DirOverride = null;
            AppConfig.Replace(new AppConfig());
            try { Directory.Delete(store, true); } catch { }
        }
    }

    private static void Assert(bool ok, string what) { if (!ok) throw new Exception("failed: " + what); }

    /// <summary>Elements whose path or size differ between two trees of the same path (a live system changes meanwhile).</summary>
    private static void Compare(string title, ScanTree a, ScanTree b)
    {
        Dictionary<string, (long Len, long Disk)> Map(ScanTree t)
        {
            lock (t.Sync) return t.Root.Descendants().Where(n => n is FileNode).ToDictionary(n => n.FullPath, n => (n.LogicalSize, n.DiskSize), StringComparer.OrdinalIgnoreCase);
        }
        var ma = Map(a); var mb = Map(b);
        var onlyA = ma.Keys.Where(k => !mb.ContainsKey(k)).ToList();
        var onlyB = mb.Keys.Where(k => !ma.ContainsKey(k)).ToList();
        var sized = ma.Where(kv => mb.TryGetValue(kv.Key, out var o) && o != kv.Value).Select(kv => kv.Key).ToList();
        Out($"   {title}: files {a.Root.FileCount - b.Root.FileCount:+#;-#;0}, disk {Fmt(Math.Abs(a.Root.DiskSize - b.Root.DiskSize))} apart; {onlyA.Count} files only in the first, {onlyB.Count} only in the full scan, {sized.Count} sized differently");
        foreach (var p in onlyA.Take(4)) Out("      first only : " + p);
        foreach (var p in onlyB.Take(4)) Out("      full only  : " + p);
        foreach (var p in sized.Take(4)) Out($"      size       : {p}  {ma[p].Len}/{ma[p].Disk} vs {mb[p].Len}/{mb[p].Disk}");
    }
}
