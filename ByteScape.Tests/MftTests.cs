using System.Buffers.Binary;
using System.Text;
using ByteScape.Core.Config;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using ByteScape.Core.Scanning.Mft;
using Xunit;
using Xunit.Abstractions;

namespace ByteScape.Tests;

/// <summary>Builds FILE records byte by byte the way NTFS lays them out (header, attributes, update sequence fixups).</summary>
internal sealed class RecordBuilder
{
    private readonly List<byte[]> _attrs = new();
    private ushort _flags = 1, _seq = 1, _instance;
    private ulong _baseRef;

    private static int Align8(int n) => (n + 7) & ~7;
    private static void W16(byte[] b, int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), (ushort)v);
    private static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static void W64(byte[] b, int o, long v) => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(o), v);

    public static ulong Ref(uint record, ushort seq) => ((ulong)seq << 48) | record;

    public RecordBuilder Dir() { _flags |= 2; return this; }
    public RecordBuilder Free() { _flags = 0; return this; }
    public RecordBuilder Seq(ushort s) { _seq = s; return this; }
    public RecordBuilder ExtensionOf(uint record, ushort seq) { _baseRef = Ref(record, seq); return this; }

    private RecordBuilder Resident(uint type, byte[] value, string? name = null)
    {
        int nameLen = name?.Length ?? 0;
        int valueOfs = Align8(0x18 + nameLen * 2);
        var a = new byte[Align8(valueOfs + value.Length)];
        W32(a, 0, type); W32(a, 4, (uint)a.Length); a[8] = 0; a[9] = (byte)nameLen; W16(a, 10, 0x18); W16(a, 14, _instance++);
        W32(a, 0x10, (uint)value.Length); W16(a, 0x14, valueOfs);
        if (name != null) Encoding.Unicode.GetBytes(name).CopyTo(a, 0x18);
        value.CopyTo(a, valueOfs);
        _attrs.Add(a);
        return this;
    }

    public RecordBuilder StdInfo(long created, long modified, long accessed, uint attrs)
    {
        var v = new byte[0x48];
        W64(v, 0, created); W64(v, 8, modified); W64(v, 0x10, modified); W64(v, 0x18, accessed); W32(v, 0x20, attrs);
        return Resident(0x10, v);
    }

    /// <summary>ns: 0 POSIX, 1 Win32, 2 DOS (8.3 alias), 3 Win32 and DOS.</summary>
    public RecordBuilder FileName(ulong parent, string name, byte ns = 1)
    {
        var v = new byte[0x42 + name.Length * 2];
        BinaryPrimitives.WriteUInt64LittleEndian(v, parent);
        v[0x40] = (byte)name.Length; v[0x41] = ns;
        Encoding.Unicode.GetBytes(name).CopyTo(v, 0x42);
        return Resident(0x30, v);
    }

    public RecordBuilder ResidentData(int size, string? stream = null) => Resident(0x80, new byte[size], stream);

    public RecordBuilder Reparse(uint tag)
    {
        var v = new byte[8];
        W32(v, 0, tag);
        return Resident(0xC0, v);
    }

    public RecordBuilder AttributeList(params (uint Type, ulong Segment, long LowestVcn)[] entries)
    {
        var v = new byte[entries.Length * 0x20];
        for (int i = 0; i < entries.Length; i++)
        {
            int o = i * 0x20;
            W32(v, o, entries[i].Type); W16(v, o + 4, 0x20); v[o + 6] = 0; v[o + 7] = 0x1A;
            W64(v, o + 8, entries[i].LowestVcn); BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(o + 0x10), entries[i].Segment);
        }
        return Resident(0x20, v);
    }

    public bool InUse => (_flags & 1) != 0;

    public RecordBuilder NonResidentData(long size, long alloc, (long Lcn, long Clusters)[] runs, long lowVcn = 0, ushort flags = 0, long? totalAlloc = null, string? stream = null, uint type = 0x80)
    {
        int nameLen = stream?.Length ?? 0;
        int hdr = totalAlloc.HasValue ? 0x48 : 0x40;
        int mpOfs = Align8(hdr + nameLen * 2);
        var mp = EncodeRuns(runs);
        var a = new byte[Align8(mpOfs + mp.Length + 1)];
        W32(a, 0, type); W32(a, 4, (uint)a.Length); a[8] = 1; a[9] = (byte)nameLen; W16(a, 10, hdr); W16(a, 12, flags); W16(a, 14, _instance++);
        long clusters = runs.Sum(r => r.Clusters);
        W64(a, 0x10, lowVcn); W64(a, 0x18, lowVcn + Math.Max(0, clusters - 1)); W16(a, 0x20, mpOfs); a[0x22] = (byte)((flags & 0xFF) != 0 ? 4 : 0);
        W64(a, 0x28, alloc); W64(a, 0x30, size); W64(a, 0x38, size);
        if (totalAlloc.HasValue) W64(a, 0x40, totalAlloc.Value);
        if (stream != null) Encoding.Unicode.GetBytes(stream).CopyTo(a, hdr);
        mp.CopyTo(a, mpOfs);
        _attrs.Add(a);
        return this;
    }

    /// <summary>Mapping pairs: run length (unsigned) and LCN delta (signed, relative), minimal byte counts, 0 terminated.</summary>
    public static byte[] EncodeRuns((long Lcn, long Clusters)[] runs)
    {
        var o = new List<byte>();
        long prev = 0;
        foreach (var (lcn, len) in runs)
        {
            int lb = 1; while (lb < 8 && (len >> (8 * lb)) != 0) lb++;
            if (lcn < 0) { o.Add((byte)lb); for (int i = 0; i < lb; i++) o.Add((byte)(len >> (8 * i))); continue; }
            long d = lcn - prev;
            int ob = 1; while (ob < 8 && (d < -(1L << (8 * ob - 1)) || d >= (1L << (8 * ob - 1)))) ob++;
            o.Add((byte)((ob << 4) | lb));
            for (int i = 0; i < lb; i++) o.Add((byte)(len >> (8 * i)));
            for (int i = 0; i < ob; i++) o.Add((byte)(d >> (8 * i)));
            prev = lcn;
        }
        o.Add(0);
        return o.ToArray();
    }

    /// <summary>The record, with the update sequence applied (last two bytes of every 512 byte stride = sequence number).</summary>
    public byte[] Build(uint recordNumber, int size = 1024, bool torn = false)
    {
        var r = new byte[size];
        int usaCount = size / 512 + 1;
        int first = Align8(0x30 + usaCount * 2);
        "FILE"u8.CopyTo(r);
        W16(r, 4, 0x30); W16(r, 6, usaCount); W16(r, 0x10, _seq); W16(r, 0x12, 1); W16(r, 0x14, first); W16(r, 0x16, _flags);
        W32(r, 0x1C, (uint)size); BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(0x20), _baseRef); W16(r, 0x28, _instance); W32(r, 0x2C, recordNumber);
        int p = first;
        foreach (var a in _attrs) { a.CopyTo(r, p); p += a.Length; }
        W32(r, p, 0xFFFFFFFF); p += 8;
        if (p > size) throw new InvalidOperationException("record overflow");
        W32(r, 0x18, (uint)p);
        const ushort usn = 0x0007;
        W16(r, 0x30, usn);
        for (int i = 1; i < usaCount; i++)
        {
            int end = i * 512 - 2;
            r[0x30 + 2 * i] = r[end]; r[0x30 + 2 * i + 1] = r[end + 1];
            W16(r, end, torn && i == 2 ? usn + 1 : usn);
        }
        return r;
    }
}

