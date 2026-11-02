using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ByteScape.Core.Util;
using Microsoft.Win32.SafeHandles;

namespace ByteScape.Core.Scanning.Mft;

/// <summary>Raw, sector aligned reads of a volume (the real one, or an image in the tests).</summary>
public interface IVolumeReader
{
    void Read(long offset, Span<byte> buffer);
}

/// <summary>What NTFS reports about a volume's layout (FSCTL_GET_NTFS_VOLUME_DATA).</summary>
public sealed record NtfsGeometry(int BytesPerSector, int BytesPerCluster, int BytesPerRecord, long MftStartLcn, long MftValidDataLength);

/// <summary>
/// Reads the master file table of an NTFS volume: the $MFT's own record gives its (possibly fragmented) extents and
/// its allocation bitmap; only the stretches holding records in use are read, in large sector aligned chunks, by
/// several readers while several parsers (one per processor core) decode the chunks that arrived. Read only; needs a
/// volume handle, i.e. administrator rights.
/// </summary>
public static class MftReader
{
    private const int Chunk = 4 << 20;
    /// <summary>Free stretches shorter than this (in bytes) are read through (measured: the bytes read, not the number of reads, set the time; 256 KB keeps both low).</summary>
    private const int MergeGapBytes = 256 << 10;

    /// <summary>Where the time of a read went (reading and parsing overlap: the total is less than their sum).</summary>
    public sealed class Stats
    {
        public long Bytes, Skipped;
        public int Reads, Fragments, SmallReads, Parsers;
        public bool Bitmap;
        public TimeSpan ReadTime, ParseTime, LinkTime, Total;
        public override string ToString() =>
            $"{Bytes / (double)(1 << 20):N0} MB read in {Reads:N0} reads ({SmallReads:N0} under 256 KB; {Skipped / (double)(1 << 20):N0} MB of free records skipped{(Bitmap ? "" : ", no bitmap")}), " +
            $"$MFT in {Fragments:N0} fragments; reading {ReadTime.TotalMilliseconds:N0} ms (all readers), parsing {ParseTime.TotalMilliseconds:N0} ms ({Parsers} parsers together), " +
            $"linking {LinkTime.TotalMilliseconds:N0} ms, total {Total.TotalMilliseconds:N0} ms";
    }

    /// <summary>The $MFT's data runs and, when readable, its allocation bitmap (bit set = record in use).</summary>
    internal sealed record Layout(List<(long Lcn, long Clusters)> Runs, byte[]? Bitmap);

    /// <summary>Native memory aligned for unbuffered reads (sector aligned addresses).</summary>
    private sealed unsafe class AlignedBuffer : IDisposable
    {
        private void* _p;
        public readonly int Length;
        public AlignedBuffer(int length) { Length = length; _p = NativeMemory.AlignedAlloc((nuint)length, 4096); }
        public Span<byte> Span => new(_p, Length);
        public void Dispose() { if (_p != null) { NativeMemory.AlignedFree(_p); _p = null; } }
    }

