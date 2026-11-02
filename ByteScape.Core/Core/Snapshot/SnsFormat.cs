using System.Text;
using ByteScape.Core.Model;
using ByteScape.Core.Util;

namespace ByteScape.Core.Snapshot;

/// <summary>
/// ByteScape binary snapshot (.sns). The format is described in
/// docs/snapshot-format.md.
///
/// node := 0x02, type(0x03 folder | 0x02 file), int32 len, base64(name) [CRLF every 72 chars],
///         int64 logicalSize,
///         file:   int32 slack(disk-logical), int32 attributes
///         folder: int64 diskTotal + sum(slack of direct files)
///         FILETIME create, access, write (zero for folders),
///         byte tag, byte reserved,
///         [children...] (folders),
///         0x01, 0x00
/// </summary>
public static class SnsFormat
{
    private const byte OpOpen = 0x02;
    private const byte OpClose = 0x01;
    private const byte TypeFolder = 0x03;
    private const byte TypeFile = 0x02;

    public static bool IsSnapshotFile(string path) => path.EndsWith(".sns", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ write

    /// <summary>Writes <paramref name="root"/> and the elements accepted by <paramref name="include"/>.</summary>
    public static void Write(string path, ContainerNode root, Func<FsNode, bool> include, Action<int>? progress = null, CancellationToken token = default)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var w = new BinaryWriter(fs);
        int count = 0;
        void Close()
        {
            w.Write(OpClose); w.Write((byte)0);
            if ((++count & 0x3FF) == 0) progress?.Invoke(count);
        }
        // a stack of its own instead of recursion: however deeply folders nest, the thread's stack is not used up
        var open = new Stack<IEnumerator<FsNode>>();
        WriteHeader(w, root, include, isRoot: true);
        open.Push(Kids(root, include));
        while (open.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var kids = open.Peek();
            if (!kids.MoveNext()) { open.Pop(); Close(); continue; }
            var ch = kids.Current;
            WriteHeader(w, ch, include, isRoot: false);
            if (ch is ContainerNode c) open.Push(Kids(c, include));
            else Close();
        }
    }

    // children are written ordered by size, biggest first
    private static IEnumerator<FsNode> Kids(ContainerNode c, Func<FsNode, bool> include)
        => c.Children.Where(x => !x.IsSynthetic && include(x)).OrderByDescending(x => x.DiskSize).ToList().GetEnumerator();

    /// <summary>A node up to its children (a folder) or up to its end marker (a file).</summary>
    private static void WriteHeader(BinaryWriter w, FsNode node, Func<FsNode, bool> include, bool isRoot)
    {
        w.Write(OpOpen);
        if (node is ContainerNode c)
        {
            w.Write(TypeFolder);
            WriteName(w, isRoot ? RootName(node) : node.Name);
            w.Write(node.LogicalSize);
            long slack = 0;
            foreach (var ch in c.Children) if (ch is FileNode f && include(f)) slack += f.DiskSize - f.LogicalSize;
            w.Write(node.DiskSize + slack);
            w.Write(0L); w.Write(0L); w.Write(0L);
            w.Write((byte)node.Tag); w.Write((byte)0);
        }
        else if (node is FileNode f)
        {
            w.Write(TypeFile);
            WriteName(w, node.Name);
            w.Write(f.LogicalSize);
            w.Write((int)Math.Max(0, f.DiskSize - f.LogicalSize));
            w.Write((int)f.Attributes);
            w.Write(ToFileTime(f.Created)); w.Write(ToFileTime(f.Accessed)); w.Write(ToFileTime(f.Modified));
            w.Write((byte)node.Tag); w.Write((byte)0);
        }
    }

    private static string RootName(FsNode node)
    {
        // exporting a zoomed folder: its path becomes the snapshot root (without any snapshot frame prefix)
        var p = node is DriveNode ? node.Name : node.FullPath;
        return p.EndsWith('\\') ? p : p + "\\";
    }