/// <summary>An NTFS volume image in memory: 4 KB clusters, 1 KB records, the $MFT in two fragments (LCN 10 and LCN 40).</summary>
internal sealed class NtfsImage : IVolumeReader
{
    public const int Cluster = 4096, RecordSize = 1024, Records = 64;
    public readonly byte[] Data = new byte[48 * Cluster];
    public int Reads;
    public NtfsGeometry Geometry => new(512, Cluster, RecordSize, 10, Records * RecordSize);

    public void Read(long offset, Span<byte> buffer)
    {
        Assert.True(offset % 512 == 0 && buffer.Length % 512 == 0, $"unaligned volume read {offset}+{buffer.Length}");
        Interlocked.Increment(ref Reads);
        Data.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
    }

    public void Put(uint record, byte[] bytes)
    {
        long ofs = record < 32 ? 10L * Cluster + record * RecordSize : 40L * Cluster + (record - 32) * RecordSize;
        bytes.CopyTo(Data, ofs);
    }
}

[Collection("config")]
public class MftTests
{
    private readonly ITestOutputHelper _out;
    public MftTests(ITestOutputHelper o) => _out = o;

    private static readonly long T0 = new DateTime(2025, 5, 1, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
    private static readonly long T1 = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc).ToFileTimeUtc();
    private static ulong R(uint rec, ushort seq = 1) => RecordBuilder.Ref(rec, seq);
    private static RecordBuilder File(uint parent, string name, uint attrs = 0x20, ushort parentSeq = 1) => new RecordBuilder().StdInfo(T0, T1, T1, attrs).FileName(R(parent, parentSeq), name);

