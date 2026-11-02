using System.Diagnostics;
using System.Text;
using ByteScape.Core.Model;
using ByteScape.Core.Util;

namespace ByteScape.Core.Games;

/// <summary>A folder of a game (or left behind by one) as found in the scanned tree.</summary>
public sealed class GamePiece
{
    public required string Path { get; init; }
    public required GamePieceKind Kind { get; init; }
    /// <summary>Null for leftovers of games that are no longer installed.</summary>
    public GameInfo? Game { get; init; }
    public FsNode? Node { get; init; }
    public long Bytes { get; init; }
    /// <summary>What it is when there is no installed game to say it ("Steam app 206440, not installed").</summary>
    public string Note { get; init; } = "";

    public string KindText => Kind switch
    {
        GamePieceKind.Game => T("Game files"),
        GamePieceKind.ShaderCache => T("Shader cache"),
        GamePieceKind.Workshop => T("Workshop content"),
        GamePieceKind.CompatData => T("Compatibility data"),
        GamePieceKind.Download => T("Download / update in progress"),
        GamePieceKind.Saves => T("Saves and settings"),
        _ => T("Leftover"),
    };
}

/// <summary>One game with everything it occupies in the view.</summary>
public sealed class GameUsage
{
    public required GameInfo Game { get; init; }
    public List<GamePiece> Pieces { get; } = new();
    /// <summary>Game files found in the scanned view.</summary>
    public long GameBytes { get; internal set; }
    /// <summary>Shader cache, workshop content, saves... found in the view.</summary>
    public long ExtraBytes { get; internal set; }
    public long Bytes => GameBytes + ExtraBytes;
    /// <summary>The install folder is inside the view (otherwise only the launcher's reported size is known).</summary>
    public bool InView { get; internal set; }
    /// <summary>What the list shows: the scanned size, or the launcher's figure for games outside the view.</summary>
    public long ShownBytes => InView ? Bytes : Game.ReportedBytes;
}

public sealed class GameAuditResult
{
    public required string RootPath { get; init; }
    public required IReadOnlyList<GameUsage> Games { get; init; }
    public required IReadOnlyList<GamePiece> Leftovers { get; init; }
    public long LeftoverBytes => Leftovers.Sum(l => l.Bytes);
    public TimeSpan Elapsed { get; init; }

    /// <summary>Games (tools excluded) not played for longer than <paramref name="span"/> - or never, when the launcher knows play times.</summary>
    public IEnumerable<GameUsage> NotPlayedFor(TimeSpan span, DateTime nowUtc) =>
        Games.Where(g => !g.Game.IsTool && g.Game.Store == GameStore.Steam && (g.Game.LastPlayed == null || nowUtc - g.Game.LastPlayed.Value > span));

    public Dictionary<FsNode, bool> SpotlightFor(IEnumerable<GamePiece> pieces)
    {
        var d = new Dictionary<FsNode, bool>(ReferenceEqualityComparer.Instance);
        foreach (var p in pieces) if (p.Node != null) d[p.Node] = true;
        return d;
    }
}

