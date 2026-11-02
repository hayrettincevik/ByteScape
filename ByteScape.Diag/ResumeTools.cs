// Stopping a scan half way, keeping it, and continuing it in a new tree: the costs on a real drive.
using System.Diagnostics;
using System.IO;
using ByteScape.Core.Config;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;

namespace ByteScape.Diag;

internal static partial class Program
{
    private static void ResumeCheck(string path, int stopAfterMs)
    {
        Out($"== RESUME {path} (stop after {stopAfterMs} ms)");
        var store = Path.Combine(Path.GetTempPath(), "bytescape-diag-resume");
        ScanResume.DirOverride = store;
        try
        {
            AppConfig.Replace(new AppConfig { WatchDog = false });
            // the whole scan, for comparison
            var full = new ScanTree(DataCache.CreateRoot(path), live: true);
            var sw = Stopwatch.StartNew();
            full.StartMasterScan();
            while (full.IsMasterScanning) Thread.Sleep(10);
            double fullSec = sw.Elapsed.TotalSeconds;
            Out($"   full scan      : {fullSec,6:0.00} s  {full.Root.FileCount:N0} files  {full.Root.FolderCount:N0} folders  {Fmt(full.Root.DiskSize)}");

            // a scan stopped half way
            var tree = new ScanTree(DataCache.CreateRoot(path), live: true);
            sw.Restart();
            tree.StartMasterScan();
            Thread.Sleep(stopAfterMs);
            tree.PauseMasterScan();
            var paused = sw.Elapsed;
            Thread.Sleep(200);
            int filesAtPause = tree.Root.FileCount;
            Thread.Sleep(500);
            Out($"   paused at {paused.TotalSeconds:0.0} s: {filesAtPause:N0} files, still {tree.Root.FileCount:N0} half a second later (scan clock {tree.MasterScan!.Elapsed.TotalSeconds:0.00} s)");
            tree.StopMasterScan();
            while (tree.IsMasterScanning) Thread.Sleep(10);
            Out($"   stopped        : {tree.MasterState}, {tree.Root.FileCount:N0} files, can continue: {tree.CanResume}");

            sw.Restart();
            bool saved = ScanResume.Save(tree);
            var saveMs = sw.ElapsedMilliseconds;
            var info = ScanResume.Find(path)!;
            long bytes = Directory.EnumerateFiles(store).Sum(f => new FileInfo(f).Length);
            Out($"   kept           : {saved} in {saveMs} ms, {Fmt(bytes)} on disk ({info.Nodes:N0} elements, {info.PendingFolders:N0} folders to go)");

            // "ByteScape started again"
            var again = new ScanTree(DataCache.CreateRoot(path), live: true);
            sw.Restart();
            bool restored = ScanResume.Restore(again, info);
            var restoreMs = sw.ElapsedMilliseconds;
            Out($"   restored       : {restored} in {restoreMs} ms, {again.Root.FileCount:N0} files back");
            sw.Restart();
            again.StartMasterScan(resume: true);
            while (again.IsMasterScanning) Thread.Sleep(10);
            double restSec = sw.Elapsed.TotalSeconds;
            Out($"   continued      : {restSec,6:0.00} s  {again.Root.FileCount:N0} files  {again.Root.FolderCount:N0} folders  {Fmt(again.Root.DiskSize)}  ({again.MasterState})");
            Out($"   against the full scan: files {again.Root.FileCount - full.Root.FileCount:+#;-#;0}, folders {again.Root.FolderCount - full.Root.FolderCount:+#;-#;0}, disk {Fmt(Math.Abs(again.Root.DiskSize - full.Root.DiskSize))} apart (a live system changes meanwhile)");
            DataCache.Release(full); DataCache.Release(tree); DataCache.Release(again);
        }
        finally
        {
            ScanResume.DirOverride = null;
            AppConfig.Replace(new AppConfig());
            try { Directory.Delete(store, true); } catch { }
        }
    }
}