    /// <summary>
    /// X:\ Users\Ali with every kind of record: resident, large, compressed, sparse, hard linked, data in an extension
    /// record, a junction, a cloud sync root with an online-only file, deleted / orphaned / stale / torn records, an
    /// alternate data stream, a Unicode name, System Volume Information and NTFS metadata.
    /// </summary>
    internal static NtfsImage Build(bool mftViaAttributeList = false, bool changed = false)
    {
        var img = new NtfsImage();
        var inUse = new SortedSet<uint>();
        void Put(uint n, RecordBuilder b, bool torn = false) { img.Put(n, b.Build(n, NtfsImage.RecordSize, torn)); if (b.InUse) inUse.Add(n); }
        // $MFT itself: two fragments, either in its own record or continued in extension record 20 through an attribute list;
        // its allocation bitmap (one bit per record) lives in cluster 30
        var mft = new RecordBuilder().StdInfo(T0, T0, T0, 6).FileName(R(5, 5), "$MFT", 3)
                   .NonResidentData(NtfsImage.Records / 8, NtfsImage.Cluster, new[] { (30L, 1L) }, type: 0xB0);
        if (!mftViaAttributeList) Put(0, mft.NonResidentData(NtfsImage.Records * NtfsImage.RecordSize, NtfsImage.Records * NtfsImage.RecordSize, new[] { (10L, 8L), (40L, 8L) }));
        else
        {
            Put(0, mft.AttributeList((0x80, R(0), 0), (0x80, R(20), 8)).NonResidentData(NtfsImage.Records * NtfsImage.RecordSize, NtfsImage.Records * NtfsImage.RecordSize, new[] { (10L, 8L) }));
            Put(20, new RecordBuilder().ExtensionOf(0, 1).NonResidentData(0, 0, new[] { (40L, 8L) }, lowVcn: 8));
        }
        Put(5, new RecordBuilder().Seq(5).Dir().StdInfo(T0, T0, T0, 0x16).FileName(R(5, 5), ".", 3));
        Put(11, new RecordBuilder().Dir().StdInfo(T0, T0, T0, 0x26).FileName(R(5, 5), "$Extend", 3));
        Put(24, File(11, "$Quota").ResidentData(10));
        Put(30, new RecordBuilder().Seq(3).Dir().StdInfo(T0, T1, T1, 0x11).FileName(R(5, 5), "Users", 3));
        Put(31, new RecordBuilder().Seq(9).Dir().StdInfo(T0, T1, T1, 0x10).FileName(R(30, 3), "Ali", 3));
        if (!changed) Put(32, File(31, "notes.txt", parentSeq: 9).ResidentData(100));
        Put(33, File(31, "Large Movie.mkv", parentSeq: 9).FileName(R(31, 9), "LARGEM~1.MKV", 2)
                   .NonResidentData(changed ? 20_000_000 : 10_000_000, 10_002_432, new[] { (100L, 2442L) }));
        Put(34, File(31, "packed.bin", 0x820, 9).NonResidentData(1_000_000, 1_003_520, new[] { (3000L, 50L), (-1L, 195L) }, flags: 0x0001, totalAlloc: 204_800));
        Put(35, File(31, "sparse.vhd", 0x220, 9).NonResidentData(1L << 30, 1L << 30, new[] { (5000L, 2L), (-1L, 262142L) }, flags: 0x8000, totalAlloc: 8192));
        Put(36, File(31, "hardlink.txt", parentSeq: 9).FileName(R(30, 3), "same.txt").ResidentData(300));
        Put(37, File(31, "big.iso", parentSeq: 9));
        Put(38, new RecordBuilder().ExtensionOf(37, 1).NonResidentData(5_000_000, 5_001_216, new[] { (9000L, 1221L) }));
        Put(39, new RecordBuilder().Dir().StdInfo(T0, T1, T1, 0x410).FileName(R(31, 9), "Link").Reparse(0xA0000003));
        Put(40, File(39, "inside.txt").ResidentData(10));
        Put(41, new RecordBuilder().Dir().StdInfo(T0, T1, T1, 0x410).FileName(R(31, 9), "OneDrive").Reparse(0x9000001A));
        Put(42, File(41, "cloud.docx").ResidentData(200));
        Put(43, File(41, "online.mp4", 0x400220).NonResidentData(50L << 20, 50L << 20, new[] { (-1L, 12800L) }, flags: 0x8000, totalAlloc: 0));
        Put(44, File(31, "gone.txt", parentSeq: 9).ResidentData(10).Free());
        Put(45, File(50, "orphan.txt").ResidentData(10));
        Put(46, File(31, "stale.txt", parentSeq: 99).ResidentData(10));
        Put(47, File(31, "ads.txt", parentSeq: 9).ResidentData(10).ResidentData(26, "Zone.Identifier"));
        Put(48, File(31, "ÇALIŞMA ödev.docx", parentSeq: 9).ResidentData(500));
        Put(49, new RecordBuilder().Dir().StdInfo(T0, T1, T1, 0x16).FileName(R(5, 5), "System Volume Information", 3));
        Put(51, File(49, "tracking.log").ResidentData(400));
        Put(52, File(31, "torn.txt", parentSeq: 9).ResidentData(10), torn: true);
        Put(53, File(31, "zero.txt", parentSeq: 9).ResidentData(0));
        // compressed by Windows (WOF / CompactOS): the main stream is empty and sparse, the data sits in a hidden named stream
        Put(55, File(31, "compressed.pak", parentSeq: 9)
                   .NonResidentData(557_008, 557_056, new[] { (-1L, 136L) }, flags: 0x8000, totalAlloc: 0)
                   .NonResidentData(180_000, 188_416, new[] { (7000L, 46L) }, stream: "WofCompressedData")
                   .Reparse(0x80000017));
        if (changed) Put(54, File(30, "new.txt", parentSeq: 3).ResidentData(700));
        foreach (var r in inUse) img.Data[30 * NtfsImage.Cluster + r / 8] |= (byte)(1 << (int)(r % 8));
        return img;
    }

