using System.Runtime.CompilerServices;
using ByteScape.Core.Model;
using ByteScape.Core.Util;

namespace ByteScape.Core.Knowledge;

/// <summary>How safely the content of a known location can be removed.</summary>
public enum Safety
{
    /// <summary>Pure cache: deleting it costs nothing but a re-download / rebuild.</summary>
    Regenerable,
    /// <summary>Regenerable, but the owning program must be closed first (or its own cleanup command used).</summary>
    SafeWhenClosed,
    /// <summary>Removable, but something is lost or must be redone (settings, offline copies, VMs, backups).</summary>
    Caution,
    /// <summary>Part of Windows or another program: never delete by hand; use the tool named in the hint, if any.</summary>
    System,
}

public enum LocationCategory { PackageCache, BuildCache, IdeCache, BrowserCache, AppCache, ShaderCache, WindowsTemp, WindowsUpdate, WindowsSystem, RecycleBin, Backup, VirtualMachine, Game, Models, Cloud, Logs }

/// <summary>One curated rule: what a folder is, who makes it, what deleting it does and the proper way to clean it.</summary>
public sealed class KnownLocation
{
    // the texts are written in English (the catalog key) and read in the interface language
    private string _title = "", _description = "", _consequence = "", _cleanup = "";
    public required string Id { get; init; }
    public required string Title { get => T(_title); init => _title = value; }
    public required LocationCategory Category { get; init; }
    public required Safety Safety { get; init; }
    /// <summary>What lives here and which program creates it.</summary>
    public required string Description { get => T(_description); init => _description = value; }
    /// <summary>What happens after deleting it.</summary>
    public required string Consequence { get => T(_consequence); init => _consequence = value; }
    /// <summary>The recommended way to reclaim the space (command, tool or menu), empty when "just delete it".</summary>
    public string Cleanup { get => T(_cleanup); init => _cleanup = value; }
    /// <summary>
    /// Path patterns. Placeholders: %USERPROFILE%, %LOCALAPPDATA%, %APPDATA%, %TEMP%, %WINDIR%, %PROGRAMDATA%, %PROGRAMFILES%,
    /// %PROGRAMFILES(X86)%, %SYSTEMDRIVE% expand to any drive / any user; "**\name" matches a folder of that name anywhere.
    /// </summary>
    public required string[] Patterns { get; init; }

    internal Wildcard[] Compiled = Array.Empty<Wildcard>();
    /// <summary>Exact last path segment of each pattern (lower case) or null when it contains a wildcard.</summary>
    internal string?[] LeafNames = Array.Empty<string?>();

    public bool IsReclaimable => Safety is Safety.Regenerable or Safety.SafeWhenClosed;
}

/// <summary>Result of classifying a node: the rule and the node the rule matched (the node itself or an ancestor).</summary>
/// <param name="MatchedPath">Full path of the folder the rule matched (an ancestor outside the scanned tree when the scan started inside a known location).</param>
public sealed record KnowledgeMatch(KnownLocation Location, FsNode MatchedNode, bool Inherited, string MatchedPath);

/// <summary>
/// "What is this folder and can I delete it?" — a curated table of well known caches, temp areas and protected
/// system folders (see <see cref="KnownLocations"/>) matched against full paths. Everything below a matched folder
/// inherits its classification. Results are cached per node.
/// </summary>
public static class KnowledgeBase
{
    private static readonly object Sync = new();
    private static KnownLocation[]? _rules;
    private static Dictionary<string, List<KnownLocation>> _byLeaf = new(StringComparer.OrdinalIgnoreCase);
    private static List<KnownLocation> _wildcardLeaf = new();
    private static readonly ConditionalWeakTable<ContainerNode, Box> Cache = new();

    private sealed class Box { public KnowledgeMatch? Match; }

    public static IReadOnlyList<KnownLocation> Rules { get { Ensure(); return _rules!; } }

    private static void Ensure()
    {
        if (_rules != null) return;
        lock (Sync)
        {
            if (_rules != null) return;
            var rules = KnownLocations.All;
            var byLeaf = new Dictionary<string, List<KnownLocation>>(StringComparer.OrdinalIgnoreCase);
            var wild = new List<KnownLocation>();
            foreach (var r in rules)
            {
                r.Compiled = r.Patterns.Select(p => new Wildcard(Expand(p), pathMode: true)).ToArray();
                r.LeafNames = r.Patterns.Select(Leaf).ToArray();
                bool anyWild = false;
                foreach (var leaf in r.LeafNames)
                {
                    if (leaf == null) { anyWild = true; continue; }
                    if (!byLeaf.TryGetValue(leaf, out var list)) byLeaf[leaf] = list = new List<KnownLocation>();
                    if (!list.Contains(r)) list.Add(r);
                }
                if (anyWild) wild.Add(r);
            }
            _byLeaf = byLeaf; _wildcardLeaf = wild;
            _rules = rules;
        }
    }

