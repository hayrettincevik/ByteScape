using System.Diagnostics;
using System.Text.Json;
using ByteScape.Core.Model;
using ByteScape.Core.Util;
using Microsoft.Win32;

namespace ByteScape.Core.Games;

public enum GameStore { Steam, Epic, Gog }

/// <summary>What a folder of a game holds.</summary>
public enum GamePieceKind { Game, ShaderCache, Workshop, CompatData, Download, Saves, Leftover }

/// <summary>One installed game as its launcher records it.</summary>
public sealed class GameInfo
{
    public required GameStore Store { get; init; }
    /// <summary>Steam app id, Epic app name, GOG game id.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Install folder (no trailing backslash).</summary>
    public required string InstallDir { get; init; }
    /// <summary>Size the launcher reports (manifest), 0 when unknown.</summary>
    public long ReportedBytes { get; init; }
    public DateTime? LastPlayed { get; internal set; }
    public DateTime? LastUpdated { get; init; }
    public TimeSpan? Playtime { get; internal set; }
    /// <summary>Redistributables and runtimes that come with Steam games (not played, never "unused").</summary>
    public bool IsTool { get; init; }
    /// <summary>Other folders of the game: shader cache, workshop content, compatibility data, downloads, saves.</summary>
    public List<(GamePieceKind Kind, string Path)> ExtraDirs { get; } = new();

    public string StoreText => Store switch { GameStore.Steam => "Steam", GameStore.Epic => "Epic", _ => "GOG" };
    public override string ToString() => $"{StoreText} {Id} {Name}";
}

/// <summary>Where the launchers keep their data (the real machine, or a fake layout in the tests).</summary>
public sealed record GameSources(string? SteamPath, string? EpicDataDir, IReadOnlyList<(string Id, string Name, string Path)> GogGames, IReadOnlyList<string>? EpicLibraries = null)
{
    public static GameSources FromSystem()
    {
        string? steam = null;
        try
        {
            steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string
                    ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")?.GetValue("InstallPath") as string
                    ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam")?.GetValue("InstallPath") as string;
        }
        catch (Exception ex) { Log.Debug("Steam registry: " + ex.Message); }
        var gog = new List<(string, string, string)>();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games") ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\GOG.com\Games");
            if (k != null)
                foreach (var id in k.GetSubKeyNames())
                {
                    using var g = k.OpenSubKey(id);
                    if (g?.GetValue("path") is string path && g.GetValue("gameName") is string name) gog.Add((id, name, path));
                }
        }
        catch (Exception ex) { Log.Debug("GOG registry: " + ex.Message); }
        var epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic");
        // the launcher's default library: checked for game folders it no longer knows even when no game is installed
        var epicDefault = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games");
        return new GameSources(steam, epic, gog, Directory.Exists(Path.Combine(epic, "EpicGamesLauncher")) && Directory.Exists(epicDefault) ? new[] { epicDefault } : null);
    }
}

