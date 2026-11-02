using System.Diagnostics;
using System.Text;
using ByteScape.Core.Knowledge;
using ByteScape.Core.Model;
using ByteScape.Core.Util;

namespace ByteScape.Core.Apps;

/// <summary>One folder of a program's footprint (or a folder no installed program claims).</summary>
public sealed class AppPiece
{
    internal AppPiece(FsNode node, string path, PieceKind kind, InstalledApp? app, AppPiece? parent, long bytes)
    {
        Node = node; Path = path; Kind = kind; App = app; Parent = parent; Bytes = bytes;
    }

    public FsNode Node { get; }
    /// <summary>Real path (without a snapshot frame).</summary>
    public string Path { get; }
    public PieceKind Kind { get; }
    /// <summary>Null: not linked to an installed program.</summary>
    public InstalledApp? App { get; }
    /// <summary>The piece this one was carved out of (a Steam game inside Steam's folder), or null.</summary>
    public AppPiece? Parent { get; }
    /// <summary>Size of the folder minus the pieces of other programs inside it.</summary>
    public long Bytes { get; internal set; }
    /// <summary>Part of <see cref="Bytes"/> inside known caches / temp areas (knowledge base).</summary>
    public long ReclaimableBytes { get; internal set; }
    /// <summary>Newest file modification inside the piece (UTC), default when it holds no files.</summary>
    public DateTime LastWrite { get; internal set; }
    /// <summary>For unlinked folders: what the knowledge base knows about them.</summary>
    public string Note { get; internal set; } = "";

    public string KindText => KindLabel(Kind);

    public static string KindLabel(PieceKind k) => T(k switch
    {
        PieceKind.Program => "Program files",
        PieceKind.LocalData => "Local app data",
        PieceKind.RoamingData => "Roaming app data",
        PieceKind.LocalLowData => "LocalLow app data",
        PieceKind.SharedData => "Shared data (ProgramData)",
        PieceKind.StoreData => "Store app data",
        PieceKind.Profile => "Profile folder",
        _ => "Other",
    });

    public override string ToString() => $"{App?.Name ?? "(unlinked)"} {KindText} {Path} [{Bytes}]";
}

/// <summary>Everything one program occupies: install folder, app data, caches, Store package data.</summary>
public sealed class AppUsage
{
    internal AppUsage(InstalledApp app) { App = app; }

    public InstalledApp App { get; }
    public List<AppPiece> Pieces { get; } = new();
    public long Bytes { get; internal set; }
    public long ProgramBytes { get; internal set; }
    public long DataBytes => Bytes - ProgramBytes;
    public long ReclaimableBytes { get; internal set; }
    /// <summary>Newest file in the program's data folders: a hint of when it was last used (program files change only on updates).</summary>
    public DateTime LastDataWrite { get; internal set; }
    public DateTime LastWrite { get; internal set; }
}

public sealed class FootprintResult
{
    /// <summary>Real path of the view root the footprint was computed for.</summary>
    public required string RootPath { get; init; }
    public long ScannedBytes { get; init; }
    /// <summary>The view is a whole drive: every program location on it was visible.</summary>
    public bool IsWholeDrive { get; init; }
    /// <summary>Programs by size, largest first.</summary>
    public required IReadOnlyList<AppUsage> Apps { get; init; }
    /// <summary>First level folders of Program Files / ProgramData / AppData no installed program claims (above the size threshold).</summary>
    public required IReadOnlyList<AppPiece> Unlinked { get; init; }
    public long AppBytes { get; init; }
    public long UnlinkedBytes { get; init; }
    public TimeSpan Elapsed { get; init; }
    internal List<AppPiece> AllPieces { get; init; } = new();

    public AppUsage? Find(InstalledApp app) => Apps.FirstOrDefault(a => ReferenceEquals(a.App, app));

    /// <summary>
    /// Map highlight for some pieces: node → true for the pieces, false for pieces of other programs carved out of
    /// them. The nearest ancestor-or-self found in the map decides; nodes without one are outside.
    /// </summary>
    public Dictionary<FsNode, bool> SpotlightFor(IEnumerable<AppPiece> pieces)
    {
        var set = new HashSet<AppPiece>(pieces, ReferenceEqualityComparer.Instance);
        var d = new Dictionary<FsNode, bool>(ReferenceEqualityComparer.Instance);
        foreach (var p in set) d[p.Node] = true;
        foreach (var p in AllPieces)
            if (p.Parent != null && set.Contains(p.Parent) && !set.Contains(p)) d[p.Node] = false;
        return d;
    }
}