    /// <summary>Placeholders become wildcards so the rules apply to every drive and every user profile.</summary>
    internal static string Expand(string pattern)
    {
        var p = pattern;
        p = p.Replace("%LOCALAPPDATA%", @"?:\Users\*\AppData\Local", StringComparison.OrdinalIgnoreCase);
        p = p.Replace("%APPDATA%", @"?:\Users\*\AppData\Roaming", StringComparison.OrdinalIgnoreCase);
        p = p.Replace("%TEMP%", @"?:\Users\*\AppData\Local\Temp", StringComparison.OrdinalIgnoreCase);
        p = p.Replace("%USERPROFILE%", @"?:\Users\*", StringComparison.OrdinalIgnoreCase);
        p = p.Replace("%WINDIR%", @"?:\Windows", StringComparison.OrdinalIgnoreCase);
        p = p.Replace("%PROGRAMDATA%", @"?:\ProgramData", StringComparison.OrdinalIgnoreCase);
        p = p.Replace("%PROGRAMFILES(X86)%", @"?:\Program Files (x86)", StringComparison.OrdinalIgnoreCase);
        p = p.Replace("%PROGRAMFILES%", @"?:\Program Files", StringComparison.OrdinalIgnoreCase);
        p = p.Replace("%SYSTEMDRIVE%", "?:", StringComparison.OrdinalIgnoreCase);
        return p;
    }

    private static string? Leaf(string pattern)
    {
        int i = pattern.LastIndexOf('\\');
        var leaf = i < 0 ? pattern : pattern.Substring(i + 1);
        return leaf.IndexOfAny(new[] { '*', '?', '%' }) >= 0 ? null : leaf;
    }

    /// <summary>The rule matching exactly this node (not inherited), or null. Files only match file rules (pagefile.sys, MEMORY.DMP...).</summary>
    public static KnownLocation? MatchOwn(FsNode n)
    {
        if (n.IsSynthetic) return null;
        Ensure();
        // files can only match the few file rules; skip building the path for everything else
        if (n is not ContainerNode && !_byLeaf.ContainsKey(n.Name)) return null;
        string path;
        try { path = RealPath(n.FullPath); } catch { return null; }
        // a scan root is named by its full path ("C:\Users\x\.gradle\caches\"): its leaf is the last segment
        return MatchPath(path, n is DriveNode ? Path.GetFileName(path) : n.Name, n is ContainerNode);
    }

    internal static string RealPath(string fullPath) => FsPaths.Real(fullPath);

    /// <summary>Own rule of a folder given its real path (no inheritance, no node needed): for walks that build paths incrementally.</summary>
    internal static KnownLocation? MatchFolderPath(string path, string leaf) => MatchPath(path, leaf, true);

    /// <summary>True when a file of that name inside <paramref name="folderPath"/> is itself a reclaimable known location (crash dumps...).</summary>
    internal static bool IsReclaimableFile(string folderPath, string name)
    {
        Ensure();
        return _byLeaf.ContainsKey(name) && MatchPath(folderPath + "\\" + name, name, false) is { IsReclaimable: true };
    }

    /// <summary>Rule for a full path: candidates by exact leaf name first (cheap), then the few rules whose leaf is a wildcard (folders only).</summary>
    private static KnownLocation? MatchPath(string path, string leaf, bool isFolder)
    {
        Ensure();
        if (path.Length == 0) return null;
        KnownLocation? best = null; int bestLen = -1;
        if (_byLeaf.TryGetValue(leaf, out var list))
            foreach (var r in list) Try(r, path, ref best, ref bestLen);
        if (isFolder)
            foreach (var r in _wildcardLeaf) Try(r, path, ref best, ref bestLen);
        return best;
    }

    private static void Try(KnownLocation r, string path, ref KnownLocation? best, ref int bestLen)
    {
        for (int i = 0; i < r.Compiled.Length; i++)
        {
            if (!r.Compiled[i].IsMatch(path)) continue;
            int len = r.Patterns[i].Length;            // the most specific (longest) pattern wins
            if (len > bestLen) { best = r; bestLen = len; }
        }
    }

    /// <summary>Classification of a node: its own rule or the nearest classified ancestor's (cached).</summary>
    public static KnowledgeMatch? Classify(FsNode node)
    {
        if (node.IsSynthetic) return null;
        if (node is ContainerNode c) return ClassifyContainer(c);
        var own = MatchOwn(node);
        if (own != null) return new KnowledgeMatch(own, node, false, node.FullPath);
        if (node.Parent is ContainerNode p && ClassifyContainer(p) is { } pm) return new KnowledgeMatch(pm.Location, pm.MatchedNode, true, pm.MatchedPath);
        return null;
    }

