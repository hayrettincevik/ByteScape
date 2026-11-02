using System.Diagnostics;
using System.Runtime.InteropServices;
using ByteScape.Core.Config;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using Xunit;

namespace ByteScape.Tests;

/// <summary>File system corner cases of the folder scan: names Win32 normally cannot open, allocation beyond MAX_PATH.</summary>
[Collection("config")]
public class NtfsEdgeTests
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, IntPtr input, int inSize, IntPtr output, int outSize, out int returned, IntPtr overlapped);

    [Fact]
    public void IoPath_IsLiteral_ForTrailingDotsSpacesAndLongPaths()
    {
        Assert.Equal(@"C:\a\b", DirectoryScanner.IoPath(@"C:\a\b"));
        Assert.Equal(@"C:\", DirectoryScanner.IoPath(@"C:\"));
        Assert.Equal(@"C:\a\.git\b.c", DirectoryScanner.IoPath(@"C:\a\.git\b.c"));
        Assert.Equal(@"\\?\C:\a.\b", DirectoryScanner.IoPath(@"C:\a.\b"));
        Assert.Equal(@"\\?\C:\a\b ", DirectoryScanner.IoPath(@"C:\a\b "));
        Assert.Equal(@"\\?\C:\a\b.", DirectoryScanner.IoPath(@"C:\a\b."));
        Assert.Equal(@"\\?\UNC\srv\share\x.", DirectoryScanner.IoPath(@"\\srv\share\x."));
        Assert.Equal(@"\\?\C:\x.", DirectoryScanner.IoPath(@"\\?\C:\x."));
        var longPath = @"C:\" + string.Join('\\', Enumerable.Repeat(new string('f', 50), 5));
        Assert.Equal(@"\\?\" + longPath, DirectoryScanner.IoPath(longPath));
    }

    [Fact]
    public void Scan_ListsFoldersEndingInDotOrSpace_AndReadsAllocationBeyondMaxPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "ntfsedge");
        string X(string p) => @"\\?\" + p;
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(X(dir), true);
            Directory.CreateDirectory(X(Path.Combine(dir, "odd.")));
            System.IO.File.WriteAllBytes(X(Path.Combine(dir, "odd.", "inside.bin")), new byte[5000]);
            Directory.CreateDirectory(X(Path.Combine(dir, "space ")));
            System.IO.File.WriteAllBytes(X(Path.Combine(dir, "space ", "x.bin")), new byte[7000]);
            var deep = Path.Combine(dir, string.Join('\\', Enumerable.Repeat("a folder with a long name, forty characters", 7)));
            Assert.True(deep.Length > 300);
            Directory.CreateDirectory(X(deep));
            using (var fs = new FileStream(X(Path.Combine(deep, "sparse.bin")), FileMode.Create, FileAccess.ReadWrite))
            {
                Assert.True(DeviceIoControl(fs.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero));
                fs.SetLength(50L << 20);
                fs.Position = 10 << 20;
                var data = new byte[4096];
                new Random(1).NextBytes(data);
                fs.Write(data);
                fs.Flush(true);
            }

            AppConfig.Replace(new AppConfig { WatchDog = false });
            var tree = new ScanTree(DataCache.CreateRoot(dir), live: true);
            tree.StartMasterScan();
            var sw = Stopwatch.StartNew();
            while (tree.IsMasterScanning && sw.ElapsedMilliseconds < 20000) Thread.Sleep(5);
            Assert.Equal(ScanState.Completed, tree.MasterState);
            var odd = (ContainerNode)tree.Root.FindChild("odd.")!;
            Assert.False(odd.AccessDenied);                                    // was "denied": Win32 opened "odd" instead
            Assert.Equal(5000, odd.FindChild("inside.bin")!.LogicalSize);
            var space = (ContainerNode)tree.Root.FindChild("space ")!;
            Assert.False(space.AccessDenied);
            Assert.Equal(7000, space.LogicalSize);
            var sparse = tree.Root.Descendants().OfType<FileNode>().Single(f => f.Name == "sparse.bin");
            Assert.Equal(50L << 20, sparse.LogicalSize);
            Assert.InRange(sparse.DiskSize, 4096, 1 << 20);                     // what is allocated, not the 50 MB length
            Assert.DoesNotContain(tree.Root.Descendants().OfType<ContainerNode>(), c => c.AccessDenied);
        }
        finally
        {
            AppConfig.Replace(new AppConfig());
            try { Directory.Delete(X(dir), true); } catch { }
        }
    }
}
