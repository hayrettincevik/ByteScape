using System.Diagnostics;
using ByteScape.Core.Config;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using Xunit;
using Xunit.Abstractions;

namespace ByteScape.Tests;

/// <summary>Scan strategies per drive kind: detection, the strategy table, overrides, the disk order walk, yielding to rescans.</summary>
[Collection("config")]
public class DriveProfileTests
{
    private readonly ITestOutputHelper _out;
    public DriveProfileTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Kind_FromSeekPenaltyAndBus()
    {
        Assert.Equal(DriveKind.Hdd, DriveProfile.KindOf(true, DriveProfile.BusSata, false));
        Assert.Equal(DriveKind.Hdd, DriveProfile.KindOf(true, DriveProfile.BusUsb, true));        // a USB hard disk
        Assert.Equal(DriveKind.SataSsd, DriveProfile.KindOf(false, DriveProfile.BusSata, false));
        Assert.Equal(DriveKind.Nvme, DriveProfile.KindOf(false, DriveProfile.BusNvme, false));
        Assert.Equal(DriveKind.Nvme, DriveProfile.KindOf(null, DriveProfile.BusNvme, false));
        Assert.Equal(DriveKind.UsbFlash, DriveProfile.KindOf(false, DriveProfile.BusUsb, true));
        Assert.Equal(DriveKind.UsbFlash, DriveProfile.KindOf(null, DriveProfile.BusSd, true));
        Assert.Equal(DriveKind.SataSsd, DriveProfile.KindOf(false, DriveProfile.BusVirtual, false));
        Assert.Equal(DriveKind.UsbFlash, DriveProfile.KindOf(null, -1, true));
        Assert.Equal(DriveKind.Unknown, DriveProfile.KindOf(null, -1, false));
        Assert.Equal((DriveKind.Network, ""), DriveProfile.Detect(@"\\server\share"));
    }