/// <summary>
/// The games installed through Steam, the Epic Games Launcher and GOG: names, folders, the size the launcher reports,
/// and for Steam when each game was last played and for how long. Also names the numbered Steam folders
/// (shadercache\1245620 is ELDEN RING) and says which game a path belongs to.
/// </summary>
public sealed class GameLibrary
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
    private readonly Dictionary<string, string> _steamNames = new(StringComparer.Ordinal);
    private readonly List<(string Path, GameInfo Game, GamePieceKind Kind)> _dirs = new();

    public IReadOnlyList<GameInfo> Games { get; }
    /// <summary>Steam library folders (each holds a steamapps folder).</summary>
    public IReadOnlyList<string> SteamLibraries { get; }
    public string? SteamPath { get; }
    /// <summary>Folders the Epic launcher installs into (for leftovers of removed games).</summary>
    public IReadOnlyList<string> EpicLibraries { get; }
    /// <summary>Steam users' play times of games that are no longer installed (app id → last played).</summary>
    public IReadOnlyDictionary<string, DateTime> SteamPlayedNotInstalled { get; }
    /// <summary>Steam libraries that are listed but not reachable (a disconnected drive): their games are unknown.</summary>
    public IReadOnlyList<string> OfflineLibraries { get; private init; } = Array.Empty<string>();

    private GameLibrary(List<GameInfo> games, List<string> steamLibs, string? steamPath, List<string> epicLibs, Dictionary<string, DateTime> played)
    {
        Games = games; SteamLibraries = steamLibs; SteamPath = steamPath; EpicLibraries = epicLibs; SteamPlayedNotInstalled = played;
        foreach (var g in games)
        {
            if (g.Store == GameStore.Steam) _steamNames[g.Id] = g.Name;
            _dirs.Add((g.InstallDir, g, GamePieceKind.Game));
            foreach (var (kind, path) in g.ExtraDirs) _dirs.Add((path, g, kind));
        }
        _dirs.Sort((a, b) => b.Path.Length.CompareTo(a.Path.Length));   // the most specific folder wins
    }

    public static readonly GameLibrary Empty = new(new(), new(), null, new(), new());

    public static GameLibrary Load() => Load(GameSources.FromSystem());

    public static GameLibrary Load(GameSources src)
    {
        var sw = Stopwatch.StartNew();
        var games = new List<GameInfo>();
        var steamLibs = new List<string>();
        var played = new Dictionary<string, (DateTime Last, int Minutes)>(StringComparer.Ordinal);
        string? steam = Norm(src.SteamPath);
        if (steam != null && Directory.Exists(steam))
        {
            steamLibs.Add(steam);
            var lf = VdfNode.TryLoad(Path.Combine(steam, "steamapps", "libraryfolders.vdf"));
            var folders = lf?.Block("libraryfolders") ?? lf?.Block("LibraryFolders");
            if (folders != null)
                foreach (var key in folders.BlockOrder)
                    if (Norm(folders.Block(key)?["path"]) is { } libPath && !steamLibs.Contains(libPath, StringComparer.OrdinalIgnoreCase)) steamLibs.Add(libPath);
            // play history of every Steam user on this machine
            try
            {
                foreach (var user in Directory.EnumerateDirectories(Path.Combine(steam, "userdata")))
                {
                    var apps = VdfNode.TryLoad(Path.Combine(user, "config", "localconfig.vdf"))?.Path("UserLocalConfigStore", "Software", "Valve", "Steam", "apps");
                    if (apps == null) continue;
                    foreach (var id in apps.BlockOrder)
                    {
                        var a = apps.Block(id)!;
                        long last = a.Long("LastPlayed");
                        int minutes = (int)a.Long("Playtime");
                        if (last <= 0 && minutes <= 0) continue;
                        var t = last > 0 ? DateTimeOffset.FromUnixTimeSeconds(last).UtcDateTime : default;
                        played[id] = played.TryGetValue(id, out var o) ? (t > o.Last ? t : o.Last, Math.Max(minutes, o.Minutes)) : (t, minutes);
                    }
                }
            }
            catch (Exception ex) { Log.Debug("Steam user data: " + ex.Message); }
            foreach (var steamLib in steamLibs)
            {
                var apps = Path.Combine(steamLib, "steamapps");
                IEnumerable<string> manifests;
                try { manifests = Directory.EnumerateFiles(apps, "appmanifest_*.acf").ToList(); } catch { continue; }
                foreach (var m in manifests)
                {
                    var st = VdfNode.TryLoad(m)?.Block("AppState");
                    if (st?["appid"] is not { Length: > 0 } id || st["installdir"] is not { Length: > 0 } dir) continue;
                    var name = st["name"] is { Length: > 0 } n ? n : F($"Steam app {id}");
                    long lastPlayed = st.Long("LastPlayed"), lastUpdated = st.Long("LastUpdated");
                    var g = new GameInfo
                    {
                        Store = GameStore.Steam, Id = id, Name = name, InstallDir = Path.Combine(apps, "common", dir),
                        ReportedBytes = st.Long("SizeOnDisk"),
                        LastUpdated = lastUpdated > 0 ? DateTimeOffset.FromUnixTimeSeconds(lastUpdated).UtcDateTime : null,
                        LastPlayed = lastPlayed > 0 ? DateTimeOffset.FromUnixTimeSeconds(lastPlayed).UtcDateTime : null,
                        IsTool = IsSteamTool(id, name),
                    };
                    if (played.TryGetValue(id, out var p))
                    {
                        if (p.Last != default && (g.LastPlayed == null || p.Last > g.LastPlayed)) g.LastPlayed = p.Last;
                        if (p.Minutes > 0) g.Playtime = TimeSpan.FromMinutes(p.Minutes);
                    }
                    g.ExtraDirs.Add((GamePieceKind.ShaderCache, Path.Combine(apps, "shadercache", id)));
                    g.ExtraDirs.Add((GamePieceKind.Workshop, Path.Combine(apps, "workshop", "content", id)));
                    g.ExtraDirs.Add((GamePieceKind.CompatData, Path.Combine(apps, "compatdata", id)));
                    g.ExtraDirs.Add((GamePieceKind.Download, Path.Combine(apps, "downloading", id)));
                    g.ExtraDirs.Add((GamePieceKind.Download, Path.Combine(apps, "temp", id)));
                    try { foreach (var user in Directory.EnumerateDirectories(Path.Combine(steam, "userdata"))) g.ExtraDirs.Add((GamePieceKind.Saves, Path.Combine(user, id))); } catch { }
                    if (!games.Any(x => x.Store == GameStore.Steam && x.Id == id)) games.Add(g);
                }
            }
        }
        var epicLibs = new List<string>();
        foreach (var e in src.EpicLibraries ?? Array.Empty<string>()) if (Norm(e) is { } el) epicLibs.Add(el);
        if (Norm(src.EpicDataDir) is { } epic)
        {
            try
            {
                var dir = Path.Combine(epic, "EpicGamesLauncher", "Data", "Manifests");
                if (Directory.Exists(dir))
                    foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(File.ReadAllText(item));
                            var r = doc.RootElement;
                            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                            if (S("AppName") is not { Length: > 0 } app || Norm(S("InstallLocation")) is not { } loc) continue;
                            if (S("MainGameAppName") is { Length: > 0 } main && !string.Equals(main, app, OIC)) continue;   // DLC: part of its game
                            if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) continue;
                            long size = r.TryGetProperty("InstallSize", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                            games.Add(new GameInfo
                            {
                                Store = GameStore.Epic, Id = app, Name = S("DisplayName") is { Length: > 0 } dn ? dn : Path.GetFileName(loc),
                                InstallDir = loc, ReportedBytes = size, LastUpdated = File.GetLastWriteTimeUtc(item),
                            });
                            var parent = Path.GetDirectoryName(loc);
                            if (parent != null && !epicLibs.Contains(parent, StringComparer.OrdinalIgnoreCase)) epicLibs.Add(parent);
                        }
                        catch (Exception ex) { Log.Debug("Epic manifest " + item + ": " + ex.Message); }
                    }
            }
            catch (Exception ex) { Log.Debug("Epic manifests: " + ex.Message); }
        }
        foreach (var (id, name, path) in src.GogGames)
            if (Norm(path) is { } p) games.Add(new GameInfo { Store = GameStore.Gog, Id = id, Name = name, InstallDir = p });
        var notInstalled = played.Where(kv => kv.Value.Last != default && !games.Any(g => g.Store == GameStore.Steam && g.Id == kv.Key))
                                 .ToDictionary(kv => kv.Key, kv => kv.Value.Last);
        var lib = new GameLibrary(games, steamLibs, steam, epicLibs, notInstalled) { OfflineLibraries = steamLibs.Where(l => !Directory.Exists(l)).ToList() };
        Log.Debug($"Game library: {games.Count} games ({games.Count(g => g.Store == GameStore.Steam)} Steam in {steamLibs.Count} libraries, {games.Count(g => g.Store == GameStore.Epic)} Epic, {games.Count(g => g.Store == GameStore.Gog)} GOG) read in {sw.ElapsedMilliseconds} ms");
        return lib;
    }

    /// <summary>Redistributables, runtimes and tools Steam installs alongside games.</summary>
    internal static bool IsSteamTool(string id, string name) =>
        id is "228980" or "1070560" or "1391110" or "1628350" or "1493710" or "1580130" or "250820"
        || name.Contains("Redistributable", OIC) || name.StartsWith("Steamworks", OIC) || name.StartsWith("Steam Linux Runtime", OIC)
        || name.StartsWith("Proton", OIC) || name.Contains("SteamVR", OIC) || name.EndsWith(" Dedicated Server", OIC);

    /// <summary>Launcher paths come as "c:/program files (x86)/steam": backslashes, and the real spelling of every folder that exists.</summary>
    private static string? Norm(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        var s = p.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
        if (s.Length >= 2 && s[1] == ':') s = char.ToUpperInvariant(s[0]) + s.Substring(1);
        return s.Length < 3 ? null : RealCase(s);
    }

    private static string RealCase(string path)
    {
        try
        {
            if (path.Length < 3 || path[1] != ':' || !Directory.Exists(path)) return path;
            var result = path.Substring(0, 2);
            foreach (var part in path.Substring(3).Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                var parent = result + "\\";
                var match = new DirectoryInfo(parent).EnumerateDirectories(part).FirstOrDefault();
                result = Path.Combine(parent, match?.Name ?? part);
            }
            return result;
        }
        catch { return path; }
    }

    /// <summary>The game a path belongs to and what the folder is (the most specific known folder on the path), or null.</summary>
    public (GameInfo Game, GamePieceKind Kind, string Folder)? OwnerOf(string realPath)
    {
        foreach (var (path, game, kind) in _dirs)
            if (realPath.Equals(path, OIC) || (realPath.Length > path.Length && realPath[path.Length] == '\\' && realPath.StartsWith(path, OIC)))
                return (game, kind, path);
        return null;
    }

    /// <summary>
    /// Map label of a numbered Steam folder: "1102130 · Florence" for steamapps\shadercache|compatdata|downloading|temp\&lt;id&gt;,
    /// steamapps\workshop\content\&lt;id&gt; and userdata\&lt;user&gt;\&lt;id&gt;; null for everything else (cheap for other nodes).
    /// </summary>
    public string? LabelFor(FsNode n)
    {
        if (n is not ContainerNode || n.Name.Length is < 1 or > 10 || !n.Name.All(char.IsAsciiDigit) || n.Parent is not { } p || p.Parent is not { } pp) return null;
        bool steamFolder =
            (pp.Name.Equals("steamapps", OIC) && (p.Name.Equals("shadercache", OIC) || p.Name.Equals("compatdata", OIC) || p.Name.Equals("downloading", OIC) || p.Name.Equals("temp", OIC)))
            || (p.Name.Equals("content", OIC) && pp.Name.Equals("workshop", OIC))
            || pp.Name.Equals("userdata", OIC);
        if (!steamFolder) return null;
        if (_steamNames.TryGetValue(n.Name, out var name)) return n.Name + " · " + name;
        // unknown ids only inside this machine's Steam folders (another program's "userdata\x\123" is not Steam's)
        bool userdata = pp.Name.Equals("userdata", OIC);
        var steamRoot = userdata || pp.Name.Equals("steamapps", OIC) ? pp.Parent : pp.Parent?.Parent;
        string rootPath;
        try { rootPath = steamRoot?.RealPath() ?? ""; } catch { return null; }
        bool ours = userdata ? SteamPath != null && rootPath.Equals(SteamPath, OIC) : SteamLibraries.Contains(rootPath, StringComparer.OrdinalIgnoreCase);
        if (!ours) return null;
        if (userdata) return n.Name switch { "7" => "7 · Steam client settings", "760" => "760 · Steam screenshots", _ => n.Name + " · " + NotInstalledText };
        return n.Name + " · " + NotInstalledText;
    }

    /// <summary>What an unknown app id is: not installed, unless a library drive is disconnected.</summary>
    public string NotInstalledText => T(OfflineLibraries.Count > 0 ? "not installed or on a disconnected drive" : "not installed");

    public string? SteamName(string appId) => _steamNames.TryGetValue(appId, out var n) ? n : null;

    // ------------------------------------------------------------------ this machine

    private static Task<GameLibrary>? _shared;
    private static bool _pinned;
    private static readonly object SharedSync = new();

    public static Task<GameLibrary> GetAsync(bool reload = false)
    {
        lock (SharedSync)
        {
            if (_shared == null || (reload && !_pinned) || _shared.IsFaulted) _shared = Task.Run(() => Load());
            return _shared;
        }
    }

    public static GameLibrary? Loaded { get { lock (SharedSync) return _shared is { IsCompletedSuccessfully: true } t ? t.Result : null; } }

    /// <summary>Tests: use this library instead of the machine's until null is set.</summary>
    internal static void SetShared(GameLibrary? lib) { lock (SharedSync) { _shared = lib == null ? null : Task.FromResult(lib); _pinned = lib != null; } }
}
