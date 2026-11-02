using ByteScape.Core.Model;

namespace ByteScape.Core.Compare;

public enum ChangeKind { Added, Removed, Grown, Shrunk }

/// <summary>One changed element: a folder or file that grew, shrank, appeared or vanished between two trees.</summary>
public sealed record DiffEntry(string Path, string Name, bool IsFolder, long OldBytes, long NewBytes, ChangeKind Kind, FsNode? Current, int Depth)
{
    public long Delta => NewBytes - OldBytes;
    public long AbsDelta => Math.Abs(NewBytes - OldBytes);
}

/// <summary>Result of comparing a baseline (older snapshot) with the current tree.</summary>
public sealed class DiffResult
{
    public required string BaselinePath { get; init; }
    public required DateTime BaselineTime { get; init; }
    public required string CurrentPath { get; init; }
    public required long OldTotal { get; init; }
    public required long NewTotal { get; init; }
    public long Delta => NewTotal - OldTotal;
    /// <summary>Every changed folder and file, biggest absolute change first.</summary>
    public required IReadOnlyList<DiffEntry> Entries { get; init; }
    /// <summary>Change of every current node that differs from the baseline (folders carry the sum of their subtree), for the map overlay.</summary>
    public required IReadOnlyDictionary<FsNode, long> DeltaOf { get; init; }
    /// <summary>Current nodes that did not exist in the baseline.</summary>
    public required IReadOnlySet<FsNode> AddedNodes { get; init; }
    public int AddedCount { get; init; }
    public int RemovedCount { get; init; }
    public long AddedBytes { get; init; }
    public long RemovedBytes { get; init; }
    /// <summary>Largest single-node change (used to scale the overlay tint).</summary>
    public long MaxAbsDelta { get; init; }
}

/// <summary>
/// Compares two trees by relative path (case-insensitive names). Folders are compared by their subtree sizes,
/// so a folder that grew appears together with the files inside it that caused the growth; the list can then be
/// filtered by kind or restricted to files or folders.
/// </summary>
public static class TreeDiff
{
    /// <summary>
    /// Finds the node of <paramref name="baselineRoot"/> that corresponds to <paramref name="currentPath"/>
    /// (the baseline may cover a bigger area than the current view), or null when the areas do not overlap.
    /// </summary>
    public static ContainerNode? Align(ContainerNode baselineRoot, string currentPath)
    {
        // a loaded snapshot view carries a "[SNAPSHOT: file]" frame in its paths: compare real paths on both sides
        string bp = FsPaths.Real(baselineRoot.FullPath), cp = FsPaths.Real(currentPath);
        if (string.Equals(bp, cp, StringComparison.OrdinalIgnoreCase)) return baselineRoot;
        if (!cp.StartsWith(bp + "\\", StringComparison.OrdinalIgnoreCase)) return null;
        ContainerNode cur = baselineRoot;
        foreach (var part in cp.Substring(bp.Length).Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (cur.FindChild(part) is not ContainerNode next) return null;
            cur = next;
        }
        return cur;
    }


    public static DiffResult Compare(ContainerNode baseline, string baselinePath, DateTime baselineTime, ContainerNode current, object? currentSync, CancellationToken token = default)
    {
        var entries = new List<DiffEntry>();
        var deltaOf = new Dictionary<FsNode, long>(ReferenceEqualityComparer.Instance);
        var added = new HashSet<FsNode>(ReferenceEqualityComparer.Instance);
        int addedCount = 0, removedCount = 0; long addedBytes = 0, removedBytes = 0, maxAbs = 0;
        var sync = currentSync ?? new object();

        void Walk(ContainerNode o, ContainerNode n, int depth)
        {
            token.ThrowIfCancellationRequested();
            List<FsNode> newKids; long nSize;
            lock (sync) { newKids = n.Children.Where(k => !k.IsSynthetic).ToList(); nSize = n.DiskSize; }
            var oldKids = o.Children.Where(k => !k.IsSynthetic).ToList();
            long delta = nSize - o.DiskSize;
            if (delta != 0 && depth > 0)
            {
                entries.Add(new DiffEntry(n.FullPath, n.Name, true, o.DiskSize, nSize, delta > 0 ? ChangeKind.Grown : ChangeKind.Shrunk, n, depth));
                deltaOf[n] = delta;
                maxAbs = Math.Max(maxAbs, Math.Abs(delta));
            }
            var oldByName = new Dictionary<string, FsNode>(oldKids.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var k in oldKids) oldByName.TryAdd(k.Name, k);
            foreach (var k in newKids)
            {
                if (oldByName.Remove(k.Name, out var ok) && (ok is ContainerNode) == (k is ContainerNode))
                {
                    if (k is ContainerNode kc) Walk((ContainerNode)ok, kc, depth + 1);
                    else
                    {
                        long d = k.DiskSize - ok.DiskSize;
                        if (d != 0)
                        {
                            entries.Add(new DiffEntry(k.FullPath, k.Name, false, ok.DiskSize, k.DiskSize, d > 0 ? ChangeKind.Grown : ChangeKind.Shrunk, k, depth + 1));
                            deltaOf[k] = d; maxAbs = Math.Max(maxAbs, Math.Abs(d));
                        }
                    }
                }
                else
                {
                    if (ok != null) MarkRemoved(ok, o.FullPath, depth + 1);   // same name, different type
                    long size; lock (sync) size = k.DiskSize;
                    entries.Add(new DiffEntry(k.FullPath, k.Name, k is ContainerNode, 0, size, ChangeKind.Added, k, depth + 1));
                    deltaOf[k] = size; added.Add(k); addedCount++; addedBytes += size; maxAbs = Math.Max(maxAbs, size);
                    if (k is ContainerNode ac) MarkAddedChildren(ac);
                }
            }
            foreach (var ok in oldByName.Values) MarkRemoved(ok, o.FullPath, depth + 1);
        }

        void MarkAddedChildren(ContainerNode c)
        {
            List<FsNode> kids; lock (sync) kids = c.Children.Where(k => !k.IsSynthetic).ToList();
            foreach (var k in kids) { added.Add(k); deltaOf[k] = k.DiskSize; if (k is ContainerNode kc) MarkAddedChildren(kc); }
        }

        void MarkRemoved(FsNode ok, string parentPath, int depth)
        {
            entries.Add(new DiffEntry(parentPath.TrimEnd('\\') + "\\" + ok.Name, ok.Name, ok is ContainerNode, ok.DiskSize, 0, ChangeKind.Removed, null, depth));
            removedCount++; removedBytes += ok.DiskSize; maxAbs = Math.Max(maxAbs, ok.DiskSize);
        }

        Walk(baseline, current, 0);
        entries.Sort((a, b) => b.AbsDelta.CompareTo(a.AbsDelta));
        long newTotal; lock (sync) newTotal = current.DiskSize;
        return new DiffResult
        {
            BaselinePath = baselinePath, BaselineTime = baselineTime, CurrentPath = current.RealPath(),
            OldTotal = baseline.DiskSize, NewTotal = newTotal, Entries = entries, DeltaOf = deltaOf, AddedNodes = added,
            AddedCount = addedCount, RemovedCount = removedCount, AddedBytes = addedBytes, RemovedBytes = removedBytes, MaxAbsDelta = maxAbs,
        };
    }
}