    /// <param name="parsers">Threads decoding records; 0 = one per processor core (at most 8).</param>
    /// <param name="pause">Closed while the scan is paused: the readers wait on it before each chunk.</param>
    public static MftIndex Read(IVolumeReader vol, NtfsGeometry g, CancellationToken token, bool adsSizes = true, Action<double>? progress = null, Stats? stats = null, int readers = 1, bool useBitmap = true, int parsers = 0, ManualResetEventSlim? pause = null)
    {
        var total0 = Stopwatch.StartNew();
        int rs = g.BytesPerRecord;
        if (rs < 512 || rs > 65536 || g.BytesPerCluster < 512 || g.MftValidDataLength < rs) throw new InvalidDataException("Unexpected NTFS geometry");
        var layout = MftLayout(vol, g, useBitmap);
        var runs = layout.Runs;
        long mftBytes = runs.Sum(r => r.Clusters) * (long)g.BytesPerCluster;
        long records = Math.Min(g.MftValidDataLength, mftBytes) / rs;
        if (records > int.MaxValue) throw new InvalidDataException("MFT too large");
        var ix = new MftIndex((int)records, g.BytesPerCluster);
        int perChunk = Math.Max(1, Chunk / rs);
        int mergeGap = Math.Max(1, MergeGapBytes / rs);
        long chunks = (records + perChunk - 1) / perChunk;
        var bitmap = layout.Bitmap;

        // the stretches of a chunk that hold records in use (everything when there is no bitmap, or past its end)
        List<(int First, int Count)> Plan(long chunk)
        {
            long r0 = chunk * perChunk;
            int n = (int)Math.Min(perChunk, records - r0);
            var segs = new List<(int, int)>();
            if (bitmap == null) { segs.Add((0, n)); return segs; }
            int start = -1, last = -1;
            for (int i = 0; i < n; i++)
            {
                long r = r0 + i;
                bool used = r / 8 >= bitmap.Length || (bitmap[r / 8] & (1 << (int)(r % 8))) != 0;
                if (!used) continue;
                if (start >= 0 && i - last > mergeGap) { segs.Add((start, last - start + 1)); start = -1; }
                if (start < 0) start = i;
                last = i;
            }
            if (start >= 0) segs.Add((start, last - start + 1));
            return segs;
        }

        readers = Math.Clamp(readers, 1, 8);
        parsers = parsers > 0 ? Math.Clamp(parsers, 1, 16) : Math.Clamp(Environment.ProcessorCount, 1, 8);
        int pool = readers + parsers + 1;
        var buffers = new List<AlignedBuffer>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var free = new BlockingCollection<AlignedBuffer>(pool);
        using var full = new BlockingCollection<(AlignedBuffer Buf, long Chunk, List<(int First, int Count)> Segs)>(pool);
        Exception? readError = null, parseError = null;
        long readTicks = 0, parseTicks = 0, nextChunk = -1, bytesRead = 0, skipped = 0, done = 0;
        int active = readers, inUse = 0;
        // names per chunk, joined in chunk order afterwards: the index is the same whatever order the chunks were parsed in
        var chunkLinks = new List<MftIndex.NameLink>?[chunks];
        // extension records write into their base record's entries: kept aside and parsed once every base record is in
        var extensions = new ConcurrentBag<(uint Record, byte[] Bytes)>();
        var counting = new CountingReader(vol);
        var threads = new List<Thread>();
        try
        {
            for (int i = 0; i < pool; i++) { var b = new AlignedBuffer(perChunk * rs); buffers.Add(b); free.Add(b); }
            void ReadLoop()
            {
                try
                {
                    while (true)
                    {
                        long c = Interlocked.Increment(ref nextChunk);
                        if (c >= chunks) break;
                        var segs = Plan(c);
                        long n = Math.Min(perChunk, records - c * perChunk);
                        long want = segs.Sum(s => (long)s.Count);
                        Interlocked.Add(ref skipped, (n - want) * rs);
                        if (segs.Count == 0) continue;
                        pause?.Wait(cts.Token);
                        var buf = free.Take(cts.Token);
                        long t = Stopwatch.GetTimestamp();
                        foreach (var (first, count) in segs)
                            ReadVirtual(counting, g, runs, (c * perChunk + first) * rs, buf.Span.Slice(first * rs, count * rs));
                        Interlocked.Add(ref readTicks, Stopwatch.GetTimestamp() - t);
                        Interlocked.Add(ref bytesRead, want * rs);
                        full.Add((buf, c, segs), cts.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Interlocked.CompareExchange(ref readError, ex, null); cts.Cancel(); }
                finally { if (Interlocked.Decrement(ref active) == 0) full.CompleteAdding(); }
            }
            void ParseLoop()
            {
                try
                {
                    foreach (var (buf, c, segs) in full.GetConsumingEnumerable(cts.Token))
                    {
                        long t = Stopwatch.GetTimestamp();
                        var links = new List<MftIndex.NameLink>(perChunk);
                        int used = 0;
                        var span = buf.Span;
                        foreach (var (first, count) in segs)
                            for (int i = first; i < first + count; i++)
                            {
                                var rec = span.Slice(i * rs, rs);
                                if (!MftParser.ApplyFixups(rec)) continue;
                                uint recNo = (uint)(c * perChunk + i);
                                if (MftParser.IsExtension(rec)) extensions.Add((recNo, rec.ToArray()));
                                else if (MftParser.ParseInto(rec, recNo, ix, adsSizes, links)) used++;
                            }
                        chunkLinks[c] = links;
                        Interlocked.Add(ref inUse, used);
                        Interlocked.Add(ref parseTicks, Stopwatch.GetTimestamp() - t);
                        free.Add(buf);
                        progress?.Invoke((double)Interlocked.Increment(ref done) / chunks);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Interlocked.CompareExchange(ref parseError, ex, null); cts.Cancel(); }
            }
            for (int i = 0; i < readers; i++)
            {
                var th = new Thread(ReadLoop) { IsBackground = true, Name = "MftRead" + i, Priority = Thread.CurrentThread.Priority };
                threads.Add(th);
                th.Start();
            }
            var parserThreads = new List<Thread>();
            for (int i = 0; i < parsers; i++)
            {
                var th = new Thread(ParseLoop) { IsBackground = true, Name = "MftParse" + i, Priority = Thread.CurrentThread.Priority };
                parserThreads.Add(th);
                th.Start();
            }
            foreach (var th in parserThreads) th.Join();
        }
        finally
        {
            cts.Cancel();       // unblocks the readers when parsing failed; harmless once they are done
            foreach (var th in threads) th.Join();
            foreach (var b in buffers) b.Dispose();
        }
        if (readError != null) throw readError;
        if (parseError != null) throw parseError;
        token.ThrowIfCancellationRequested();
        long te = Stopwatch.GetTimestamp();
        foreach (var l in chunkLinks) if (l != null) ix.Links.AddRange(l);
        foreach (var (recNo, bytes) in extensions.OrderBy(e => e.Record))
            if (MftParser.ParseInto(bytes, recNo, ix, adsSizes, ix.Links)) inUse++;
        ix.InUseCount += inUse;
        parseTicks += Stopwatch.GetTimestamp() - te;
        var tl = Stopwatch.StartNew();
        ix.BuildChildren();
        if (stats != null)
        {
            stats.Bytes = bytesRead; stats.Skipped = skipped; stats.Bitmap = bitmap != null;
            stats.Fragments = runs.Count;
            stats.Reads = counting.Count;
            stats.SmallReads = counting.Small;
            stats.ReadTime = Stopwatch.GetElapsedTime(0, readTicks);
            stats.ParseTime = Stopwatch.GetElapsedTime(0, parseTicks);
            stats.Parsers = parsers;
            stats.LinkTime = tl.Elapsed;
            stats.Total = total0.Elapsed;
        }
        return ix;
    }

    private sealed class CountingReader : IVolumeReader
    {
        private readonly IVolumeReader _inner;
        public int Count, Small;
        public CountingReader(IVolumeReader inner) { _inner = inner; }
        public void Read(long offset, Span<byte> buffer)
        {
            Interlocked.Increment(ref Count);
            if (buffer.Length < 256 << 10) Interlocked.Increment(ref Small);
            _inner.Read(offset, buffer);
        }
    }

    /// <summary>
    /// Extents of the $MFT file from record 0 (and, for a very fragmented MFT, the extension records its attribute
    /// list names), plus its allocation bitmap when it can be read completely enough to trust.
    /// </summary>
    internal static Layout MftLayout(IVolumeReader vol, NtfsGeometry g, bool withBitmap)
    {
        int rs = g.BytesPerRecord;
        var rec0 = ReadAligned(vol, g, g.MftStartLcn * g.BytesPerCluster, rs);
        if (!MftParser.ApplyFixups(rec0)) throw new InvalidDataException("$MFT record 0 is damaged");
        var extents = MftParser.DataExtents(rec0);
        var first = extents.Where(e => e.LowestVcn == 0).Select(e => e.Runs).FirstOrDefault() ?? throw new InvalidDataException("$MFT has no data runs");
        foreach (var ext in MftParser.DataExtensionRecords(rec0, 0))
        {
            // extension records of the $MFT lie in its first extent
            var r = new byte[rs];
            ReadVirtual(vol, g, first, (long)ext * rs, r);
            if (MftParser.ApplyFixups(r)) extents.AddRange(MftParser.DataExtents(r).Where(e => e.LowestVcn > 0));
        }
        var all = new List<(long, long)>();
        foreach (var e in extents.OrderBy(e => e.LowestVcn).DistinctBy(e => e.LowestVcn)) all.AddRange(e.Runs);

        byte[]? bitmap = null;
        if (withBitmap)
        {
            try
            {
                var (resident, bmRuns, size) = MftParser.BitmapOf(rec0);
                if (resident != null) bitmap = resident;
                else if (bmRuns != null && size > 0 && size < 1L << 30)
                {
                    // only the part the runs of this record cover: bits past it count as "in use" (read, never skipped)
                    long covered = Math.Min(size, bmRuns.Sum(r => r.Clusters) * (long)g.BytesPerCluster);
                    var buf = new byte[(covered + g.BytesPerCluster - 1) / g.BytesPerCluster * g.BytesPerCluster];
                    ReadVirtual(vol, g, bmRuns, 0, buf);
                    bitmap = buf.AsSpan(0, (int)covered).ToArray();
                }
            }
            catch (Exception ex) { Log.Debug("$MFT bitmap unreadable, reading every record: " + ex.Message); bitmap = null; }
        }
        return new Layout(all, bitmap);
    }

    private static byte[] ReadAligned(IVolumeReader vol, NtfsGeometry g, long offset, int length)
    {
        int sector = Math.Max(512, g.BytesPerSector);
        long start = offset / sector * sector;
        int span = (int)((offset + length - start + sector - 1) / sector * sector);
        var tmp = new byte[span];
        vol.Read(start, tmp);
        return tmp.AsSpan((int)(offset - start), length).ToArray();
    }

    /// <summary>Reads bytes of a non-resident attribute (virtual offsets) through its runs; sparse or missing parts read as zeros.</summary>
    internal static void ReadVirtual(IVolumeReader vol, NtfsGeometry g, List<(long Lcn, long Clusters)> runs, long vOffset, Span<byte> dst)
    {
        long cluster = g.BytesPerCluster;
        long runStart = 0;
        int done = 0;
        foreach (var (lcn, clusters) in runs)
        {
            long runBytes = clusters * cluster;
            long runEnd = runStart + runBytes;
            long want = vOffset + done;
            if (want < runEnd && done < dst.Length)
            {
                long inRun = want - runStart;
                int n = (int)Math.Min(dst.Length - done, runBytes - inRun);
                if (lcn < 0) dst.Slice(done, n).Clear();
                else vol.Read(lcn * cluster + inRun, dst.Slice(done, n));
                done += n;
            }
            if (done >= dst.Length) return;
            runStart = runEnd;
        }
        if (done < dst.Length) dst.Slice(done).Clear();
    }
}

/// <summary>
/// A local volume opened for raw reading (\\.\C:). Large aligned reads go through a second, unbuffered handle
/// (FILE_FLAG_NO_BUFFERING): straight from the disk into our buffer instead of through the cache manager, which is
/// both faster and leaves the file cache alone. Small or unaligned reads use the buffered handle.
/// </summary>
public sealed class WindowsVolume : IVolumeReader, IDisposable
{
    private readonly SafeFileHandle _h;
    private readonly string _name;
    private volatile bool _directOk;
    // one unbuffered handle per reader thread: the I/O manager serializes the requests of a synchronous handle, so
    // readers sharing one would take turns instead of keeping several reads in flight
    private readonly ThreadLocal<SafeFileHandle?> _direct;
    private int _sector = 512;
    public int DirectReads, BufferedReads;

    private WindowsVolume(SafeFileHandle h, string name, bool directOk)
    {
        _h = h; _name = name; _directOk = directOk;
        _direct = new ThreadLocal<SafeFileHandle?>(() =>
        {
            var d = CreateFileW(_name, 0x80000000, 3, IntPtr.Zero, 3, 0x20000000 /*NO_BUFFERING*/, IntPtr.Zero);
            if (d.IsInvalid) { d.Dispose(); return null; }
            return d;
        }, trackAllValues: true);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

    /// <summary>Opens a drive ("C:") for reading; throws UnauthorizedAccessException without administrator rights.</summary>
    public static WindowsVolume Open(string drive)
    {
        var name = @"\\.\" + drive.TrimEnd('\\');
        var h = CreateFileW(name, 0x80000000 /*GENERIC_READ*/, 3 /*share read+write*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0x08000000 /*SEQUENTIAL_SCAN*/, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            if (err == 5) throw new UnauthorizedAccessException("Reading " + name + " needs administrator rights");
            throw new IOException("Cannot open " + name + " (error " + err + ")");
        }
        var probe = CreateFileW(name, 0x80000000, 3, IntPtr.Zero, 3, 0x20000000 /*NO_BUFFERING*/, IntPtr.Zero);
        bool directOk = !probe.IsInvalid;
        probe.Dispose();
        return new WindowsVolume(h, name, directOk);
    }

    public NtfsGeometry Geometry()
    {
        var b = new byte[96];
        if (!DeviceIoControl(_h, 0x00090064 /*FSCTL_GET_NTFS_VOLUME_DATA*/, IntPtr.Zero, 0, b, b.Length, out _, IntPtr.Zero))
            throw new IOException("Not an NTFS volume (FSCTL_GET_NTFS_VOLUME_DATA error " + Marshal.GetLastWin32Error() + ")");
        var g = new NtfsGeometry(BitConverter.ToInt32(b, 40), BitConverter.ToInt32(b, 44), BitConverter.ToInt32(b, 48), BitConverter.ToInt64(b, 64), BitConverter.ToInt64(b, 56));
        _sector = Math.Max(512, g.BytesPerSector);
        return g;
    }

    public unsafe void Read(long offset, Span<byte> buffer)
    {
        SafeFileHandle? direct = null;
        if (_directOk && offset % _sector == 0 && buffer.Length % _sector == 0)
        {
            bool aligned;
            fixed (byte* p = buffer) aligned = (nint)p % _sector == 0;
            if (aligned) direct = _direct.Value;
        }
        if (direct != null)
        {
            try { ReadAll(direct, offset, buffer); Interlocked.Increment(ref DirectReads); return; }
            catch (IOException ex)
            {
                Log.Debug("Unbuffered volume read failed (" + ex.Message + "), using buffered reads");
                _directOk = false;
            }
        }
        ReadAll(_h, offset, buffer);
        Interlocked.Increment(ref BufferedReads);
    }

    private static void ReadAll(SafeFileHandle h, long offset, Span<byte> buffer)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            int n = RandomAccess.Read(h, buffer.Slice(done), offset + done);
            if (n <= 0) throw new EndOfStreamException("Volume read past the end at " + (offset + done));
            done += n;
        }
    }

    public void Dispose()
    {
        _h.Dispose();
        foreach (var d in _direct.Values) d?.Dispose();
        _direct.Dispose();
    }
}

/// <summary>When and how the fast NTFS scan is used by a master scan.</summary>
public static class MftScan
{
    /// <summary>Tests: pretend the process is (not) elevated.</summary>
    internal static Func<bool> IsElevated = () => Environment.IsPrivilegedProcess;

    /// <summary>
    /// Diagnostics: read the master file table for folder scans too. Off in the application: the whole drive's table is read
    /// whatever the folder (7 s on a 1.8 million record C:), while listing a folder of 10,000 files takes 0.03 s.
    /// </summary>
    public static bool ForFolders { get; set; }

    /// <summary>
    /// True when a master scan of <paramref name="path"/> can read the master file table: the option is on, links are
    /// not followed (the MFT scan never follows them), the process runs as administrator and the path is the root of a
    /// local NTFS drive.
    /// </summary>
    public static bool CanUse(string path, out string reason)
    {
        var cfg = Config.AppConfig.Current;
        reason = "";
        if (!cfg.UseMftScan) { reason = "turned off in the configuration"; return false; }
        if (cfg.FollowReparsePoints) { reason = "junctions and symbolic links are followed"; return false; }
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length < 2 || path[1] != ':') { reason = "not a local drive"; return false; }
        if (!ForFolders && !DataCache.IsDriveRoot(path)) { reason = "a folder: listing it is faster than reading the whole drive's master file table"; return false; }
        if (!IsElevated()) { reason = "not running as administrator"; return false; }
        try
        {
            var d = new DriveInfo(path.Substring(0, 2));
            if (!string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) { reason = d.DriveFormat + " is not NTFS"; return false; }
            if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) { reason = d.DriveType + " drive"; return false; }
        }
        catch (Exception ex) { reason = ex.Message; return false; }
        return true;
    }

