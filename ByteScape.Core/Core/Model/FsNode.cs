using System.Runtime.CompilerServices;

namespace ByteScape.Core.Model;

/// <summary>Kind of element in the scanned tree.</summary>
public enum NodeKind
{
    Drive,
    Folder,
    File,
    FreeSpace,      // synthetic: free space of the drive (root only)
    UnknownSpace,   // synthetic: space not scanned yet, or that could not be read (root only)
    MissingData,    // synthetic: placeholder while a container has no scanned content yet
    Rest,           // synthetic: aggregate of the elements too small to get their own rectangle
}

/// <summary>Tag colors. Stored as a bitmask so a snapshot byte can carry them.</summary>
[Flags]
public enum TagColor : byte
{
    None = 0,
    Red = 1,
    Yellow = 2,
    Green = 4,
    Blue = 8,
    All = Red | Yellow | Green | Blue,
}

/// <summary>
/// Base element of the scanned tree. All size bookkeeping is propagated upwards through
/// <see cref="ContainerNode"/> so folder sizes are always up to date while scanning.
/// The whole tree is guarded by <see cref="ScanTree.Sync"/>; callers that walk the tree
/// from the UI thread must take that lock.
/// </summary>
public abstract class FsNode
{
    protected FsNode(string name)
    {
        Name = name;
    }

    public string Name { get; internal set; }
    public ContainerNode? Parent { get; internal set; }

    /// <summary>Logical size (sum of file lengths).</summary>
    public long LogicalSize { get; internal set; }

    /// <summary>Size occupied on disk (rounded up to cluster / compressed size).</summary>
    public long DiskSize { get; internal set; }

    /// <summary>Own tag (not inherited).</summary>
    public TagColor Tag { get; set; }

    /// <summary>Tick count of the last external modification, used for the blink effect.</summary>
    public long ChangedAtTicks { get; internal set; }

    public abstract NodeKind Kind { get; }

    public virtual bool IsContainer => false;
    public virtual bool IsSynthetic => false;

    /// <summary>
    /// The deepest folder level a scan lists and a snapshot may hold (root = 0). Real folder trees stay far shallower (a
    /// few dozen levels even in package caches); a chain built deeper — to crash the program through its stack, since many
    /// walks over the tree recurse per level — is cut here: what lies deeper is not listed (the folder shows as not
    /// accessible) and such a snapshot is refused. The tests run every walk at this depth on half an interface thread's stack.
    /// </summary>
    public const int MaxDepth = 256;

    /// <summary>Nesting level, root = 0.</summary>
    public int Level
    {
        get
        {
            int l = 0;
            for (var p = Parent; p != null; p = p.Parent) l++;
            return l;
        }
    }

    /// <summary>Tag including tags inherited from parents.</summary>
    public TagColor EffectiveTag
    {
        get
        {
            var t = Tag;
            for (var p = Parent; p != null; p = p.Parent) t |= p.Tag;
            return t;
        }
    }

    public ContainerNode Root
    {
        get
        {
            FsNode n = this;
            while (n.Parent != null) n = n.Parent;
            return (ContainerNode)n;
        }
    }

    /// <summary>Full path of this element on disk (or inside the snapshot).</summary>
    public virtual string FullPath
    {
        get
        {
            if (Parent == null) return Name;
            // built once, top down: concatenating level by level made a new string per level (quadratic in the depth, and
            // paths are asked for millions of times by safety checks, the knowledge base, tooltips and reports)
            int depth = 0;
            FsNode top = this;
            while (top.Parent != null && top is not DriveNode) { top = top.Parent; depth++; }
            if (depth == 1 && top.FullPath is var only) return only.EndsWith('\\') ? only + Name : only + "\\" + Name;
            var chain = new FsNode[depth];
            for (FsNode n = this; depth > 0; n = n.Parent!) chain[--depth] = n;
            var sb = new System.Text.StringBuilder(top.FullPath, 260); // a drive root (or snapshot frame) knows its own path
            foreach (var n in chain)
            {
                if (sb.Length == 0 || sb[sb.Length - 1] != '\\') sb.Append('\\');
                sb.Append(n.Name);
            }
            return sb.ToString();
        }
    }

    public bool IsAncestorOf(FsNode other)
    {
        for (var p = other.Parent; p != null; p = p.Parent)
            if (ReferenceEquals(p, this)) return true;
        return false;
    }

    public override string ToString() => $"{Kind} {Name} [{DiskSize}]";
}

