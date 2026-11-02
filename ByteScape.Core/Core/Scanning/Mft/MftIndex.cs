using System.Buffers.Binary;
using System.Text;

namespace ByteScape.Core.Scanning.Mft;

/// <summary>
/// What the NTFS master file table says about every file and folder of a volume, indexed by record number: flags,
/// attributes, times, sizes of the unnamed data stream, alternate stream bytes, and the names (hard links) with their
/// parent folders. Built by <see cref="MftParser"/>; the children of a folder come in the order a directory listing
/// gives them (NTFS name order).
/// </summary>
public sealed class MftIndex
{
    internal const byte InUse = 1, Directory = 2, Link = 4, HasData = 8, DataSparseOrCompressed = 16, Wof = 64;
    /// <summary>Records 0..15 are NTFS metadata ($MFT, $Bitmap, $Extend...): never listed, like a directory listing.</summary>
    internal const uint FirstUserRecord = 16;
    public const uint RootRecord = 5;

    internal readonly byte[] Flags;
    internal readonly ushort[] Seq;
    internal readonly uint[] Attrs;
    internal readonly long[] Created, Modified, Accessed;
    internal readonly long[] Length, AllocLen, TotalAlloc, AdsBytes;
    /// <summary>Allocation of the "WofCompressedData" stream of files compressed by Windows (WOF / CompactOS).</summary>
    internal readonly long[] WofAlloc;
    internal readonly List<NameLink> Links = new();
    private int[]? _first, _next;

    internal readonly record struct NameLink(uint Record, uint Parent, ushort ParentSeq, string Name);

    public MftIndex(int records, int bytesPerCluster)
    {
        Count = records;
        BytesPerCluster = bytesPerCluster;
        Flags = new byte[records]; Seq = new ushort[records]; Attrs = new uint[records];
        Created = new long[records]; Modified = new long[records]; Accessed = new long[records];
        Length = new long[records]; AllocLen = new long[records]; TotalAlloc = new long[records]; AdsBytes = new long[records]; WofAlloc = new long[records];
        Links.Capacity = records;
    }

    public int Count { get; }
    public int BytesPerCluster { get; }
    /// <summary>Records in use (files, folders and extension records' owners).</summary>
    public int InUseCount { get; internal set; }

    public bool IsInUse(uint r) => r < Count && (Flags[r] & InUse) != 0;
    public bool IsDirectory(uint r) => r < Count && (Flags[r] & Directory) != 0;
    /// <summary>Junction / symbolic link / mount point (name surrogate reparse point).</summary>
    public bool IsLink(uint r) => r < Count && (Flags[r] & Link) != 0;
    public long LengthOf(uint r) => Length[r];
    /// <summary>The NTFS file reference (record number and sequence), as a directory listing reports the file id.</summary>
    public long FileIdOf(uint r) => (long)((ulong)r | ((ulong)Seq[r] << 48));
    public long AdsBytesOf(uint r) => AdsBytes[r];
    public bool HasAds(uint r) => AdsBytes[r] > 0 || (Flags[r] & 32) != 0;
    public FileAttributes AttributesOf(uint r)
    {
        // $STANDARD_INFORMATION keeps NTFS-only bits high up; a directory listing reports the documented ones plus "directory"
        var a = (FileAttributes)(Attrs[r] & 0x00FF_FFFF);
        if (IsDirectory(r)) a |= FileAttributes.Directory;
        return a == 0 ? FileAttributes.Normal : a;
    }
    public DateTime CreatedUtc(uint r) => Time(Created[r]);
    public DateTime ModifiedUtc(uint r) => Time(Modified[r]);
    public DateTime AccessedUtc(uint r) => Time(Accessed[r]);

    private static DateTime Time(long ft)
    {
        if (ft <= 0 || ft > DateTime.MaxValue.ToFileTimeUtc()) return default;
        return DateTime.FromFileTimeUtc(ft);
    }