    /// <summary>Reads the MFT of the drive <paramref name="path"/> lies on.</summary>
    public static MftIndex ReadVolume(string path, CancellationToken token, bool adsSizes, Action<double>? progress = null, MftReader.Stats? stats = null, ManualResetEventSlim? pause = null)
    {
        stats ??= new MftReader.Stats();
        using var vol = WindowsVolume.Open(path.Substring(0, 2));
        var g = vol.Geometry();
        // several outstanding reads for SSDs, one for spinning disks (the MFT is read in order there)
        int readers = DriveProfile.For(path).MftReaders;   // HDD 1 (reads in order), SATA SSD 4, NVMe 8
        var ix = MftReader.Read(vol, g, token, adsSizes, progress, stats, readers, pause: pause);
        Log.Debug($"NTFS MFT of {path.Substring(0, 2)}: {ix.Count:N0} records ({ix.InUseCount:N0} in use), {ix.Links.Count:N0} names; {stats}; {vol.DirectReads:N0} unbuffered / {vol.BufferedReads:N0} buffered reads");
        LastReadInfo = $"{stats}; {vol.DirectReads:N0} unbuffered / {vol.BufferedReads:N0} buffered reads, {readers} readers";
        return ix;
    }

    /// <summary>Diagnostics: how the last volume read went.</summary>
    public static string LastReadInfo { get; private set; } = "";
}