/// <summary>A file. Timestamps are UTC.</summary>
public sealed class FileNode : FsNode
{
    public FileNode(string name) : base(name) { }

    public override NodeKind Kind => NodeKind.File;

    public FileAttributes Attributes { get; internal set; }
    public DateTime Created { get; internal set; }
    public DateTime Accessed { get; internal set; }
    public DateTime Modified { get; internal set; }

    /// <summary>True when the file carries at least one NTFS alternate data stream (only known when ADS scan is on).</summary>
    public bool HasAlternateDataStream { get; internal set; }

    /// <summary>NTFS file id (the names of a hard-linked file share it); 0 when not read.</summary>
    public long FileId { get; internal set; }

    /// <summary>A hard link whose space is counted at another name of the file: it counts 0 on disk here.</summary>
    public bool CountedElsewhere { get; internal set; }

    public string Extension
    {
        get
        {
            int i = Name.LastIndexOf('.');
            return i < 0 ? string.Empty : Name.Substring(i + 1);
        }
    }

    public string NameWithoutExtension
    {
        get
        {
            int i = Name.LastIndexOf('.');
            return i < 0 ? Name : Name.Substring(0, i);
        }
    }
}

/// <summary>Anything that can hold children (drive, folder, snapshot frame).</summary>
public abstract class ContainerNode : FsNode
{
    private readonly List<FsNode> _children = new();

    protected ContainerNode(string name) : base(name) { }

    public override bool IsContainer => true;

    /// <summary>Live children list. Take <see cref="ScanTree.Sync"/> before enumerating.</summary>
    public IReadOnlyList<FsNode> Children => _children;

    public int ChildCount => _children.Count;

    /// <summary>True when the direct children of this container have been listed at least once.</summary>
    public bool Enumerated { get; internal set; }

    /// <summary>True when the scanner finished enumerating this container (recursively).</summary>
    public bool ScanComplete { get; internal set; }

    /// <summary>True when the scanner could not open this container (access denied etc).</summary>
    public bool AccessDenied { get; internal set; }

    /// <summary>Number of files below this node (recursive).</summary>
    public int FileCount { get; internal set; }

    /// <summary>Number of folders below this node (recursive).</summary>
    public int FolderCount { get; internal set; }

    /// <summary>Monotonic counter bumped whenever the child list or a size below changes.</summary>
    public int Version { get; internal set; }

    public FsNode? FindChild(string name)
    {
        foreach (var c in _children)
            if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    /// <summary>Adds a child and propagates its size to all ancestors.</summary>
    public void AddChild(FsNode child)
    {
        child.Parent = this;
        _children.Add(child);
        int files = 0, folders = 0;
        if (child is FileNode) files = 1;
        else if (child is ContainerNode cc) { folders = 1; files = cc.FileCount; folders += cc.FolderCount; }
        Propagate(child.LogicalSize, child.DiskSize, files, folders);
    }

    public void RemoveChild(FsNode child)
    {
        if (!_children.Remove(child)) return;
        int files = 0, folders = 0;
        if (child is FileNode) files = 1;
        else if (child is ContainerNode cc) { folders = 1; files = cc.FileCount; folders += cc.FolderCount; }
        Propagate(-child.LogicalSize, -child.DiskSize, -files, -folders);
        child.Parent = null;
    }

    /// <summary>Changes the size of a direct child and propagates the delta upwards.</summary>
    internal void ResizeChild(FsNode child, long logical, long disk)
    {
        long dl = logical - child.LogicalSize;
        long dd = disk - child.DiskSize;
        child.LogicalSize = logical;
        child.DiskSize = disk;
        Propagate(dl, dd, 0, 0);
    }

    internal void Propagate(long logicalDelta, long diskDelta, int fileDelta, int folderDelta)
    {
        for (ContainerNode? n = this; n != null; n = n.Parent)
        {
            n.LogicalSize += logicalDelta;
            n.DiskSize += diskDelta;
            n.FileCount += fileDelta;
            n.FolderCount += folderDelta;
            n.Version++;
        }
    }

    internal void Touch()
    {
        for (ContainerNode? n = this; n != null; n = n.Parent) n.Version++;
    }

    /// <summary>Recursive enumeration (depth first), including this node when <paramref name="includeSelf"/>.</summary>
    public IEnumerable<FsNode> Descendants(bool includeSelf = false)
    {
        if (includeSelf) yield return this;
        var stack = new Stack<FsNode>();
        for (int i = _children.Count - 1; i >= 0; i--) stack.Push(_children[i]);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            if (n is ContainerNode c)
                for (int i = c._children.Count - 1; i >= 0; i--) stack.Push(c._children[i]);
        }
    }