    internal static void WriteName(BinaryWriter w, string name)
    {
        // the base64 text is wrapped with CRLF every 72 characters. Encoded in one buffer, without the five objects
        // per name the string route made: a snapshot of a big drive writes millions of names
        int utf8Len = Encoding.UTF8.GetByteCount(name);
        int b64Len = (utf8Len + 2) / 3 * 4;
        int outLen = b64Len == 0 ? 0 : b64Len + (b64Len - 1) / 72 * 2;
        int need = utf8Len + b64Len + outLen;
        byte[]? rented = null;
        Span<byte> buf = need <= 1024 ? stackalloc byte[1024] : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(need));
        try
        {
            var utf8 = buf.Slice(0, utf8Len);
            Encoding.UTF8.GetBytes(name, utf8);
            var b64 = buf.Slice(utf8Len, b64Len);
            System.Buffers.Text.Base64.EncodeToUtf8(utf8, b64, out _, out int written);
            var text = buf.Slice(utf8Len + b64Len, outLen);
            int o = 0;
            for (int i = 0; i < written; i += 72)
            {
                if (i > 0) { text[o++] = (byte)'\r'; text[o++] = (byte)'\n'; }
                int n = Math.Min(72, written - i);
                b64.Slice(i, n).CopyTo(text.Slice(o));
                o += n;
            }
            w.Write(o);
            w.Write(text.Slice(0, o));
        }
        finally { if (rented != null) System.Buffers.ArrayPool<byte>.Shared.Return(rented); }
    }

    private static long ToFileTime(DateTime utc)
    {
        if (utc == default) return 0;
        try { return DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToFileTimeUtc(); } catch { return 0; }
    }

    // ------------------------------------------------------------------ read

    /// <summary>
    /// Reads a snapshot. Folders are opened and closed on a stack of its own (no recursion), and one nested deeper than
    /// <see cref="FsNode.MaxDepth"/> levels ends the read: a crafted file cannot exhaust the thread's stack.
    /// </summary>
    public static DriveNode Read(string path, Action<long, long>? progress = null, CancellationToken token = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var r = new BinaryReader(fs);
        DriveNode? root = null;
        var open = new Stack<ContainerNode>();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int op = fs.ReadByte();   // BinaryReader does no read-ahead for binary reads, so mixing both is safe
            if (op < 0) throw new InvalidDataException("Bad structure: unexpected end of file");
            if (op == OpClose && open.Count > 0)
            {
                fs.ReadByte();
                var done = open.Pop();
                done.Enumerated = true;
                done.ScanComplete = true;
                progress?.Invoke(fs.Position, fs.Length);
                if (open.Count == 0) return root!;
                continue;
            }
            if (op != OpOpen) throw new InvalidDataException(open.Count == 0 ? $"Bad structure: expected node at offset {fs.Position - 1}" : $"Bad structure at offset {fs.Position - 1}");
            var parent = open.Count > 0 ? open.Peek() : null;
            var node = ReadHeader(r, parent, fs);
            if (node is ContainerNode c)
            {
                if (open.Count > FsNode.MaxDepth) throw new InvalidDataException($"The snapshot nests folders more than {FsNode.MaxDepth} levels deep");
                if (parent == null) root = (DriveNode)c;
                open.Push(c);
            }
            else if (parent == null) throw new InvalidDataException("Snapshot has no root container");
            else progress?.Invoke(fs.Position, fs.Length);
        }
    }

    /// <summary>A node after its open marker: a folder up to its children, a file with its end marker; added to <paramref name="parent"/>.</summary>
    private static FsNode ReadHeader(BinaryReader r, ContainerNode? parent, FileStream fs)
    {
        byte type = r.ReadByte();
        int len = r.ReadInt32();
        if (len < 0 || len > 1 << 20) throw new InvalidDataException("Bad structure: name length");
        var nameBytes = r.ReadBytes(len);
        string name = Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.ASCII.GetString(nameBytes)));
        long logical = r.ReadInt64();

        if (type == TypeFolder)
        {
            r.ReadInt64();                       // disk + slack (recomputed from children)
            r.ReadInt64(); r.ReadInt64(); r.ReadInt64();
            byte tag = r.ReadByte(); r.ReadByte();
            ContainerNode c = parent == null ? new DriveNode(name.EndsWith('\\') ? name : name + "\\") { IsDrive = false } : new FolderNode(name);
            c.Tag = (TagColor)(tag & 0x0F);
            parent?.AddChild(c);
            return c;
        }
        if (type == TypeFile)
        {
            int slack = r.ReadInt32();
            int attrs = r.ReadInt32();
            long ftc = r.ReadInt64(), fta = r.ReadInt64(), ftw = r.ReadInt64();
            byte tag = r.ReadByte(); r.ReadByte();
            var f = new FileNode(name)
            {
                LogicalSize = logical, DiskSize = logical + Math.Max(0, slack), Attributes = (FileAttributes)attrs,
                Created = FromFileTime(ftc), Accessed = FromFileTime(fta), Modified = FromFileTime(ftw), Tag = (TagColor)(tag & 0x0F),
            };
            byte c1 = r.ReadByte(), c2 = r.ReadByte();
            if (c1 != OpClose) throw new InvalidDataException($"Bad structure: expected end of file record at offset {fs.Position - 2}");
            parent?.AddChild(f);
            return f;
        }
        throw new InvalidDataException($"Bad structure: unknown node type {type}");
    }

    private static DateTime FromFileTime(long ft)
    {
        if (ft <= 0) return default;
        try { return DateTime.FromFileTimeUtc(ft); } catch { return default; }
    }
}