    [Fact]
    public void Fixups_AndMappingPairs()
    {
        var rec = File(5, "a.txt").ResidentData(10).Build(99);
        var copy = (byte[])rec.Clone();
        Assert.True(MftParser.ApplyFixups(copy));
        Assert.Equal(0x0007, BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(510)));      // on disk: the sequence number
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(510)));           // fixed up: the real bytes
        Assert.False(MftParser.ApplyFixups(File(5, "b.txt").Build(98, torn: true)));          // torn write
        Assert.False(MftParser.ApplyFixups(new byte[1024]));                                  // free space, no FILE signature
        var runs = new[] { (1000L, 16L), (300L, 5L), (-1L, 7L), (70000L, 300000L), (2L, 1L) };
        Assert.Equal(runs, MftParser.DecodeRuns(RecordBuilder.EncodeRuns(runs)).ToArray());   // backwards (negative) deltas, sparse runs, 3 byte lengths
        Assert.Empty(MftParser.DecodeRuns(new byte[] { 0 }));
        Assert.Empty(MftParser.DecodeRuns(new byte[] { 0x21, 5 }));                           // truncated: stops safely
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParallelParsing_GivesTheSameIndex_AsOneParser(bool mftViaAttributeList)
    {
        // every kind of record (hard links, data and names in extension records, compressed, sparse, WOF, ADS) parsed by
        // one thread and by several, with several readers delivering the chunks out of order
        var img = Build(mftViaAttributeList);
        var one = MftReader.Read(img, img.Geometry, CancellationToken.None, parsers: 1);
        foreach (var (readers, parsers) in new[] { (1, 2), (4, 4), (4, 8), (2, 0) })
        {
            var stats = new MftReader.Stats();
            var many = MftReader.Read(img, img.Geometry, CancellationToken.None, stats: stats, readers: readers, parsers: parsers);
            Assert.Equal(parsers == 0 ? Math.Clamp(Environment.ProcessorCount, 1, 8) : parsers, stats.Parsers);
            Assert.Equal(one.InUseCount, many.InUseCount);
            Assert.Equal(one.Links.Count, many.Links.Count);
            Assert.Equal(one.Flags, many.Flags);
            Assert.Equal(one.Seq, many.Seq);
            Assert.Equal(one.Length, many.Length);
            Assert.Equal(one.AdsBytes, many.AdsBytes);
            Assert.Equal(one.WofAlloc, many.WofAlloc);
            for (uint r = 0; r < one.Count; r++)
            {
                Assert.Equal(one.DiskSizeOf(r), many.DiskSizeOf(r));
                if (one.IsDirectory(r)) Assert.Equal(one.ChildrenOf(r), many.ChildrenOf(r));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Index_ReadsEveryKindOfRecord(bool mftViaAttributeList)
    {
        var img = Build(mftViaAttributeList);
        var ix = MftReader.Read(img, img.Geometry, CancellationToken.None);
        Assert.Equal(NtfsImage.Records, ix.Count);
        uint Find(uint folder, string name) => ix.ChildrenOf(folder).Single(c => c.Name == name).Record;
        var root = ix.ChildrenOf(MftIndex.RootRecord).Select(c => c.Name).ToArray();
        Assert.Equal(new[] { "System Volume Information", "Users" }, root);                    // no $MFT / $Extend, name order
        var users = ix.ChildrenOf(30).Select(c => c.Name).ToArray();
        Assert.Equal(new[] { "Ali", "same.txt" }, users);                                       // the second hard link
        var ali = ix.ChildrenOf(31).Select(c => c.Name).ToArray();
        Assert.Equal(new[] { "ads.txt", "big.iso", "compressed.pak", "hardlink.txt", "Large Movie.mkv", "Link", "notes.txt", "OneDrive", "packed.bin", "sparse.vhd", "zero.txt", "ÇALIŞMA ödev.docx" }, ali);
        // no DOS alias, deleted, orphaned, stale-parent or torn records
        Assert.DoesNotContain("LARGEM~1.MKV", ali); Assert.DoesNotContain("gone.txt", ali); Assert.DoesNotContain("stale.txt", ali); Assert.DoesNotContain("torn.txt", ali);
        Assert.Equal(10_000_000, ix.LengthOf(Find(31, "Large Movie.mkv")));
        Assert.Equal(10_002_432, ix.DiskSizeOf(Find(31, "Large Movie.mkv")));
        Assert.Equal(204_800, ix.DiskSizeOf(Find(31, "packed.bin")));                          // compressed: allocated clusters
        Assert.Equal(8192, ix.DiskSizeOf(Find(31, "sparse.vhd")));
        Assert.Equal(1L << 30, ix.LengthOf(Find(31, "sparse.vhd")));
        Assert.Equal(5_000_000, ix.LengthOf(Find(31, "big.iso")));                             // data in the extension record
        Assert.Equal(4096, ix.DiskSizeOf(Find(31, "notes.txt")));                               // resident: rounded like a listing
        Assert.Equal(0, ix.DiskSizeOf(Find(31, "zero.txt")));
        Assert.Equal(0, ix.DiskSizeOf(Find(41, "online.mp4")));                                 // cloud placeholder, online-only
        Assert.Equal(557_008, ix.LengthOf(Find(31, "compressed.pak")));
        Assert.Equal(188_416, ix.DiskSizeOf(Find(31, "compressed.pak")));                      // Windows compression: the hidden stream
        Assert.False(ix.HasAds(Find(31, "compressed.pak")));                                    // ... which is not an alternate data stream
        Assert.True(ix.IsLink(Find(31, "Link")));
        Assert.False(ix.IsLink(Find(31, "OneDrive")));
        Assert.Equal(26, ix.AdsBytesOf(Find(31, "ads.txt")));
        Assert.Equal(FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System, ix.AttributesOf(49));
        Assert.Equal(new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc), ix.ModifiedUtc(Find(31, "notes.txt")));
        Assert.Equal(31u, ix.Resolve(@"users\ALI"));
        Assert.Null(ix.Resolve(@"Users\Nobody"));
        Assert.Null(ix.Resolve(@"Users\same.txt"));                                              // a file is not a folder
        Assert.True(img.Reads <= 5, "reads: " + img.Reads);                                      // record 0, the bitmap, then the MFT in large chunks
    }

    [Fact]
    public void Index_SkipsFreeRecords_ByTheMftBitmap_WithTheSameResult()
    {
        var img = Build();
        var withBitmap = new MftReader.Stats();
        var a = MftReader.Read(img, img.Geometry, CancellationToken.None, stats: withBitmap);
        var without = new MftReader.Stats();
        var b = MftReader.Read(img, img.Geometry, CancellationToken.None, stats: without, useBitmap: false);
        _out.WriteLine("bitmap: " + withBitmap + "\nnone:   " + without);
        Assert.True(withBitmap.Bitmap);
        Assert.True(withBitmap.Skipped >= 8 * NtfsImage.RecordSize);            // the free tail after record 55
        Assert.True(withBitmap.Bytes < without.Bytes);
        Assert.False(without.Bitmap);
        Assert.Equal(0, without.Skipped);
        foreach (var dir in new uint[] { MftIndex.RootRecord, 30, 31, 41, 49 })
            Assert.Equal(b.ChildrenOf(dir), a.ChildrenOf(dir));
        Assert.Equal(b.InUseCount, a.InUseCount);
    }

    private static (ScanTree tree, DriveNode root) Tree(string path = "X:\\")
    {
        var root = new DriveNode(path) { IsDrive = false, ClusterSize = NtfsImage.Cluster };
        return (new ScanTree(root, live: true), root);
    }

    private static List<string> Lines(ContainerNode root) =>
        root.Descendants().Select(n => n.FullPath + (n is FileNode f ? $" {f.LogicalSize}/{f.DiskSize}" : "\\")).OrderBy(s => s, StringComparer.Ordinal).ToList();

    [Fact]
    public void Scanner_BuildsTheTreeFromTheMft_AndReconcilesARescan()
    {
        AppConfig.Replace(new AppConfig { ScanADS = true });
        try
        {
            var img = Build();
            var ix = MftReader.Read(img, img.Geometry, CancellationToken.None);
            var (tree, root) = Tree();
            var scanner = new DirectoryScanner(tree, CancellationToken.None);
            Assert.True(scanner.ScanFromMft(ix, root, "X:\\"));
            var lines = Lines(root);
            foreach (var l in lines) _out.WriteLine(l);
            var expected = new[]
            {
                @"X:\System Volume Information\", @"X:\System Volume Information\tracking.log 400/4096",
                @"X:\Users\", @"X:\Users\Ali\", @"X:\Users\Ali\ads.txt 10/8192", @"X:\Users\Ali\big.iso 5000000/5001216", @"X:\Users\Ali\compressed.pak 557008/188416",
                @"X:\Users\Ali\hardlink.txt 300/4096", @"X:\Users\Ali\Large Movie.mkv 10000000/10002432", @"X:\Users\Ali\notes.txt 100/4096",
                @"X:\Users\Ali\OneDrive\", @"X:\Users\Ali\OneDrive\cloud.docx 200/4096", @"X:\Users\Ali\OneDrive\online.mp4 52428800/0",
                @"X:\Users\Ali\packed.bin 1000000/204800", @"X:\Users\Ali\sparse.vhd 1073741824/8192", @"X:\Users\Ali\zero.txt 0/0",
                @"X:\Users\Ali\ÇALIŞMA ödev.docx 500/4096", @"X:\Users\same.txt 300/4096",
            };
            Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), lines);
            var ali = (ContainerNode)((ContainerNode)root.FindChild("Users")!).FindChild("Ali")!;
            var notes = (FileNode)ali.FindChild("notes.txt")!;
            Assert.Equal(new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc), notes.Modified);
            Assert.Equal(FileAttributes.Archive, notes.Attributes);
            Assert.True(((FileNode)ali.FindChild("ads.txt")!).HasAlternateDataStream);
            Assert.Equal(Core.Cloud.CloudState.OnlineOnly, Core.Cloud.CloudFiles.StateOf(((ContainerNode)ali.FindChild("OneDrive")!).FindChild("online.mp4")!));
            Assert.Equal(root.Descendants().OfType<FileNode>().Sum(f => f.DiskSize), root.DiskSize);   // sizes propagated
            Assert.Equal(14, root.FileCount);
            // folders keep their NTFS ids (quick rescans find them by the ids the change journal names)
            var folderIds = root.Descendants().OfType<FolderNode>().Select(f => f.FileId).ToList();
            Assert.All(folderIds, id => Assert.NotEqual(0, id));
            Assert.Equal(folderIds.Count, folderIds.Distinct().Count());

            // a rescan reconciles: removed, grown and new files; unchanged nodes stay the same objects
            var movie = ali.FindChild("Large Movie.mkv");
            var img2 = Build(changed: true);
            var ix2 = MftReader.Read(img2, img2.Geometry, CancellationToken.None);
            tree.ResetScanFlags(root);
            Assert.True(new DirectoryScanner(tree, CancellationToken.None).ScanFromMft(ix2, root, "X:\\"));
            Assert.Null(ali.FindChild("notes.txt"));
            Assert.Same(movie, ali.FindChild("Large Movie.mkv"));
            Assert.Equal(20_000_000, movie!.LogicalSize);
            Assert.NotNull(((ContainerNode)root.FindChild("Users")!).FindChild("new.txt"));
        }
        finally { AppConfig.Replace(new AppConfig()); }
    }

    [Fact]
    public void Scanner_StartsAtASubFolder_AndHonoursExclusions()
    {
        AppConfig.Replace(new AppConfig { ExcludePaths = @"*\OneDrive" });
        try
        {
            var img = Build();
            var ix = MftReader.Read(img, img.Geometry, CancellationToken.None);
            var (tree, root) = Tree(@"X:\Users\Ali\");
            Assert.True(new DirectoryScanner(tree, CancellationToken.None).ScanFromMft(ix, root, @"X:\Users\Ali\"));
            Assert.Null(root.FindChild("OneDrive"));
            Assert.NotNull(root.FindChild("notes.txt"));
            Assert.Equal(10, root.FileCount);
            var (t2, r2) = Tree(@"X:\Nowhere\");
            Assert.False(new DirectoryScanner(t2, CancellationToken.None).ScanFromMft(ix, r2, @"X:\Nowhere\"));
        }
        finally { AppConfig.Replace(new AppConfig()); }
    }

    [Fact]
    public void Eligibility_AndFallbackWithoutAdministratorRights()
    {
        var old = MftScan.IsElevated;
        try
        {
            AppConfig.Replace(new AppConfig());
            MftScan.IsElevated = () => false;
            Assert.False(MftScan.CanUse(@"C:\", out var why));
            Assert.Equal("not running as administrator", why);
            MftScan.IsElevated = () => true;
            AppConfig.Replace(new AppConfig { UseMftScan = false });
            Assert.False(MftScan.CanUse(@"C:\", out why)); Assert.Contains("turned off", why);
            AppConfig.Replace(new AppConfig { FollowReparsePoints = true });
            Assert.False(MftScan.CanUse(@"C:\", out why)); Assert.Contains("followed", why);
            AppConfig.Replace(new AppConfig());
            Assert.False(MftScan.CanUse(@"\\server\share\x", out why));
            Assert.True(MftScan.CanUse(@"C:\", out why), why);                                    // this machine's C: is NTFS
            // a folder is listed: reading the whole drive's table would cost seconds for any folder
            Assert.False(MftScan.CanUse(@"C:\Users", out why)); Assert.Contains("folder", why);
            MftScan.ForFolders = true;
            Assert.True(MftScan.CanUse(@"C:\Users", out why), why);
            if (!Environment.IsPrivilegedProcess)
            {
                // without the rights the volume cannot be opened: the master scan falls back to listing folders and completes
                Assert.Throws<UnauthorizedAccessException>(() => WindowsVolume.Open("C:"));
                var dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "mft-fallback");
                Directory.CreateDirectory(Path.Combine(dir, "sub"));
                System.IO.File.WriteAllBytes(Path.Combine(dir, "sub", "a.bin"), new byte[5000]);
                var (tree, _) = DataCache.Acquire(dir);
                try
                {
                    tree.StartMasterScan();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (tree.IsMasterScanning && sw.ElapsedMilliseconds < 10000) Thread.Sleep(10);
                    Assert.Equal(ScanState.Completed, tree.MasterState);
                    Assert.Equal("directory listing", tree.MasterScan!.Method);
                    Assert.Equal(1, tree.Root.FileCount);
                }
                finally { DataCache.Release(tree); Directory.Delete(dir, true); }
            }
        }
        finally { MftScan.IsElevated = old; MftScan.ForFolders = false; AppConfig.Replace(new AppConfig()); }
    }

    [Fact]
    public void Index_OfManyRecords_IsFast()
    {
        // 100k records in one image, each parsed and linked (a real C: has ~1.2M: this bounds the per-record cost)
        const int n = 100_000;
        int clusters = n * NtfsImage.RecordSize / NtfsImage.Cluster;
        var data = new byte[(10 + clusters) * NtfsImage.Cluster];
        var dirRec = new RecordBuilder().Seq(5).Dir().StdInfo(T0, T0, T0, 0x16).FileName(R(5, 5), ".", 3).Build(5);
        var mftRec = new RecordBuilder().StdInfo(T0, T0, T0, 6).FileName(R(5, 5), "$MFT", 3).NonResidentData((long)n * 1024, (long)n * 1024, new[] { (10L, (long)clusters) }).Build(0);
        mftRec.CopyTo(data, 10 * NtfsImage.Cluster);
        dirRec.CopyTo(data, 10 * NtfsImage.Cluster + 5 * 1024);
        for (int i = 16; i < n; i++)
            new RecordBuilder().StdInfo(T0, T1, T1, 0x20).FileName(R(5, 5), "file_" + i.ToString("D7") + ".dat").ResidentData(100).Build((uint)i)
                .CopyTo(data, 10 * NtfsImage.Cluster + i * 1024);
        var vol = new ArrayVolume(data);
        var g = new NtfsGeometry(512, NtfsImage.Cluster, 1024, 10, (long)n * 1024);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stats = new MftReader.Stats();
        var ix = MftReader.Read(vol, g, CancellationToken.None, stats: stats);
        var kids = ix.ChildrenOf(MftIndex.RootRecord);
        sw.Stop();
        _out.WriteLine($"{n:N0} records: {sw.ElapsedMilliseconds} ms; {stats}");
        Assert.Equal(n - 16, kids.Count);
        Assert.True(sw.ElapsedMilliseconds < 5000, "took " + sw.ElapsedMilliseconds + " ms");
        // several readers (chunks arrive out of order) give the same index
        var ix4 = MftReader.Read(vol, g, CancellationToken.None, readers: 4);
        Assert.Equal(kids, ix4.ChildrenOf(MftIndex.RootRecord));
        Assert.Equal(ix.InUseCount, ix4.InUseCount);
        // one parser against several: same index, and the time it takes
        var s1 = new MftReader.Stats();
        var sw1 = System.Diagnostics.Stopwatch.StartNew();
        var p1 = MftReader.Read(vol, g, CancellationToken.None, stats: s1, readers: 4, parsers: 1);
        sw1.Stop();
        var sN = new MftReader.Stats();
        var swN = System.Diagnostics.Stopwatch.StartNew();
        var pN = MftReader.Read(vol, g, CancellationToken.None, stats: sN, readers: 4, parsers: 4);
        swN.Stop();
        _out.WriteLine($"1 parser: {sw1.ElapsedMilliseconds} ms ({s1.ParseTime.TotalMilliseconds:0} ms parsing); 4 parsers: {swN.ElapsedMilliseconds} ms ({sN.ParseTime.TotalMilliseconds:0} ms parsing, together)");
        Assert.Equal(p1.ChildrenOf(MftIndex.RootRecord), pN.ChildrenOf(MftIndex.RootRecord));
        Assert.Equal(p1.Length, pN.Length);
        // a failing read surfaces as the read error, not as a hang or a cancellation
        Assert.Throws<IOException>(() => MftReader.Read(new FailingVolume(vol, failAfter: 5), g, CancellationToken.None, readers: 4));
        var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => MftReader.Read(vol, g, cts.Token, readers: 4));
    }

    private sealed class FailingVolume : IVolumeReader
    {
        private readonly IVolumeReader _inner;
        private int _left;
        public FailingVolume(IVolumeReader inner, int failAfter) { _inner = inner; _left = failAfter; }
        public void Read(long offset, Span<byte> buffer)
        {
            if (Interlocked.Decrement(ref _left) < 0) throw new IOException("simulated read error");
            _inner.Read(offset, buffer);
        }
    }

    private sealed class ArrayVolume : IVolumeReader
    {
        private readonly byte[] _d;
        public ArrayVolume(byte[] d) { _d = d; }
        public void Read(long offset, Span<byte> buffer) => _d.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
    }
}