    internal void SortChildrenBySizeDescending()
    {
        _children.Sort((a, b) => b.DiskSize.CompareTo(a.DiskSize));
    }

    internal List<FsNode> ChildrenSnapshot() => new(_children);
}

public sealed class FolderNode : ContainerNode
{
    public FolderNode(string name) : base(name) { }

    /// <summary>NTFS file id of the folder when the listing read it (its master file table record: the disk order of a scan).</summary>
    public long FileId { get; internal set; }
    public override NodeKind Kind => NodeKind.Folder;
    public FileAttributes Attributes { get; internal set; }
    public DateTime Created { get; internal set; }
    public DateTime Accessed { get; internal set; }
    public DateTime Modified { get; internal set; }
}

/// <summary>
/// Root of a scan. It is a drive when the path is a drive root ("C:\"), otherwise it
/// behaves like a folder but keeps the full path as its name.
/// </summary>
public sealed class DriveNode : ContainerNode
{
    public DriveNode(string rootPath) : base(rootPath) { }

    public override NodeKind Kind => NodeKind.Drive;

    /// <summary>True when the root is a real drive (free/unknown space are meaningful only then).</summary>
    public bool IsDrive { get; internal set; }

    /// <summary>Synthetic "[SNAPSHOT: file]" wrapper around a loaded snapshot; exports start at its content.</summary>
    public bool IsSnapshotFrame { get; internal set; }

    public long TotalBytes { get; internal set; }
    public long FreeBytes { get; internal set; }
    public int ClusterSize { get; internal set; } = 4096;

    /// <summary>Space the OS reports as used but the scanner has not (yet) accounted for.</summary>
    public long UnknownBytes => IsDrive ? Math.Max(0, TotalBytes - FreeBytes - DiskSize) : 0;

    /// <summary>Root paths carry a trailing backslash ("C:\", "C:\data\"); inside a snapshot frame the frame name is prepended.</summary>
    public override string FullPath => Parent == null ? Name : Parent.FullPath + "\\" + Name;
}

/// <summary>Path helpers shared by the knowledge base, the comparison and the views.</summary>
public static class FsPaths
{
    /// <summary>Path without the "[SNAPSHOT: file]" frame a loaded snapshot is wrapped in, without a trailing backslash.</summary>
    public static string Real(string fullPath)
    {
        var p = fullPath;
        if (p.StartsWith("[SNAPSHOT: ", StringComparison.Ordinal))
        {
            int i = p.IndexOf(@"]\", StringComparison.Ordinal);
            if (i > 0) p = p.Substring(i + 2);
        }
        return p.TrimEnd('\\');
    }

    /// <summary>Real (frame-less) path of a node.</summary>
    public static string RealPath(this FsNode n) => Real(n.FullPath);
}

/// <summary>Synthetic root-level element: free space of the drive.</summary>
public sealed class FreeSpaceNode : FsNode
{
    public FreeSpaceNode() : base("Free Space") { }
    public override NodeKind Kind => NodeKind.FreeSpace;
    public override bool IsSynthetic => true;
}

/// <summary>Synthetic root-level element: space not scanned yet (or, once the scan ended, space that could not be read).</summary>
public sealed class UnknownSpaceNode : FsNode
{
    public UnknownSpaceNode() : base("Not scanned yet") { }
    public override NodeKind Kind => NodeKind.UnknownSpace;
    public override bool IsSynthetic => true;
    public bool ScanFinished { get; set; }
    public bool Filtering { get; set; }
    public string DisplayName => T(ScanFinished ? (Filtering ? "Unreadable space (filter on)" : "Unreadable space") : "Not scanned yet");
}

/// <summary>
/// Synthetic element standing for all children of a container that are too small to be drawn
/// individually. It keeps the area they occupy (so proportions stay honest) without leaving a void.
/// </summary>
public sealed class RestNode : FsNode
{
    public RestNode() : base("...") { }
    public override NodeKind Kind => NodeKind.Rest;
    public override bool IsSynthetic => true;
    public int Count { get; set; }
    public long Bytes { get; set; }
}

/// <summary>Synthetic element shown inside a container that has not been scanned yet.</summary>
public sealed class MissingDataNode : FsNode
{
    public MissingDataNode() : base("...") { }
    public override NodeKind Kind => NodeKind.MissingData;
    public override bool IsSynthetic => true;
}