    /// <summary>
    /// Allocated size like the directory scanner reports it: the length rounded to clusters, or the really allocated
    /// clusters for compressed, sparse and cloud placeholder files (online-only files: 0).
    /// </summary>
    public long DiskSizeOf(uint r)
    {
        // compressed by Windows (WOF): what the hidden compressed stream occupies (0 for WIM-backed files: their data is elsewhere)
        if ((Flags[r] & Wof) != 0) return RoundToCluster(WofAlloc[r]);
        long size = (Flags[r] & DataSparseOrCompressed) != 0 && TotalAlloc[r] >= 0 ? TotalAlloc[r] : Length[r];
        var a = (FileAttributes)Attrs[r];
        if ((Flags[r] & DataSparseOrCompressed) == 0 && (a & (FileAttributes.Offline | Cloud.CloudFiles.RecallOnDataAccess)) != 0) size = AllocLen[r];
        return RoundToCluster(size);
    }

    public long RoundToCluster(long size) => size <= 0 ? 0 : (size + BytesPerCluster - 1) / BytesPerCluster * BytesPerCluster;

    /// <summary>Links every name to its parent folder (stale parents - reused records - and metadata are dropped).</summary>
    internal void BuildChildren()
    {
        var first = new int[Count];
        Array.Fill(first, -1);
        var next = new int[Links.Count];
        for (int i = Links.Count - 1; i >= 0; i--)
        {
            var l = Links[i];
            next[i] = -1;
            if (l.Record == l.Parent || l.Record < FirstUserRecord || !IsInUse(l.Record)) continue;
            if (!IsInUse(l.Parent) || !IsDirectory(l.Parent)) continue;
            if (l.ParentSeq != 0 && Seq[l.Parent] != 0 && l.ParentSeq != Seq[l.Parent]) continue;   // the parent record was reused
            next[i] = first[l.Parent];
            first[l.Parent] = i;
        }
        _first = first; _next = next;
    }

    /// <summary>Names and records inside a folder, in NTFS name order (case insensitive).</summary>
    public List<(string Name, uint Record)> ChildrenOf(uint folder)
    {
        var list = new List<(string, uint)>();
        if (_first == null || folder >= Count) return list;
        for (int i = _first[folder]; i >= 0; i = _next![i]) list.Add((Links[i].Name, Links[i].Record));
        list.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>Record of a folder given its path relative to the volume root ("Users\Ali"), or null.</summary>
    public uint? Resolve(string relativePath)
    {
        uint cur = RootRecord;
        foreach (var part in relativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            uint? found = null;
            foreach (var (name, rec) in ChildrenOf(cur))
                if (string.Equals(name, part, StringComparison.OrdinalIgnoreCase)) { found = rec; break; }
            if (found == null || !IsDirectory(found.Value)) return null;
            cur = found.Value;
        }
        return cur;
    }
}

/// <summary>NTFS file record parsing (FILE records, update sequence fixups, attributes, mapping pairs).</summary>
public static class MftParser
{
    public const uint AttrStandardInformation = 0x10, AttrAttributeList = 0x20, AttrFileName = 0x30, AttrData = 0x80, AttrReparsePoint = 0xC0, AttrEnd = 0xFFFFFFFF;
    private const string WofStream = "WofCompressedData";

    private static bool IsName(ReadOnlySpan<byte> a, string name)
    {
        int ofs = U16(a, 0x0A);
        if (ofs + name.Length * 2 > a.Length) return false;
        for (int i = 0; i < name.Length; i++)
            if (U16(a, ofs + 2 * i) != name[i]) return false;
        return true;
    }
    private const ushort FlagCompressed = 0x00FF, FlagSparse = 0x8000;