    [Fact]
    public void DeviceDescriptor_IsParsed()
    {
        var d = new byte[64];
        BitConverter.GetBytes(1).CopyTo(d, 0);
        d[10] = 1;                                                  // removable
        BitConverter.GetBytes(40).CopyTo(d, 12);                   // vendor at 40
        BitConverter.GetBytes(48).CopyTo(d, 16);                   // product at 48
        BitConverter.GetBytes(DriveProfile.BusUsb).CopyTo(d, 28);
        System.Text.Encoding.ASCII.GetBytes("SanDisk\0").CopyTo(d, 40);
        System.Text.Encoding.ASCII.GetBytes("Ultra  \0").CopyTo(d, 48);
        Assert.Equal((DriveProfile.BusUsb, true, "SanDisk Ultra"), DriveProfile.ParseDeviceDescriptor(d));
        Assert.Null(DriveProfile.ParseDeviceDescriptor(new byte[20]));
        // this machine's drives answer without administrator rights
        foreach (var drive in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.CDRom))
            Assert.Equal(DriveKind.Optical, DriveProfile.Detect(drive.Name).Kind);           // not "USB flash" for its removable media
        var c = DriveProfile.Detect("C:");
        _out.WriteLine($"C: = {c.Kind} {c.Product}");
        Assert.NotEqual(DriveKind.Network, c.Kind);
    }

    [Fact]
    public void Strategy_PerKind()
    {
        var hdd = DriveProfile.StrategyFor(DriveKind.Hdd, 4);
        Assert.Equal((1, true, 1, 64 << 10, true), (hdd.Workers, hdd.DiskOrder, hdd.MftReaders, hdd.ListBufferBytes, hdd.YieldToSubScans));
        Assert.Equal("HDD, folders in disk order", hdd.Short);
        var ssd = DriveProfile.StrategyFor(DriveKind.SataSsd, 4);
        Assert.Equal((4, false, 4, 0, false), (ssd.Workers, ssd.DiskOrder, ssd.MftReaders, ssd.ListBufferBytes, ssd.YieldToSubScans));
        Assert.Equal("SSD, 4 folders in parallel", ssd.Short);
        Assert.Equal(8, DriveProfile.StrategyFor(DriveKind.SataSsd, 32).Workers);
        Assert.Equal(2, DriveProfile.StrategyFor(DriveKind.SataSsd, 1).Workers);
        var nvme = DriveProfile.StrategyFor(DriveKind.Nvme, 4);
        Assert.Equal((8, 8), (nvme.Workers, nvme.MftReaders));
        Assert.Equal(16, DriveProfile.StrategyFor(DriveKind.Nvme, 32).Workers);
        var usb = DriveProfile.StrategyFor(DriveKind.UsbFlash, 8);
        Assert.Equal((2, 64 << 10), (usb.Workers, usb.ListBufferBytes));
        var dvd = DriveProfile.StrategyFor(DriveKind.Optical, 8);
        Assert.Equal((1, false, true), (dvd.Workers, dvd.DiskOrder, dvd.YieldToSubScans));
        Assert.Equal("CD / DVD, one folder at a time", dvd.Short);
        var net = DriveProfile.StrategyFor(DriveKind.Network, 4);
        Assert.Equal((8, 64 << 10), (net.Workers, net.ListBufferBytes));
        Assert.Equal(DriveProfile.StrategyFor(DriveKind.SataSsd, 6).Workers, DriveProfile.StrategyFor(DriveKind.Unknown, 6).Workers);
    }

    [Fact]
    public void Configuration_ForcesTheKind_AndTheFolderCount()
    {
        try
        {
            AppConfig.Replace(new AppConfig { ScanDriveKind = 1 });
            var s = DriveProfile.For(@"C:\x");
            Assert.Equal(DriveKind.Hdd, s.Kind);
            Assert.True(s.Overridden && s.DiskOrder);
            AppConfig.Replace(new AppConfig { ScanDriveKind = 3 });
            Assert.Equal(DriveKind.Nvme, DriveProfile.For(@"C:\x").Kind);
            AppConfig.Replace(new AppConfig { ScanDriveKind = 1, ScanThreads = 3 });
            s = DriveProfile.For(@"C:\x");
            Assert.Equal((3, false), (s.Workers, s.DiskOrder));                    // several at once: no disk order
            AppConfig.Replace(new AppConfig { ScanDriveKind = 2 });
            Assert.Equal(DriveKind.Network, DriveProfile.For(@"\\server\share").Kind);   // a network path stays one
            AppConfig.Replace(new AppConfig());
            Assert.False(DriveProfile.For(@"C:\x").Overridden);
        }
        finally { AppConfig.Replace(new AppConfig()); }
    }

    [Fact]
    public void DiskOrderQueue_SweepsLikeAnElevator()
    {
        var q = new DiskOrderQueue<string>();
        q.Enqueue("root", 0);
        Assert.True(q.TryDequeue(out var x)); Assert.Equal("root", x);
        q.Enqueue("50", 50); q.Enqueue("10", 10); q.Enqueue("30", 30);
        Assert.True(q.TryDequeue(out x)); Assert.Equal("10", x);
        q.Enqueue("5", 5);                                           // behind the head: next sweep
        q.Enqueue("20", 20);                                         // ahead: this sweep
        q.Enqueue("unknown", 0);                                     // no record: right after the current one
        var order = new List<string>();
        while (q.TryDequeue(out x)) order.Add(x);
        Assert.Equal(new[] { "unknown", "20", "30", "50", "5" }, order);
        Assert.Equal(0, q.Count);
    }

    private static List<string> Lines(ScanTree t) => t.Root.Descendants().Select(n => n.FullPath + "|" + n.LogicalSize + "|" + n.DiskSize).OrderBy(s => s, StringComparer.Ordinal).ToList();

    private static ScanTree Scan(string dir, TimeSpan? wait = null)
    {
        var tree = new ScanTree(DataCache.CreateRoot(dir), live: true);
        tree.StartMasterScan();
        var sw = Stopwatch.StartNew();
        while (tree.IsMasterScanning && sw.Elapsed < (wait ?? TimeSpan.FromSeconds(20))) Thread.Sleep(5);
        return tree;
    }

    [Fact]
    public void HddStrategy_WalksInDiskOrder_WithTheSameResult_AndWaitsForRescans()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "hddorder");
        var listed = new List<long>();
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            var rnd = new Random(11);
            var folders = new List<string> { dir };
            for (int i = 0; i < 200; i++)
            {
                var f = Path.Combine(folders[rnd.Next(folders.Count)], "d" + i);
                Directory.CreateDirectory(f);
                folders.Add(f);
                System.IO.File.WriteAllBytes(Path.Combine(f, "x.bin"), new byte[rnd.Next(1, 9000)]);
            }
            AppConfig.Replace(new AppConfig { WatchDog = false, ScanDriveKind = 2 });
            var ssd = Scan(dir);
            Assert.Equal(ScanState.Completed, ssd.MasterState);
            Assert.Equal("SSD", ScanStrategy.KindName(ssd.MasterScan!.Strategy!.Kind));

            AppConfig.Replace(new AppConfig { WatchDog = false, ScanDriveKind = 1 });
            DirectoryScanner.Listed = n => { if (n is FolderNode f) lock (listed) listed.Add(f.FileId & 0x0000_FFFF_FFFF_FFFF); };
            var hdd = Scan(dir);
            DirectoryScanner.Listed = null;
            Assert.Equal(ScanState.Completed, hdd.MasterState);
            Assert.True(hdd.MasterScan!.Strategy!.DiskOrder);
            Assert.Equal(Lines(ssd), Lines(hdd));                                    // same tree, other order
            Assert.Equal(200, listed.Count);
            Assert.DoesNotContain(0L, listed);                                         // every folder had its record number
            int descents = listed.Zip(listed.Skip(1)).Count(p => p.Second < p.First);
            _out.WriteLine($"200 folders listed in {descents + 1} sweep(s) across the master file table");
            Assert.True(descents <= 20, "sweeps: " + (descents + 1));

            // a full scan of a spinning disk waits while a folder is rescanned
            var waiting = new ScanTree(DataCache.CreateRoot(dir), live: true);
            Interlocked.Increment(ref waiting.ActiveSubScans);
            waiting.StartMasterScan();
            Thread.Sleep(300);
            Assert.True(waiting.IsMasterScanning);
            Assert.Equal(0, waiting.Root.FileCount);
            Interlocked.Decrement(ref waiting.ActiveSubScans);
            var sw = Stopwatch.StartNew();
            while (waiting.IsMasterScanning && sw.ElapsedMilliseconds < 20000) Thread.Sleep(5);
            Assert.Equal(Lines(ssd), Lines(waiting));
        }
        finally
        {
            DirectoryScanner.Listed = null;
            AppConfig.Replace(new AppConfig());
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
