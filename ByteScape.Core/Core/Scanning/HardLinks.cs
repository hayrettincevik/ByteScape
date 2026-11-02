using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ByteScape.Core.Model;
using ByteScape.Core.Util;
using Microsoft.Win32.SafeHandles;

namespace ByteScape.Core.Scanning;

/// <summary>
/// Hard links: one NTFS file with several names (C:\Windows\WinSxS and System32 share most of their files; pnpm, Git and
/// backup tools make them too). A listing sees every name with the full size, so the space is counted once per name.
/// After a scan the names are grouped by file id; the space stays with the first name in path order and the other names
/// count 0 on disk (their length is unchanged). The groups answer "where else is this file?" for the details panel.
/// </summary>
public sealed class HardLinkRegistry
{
    private volatile Dictionary<long, FileNode[]> _groups = new();
    /// <summary>Size on disk of each file seen with several names (kept when every name counts 0 until the next pass).</summary>
    private readonly Dictionary<long, long> _fileDisk = new();
    /// <summary>The names the last pass counted at another name: the next pass gives back their own space to those left alone.</summary>
    private List<FileNode> _shadows = new();

    /// <summary>Space counted once instead of once per name, after the last pass.</summary>
    public long SavedBytes { get; private set; }
    /// <summary>Files with more than one name in the tree.</summary>
    public int Files => _groups.Count;
    /// <summary>Names beyond the first of those files.</summary>
    public int ExtraNames { get; private set; }
    public TimeSpan Elapsed { get; private set; }
    /// <summary>A pass ran on this tree (its groups are known).</summary>
    internal bool HasRun { get; private set; }

    /// <summary>The space on disk of the file of <paramref name="f"/> (for a name counted elsewhere, whose own size is 0); 0 when not known. Under the tree lock.</summary>
    internal long OwnDiskOf(FileNode f) => f.FileId != 0 && _fileDisk.TryGetValue(f.FileId, out var d) ? d : 0;

    /// <summary>
    /// A kept scan brought back: <paramref name="shadow"/> was counted at another name, its file takes <paramref name="ownDisk"/>.
    /// If that other name is gone by the next pass, this one gets the space back.
    /// </summary>
    internal void Seed(FileNode shadow, long ownDisk)
    {
        _shadows.Add(shadow);
        if (ownDisk > 0 && shadow.FileId != 0) _fileDisk[shadow.FileId] = ownDisk;
    }
    /// <summary>Where the last pass spent its time: walking the tree, grouping the ids, applying the sizes; and on how many threads.</summary>
    public (TimeSpan Walk, TimeSpan Group, TimeSpan Apply, int Threads) Timing { get; private set; }

    /// <summary>Every name of the file <paramref name="f"/> in the tree (first one: where its space is counted), or null for a single name.</summary>
    public FileNode[]? GroupOf(FileNode f) =>
        f.FileId != 0 && _groups.TryGetValue(f.FileId, out var g) && Array.IndexOf(g, f) >= 0 ? g : null;

    private const int Buckets = 64;
    private static int BucketOf(long id) => (int)((ulong)id * 0x9E37_79B9_7F4A_7C15UL >> 58);

