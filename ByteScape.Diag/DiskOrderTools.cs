// Folder order of a one-at-a-time scan: breadth first against master file table (disk) order, as record jumps.
using System.Diagnostics;
using System.IO;
using ByteScape.Core.Config;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;

namespace ByteScape.Diag;

internal static partial class Program
{
    /// <summary>
    /// Scans <paramref name="path"/> one folder at a time, breadth first and in disk order, and compares how far apart the
    /// master file table records of consecutive folders are: on a spinning disk every jump is a seek of the head.
    /// </summary>
    private static void DiskOrder(string path)
    {
        Out("== DISK ORDER " + path + "  (drive: " + DriveProfile.Detect(path).Kind + ")");
        (List<long> Records, double Seconds, long Files) Run(int kind, bool ids)
        {
            // folder ids are listed in both runs (hard links counted once), so both walks see the same record numbers
            AppConfig.Replace(new AppConfig { WatchDog = false, UseMftScan = false, ScanDriveKind = kind, ScanThreads = 1, CountHardLinksOnce = ids });
            var records = new List<long>();
            DirectoryScanner.Listed = n => { if (n is FolderNode f) records.Add(f.FileId & 0x0000_FFFF_FFFF_FFFF); };
            var tree = new ScanTree(DataCache.CreateRoot(path), live: true);
            var sw = Stopwatch.StartNew();
            tree.StartMasterScan();
            while (tree.IsMasterScanning) Thread.Sleep(20);
            DirectoryScanner.Listed = null;
            var r = (records, sw.Elapsed.TotalSeconds, (long)tree.Root.FileCount);
            DataCache.Release(tree);
            return r;
        }
        void Report(string name, (List<long> Records, double Seconds, long Files) r)
        {
            var known = r.Records.Where(x => x > 0).ToList();
            double distance = 0; int back = 0, far = 0;
            for (int i = 1; i < known.Count; i++)
            {
                long d = known[i] - known[i - 1];
                distance += Math.Abs(d);
                if (d < 0) back++;
                if (Math.Abs(d) > 4096) far++;             // more than 4 MB of master file table away
            }
            Out($"   {name,-14}: {r.Seconds,6:0.00} s  {known.Count:N0} folders  total jump {distance / 1024 / 1024:N0} M records ({distance * 1024 / (1L << 30):N0} GB of table crossed)  backwards {back:N0}  far jumps (>4 MB) {far:N0}");
        }
        // a one-at-a-time breadth first walk: SSD strategy on one thread; then the HDD strategy (disk order)
        var bfs = Run(kind: 2, ids: true);
        var disk = Run(kind: 1, ids: true);
        Report("breadth first", bfs);
        Report("disk order", disk);
        Out($"   files: {bfs.Files:N0} / {disk.Files:N0} (a live system: files come and go between the two scans)");
        AppConfig.Replace(new AppConfig());
    }
}