/// <summary>
/// "Which games take my space, and which ones do I still play?": finds every installed game's folders in the scanned
/// tree (game files, shader cache, workshop content, compatibility data, saves) and what uninstalled games left behind
/// (folders in steamapps\common without a manifest, caches and saves of app ids that are no longer installed, Epic
/// game folders the launcher does not know). Cheap: only the known folders are looked up.
/// </summary>
public static class GameAudit
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
    /// <summary>Folders the launchers keep next to the games, never leftovers.</summary>
    private static readonly HashSet<string> NotGameFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Steam Controller Configs", "Steamworks Shared", "Launcher", "DirectXRedist", "Epic Online Services", "UE_4.27", "UE_5.0",
    };

    public static GameAuditResult Compute(ContainerNode viewRoot, GameLibrary lib, Func<FsNode, long> sizeOf, CancellationToken token, object? sync = null)
    {
        var sw = Stopwatch.StartNew();
        // a loaded snapshot: its content starts inside the "[SNAPSHOT: file]" frame
        var root = viewRoot is DriveNode { IsSnapshotFrame: true } f && f.ChildCount == 1 && f.Children[0] is ContainerNode inner ? inner : viewRoot;
        string rootPath;
        try { rootPath = root.RealPath(); } catch { rootPath = ""; }

        FsNode? Find(string path)
        {
            if (rootPath.Length == 0) return null;
            if (path.Equals(rootPath, OIC)) return root;
            if (!(path.Length > rootPath.Length && path.StartsWith(rootPath, OIC) && (path[rootPath.Length] == '\\' || rootPath.EndsWith(':'))))
                return null;
            FsNode cur = root;
            foreach (var part in path.Substring(rootPath.Length).Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                if (cur is not ContainerNode c) return null;
                FsNode? next;
                if (sync == null) next = c.FindChild(part); else lock (sync) next = c.FindChild(part);
                if (next == null) return null;
                cur = next;
            }
            return cur;
        }
        List<FsNode> Kids(ContainerNode c)
        {
            if (sync == null) return c.ChildrenSnapshot();
            lock (sync) return c.ChildrenSnapshot();
        }

        var games = new List<GameUsage>();
        foreach (var g in lib.Games)
        {
            token.ThrowIfCancellationRequested();
            var u = new GameUsage { Game = g };
            var node = Find(g.InstallDir);
            if (node != null)
            {
                u.InView = true;
                long b = sizeOf(node);
                u.GameBytes = b;
                u.Pieces.Add(new GamePiece { Path = g.InstallDir, Kind = GamePieceKind.Game, Game = g, Node = node, Bytes = b });
            }
            foreach (var (kind, path) in g.ExtraDirs)
            {
                var n = Find(path);
                if (n == null) continue;
                long b = sizeOf(n);
                if (b <= 0) continue;   // an empty cache or save folder: nothing to show
                u.ExtraBytes += b;
                u.Pieces.Add(new GamePiece { Path = path, Kind = kind, Game = g, Node = n, Bytes = b });
            }
            games.Add(u);
        }

        var leftovers = new List<GamePiece>();
        foreach (var libPath in lib.SteamLibraries)
        {
            var apps = System.IO.Path.Combine(libPath, "steamapps");
            var installed = lib.Games.Where(g => g.Store == GameStore.Steam && g.InstallDir.StartsWith(apps + "\\", OIC)).ToList();
            // game folders without a manifest: the game was uninstalled but files stayed (saves, mods, config)
            if (Find(System.IO.Path.Combine(apps, "common")) is ContainerNode common)
                foreach (var k in Kids(common))
                    if (k is ContainerNode kc && !NotGameFolders.Contains(kc.Name) && !installed.Any(g => System.IO.Path.GetFileName(g.InstallDir).Equals(kc.Name, OIC)))
                        leftovers.Add(new GamePiece { Path = System.IO.Path.Combine(apps, "common", kc.Name), Kind = GamePieceKind.Leftover, Node = kc, Bytes = sizeOf(kc), Note = T("No longer installed (no Steam manifest)") });
            // caches, workshop items and downloads of app ids that are not installed in this library
            foreach (var (sub, kind) in new[] { ("shadercache", GamePieceKind.ShaderCache), (@"workshop\content", GamePieceKind.Workshop), ("compatdata", GamePieceKind.CompatData), ("downloading", GamePieceKind.Download), ("temp", GamePieceKind.Download) })
                if (Find(System.IO.Path.Combine(apps, sub)) is ContainerNode dir)
                    foreach (var k in Kids(dir))
                        if (k is ContainerNode kc && kc.Name.All(char.IsAsciiDigit) && !installed.Any(g => g.Id == kc.Name))
                            leftovers.Add(new GamePiece { Path = System.IO.Path.Combine(apps, sub, kc.Name), Kind = kind, Node = kc, Bytes = sizeOf(kc), Note = AppText(lib, kc.Name) });
        }
        // saves and settings of Steam games that are not installed anymore (kept by Steam Cloud too, usually)
        if (lib.SteamPath != null && Find(System.IO.Path.Combine(lib.SteamPath, "userdata")) is ContainerNode users)
            foreach (var user in Kids(users).OfType<ContainerNode>())
                foreach (var k in Kids(user))
                    if (k is ContainerNode kc && kc.Name.All(char.IsAsciiDigit) && kc.Name != "7" && kc.Name != "760" && lib.SteamName(kc.Name) == null)
                        leftovers.Add(new GamePiece { Path = System.IO.Path.Combine(lib.SteamPath, "userdata", user.Name, kc.Name), Kind = GamePieceKind.Saves, Node = kc, Bytes = sizeOf(kc), Note = AppText(lib, kc.Name) + " — " + T("saves, keep them if you may reinstall") });
        // Epic game folders the launcher does not know anymore
        foreach (var epicLib in lib.EpicLibraries)
            if (Find(epicLib) is ContainerNode el)
                foreach (var k in Kids(el))
                    if (k is ContainerNode kc && !NotGameFolders.Contains(kc.Name) && !lib.Games.Any(g => g.Store == GameStore.Epic && g.InstallDir.Equals(System.IO.Path.Combine(epicLib, kc.Name), OIC)))
                        leftovers.Add(new GamePiece { Path = System.IO.Path.Combine(epicLib, kc.Name), Kind = GamePieceKind.Leftover, Node = kc, Bytes = sizeOf(kc), Note = T("Not in the Epic Games Launcher's list") });

        games.Sort((a, b) => b.ShownBytes.CompareTo(a.ShownBytes));
        leftovers.RemoveAll(l => l.Bytes <= 0 && l.Kind != GamePieceKind.Leftover);   // empty cache / save folders: nothing to see
        leftovers.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        return new GameAuditResult { RootPath = rootPath, Games = games, Leftovers = leftovers, Elapsed = sw.Elapsed };
    }

    private static string AppText(GameLibrary lib, string id) =>
        lib.SteamPlayedNotInstalled.TryGetValue(id, out var last)
            ? F($"Steam app {id}, {lib.NotInstalledText} (last played {last.ToLocalTime():d})")
            : F($"Steam app {id}, {lib.NotInstalledText}");

    /// <summary>Plain text / tab separated report (window "Copy list" / "Save", command line "gamesreport").</summary>
    public static string ToText(GameAuditResult r, DateTime nowUtc)
    {
        var sb = new StringBuilder();
        var year = r.NotPlayedFor(TimeSpan.FromDays(365), nowUtc).ToList();
        sb.AppendLine(Branding.Name + ": " + T("games"));
        sb.AppendLine(T("View:") + " " + (r.RootPath.Length == 2 ? r.RootPath + "\\" : r.RootPath));
        var tools = r.Games.Where(g => g.Game.IsTool).ToList();
        sb.AppendLine(T("Games:") + " " + P(r.Games.Count(g => !g.Game.IsTool), "{0} game, {1}", "{0} games, {1}", Format.Size(r.Games.Where(g => !g.Game.IsTool).Sum(g => g.ShownBytes)))
                      + (tools.Count > 0 ? " (+ " + P(tools.Count, "{0} redistributable / tool, {1}", "{0} redistributables / tools, {1}", Format.Size(tools.Sum(g => g.ShownBytes))) + ")" : ""));
        sb.AppendLine(T("Not played for a year:") + " " + P(year.Count, "{0} game, {1}", "{0} games, {1}", Format.Size(year.Sum(g => g.ShownBytes))));
        sb.AppendLine(T("Leftovers:") + " " + P(r.Leftovers.Count, "{0} folder, {1}", "{0} folders, {1}", Format.Size(r.LeftoverBytes)));
        sb.AppendLine();
        sb.AppendLine("Game\tStore\tSize\tGame files\tExtras\tLast played\tPlay time\tUpdated\tFolder");
        foreach (var u in r.Games)
        {
            var g = u.Game;
            sb.Append(g.Name).Append(g.IsTool ? " (" + T("tool") + ")" : "").Append('\t').Append(g.StoreText).Append('\t')
              .Append(Format.Size(u.ShownBytes)).Append(u.InView ? "" : " (" + T("reported") + ")").Append('\t').Append(Format.Size(u.GameBytes)).Append('\t').Append(Format.Size(u.ExtraBytes)).Append('\t')
              .Append(g.LastPlayed is { } lp ? Format.Date(lp) : g.Store == GameStore.Steam ? T("never") : "—").Append('\t')
              .Append(g.Playtime is { } pt ? PlaytimeText(pt) : "").Append('\t')
              .Append(g.LastUpdated is { } lu ? Format.Date(lu) : "").Append('\t').AppendLine(g.InstallDir);
            foreach (var p in u.Pieces.Where(p => p.Kind != GamePieceKind.Game))
                sb.Append("\t- ").Append(p.KindText).Append('\t').Append(Format.Size(p.Bytes)).Append('\t').AppendLine(p.Path);
        }
        sb.AppendLine();
        sb.AppendLine(T("Left behind by uninstalled games (check before deleting: saves and mods may be in there)"));
        sb.AppendLine("Size\tWhat\tNote\tFolder");
        foreach (var l in r.Leftovers)
            sb.Append(Format.Size(l.Bytes)).Append('\t').Append(l.KindText).Append('\t').Append(l.Note).Append('\t').AppendLine(l.Path);
        return sb.ToString();
    }

    public static string PlaytimeText(TimeSpan t) => t.TotalHours >= 1 ? Fmt("{0:N0} h", (int)Math.Round(t.TotalHours)) : Fmt("{0} min", (int)t.TotalMinutes);
}