    /// <summary>
    /// Groups the names of the tree's files by file id and counts each file's space once. The tree is walked by several
    /// threads (a folder's children are copied under the tree lock and looked at outside it), the ids are grouped in 64
    /// buckets in parallel, and the sizes are applied one group at a time under the lock. The result does not depend on
    /// the number of threads (<paramref name="threads"/> 0 = one per processor core, at most 8).
    /// </summary>
    internal void Apply(ScanTree tree, CancellationToken token, int threads = 0)
    {
        var sw = Stopwatch.StartNew();
        int workers = threads > 0 ? threads : Math.Clamp(Environment.ProcessorCount, 1, 8);

        // 1. every file with an id, into buckets by id (each worker its own lists)
        var perWorker = new List<(long Id, FileNode Node)>[workers][];
        var queue = new ConcurrentQueue<ContainerNode>();
        queue.Enqueue(tree.Root);
        int pending = 1;
        Exception? failed = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        void Walk(int w)
        {
            var mine = perWorker[w] = new List<(long, FileNode)>[Buckets];
            for (int b = 0; b < Buckets; b++) mine[b] = new List<(long, FileNode)>();
            var kids = new List<FsNode>();
            var spin = new SpinWait();
            try
            {
                while (Volatile.Read(ref pending) > 0 && !stop.IsCancellationRequested)
                {
                    if (!queue.TryDequeue(out var c)) { spin.SpinOnce(); continue; }
                    kids.Clear();
                    lock (tree.Sync) kids.AddRange(c.Children);
                    foreach (var k in kids)
                    {
                        if (k is FileNode f) { if (f.FileId != 0) mine[BucketOf(f.FileId)].Add((f.FileId, f)); }
                        else if (k is ContainerNode kc && !kc.IsSynthetic) { Interlocked.Increment(ref pending); queue.Enqueue(kc); }
                    }
                    Interlocked.Decrement(ref pending);
                }
            }
            catch (Exception ex) { Interlocked.CompareExchange(ref failed, ex, null); stop.Cancel(); }   // the others stop too
        }
        if (workers == 1) Walk(0);
        else
        {
            var ts = Enumerable.Range(0, workers).Select(w => new Thread(() => Walk(w)) { IsBackground = true, Name = "HardLinks" + w }).ToList();
            foreach (var t in ts) t.Start();
            foreach (var t in ts) t.Join();
        }
        if (failed != null) throw failed;
        token.ThrowIfCancellationRequested();
        var walked = sw.Elapsed;

        // 2. per bucket, in parallel: the ids with several names, their names in path order; names left alone
        var bucketGroups = new List<(long Id, FileNode[] Members)>[Buckets];
        int files = 0;
        Parallel.For(0, Buckets, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token }, b =>
        {
            int n = 0;
            foreach (var w in perWorker) n += w[b].Count;
            var ids = new long[n];
            var nodes = new FileNode[n];
            int at = 0;
            foreach (var w in perWorker)
                foreach (var (id, node) in w[b]) { ids[at] = id; nodes[at] = node; at++; }
            Array.Sort(ids, nodes);
            var groups = bucketGroups[b] = new List<(long, FileNode[])>();
            // the names of one file usually sit in few folders (a WinSxS component and System32): each folder's path is built once
            var folderPaths = new Dictionary<ContainerNode, string>(ReferenceEqualityComparer.Instance);
            string PathOf(FileNode f)
            {
                if (f.Parent is not ContainerNode p) return f.Name;
                if (!folderPaths.TryGetValue(p, out var pp)) folderPaths[p] = pp = p.FullPath;
                return pp.EndsWith('\\') ? pp + f.Name : pp + "\\" + f.Name;
            }
            for (int i = 0; i < n;)
            {
                int j = i + 1;
                while (j < n && ids[j] == ids[i]) j++;
                if (j - i > 1)
                {
                    // the space stays with the first name in path order (System32 before WinSxS), whatever order the scan found them in
                    var members = nodes.AsSpan(i, j - i).ToArray();
                    var paths = new string[members.Length];
                    for (int k = 0; k < members.Length; k++) paths[k] = PathOf(members[k]);
                    Array.Sort(paths, members, StringComparer.OrdinalIgnoreCase);
                    groups.Add((ids[i], members));
                }
                i = j;
            }
            Interlocked.Add(ref files, n);
        });
        var grouped = sw.Elapsed;