    private static KnowledgeMatch? ClassifyContainer(ContainerNode c)
    {
        if (Cache.TryGetValue(c, out var box)) return box.Match;
        KnowledgeMatch? m = null;
        var own = MatchOwn(c);
        if (own != null) m = new KnowledgeMatch(own, c, false, RealPath(c.FullPath));
        else if (c.Parent is ContainerNode p)
        {
            var pm = ClassifyContainer(p);
            if (pm != null) m = new KnowledgeMatch(pm.Location, pm.MatchedNode, true, pm.MatchedPath);
        }
        else if (!c.IsSynthetic)
        {
            // the scanned root itself: its ancestors are not in the tree, classify them by path
            string path;
            try { path = RealPath(c.FullPath); } catch { path = ""; }
            for (var dir = SafeParent(path); dir != null; dir = SafeParent(dir))
            {
                var r = MatchPath(dir, Path.GetFileName(dir), true);
                if (r != null) { m = new KnowledgeMatch(r, c, true, dir); break; }
            }
        }
        Cache.AddOrUpdate(c, new Box { Match = m });
        return m;
    }

    private static string? SafeParent(string path)
    {
        try { var p = Path.GetDirectoryName(path); return string.IsNullOrEmpty(p) ? null : p.TrimEnd('\\'); } catch { return null; }
    }

    /// <summary>True for a node inside a location whose content can be removed without losing anything (cache, temp).</summary>
    public static bool IsReclaimable(FsNode node) => Classify(node) is { Location.IsReclaimable: true };

    /// <summary>
    /// Sums the reclaimable locations below <paramref name="root"/> (each matched folder counted once, no double
    /// counting of nested rules). Paths are built incrementally and the tree lock (<paramref name="sync"/>) is taken
    /// per folder only, so a running scan and the UI are never blocked for long; meant for a background thread.
    /// </summary>
    public static ReclaimSummary Summarize(ContainerNode root, Func<FsNode, long> sizeOf, CancellationToken token, object? sync = null)
    {
        Ensure();
        var items = new List<ReclaimItem>();
        string rootPath;
        try { rootPath = RealPath(root.FullPath); } catch { rootPath = ""; }
        Walk(root, rootPath, true);
        items.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        long total = 0; foreach (var i in items) total += i.Bytes;
        return new ReclaimSummary(total, items);

        List<FsNode> Kids(ContainerNode c)
        {
            if (sync == null) return c.ChildrenSnapshot();
            lock (sync) return c.ChildrenSnapshot();
        }

        void Walk(ContainerNode c, string path, bool isRoot)
        {
            token.ThrowIfCancellationRequested();
            // the root may be a known location itself or lie inside one (scanning .gradle\caches or a Temp sub folder directly)
            var own = isRoot ? Classify(c)?.Location : MatchPath(path, c.Name, true);
            if (own is { IsReclaimable: true })
            {
                long s = sizeOf(c) - Excluded(c, path);
                if (s > 0) items.Add(new ReclaimItem(c, own, s));
                return;
            }
            bool frame = c is DriveNode { IsSnapshotFrame: true };     // children of the "[SNAPSHOT: file]" frame start their own real path
            foreach (var k in Kids(c))
            {
                if (k.IsSynthetic) continue;
                if (frame) { if (k is ContainerNode fc) Walk(fc, RealPath(k.Name), false); continue; }
                if (k is ContainerNode kc) Walk(kc, path + "\\" + k.Name, false);
                else if (_byLeaf.ContainsKey(k.Name) && MatchPath(path + "\\" + k.Name, k.Name, false) is { IsReclaimable: true } fr) { long s = sizeOf(k); if (s > 0) items.Add(new ReclaimItem(k, fr, s)); }
            }
        }

        long Excluded(ContainerNode c, string path)
        {
            long sum = 0;
            foreach (var k in Kids(c))
            {
                if (k is not ContainerNode kc || kc.IsSynthetic) continue;
                var kp = path + "\\" + k.Name;
                var own = MatchPath(kp, k.Name, true);
                if (own != null && !own.IsReclaimable) sum += sizeOf(kc);
                else sum += Excluded(kc, kp);
            }
            return sum;
        }
    }
}

public sealed record ReclaimItem(FsNode Node, KnownLocation Location, long Bytes);

public sealed record ReclaimSummary(long TotalBytes, IReadOnlyList<ReclaimItem> Items)
{
    public static readonly ReclaimSummary Empty = new(0, Array.Empty<ReclaimItem>());
}