/// <summary>
/// "Which program uses my disk?": walks a scanned tree once and hands every folder a program claims (see
/// <see cref="AppCatalog"/>) to that program, including caches and app data far away from its install folder.
/// A claim inside another program's piece is carved out of it (Steam games out of Steam). Unowned subtrees are
/// only walked as deep as a claim can start. Meant for a background thread: the tree lock is taken per folder.
/// </summary>
public static class AppFootprint
{
    public static FootprintResult Compute(ContainerNode root, AppCatalog catalog, Func<FsNode, long> sizeOf, CancellationToken token, object? sync = null, long minUnlinkedBytes = 1 << 20)
    {
        var sw = Stopwatch.StartNew();
        var pieces = new List<AppPiece>();
        var segs = new List<string>(32);
        string rootPath;
        try { rootPath = root.RealPath(); } catch { rootPath = ""; }

        List<FsNode> Kids(ContainerNode c)
        {
            if (sync == null) return c.ChildrenSnapshot();
            lock (sync) return c.ChildrenSnapshot();
        }

        AppPiece Add(FsNode node, string path, PieceKind kind, InstalledApp? app, AppPiece? parent)
        {
            var p = new AppPiece(node, path, kind, app, parent, sizeOf(node));
            pieces.Add(p);
            return p;
        }

        void WalkFrom(ContainerNode c, string path)
        {
            if (path.Length == 0) return;
            segs.Clear();
            segs.AddRange(path.Split('\\'));
            // a view that starts inside a program's folder (or an unlinked one) belongs to it: its ancestors decide
            AppPiece? owner = null;
            for (int n = segs.Count; n >= 2 && owner == null; n--)
            {
                var sub = segs.GetRange(0, n);
                if (catalog.Match(string.Join('\\', sub), sub) is { } claim) owner = Add(c, path, claim.Kind, claim.App, null);
            }
            for (int n = segs.Count; n >= 2 && owner == null; n--)
                if (AppCatalog.IsUnlinkedCandidate(segs.GetRange(0, n), out var kind)) owner = Add(c, path, kind, null, null);
            bool reclaim = KnowledgeBase.IsReclaimable(c);
            if (owner != null && reclaim) owner.ReclaimableBytes = owner.Bytes;
            Walk(c, path, owner, reclaim);
        }

        void Walk(ContainerNode c, string path, AppPiece? owner, bool inReclaim)
        {
            token.ThrowIfCancellationRequested();
            // no claim starts below this depth: an unowned subtree (Windows, a deep project tree) has nothing more to tell
            if (owner == null && segs.Count >= catalog.MaxMatchDepth) return;
            foreach (var k in Kids(c))
            {
                if (k.IsSynthetic) continue;
                if (k is FileNode f)
                {
                    if (owner == null) continue;
                    if (f.Modified > owner.LastWrite) owner.LastWrite = f.Modified;
                    if (!inReclaim && KnowledgeBase.IsReclaimableFile(path, f.Name)) owner.ReclaimableBytes += sizeOf(f);
                    continue;
                }
                if (k is not ContainerNode kc) continue;
                var kp = path + "\\" + kc.Name;
                segs.Add(kc.Name);
                var piece = owner;
                bool reclaim = inReclaim;
                if (segs.Count <= catalog.MaxMatchDepth)
                {
                    var claim = catalog.Match(kp, segs);
                    if (claim != null && !ReferenceEquals(claim.App, owner?.App))
                    {
                        piece = Add(kc, kp, claim.Kind, claim.App, owner);
                        if (owner != null)
                        {
                            owner.Bytes -= piece.Bytes;           // carved out: a Steam game is not Steam's
                            if (inReclaim) { owner.ReclaimableBytes -= piece.Bytes; piece.ReclaimableBytes = piece.Bytes; }
                        }
                    }
                    else if (owner == null && AppCatalog.IsUnlinkedCandidate(segs, out var kind))
                        piece = Add(kc, kp, kind, null, null);
                }
                if (!reclaim && piece != null && KnowledgeBase.MatchFolderPath(kp, kc.Name) is { IsReclaimable: true })
                {
                    reclaim = true;
                    piece.ReclaimableBytes += sizeOf(kc);
                }
                Walk(kc, kp, piece, reclaim);
                segs.RemoveAt(segs.Count - 1);
            }
        }

        if (root is DriveNode { IsSnapshotFrame: true })
        {
            // the frame's content starts its own real path
            foreach (var k in Kids(root))
                if (k is ContainerNode kc && !kc.IsSynthetic) WalkFrom(kc, FsPaths.Real(kc.Name));
        }
        else WalkFrom(root, rootPath);

        var usages = new Dictionary<InstalledApp, AppUsage>(ReferenceEqualityComparer.Instance);
        var unlinked = new List<AppPiece>();
        foreach (var p in pieces)
        {
            if (p.Bytes <= 0) continue;
            p.ReclaimableBytes = Math.Clamp(p.ReclaimableBytes, 0, p.Bytes);
            if (p.App == null)
            {
                if (p.Bytes < minUnlinkedBytes) continue;
                p.Note = NoteFor(p);
                unlinked.Add(p);
                continue;
            }
            if (!usages.TryGetValue(p.App, out var u)) usages[p.App] = u = new AppUsage(p.App);
            u.Pieces.Add(p);
            u.Bytes += p.Bytes;
            u.ReclaimableBytes += p.ReclaimableBytes;
            if (p.Kind == PieceKind.Program) u.ProgramBytes += p.Bytes;
            else if (p.LastWrite > u.LastDataWrite) u.LastDataWrite = p.LastWrite;
            if (p.LastWrite > u.LastWrite) u.LastWrite = p.LastWrite;
        }
        foreach (var u in usages.Values) u.Pieces.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        var apps = usages.Values.OrderByDescending(u => u.Bytes).ThenBy(u => u.App.Name, StringComparer.OrdinalIgnoreCase).ToList();
        unlinked.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        string shown = root is DriveNode { IsSnapshotFrame: true } fr && fr.ChildCount == 1 ? FsPaths.Real(fr.Children[0].Name) : rootPath;

        // what the knowledge base knows about an unlinked folder: its own rule, or the rule of the sub folder holding most of it
        string NoteFor(AppPiece p)
        {
            if (KnowledgeBase.MatchFolderPath(p.Path, Path.GetFileName(p.Path)) is { } own) return own.Title;
            if (p.Node is not ContainerNode c) return "";
            KnownLocation? best = null; long bestSize = 0;
            foreach (var k in Kids(c))
                if (k is ContainerNode kc && !kc.IsSynthetic && KnowledgeBase.MatchFolderPath(p.Path + "\\" + kc.Name, kc.Name) is { } r && sizeOf(kc) > bestSize) { best = r; bestSize = sizeOf(kc); }
            return best != null && bestSize * 3 >= p.Bytes ? F($"Contains {best.Title}") : "";
        }
        return new FootprintResult
        {
            RootPath = shown,
            ScannedBytes = sizeOf(root),
            IsWholeDrive = shown.Length == 2 && shown[1] == ':',
            Apps = apps,
            Unlinked = unlinked,
            AllPieces = pieces,
            AppBytes = apps.Sum(a => a.Bytes),
            UnlinkedBytes = unlinked.Sum(p => p.Bytes),
            Elapsed = sw.Elapsed,
        };
    }