        // 3. the sizes, one group at a time under the lock (a resize changes every folder above the file)
        var result = new Dictionary<long, FileNode[]>();
        var shadows = new List<FileNode>();
        long saved = 0;
        int extra = 0;
        for (int b = 0; b < Buckets; b++)
        {
            foreach (var (id, members) in bucketGroups[b])
            {
                long disk = 0;
                lock (tree.Sync)
                {
                    foreach (var m in members) if (!m.CountedElsewhere && m.DiskSize > disk) disk = m.DiskSize;
                    if (disk == 0 && _fileDisk.TryGetValue(id, out var known)) disk = known;
                    _fileDisk[id] = disk;
                    for (int k = 0; k < members.Length; k++)
                    {
                        var m = members[k];
                        if (m.Parent is not ContainerNode p) continue;
                        bool elsewhere = k > 0;
                        long want = elsewhere ? 0 : disk;
                        m.CountedElsewhere = elsewhere;
                        if (elsewhere) shadows.Add(m);
                        if (m.DiskSize != want) p.ResizeChild(m, m.LogicalSize, want);
                    }
                }
                result[id] = members;
                saved += disk * (members.Length - 1);
                extra += members.Length - 1;
            }
        }
        // a name counted elsewhere last time whose file has no other name any more: its own space again
        foreach (var old in _shadows)
            if (old.CountedElsewhere && !result.ContainsKey(old.FileId))
                lock (tree.Sync)
                {
                    old.CountedElsewhere = false;
                    if (old.Parent is ContainerNode sp && _fileDisk.TryGetValue(old.FileId, out var own)) sp.ResizeChild(old, old.LogicalSize, own);
                }
        _shadows = shadows;
        _groups = result;
        SavedBytes = saved;
        ExtraNames = extra;
        Elapsed = sw.Elapsed;
        HasRun = true;
        Timing = (walked, grouped - walked, sw.Elapsed - grouped, workers);
        if (result.Count > 0)
            Log.Debug($"Hard links: {result.Count:N0} files with {extra:N0} more names, {Format.Size(saved)} counted once ({files:N0} files, {sw.ElapsedMilliseconds} ms on {workers} threads: walk {walked.TotalMilliseconds:0}, group {(grouped - walked).TotalMilliseconds:0}, apply {(sw.Elapsed - grouped).TotalMilliseconds:0} ms)");
    }
}

/// <summary>
/// A directory listing that also gives each entry's NTFS file id (the names of one file share it), through
/// GetFileInformationByHandleEx(FileIdBothDirectoryInfo): batches of entries per system call, like FindFirstFile.
/// </summary>
internal static class IdLister
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, byte[] buffer, int size);

    private const int FileIdBothDirectoryInfo = 10, FileIdBothDirectoryRestartInfo = 11, ErrorNoMoreFiles = 18;

    [ThreadStatic] private static byte[]? _buffer;

    public static List<DirectoryScanner.Entry> List(string path, CancellationToken token)
    {
        // FILE_LIST_DIRECTORY, share everything, OPEN_EXISTING, BACKUP_SEMANTICS (needed to open a folder)
        using var h = CreateFile(path, 0x1, 0x7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (h.IsInvalid) throw Error(Marshal.GetLastWin32Error(), path);
        var buf = _buffer ??= new byte[64 * 1024];
        var list = new List<DirectoryScanner.Entry>();
        int cls = FileIdBothDirectoryRestartInfo;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!GetFileInformationByHandleEx(h, cls, buf, buf.Length))
            {
                int e = Marshal.GetLastWin32Error();
                if (e == ErrorNoMoreFiles) break;
                throw Error(e, path);
            }
            cls = FileIdBothDirectoryInfo;
            var span = buf.AsSpan();
            for (int off = 0; ;)
            {
                var entry = span.Slice(off);
                int next = BinaryPrimitives.ReadInt32LittleEndian(entry);
                int nameBytes = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(60));
                var name = new string(MemoryMarshal.Cast<byte, char>(entry.Slice(104, nameBytes)));
                if (name != "." && name != "..")
                {
                    var attrs = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(56));
                    bool dir = (attrs & FileAttributes.Directory) != 0;
                    list.Add(new DirectoryScanner.Entry(name, dir, dir ? 0 : BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(40)), attrs,
                        Time(entry.Slice(8)), Time(entry.Slice(16)), Time(entry.Slice(24)), BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(96))));
                }
                if (next == 0) break;
                off += next;
            }
        }
        return list;
    }

    private static DateTime Time(ReadOnlySpan<byte> b)
    {
        long v = BinaryPrimitives.ReadInt64LittleEndian(b);
        return v <= 0 ? DateTime.FromFileTimeUtc(0) : DateTime.FromFileTimeUtc(v);
    }

    private static Exception Error(int code, string path) => code switch
    {
        5 => new UnauthorizedAccessException("Access to the path '" + path + "' is denied."),
        2 or 3 => new DirectoryNotFoundException("Could not find a part of the path '" + path + "'."),
        _ => new IOException(new Win32Exception(code).Message + " : '" + path + "'", code),
    };
}