/// <summary>
/// Loader for Sysinternals "du" text output (du -l / du -v style: size in KB followed by the path),
/// the other snapshot input format that can be opened.
/// </summary>
public static class DuFormat
{
    public static bool IsDuFile(string path) => path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".du", StringComparison.OrdinalIgnoreCase);

    private static readonly char[] PathSeparators = { '\\', '/' };

    public static DriveNode? Read(string path)
    {
        var lines = File.ReadAllLines(path);
        var entries = new List<(long sizeBytes, string p)>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            // formats: "<size>\t<path>", "<size> <path>", "<path>,<size>" (csv)
            string[] parts = line.Split(new[] { '\t' }, 2);
            if (parts.Length < 2) parts = line.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) { var c = line.LastIndexOf(','); if (c > 0) parts = new[] { line.Substring(c + 1), line.Substring(0, c) }; }
            if (parts.Length < 2) continue;
            string a = parts[0].Trim().Trim('"'), b = parts[1].Trim().Trim('"');
            // a real entry has a path with a separator; this skips the "Size (KB) Path" header and the "Files: 4" totals
            if (long.TryParse(a.Replace(",", "").Replace(".", ""), out long kb) && b.IndexOfAny(PathSeparators) >= 0) entries.Add((kb * 1024, b));
            else if (long.TryParse(b.Replace(",", "").Replace(".", ""), out kb) && a.IndexOfAny(PathSeparators) >= 0) entries.Add((kb * 1024, a));
        }
        if (entries.Count == 0) return null;
        var rootPath = entries.Select(e => e.p).OrderBy(p => p.Length).First().TrimEnd('\\');
        var root = new DriveNode(rootPath) { IsDrive = false };
        var map = new Dictionary<string, ContainerNode>(StringComparer.OrdinalIgnoreCase) { [rootPath] = root };
        ContainerNode GetFolder(string p)
        {
            p = p.TrimEnd('\\');
            if (map.TryGetValue(p, out var n)) return n;
            var parentPath = Path.GetDirectoryName(p) ?? rootPath;
            var parent = GetFolder(parentPath);
            var f = new FolderNode(Path.GetFileName(p)) { Enumerated = true, ScanComplete = true };
            parent.AddChild(f);
            map[p] = f;
            return f;
        }
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (size, p) in entries) sizes[p.TrimEnd('\\')] = size;
        foreach (var (size, p) in entries)
        {
            var np = p.TrimEnd('\\');
            if (string.Equals(np, rootPath, StringComparison.OrdinalIgnoreCase)) continue;
            // no deeper than a scan goes (GetFolder recurses per level)
            if (np.Length > rootPath.Length && np.Substring(rootPath.Length).Count(ch => ch is '\\' or '/') > FsNode.MaxDepth) continue;
            bool hasChildren = sizes.Keys.Any(k => k.StartsWith(np + "\\", StringComparison.OrdinalIgnoreCase));
            if (hasChildren) { GetFolder(np); continue; }
            var parent = GetFolder(Path.GetDirectoryName(np) ?? rootPath);
            // leaf: represent as folder with a single "content" file so sizes show up
            var folder = new FolderNode(Path.GetFileName(np)) { Enumerated = true, ScanComplete = true };
            parent.AddChild(folder);
            map[np] = folder;
            if (size > 0) folder.AddChild(new FileNode("(folder content)") { LogicalSize = size, DiskSize = size });
        }
        // folders whose own size exceeds the children sum get a content file for the difference
        foreach (var (p, n) in map.ToArray())
        {
            if (!sizes.TryGetValue(p, out var s)) continue;
            long diff = s - n.DiskSize;
            if (diff > 0) n.AddChild(new FileNode("(files in folder)") { LogicalSize = diff, DiskSize = diff });
        }
        root.Enumerated = true; root.ScanComplete = true;
        Log.Debug($"Disk Usage output loaded: {entries.Count} entries");
        return root;
    }
}