    /// <summary>"31.8%" (invariant, like the sizes next to it).</summary>
    public static string Pct(long part, long whole) => whole <= 0 ? "0%" : (100.0 * part / whole).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";

    /// <summary>"C:\" for a drive, the folder path otherwise.</summary>
    public static string DisplayPath(string realPath) => realPath.Length == 2 && realPath[1] == ':' ? realPath + "\\" : realPath;

    /// <summary>Plain text / tab separated report (window "Copy list" / "Save", command line "appsreport").</summary>
    public static string ToText(FootprintResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Branding.Name + ": " + T("programs on disk"));
        sb.AppendLine(T("View:") + " " + DisplayPath(r.RootPath) + "  (" + F($"{Format.Size(r.ScannedBytes)} scanned") + ")");
        sb.AppendLine(T("Programs:") + " " + P(r.Apps.Count, "{1} in {0} program ({2} of the view)", "{1} in {0} programs ({2} of the view)", Format.Size(r.AppBytes), Pct(r.AppBytes, r.ScannedBytes)));
        sb.AppendLine(T("Not linked:") + " " + P(r.Unlinked.Count, "{1} in {0} folder", "{1} in {0} folders", Format.Size(r.UnlinkedBytes)));
        if (!r.IsWholeDrive) sb.AppendLine(T("Note:") + " " + T("only folders inside this view are counted; scan the whole drive for complete totals."));
        sb.AppendLine();
        sb.AppendLine("Program\tPublisher\tType\tTotal\tProgram files\tData\tReclaimable\tLast data write\tFolders");
        foreach (var u in r.Apps)
        {
            sb.Append(u.App.Name).Append('\t').Append(u.App.Publisher).Append('\t').Append(u.App.TypeText).Append('\t')
              .Append(Format.Size(u.Bytes)).Append('\t').Append(Format.Size(u.ProgramBytes)).Append('\t').Append(Format.Size(u.DataBytes)).Append('\t')
              .Append(Format.Size(u.ReclaimableBytes)).Append('\t').Append(Format.Date(u.LastDataWrite)).Append('\t').Append(u.Pieces.Count).AppendLine();
            foreach (var p in u.Pieces)
                sb.Append("\t- ").Append(p.KindText).Append('\t').Append(Format.Size(p.Bytes)).Append('\t').AppendLine(p.Path);
        }
        sb.AppendLine();
        sb.AppendLine(T("Not linked to an installed program (portable tools, removed programs, Windows or developer components: check before deleting)"));
        sb.AppendLine("Size\tWhere\tLast write\tNote\tPath");
        foreach (var p in r.Unlinked)
            sb.Append(Format.Size(p.Bytes)).Append('\t').Append(p.KindText).Append('\t').Append(Format.Date(p.LastWrite)).Append('\t').Append(p.Note).Append('\t').AppendLine(p.Path);
        return sb.ToString();
    }
}