    private static ushort U16(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(o));
    private static uint U32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(o));
    private static long I64(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadInt64LittleEndian(s.Slice(o));
    private static ulong U64(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(o));

    /// <summary>
    /// Checks the "FILE" signature and undoes the update sequence: the last two bytes of every 512 byte stride carry
    /// the sequence number and their real content is in the array. False for free or torn (half written) records.
    /// </summary>
    public static bool ApplyFixups(Span<byte> rec)
    {
        if (rec.Length < 48 || rec[0] != (byte)'F' || rec[1] != (byte)'I' || rec[2] != (byte)'L' || rec[3] != (byte)'E') return false;
        int usaOfs = U16(rec, 4), usaCount = U16(rec, 6);
        if (usaCount < 2 || usaOfs < 8 || usaOfs + usaCount * 2 > rec.Length || (usaCount - 1) * 512 > rec.Length) return false;
        ushort usn = U16(rec, usaOfs);
        for (int i = 1; i < usaCount; i++)
        {
            int end = i * 512 - 2;
            if (U16(rec, end) != usn) return false;
            rec[end] = rec[usaOfs + 2 * i];
            rec[end + 1] = rec[usaOfs + 2 * i + 1];
        }
        return true;
    }

    /// <summary>Decodes the mapping pairs of a non-resident attribute into (lcn, clusters) runs; lcn -1 = sparse run.</summary>
    public static List<(long Lcn, long Clusters)> DecodeRuns(ReadOnlySpan<byte> mp)
    {
        var runs = new List<(long, long)>();
        long lcn = 0;
        int p = 0;
        while (p < mp.Length && mp[p] != 0)
        {
            int h = mp[p++];
            int lenBytes = h & 0x0F, ofsBytes = h >> 4;
            if (lenBytes == 0 || lenBytes > 8 || ofsBytes > 8 || p + lenBytes + ofsBytes > mp.Length) break;
            long len = 0;
            for (int i = 0; i < lenBytes; i++) len |= (long)mp[p + i] << (8 * i);
            p += lenBytes;
            if (ofsBytes == 0) { runs.Add((-1, len)); continue; }
            long ofs = 0;
            for (int i = 0; i < ofsBytes; i++) ofs |= (long)mp[p + i] << (8 * i);
            if (ofsBytes < 8 && (mp[p + ofsBytes - 1] & 0x80) != 0) ofs |= -1L << (8 * ofsBytes);   // relative, signed
            p += ofsBytes;
            lcn += ofs;
            runs.Add((lcn, len));
        }
        return runs;
    }

    /// <summary>Resident value of an attribute.</summary>
    private static ReadOnlySpan<byte> Value(ReadOnlySpan<byte> a)
    {
        uint len = U32(a, 0x10);
        int ofs = U16(a, 0x14);
        return ofs + len <= a.Length ? a.Slice(ofs, (int)len) : ReadOnlySpan<byte>.Empty;
    }

    /// <summary>
    /// Adds what a (fixed up) record holds to the index. Extension records (base reference set) contribute their
    /// attributes to the base record, so files whose attributes do not fit one record need no attribute list parsing.
    /// </summary>
    public static void Parse(ReadOnlySpan<byte> rec, uint recNo, MftIndex ix, bool adsSizes = true)
    {
        if (ParseInto(rec, recNo, ix, adsSizes, ix.Links)) ix.InUseCount++;
    }

    /// <summary>An extension record in use: its attributes belong to another (base) record.</summary>
    internal static bool IsExtension(ReadOnlySpan<byte> rec) => (U16(rec, 0x16) & 1) != 0 && U64(rec, 0x20) != 0;

    /// <summary>
    /// <see cref="Parse"/> with the names going to <paramref name="links"/>; true for a base record in use. A base record
    /// writes only its own entries of the index, so base records can be parsed in parallel; an extension record writes
    /// into its base record's entries and must not run alongside it.
    /// </summary>
    internal static bool ParseInto(ReadOnlySpan<byte> rec, uint recNo, MftIndex ix, bool adsSizes, List<MftIndex.NameLink> links)
    {
        ushort flags = U16(rec, 0x16);
        if ((flags & 1) == 0) return false;                             // free record
        // the base reference carries a sequence number too: an extension record of $MFT (record 0) is not zero
        ulong baseRef = U64(rec, 0x20);
        bool extension = baseRef != 0;
        uint owner = extension ? (uint)(baseRef & 0x0000_FFFF_FFFF_FFFF) : recNo;
        if (owner >= ix.Count) return false;
        if (!extension)
        {
            ix.Seq[owner] = U16(rec, 0x10);
            ix.Flags[owner] |= MftIndex.InUse;
            if ((flags & 2) != 0) ix.Flags[owner] |= MftIndex.Directory;
        }
        int ofs = U16(rec, 0x14);
        int used = (int)Math.Min(U32(rec, 0x18), (uint)rec.Length);
        while (ofs + 16 <= used)
        {
            uint type = U32(rec, ofs);
            if (type == AttrEnd) break;
            int len = (int)U32(rec, ofs + 4);
            if (len < 16 || ofs + len > used) break;
            var a = rec.Slice(ofs, len);
            bool nonResident = a[8] != 0;
            int nameLen = a[9];
            ushort aflags = U16(a, 0x0C);
            switch (type)
            {
                case AttrStandardInformation when !nonResident:
                {
                    var v = Value(a);
                    if (v.Length >= 0x24)
                    {
                        ix.Created[owner] = I64(v, 0x00);
                        ix.Modified[owner] = I64(v, 0x08);
                        ix.Accessed[owner] = I64(v, 0x18);
                        ix.Attrs[owner] = U32(v, 0x20);
                    }
                    break;
                }
                case AttrFileName when !nonResident:
                {
                    var v = Value(a);
                    if (v.Length < 0x42) break;
                    int chars = v[0x40];
                    byte ns = v[0x41];
                    if (ns == 2 || 0x42 + chars * 2 > v.Length) break;      // the 8.3 DOS alias of a long name
                    ulong parent = U64(v, 0);
                    links.Add(new MftIndex.NameLink(owner, (uint)(parent & 0x0000_FFFF_FFFF_FFFF), (ushort)(parent >> 48), Encoding.Unicode.GetString(v.Slice(0x42, chars * 2))));
                    break;
                }
                case AttrData:
                {
                    bool named = nameLen > 0;
                    // Windows' own compression (CompactOS, "compact /exe"): the data is in this hidden stream, the main one is empty
                    bool wofData = named && nameLen == WofStream.Length && IsName(a, WofStream);
                    if (!nonResident)
                    {
                        long size = U32(a, 0x10);
                        if (wofData) ix.WofAlloc[owner] = size;
                        else if (named) { if (adsSizes) ix.AdsBytes[owner] += size; ix.Flags[owner] |= 32; }
                        else { ix.Length[owner] = size; ix.AllocLen[owner] = 0; ix.TotalAlloc[owner] = -1; ix.Flags[owner] |= MftIndex.HasData; }
                    }
                    else if (a.Length >= 0x40 && I64(a, 0x10) == 0)               // sizes live in the first extent only
                    {
                        long alloc = I64(a, 0x28), size = I64(a, 0x30);
                        // the total allocated field exists only in compressed or sparse attributes (a stream name may sit there otherwise)
                        long total = (aflags & (FlagCompressed | FlagSparse)) != 0 && a.Length >= 0x48 ? I64(a, 0x40) : -1;
                        if (wofData) ix.WofAlloc[owner] = total >= 0 ? total : alloc;
                        else if (named) { if (adsSizes) ix.AdsBytes[owner] += size; ix.Flags[owner] |= 32; }
                        else
                        {
                            ix.Length[owner] = size; ix.AllocLen[owner] = alloc; ix.TotalAlloc[owner] = total;
                            ix.Flags[owner] |= MftIndex.HasData;
                            if ((aflags & (FlagCompressed | FlagSparse)) != 0) ix.Flags[owner] |= MftIndex.DataSparseOrCompressed;
                        }
                    }
                    break;
                }
                case AttrReparsePoint when !nonResident:
                {
                    var v = Value(a);
                    if (v.Length < 4) break;
                    uint tag = U32(v, 0);
                    if ((tag & 0x2000_0000) != 0) ix.Flags[owner] |= MftIndex.Link;   // name surrogate: junction, symlink, mount point
                    else if (tag == 0x8000_0017) ix.Flags[owner] |= MftIndex.Wof;      // IO_REPARSE_TAG_WOF: compressed by Windows
                    break;
                }
            }
            ofs += len;
        }
        return !extension;
    }

    /// <summary>The runs of the unnamed $DATA extent(s) found in a record, with the extent's first VCN (for the $MFT itself).</summary>
    public static List<(long LowestVcn, List<(long Lcn, long Clusters)> Runs)> DataExtents(ReadOnlySpan<byte> rec)
    {
        var list = new List<(long, List<(long, long)>)>();
        int ofs = U16(rec, 0x14);
        int used = (int)Math.Min(U32(rec, 0x18), (uint)rec.Length);
        while (ofs + 16 <= used)
        {
            uint type = U32(rec, ofs);
            if (type == AttrEnd) break;
            int len = (int)U32(rec, ofs + 4);
            if (len < 16 || ofs + len > used) break;
            var a = rec.Slice(ofs, len);
            if (type == AttrData && a[8] != 0 && a[9] == 0 && a.Length >= 0x40)
            {
                int mpo = U16(a, 0x20);
                if (mpo < a.Length) list.Add((I64(a, 0x10), DecodeRuns(a.Slice(mpo))));
            }
            ofs += len;
        }
        return list;
    }

    /// <summary>
    /// The $MFT's allocation bitmap ($BITMAP, unnamed; a set bit = record in use): its resident value, or the runs and
    /// the size of its first extent. Nothing when it is not in this record.
    /// </summary>
    public static (byte[]? Resident, List<(long Lcn, long Clusters)>? Runs, long Size) BitmapOf(ReadOnlySpan<byte> rec)
    {
        int ofs = U16(rec, 0x14);
        int used = (int)Math.Min(U32(rec, 0x18), (uint)rec.Length);
        while (ofs + 16 <= used)
        {
            uint type = U32(rec, ofs);
            if (type == AttrEnd) break;
            int len = (int)U32(rec, ofs + 4);
            if (len < 16 || ofs + len > used) break;
            var a = rec.Slice(ofs, len);
            if (type == 0xB0 && a[9] == 0)
            {
                if (a[8] == 0) { var v = Value(a); return (v.ToArray(), null, v.Length); }
                if (a.Length >= 0x40 && I64(a, 0x10) == 0)
                {
                    int mpo = U16(a, 0x20);
                    if (mpo < a.Length) return (null, DecodeRuns(a.Slice(mpo)), I64(a, 0x30));
                }
            }
            ofs += len;
        }
        return (null, null, 0);
    }

    /// <summary>Records holding further $DATA extents of this record (its resident $ATTRIBUTE_LIST), for a fragmented $MFT.</summary>
    public static List<uint> DataExtensionRecords(ReadOnlySpan<byte> rec, uint self)
    {
        var list = new List<uint>();
        int ofs = U16(rec, 0x14);
        int used = (int)Math.Min(U32(rec, 0x18), (uint)rec.Length);
        while (ofs + 16 <= used)
        {
            uint type = U32(rec, ofs);
            if (type == AttrEnd) break;
            int len = (int)U32(rec, ofs + 4);
            if (len < 16 || ofs + len > used) break;
            var a = rec.Slice(ofs, len);
            if (type == AttrAttributeList && a[8] == 0)
            {
                var v = Value(a);
                int p = 0;
                while (p + 0x1A <= v.Length)
                {
                    uint t = U32(v, p);
                    int elen = U16(v, p + 4);
                    if (elen < 0x1A) break;
                    uint seg = (uint)(U64(v, p + 0x10) & 0x0000_FFFF_FFFF_FFFF);
                    if (t == AttrData && seg != self && !list.Contains(seg)) list.Add(seg);
                    p += elen;
                }
            }
            ofs += len;
        }
        return list;
    }
}
