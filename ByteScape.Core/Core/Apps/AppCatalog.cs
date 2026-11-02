using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ByteScape.Core.Knowledge;
using ByteScape.Core.Model;
using ByteScape.Core.Util;
using Microsoft.Win32;

namespace ByteScape.Core.Apps;

/// <summary>
/// The programs installed on this machine (uninstall registry entries and Store packages) and the rules that tell
/// which folders belong to which program: install folders, curated aliases (<see cref="AppAliases"/>), Store package
/// folders and name matching of the first levels below Program Files, ProgramData and AppData
/// ("AppData\Roaming\Spotify", "AppData\Local\Google\Chrome", "ProgramData\Epic\EpicGamesLauncher").
/// Immutable after construction and safe to use from several threads.
/// </summary>
public sealed class AppCatalog
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
    /// <summary>Heuristics and aliases never look deeper than this many path segments ("C:" counts as one).</summary>
    private const int HeuristicDepth = 8;

    private readonly List<InstalledApp> _apps;
    private readonly Dictionary<string, InstalledApp> _installDirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<InstalledApp>> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<InstalledApp>> _byPublisher = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InstalledApp> _byFamily = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, InstalledApp> _otherFamilies = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Wildcard Pattern, int Length, InstalledApp App)> _aliases = new();
    private readonly Dictionary<string, List<int>> _aliasByLeaf = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<int> _aliasWild = new();

    public IReadOnlyList<InstalledApp> Apps => _apps;

    /// <summary>Deepest path (in segments) a claim can start at; below it only the owner's content is walked.</summary>
    public int MaxMatchDepth { get; }

    public static readonly AppCatalog Empty = new(new List<InstalledApp>());

    private AppCatalog(List<InstalledApp> apps)
    {
        _apps = apps;
        int maxDepth = 0;
        // visible programs claim their install folders first, components only folders nobody else claims (Office's MUI parts)
        foreach (var a in apps.OrderBy(a => a.IsComponent))
            foreach (var d in a.InstallDirs)
                if (_installDirs.TryAdd(d, a)) maxDepth = Math.Max(maxDepth, d.Split('\\').Length);
        foreach (var a in apps)
        {
            if (a.PackageFamily != null) _byFamily.TryAdd(a.PackageFamily, a);
            foreach (var f in a.ExtraFamilies) _byFamily.TryAdd(f, a);
            foreach (var k in a.Keys) Add(_byKey, k, a);
            if (!a.IsComponent) foreach (var k in a.PublisherKeys) Add(_byPublisher, k, a);
        }
        // several programs with one key: desktop before Store, then the one that has an install folder
        foreach (var list in _byKey.Values) list.Sort((x, y) => x.Source != y.Source ? x.Source.CompareTo(y.Source) : y.InstallDirs.Count.CompareTo(x.InstallDirs.Count));
        foreach (var alias in AppAliases.All)
        {
            var app = apps.FirstOrDefault(x => !x.IsComponent && alias.Name.IsMatch(x.Name)) ?? apps.FirstOrDefault(x => alias.Name.IsMatch(x.Name));
            if (app == null) continue;
            foreach (var p in alias.Patterns)
            {
                int i = _aliases.Count;
                _aliases.Add((new Wildcard(KnowledgeBase.Expand(p), pathMode: true), p.Length, app));
                string leaf = p.Substring(p.LastIndexOf('\\') + 1);
                if (leaf.IndexOfAny(new[] { '*', '?', '%' }) >= 0) _aliasWild.Add(i);
                else Add(_aliasByLeaf, leaf, i);
            }
        }
        MaxMatchDepth = Math.Max(HeuristicDepth, maxDepth);
    }

    private static void Add<TK, TV>(Dictionary<TK, List<TV>> d, TK key, TV value) where TK : notnull
    {
        if (!d.TryGetValue(key, out var l)) d[key] = l = new List<TV>();
        if (!l.Contains(value)) l.Add(value);
    }

    // ------------------------------------------------------------------ building

    /// <summary>Builds the catalog from raw entries (the registry reader and the tests use this).</summary>
    public static AppCatalog FromEntries(IEnumerable<UninstallEntry> entries, IEnumerable<StorePackage> packages)
    {
        var apps = new List<InstalledApp>();
        var groups = entries
            .Where(e => !e.IsUpdate && !string.IsNullOrWhiteSpace(e.DisplayName))
            .GroupBy(e => Key(CleanName(e.DisplayName)) is { Length: > 0 } k ? k : e.DisplayName.Trim().ToLowerInvariant(), StringComparer.Ordinal);
        foreach (var g in groups)
        {
            var list = g.OrderBy(e => e.SystemComponent).ToList();
            var first = list[0];
            var names = list.Select(e => e.DisplayName.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var app = new InstalledApp
            {
                Name = names.Count == 1 ? names[0] : CleanName(first.DisplayName),
                Publisher = list.Select(e => e.Publisher.Trim()).FirstOrDefault(p => p.Length > 0) ?? "",
                Version = first.Version.Trim(),
                Source = AppSource.Desktop,
                IsComponent = list.All(e => e.SystemComponent),
            };
            app.UninstallCommand = list.Select(e => e.UninstallString.Trim()).FirstOrDefault(u => u.Length > 0) ?? "";
            foreach (var e in list)
                if (DateTime.TryParseExact(e.InstallDate.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) && d.ToUniversalTime() > app.InstallDate)
                    app.InstallDate = d.ToUniversalTime();
            foreach (var e in list)
            {
                app.EstimatedBytes += Math.Max(0, e.EstimatedSizeKb) * 1024;
                var dir = InstallDirOf(e);
                if (dir != null && !app.InstallDirs.Contains(dir, StringComparer.OrdinalIgnoreCase)) app.InstallDirs.Add(dir);
            }
            if (!app.IsComponent) AddKeys(app, CleanName(first.DisplayName));
            apps.Add(app);
        }
        var desktopByName = new Dictionary<string, InstalledApp>(StringComparer.Ordinal);
        foreach (var a in apps) if (!a.IsComponent) desktopByName.TryAdd(Key(CleanName(a.Name)), a);
        foreach (var g in packages.GroupBy(p => p.Family, StringComparer.OrdinalIgnoreCase))
        {
            var family = g.Key;
            var name = g.Select(p => p.DisplayName.Trim()).FirstOrDefault(n => n.Length > 0 && !n.StartsWith("@{", StringComparison.Ordinal) && !n.StartsWith("ms-resource:", OIC))
                       ?? PrettyPackageName(g.Select(p => Path.GetFileName(p.RootFolder.TrimEnd('\\'))).FirstOrDefault(r => r.Length > 0 && FamilyOf(r) == null && IsFamilyName(r)) ?? family);
            // a desktop program with a package identity (Edge): one program, the package's data folder is its data
            if (desktopByName.TryGetValue(Key(CleanName(name)), out var desktop)) { desktop.ExtraFamilies.Add(family); continue; }
            bool windows = g.Any(p => IsUnder(p.RootFolder, "Windows")) || IsWindowsPackage(family);
            var app = new InstalledApp { Name = name, Source = AppSource.Store, PackageFamily = family, IsComponent = windows, Publisher = windows || IsMicrosoftPackage(family) ? "Microsoft" : "" };
            foreach (var p in g)
            {
                var root = CleanPath(p.RootFolder);
                if (root != null && !IsUnder(root, "Windows") && !app.InstallDirs.Contains(root, StringComparer.OrdinalIgnoreCase)) app.InstallDirs.Add(root);
            }
            // only third party packages are matched by name (Claude, WhatsApp...): Microsoft's own ("Photos", "Notepad") would steal other folders
            if (!windows && !IsMicrosoftPackage(family)) AddKeys(app, name);
            apps.Add(app);
        }
        return new AppCatalog(apps);
    }

    private static void AddKeys(InstalledApp app, string cleanName)
    {
        void K(string? s)
        {
            if (s == null) return;
            var k = Key(s);
            if (k.Length >= 3 && !StopKeys.Contains(k) && !k.All(char.IsDigit)) app.Keys.Add(k);
        }
        K(cleanName);
        K(WithoutPublisher(cleanName, app.Publisher));
        foreach (var d in app.InstallDirs) K(Path.GetFileName(d));
        foreach (var pk in PublisherKeysOf(app.Publisher)) app.PublisherKeys.Add(pk);
    }

    /// <summary>
    /// Display name without versions, architectures and bracketed remarks, so the x86 and x64 entries (and the per
    /// user and per machine ones) of a program merge and match folder names: "Python 3.14.3 (64-bit)" → "Python".
    /// </summary>
    public static string CleanName(string name)
    {
        var s = Regex.Replace(name, @"\([^)]*\)|\[[^\]]*\]", " ");
        s = Regex.Replace(s, @"(?i)(?<![\w.])(x64|x86|amd64|arm64|64-bit|32-bit|64 bit|32 bit|sürümü|version|versión|versione)(?![\w.])", " ");
        s = Regex.Replace(s, @"(?i)(?<![\w.])v?\d+(?:\.\d+)+[a-z0-9]*(?![\w.])", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '–', ',', ':');
        return s.Length > 0 ? s : name.Trim();
    }

    /// <summary>Lower case letters and digits only, accents and the Turkish dotted/dotless i folded: "Telegram Desktop" → "telegramdesktop", "obs-studio" → "obsstudio".</summary>
    public static string Key(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            char c = ch == 'ı' ? 'i' : ch;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static readonly HashSet<string> LegalWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "inc", "incorporated", "llc", "ltd", "limited", "corp", "corporation", "co", "company", "gmbh", "ag", "ab", "sa", "bv", "oy",
        "plc", "pty", "srl", "sro", "kft", "as", "spa", "fz", "fzllc", "kg", "se", "ltda", "sas", "sarl", "ug", "the",
    };

    private static List<string> PublisherWords(string publisher) =>
        Regex.Split(publisher, @"[\s,/\\]+").Select(w => w.Trim('.', '-')).Where(w => w.Length > 0 && !LegalWords.Contains(w.Replace(".", "").Replace("-", ""))).ToList();

    /// <summary>"Brother Industries, Ltd." → "brotherindustries", "brother": the names a vendor's folder may carry.</summary>
    internal static IEnumerable<string> PublisherKeysOf(string publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) yield break;
        var words = PublisherWords(publisher);
        if (words.Count == 0) yield break;
        var full = Key(string.Join(" ", words));
        if (full.Length >= 3 && !StopKeys.Contains(full)) yield return full;
        var first = Key(words[0]);
        if (first.Length >= 4 && first != full && !StopKeys.Contains(first)) yield return first;
    }

    /// <summary>"Google Chrome" by "Google LLC" → "Chrome" (the name of the folder inside the vendor's folder).</summary>
    internal static string? WithoutPublisher(string cleanName, string publisher)
    {
        var pub = PublisherWords(publisher);
        var words = cleanName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int i = 0;
        while (i < words.Length - 1 && i < pub.Count && Key(words[i]) == Key(pub[i])) i++;
        return i == 0 ? null : string.Join(" ", words.Skip(i));
    }

    /// <summary>Normalised names that are never a program's folder name on their own.</summary>
    internal static readonly HashSet<string> StopKeys = new(StringComparer.Ordinal)
    {
        "app", "apps", "application", "data", "cache", "caches", "temp", "tmp", "log", "logs", "update", "updates", "updater", "common",
        "shared", "tools", "tool", "service", "services", "client", "launcher", "setup", "installer", "install", "uninstall", "user",
        "users", "config", "settings", "backup", "backups", "files", "file", "program", "programs", "software", "games", "game",
        "microsoft", "windows", "google", "system", "driver", "drivers", "runtime", "runtimes", "sdk", "plugins", "plugin", "bin",
        "lib", "share", "local", "roaming", "locallow", "default", "profile", "profiles", "downloads", "documents", "desktop",
        "packages", "package", "crashpad", "crashdumps", "gpucache", "help", "support", "helper", "host", "agent", "core",
        "framework", "net", "dotnet", "redistributable", "components", "component", "assets", "resources", "media", "web",
        "browser", "player", "current", "x64", "x86", "win64", "win32",
    };

    /// <summary>Vendors whose folder holds many products: never given to their only installed program as a whole.</summary>
    internal static readonly HashSet<string> BigVendors = new(StringComparer.Ordinal)
    {
        "microsoft", "google", "apple", "adobe", "adobesystems", "amazon", "intel", "nvidia", "amd", "advancedmicrodevices", "realtek",
        "oracle", "mozilla", "jetbrains", "epic", "epicgames", "valve", "electronicarts", "ubisoft", "autodesk", "corel", "meta", "facebook",
        "tencent", "bytedance", "samsung", "sony", "hewlettpackard", "dell", "lenovo", "asus", "asustek", "asustekcomputer", "acer",
        "qualcomm", "broadcom", "vmware", "cisco", "ibm", "sap", "huawei", "xiaomi", "logmein", "citrix", "canonical",
    };

    /// <summary>First level folders of the data roots that are Windows' or shared, never a single program's.</summary>
    private static readonly HashSet<string> NotAppFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Packages", "Package Cache", "Temp", "Tmp", "Programs", "Application Data", "History", "Temporary Internet Files",
        "VirtualStore", "Publishers", "PlaceholderTileLogoFolder", "ConnectedDevicesPlatform", "Comms", "CrashDumps", "D3DSCache",
        "Diagnostics", "ElevatedDiagnostics", "IsolatedStorage", "PeerDistRepub", "Microsoft Help", "SquirrelTemp", "cache", "Desktop",
        "Documents", "Start Menu", "Templates", "Favorites", "USOShared", "USOPrivate", "SoftwareDistribution", "ssh",
        "WindowsHolographicDevices", "Common Files", "Internet Explorer", "WindowsApps", "ModifiableWindowsApps", "Uninstall Information",
        "InstallShield Installation Information", "Windows Defender", "Windows Defender Advanced Threat Protection", "Windows Mail",
        "Windows Media Player", "Windows Multimedia Platform", "Windows NT", "Windows Photo Viewer", "Windows Portable Devices",
        "Windows Security", "Windows Sidebar", "WindowsPowerShell", "Microsoft.NET", "MSBuild", "Reference Assemblies", "Windows Kits",
        "Microsoft SDKs", "dotnet", "DIFX", "RUXIM", "Microsoft DevDiv", "Windows App Certification Kit", "Application Verifier",
        "Belgeler", "Local", "Roaming", "LocalLow", "Low", "Default", "Public", "All Users", "Default User",
    };

    internal static bool IsNotAppFolder(string name) => NotAppFolders.Contains(name) || name.StartsWith("regid.", OIC);

    // ------------------------------------------------------------------ install folders of uninstall entries

    /// <summary>
    /// Install folder of an entry: InstallLocation, else the folder of its icon / uninstaller when that folder is named
    /// like the program (a web app shortcut's uninstaller is chrome.exe: Chrome's folder is not the web app's).
    /// </summary>
    internal static string? InstallDirOf(UninstallEntry e)
    {
        var loc = CleanPath(e.InstallLocation);
        if (loc != null) return IsUnder(loc, "Windows") || IsDataRoot(loc) ? null : WalkUpGeneric(loc, derived: false);
        foreach (var raw in new[] { e.DisplayIcon, e.UninstallString })
        {
            var exe = ExePath(raw);
            if (exe == null) continue;
            string? dir;
            try { dir = Path.GetDirectoryName(exe); } catch { continue; }
            dir = CleanPath(dir);
            if (dir == null || RejectDerived(dir)) continue;
            dir = WalkUpGeneric(dir, derived: true);
            if (!RejectDerived(dir) && NamedLike(Path.GetFileName(dir), e.DisplayName)) return dir;
        }
        return null;
    }

    /// <summary>"RetroArch-Win64" is named like "RetroArch", "ALchemy" like "Creative ALchemy Universal"; "Chrome" is not "Telegram Web".</summary>
    private static bool NamedLike(string folder, string displayName)
    {
        var fk = Key(folder);
        if (fk.Length < 3) return false;
        var clean = CleanName(displayName);
        var nk = Key(clean);
        if (nk.Contains(fk) || fk.Contains(nk)) return true;
        foreach (var w in clean.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var wk = Key(w);
            if (wk.Length >= 4 && !StopKeys.Contains(wk) && fk.Contains(wk)) return true;
        }
        return false;
    }

    /// <summary>Quotes and trailing backslashes removed; null unless it is a rooted folder below a drive root.</summary>
    internal static string? CleanPath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        var s = p.Trim().Trim('"').Trim().TrimEnd('\\');
        if (s.Length < 4 || !char.IsLetter(s[0]) || s[1] != ':' || s[2] != '\\') return null;
        if (s.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        return s;
    }

    /// <summary>The executable / icon file of a DisplayIcon or UninstallString value ("C:\x\a.exe",0 / "C:\x\unins000.exe" /S); null for msiexec and rundll32.</summary>
    internal static string? ExePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.StartsWith('"')) { int q = s.IndexOf('"', 1); s = q > 0 ? s.Substring(1, q - 1) : s.Trim('"'); }
        else
        {
            var m = Regex.Match(s, @"^.*?\.(exe|ico|dll)\b", RegexOptions.IgnoreCase);
            if (m.Success) s = m.Value;
            int comma = s.LastIndexOf(',');
            if (comma > 2) s = s.Substring(0, comma);
        }
        var file = Path.GetFileName(s);
        if (file.StartsWith("msiexec", OIC) || file.StartsWith("rundll32", OIC)) return null;
        return CleanPath(s) is { } c && c.Contains('\\') ? c : null;
    }

    private static bool RejectDerived(string dir)
    {
        if (IsUnder(dir, "Windows") || IsDataRoot(dir)) return true;
        foreach (var bad in new[] { @"\Package Cache\", @"\Installer\", @"\InstallShield Installation Information\", @"\Common Files\", @"\Temp\", @"\Downloads\" })
            if ((dir + "\\").Contains(bad, OIC)) return true;
        var segs = dir.Split('\\');
        if (TryDataRoot(segs, out int depth, out var kind))
            return kind == PieceKind.Profile ? true : segs.Length - depth > 3;   // deep inside another program's data (a web app's icon inside Chrome's profile)
        return segs.Length < 2;
    }

    private static readonly Regex GenericLeaf = new(@"^(application|app|bin|bin64|x64|x86|win64|current|v?\d+(\.\d+)*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GenericDerivedLeaf = new(@"^(uninstall|uninst|_uninst|uninstaller|installer|setup|install)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"...\Google\Chrome\Application" → "...\Google\Chrome": generic or version named leafs are climbed, never above a program's own folder.</summary>
    private static string WalkUpGeneric(string dir, bool derived)
    {
        var d = dir;
        for (int i = 0; i < 4; i++)
        {
            var leaf = Path.GetFileName(d);
            if (!(GenericLeaf.IsMatch(leaf) || (derived && GenericDerivedLeaf.IsMatch(leaf)))) break;
            var parent = Path.GetDirectoryName(d);
            if (parent == null || parent.Length <= 3 || IsDataRoot(parent)) break;
            var pk = Key(Path.GetFileName(parent));
            if (IsNotAppFolder(Path.GetFileName(parent)) || BigVendors.Contains(pk) || StopKeys.Contains(pk)) break;
            d = parent;
        }
        return d;
    }

    private static bool IsUnder(string path, string topFolder)
    {
        var s = CleanPath(path);
        if (s == null) return false;
        var segs = s.Split('\\');
        return segs.Length >= 2 && segs[1].Equals(topFolder, OIC);
    }

    private static bool IsDataRoot(string path)
    {
        var segs = path.TrimEnd('\\').Split('\\');
        if (segs.Length <= 1) return true;
        if (TryDataRoot(segs, out int depth, out _)) return segs.Length <= depth;
        return segs[1].Equals("Users", OIC) && (segs.Length <= 3 || (segs.Length == 4 && segs[3].Equals("AppData", OIC)));
    }

    // ------------------------------------------------------------------ Store packages

    private static readonly Regex FamilyRx = new(@"^[A-Za-z0-9.\-]+_[a-z0-9]{13}$", RegexOptions.CultureInvariant);

    internal static bool IsFamilyName(string s) => FamilyRx.IsMatch(s);

    /// <summary>"Name_1.2.3.0_x64__publisherid" → "Name_publisherid"; null when it is not a package full name.</summary>
    public static string? FamilyOf(string fullName)
    {
        var parts = fullName.Split('_');
        if (parts.Length < 5 || parts[0].Length == 0) return null;
        var fam = parts[0] + "_" + parts[^1];
        return IsFamilyName(fam) ? fam : null;
    }

    private static bool IsWindowsPackage(string family) =>
        family.EndsWith("_cw5n1h2txyewy", OIC)
        || Regex.IsMatch(family, @"^Microsoft\.(VCLibs|NET\.|UI\.Xaml|WindowsAppRuntime|Services\.Store|DirectXRuntime|Advertising|Windows\.)", RegexOptions.IgnoreCase);

    private static bool IsMicrosoftPackage(string family) => family.EndsWith("_8wekyb3d8bbwe", OIC) || IsWindowsPackage(family);

    /// <summary>"5319275A.WhatsAppDesktop_cv1g1gvanyjgm" → "WhatsAppDesktop" when the package has no readable display name.</summary>
    internal static string PrettyPackageName(string family)
    {
        var n = family;
        int u = n.LastIndexOf('_');
        if (u > 0) n = n.Substring(0, u);
        n = Regex.Replace(n, @"^[0-9A-F]{8}\.", "", RegexOptions.IgnoreCase);
        n = Regex.Replace(n, @"^Microsoft\.(Windows\.)?", "", RegexOptions.IgnoreCase);
        return n.Length > 0 ? n : family;
    }

    private AppClaim? StoreClaim(string family, PieceKind kind)
    {
        if (_byFamily.TryGetValue(family, out var app)) return new AppClaim(app, kind);
        // another user's package or one missing from the repository: named after the family
        app = _otherFamilies.GetOrAdd(family, f => new InstalledApp
        {
            Name = PrettyPackageName(f), Source = AppSource.Store, PackageFamily = f, IsComponent = IsWindowsPackage(f), Publisher = IsMicrosoftPackage(f) ? "Microsoft" : "",
        });
        return new AppClaim(app, kind);
    }

    // ------------------------------------------------------------------ matching

    /// <summary>
    /// Data roots: Program Files (x86), ProgramData, AppData\Local, Local\Programs, Local\Packages, Roaming, LocalLow
    /// and the profile itself (for dot folders). <paramref name="depth"/> = number of segments of the root.
    /// </summary>
    internal static bool TryDataRoot(IReadOnlyList<string> segs, out int depth, out PieceKind kind)
    {
        depth = 0; kind = PieceKind.Other;
        if (segs.Count < 2 || segs[0].Length != 2 || segs[0][1] != ':') return false;
        var s1 = segs[1];
        if (s1.Equals("Program Files", OIC) || s1.Equals("Program Files (x86)", OIC)) { depth = 2; kind = PieceKind.Program; return true; }
        if (s1.Equals("ProgramData", OIC)) { depth = 2; kind = PieceKind.SharedData; return true; }
        if (!s1.Equals("Users", OIC) || segs.Count < 4) return false;
        if (!segs[3].Equals("AppData", OIC)) { depth = 3; kind = PieceKind.Profile; return true; }
        if (segs.Count < 5) return false;
        var s4 = segs[4];
        if (s4.Equals("Local", OIC))
        {
            if (segs.Count >= 6 && segs[5].Equals("Programs", OIC)) { depth = 6; kind = PieceKind.Program; return true; }
            if (segs.Count >= 6 && segs[5].Equals("Packages", OIC)) { depth = 6; kind = PieceKind.StoreData; return true; }
            depth = 5; kind = PieceKind.LocalData; return true;
        }
        if (s4.Equals("Roaming", OIC)) { depth = 5; kind = PieceKind.RoamingData; return true; }
        if (s4.Equals("LocalLow", OIC)) { depth = 5; kind = PieceKind.LocalLowData; return true; }
        return false;
    }

    /// <summary>Which program this exact folder belongs to (not inherited from a parent), or null.</summary>
    public AppClaim? Match(string path)
    {
        var p = FsPaths.Real(path);
        return Match(p, p.Split('\\'));
    }

    /// <summary><paramref name="segs"/> are the segments of <paramref name="path"/> ("C:", "Users", ...).</summary>
    internal AppClaim? Match(string path, IReadOnlyList<string> segs)
    {
        if (segs.Count > MaxMatchDepth) return null;
        if (_installDirs.TryGetValue(path, out var inst)) return new AppClaim(inst, PieceKind.Program);
        if (segs.Count > HeuristicDepth) return null;
        if (MatchAlias(path, segs[^1]) is { } alias) return new AppClaim(alias, KindOf(segs));
        if (!TryDataRoot(segs, out int depth, out var kind)) return null;
        int rel = segs.Count - depth;
        if (rel <= 0) return null;
        switch (kind)
        {
            case PieceKind.StoreData:
                return rel == 1 && IsFamilyName(segs[depth]) ? StoreClaim(segs[depth], kind) : null;
            case PieceKind.Profile:
                return rel == 1 && segs[depth].Length > 1 && segs[depth][0] == '.' && Pick(Key(segs[depth]), null) is { } dot ? new AppClaim(dot, kind) : null;
            case PieceKind.Program when depth == 2 && rel == 2 && segs[2].Equals("WindowsApps", OIC):
                return FamilyOf(segs[3]) is { } fam ? StoreClaim(fam, PieceKind.Program) : null;
        }
        if (rel == 1) return FirstLevel(segs[depth], kind);
        if (rel == 2) return SecondLevel(segs[depth], segs[depth + 1], kind);
        return null;
    }

    private static PieceKind KindOf(IReadOnlyList<string> segs) => TryDataRoot(segs, out int depth, out var kind) && segs.Count > depth ? kind : PieceKind.Other;

    private static readonly Regex ReverseDns = new(@"^(?:com|org|net|io|dev|app|me|co)\.([^.]+)\.(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HelperSuffix = new(@"[-_ .]?(updater|update|installer|setup)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private AppClaim? FirstLevel(string name, PieceKind kind)
    {
        if (IsNotAppFolder(name)) return null;
        var k = Key(name);
        var app = Pick(k, null);
        if (app == null && ReverseDns.Match(name) is { Success: true } m)           // com.bilingify.readest
            app = Pick(Key(m.Groups[2].Value), Key(m.Groups[1].Value));
        if (app == null && HelperSuffix.IsMatch(name))                               // cherrystudio-updater
            app = Pick(Key(HelperSuffix.Replace(name, "")), null);
        if (app == null && !BigVendors.Contains(k) && _byPublisher.TryGetValue(k, out var byVendor))
        {
            // the folder of a small vendor with a single installed program ("Brother", "Mathpix")
            InstalledApp? only = null; int n = 0;
            foreach (var a in byVendor) if (!a.IsComponent) { only = a; n++; }
            if (n == 1) app = only;
        }
        return app == null ? null : new AppClaim(app, kind);
    }

    private AppClaim? SecondLevel(string vendor, string name, PieceKind kind)
    {
        var vk = Key(vendor);
        var nk = Key(name);
        if (nk.Length < 3 || StopKeys.Contains(nk)) return null;
        // the vendor's folder: its program named like the sub folder ("Google\Chrome", "ustwo games\Alba", "Microsoft\Edge")
        if (_byPublisher.TryGetValue(vk, out var byVendor))
            foreach (var a in byVendor)
                if (a.Keys.Contains(nk) || a.Keys.Contains(vk + nk)) return new AppClaim(a, kind);
        return Pick(vk + nk, vk) is { } app ? new AppClaim(app, kind) : null;
    }

    private InstalledApp? Pick(string key, string? vendorKey)
    {
        if (key.Length < 3 || StopKeys.Contains(key) || !_byKey.TryGetValue(key, out var list)) return null;
        if (list.Count == 1 || vendorKey == null) return list[0];
        foreach (var a in list) if (a.PublisherKeys.Contains(vendorKey)) return a;
        return list[0];
    }

    private InstalledApp? MatchAlias(string path, string leaf)
    {
        if (_aliases.Count == 0) return null;
        InstalledApp? best = null; int bestLen = -1;
        void Try(int i)
        {
            var (w, len, app) = _aliases[i];
            if (len > bestLen && w.IsMatch(path)) { best = app; bestLen = len; }
        }
        if (_aliasByLeaf.TryGetValue(leaf, out var list)) foreach (var i in list) Try(i);
        foreach (var i in _aliasWild) Try(i);
        return best;
    }

    /// <summary>
    /// A first level folder of a data root that no program claims is reported as "not linked to an installed program"
    /// (portable tools, removed programs, programs that do not register): true for such candidates.
    /// </summary>
    internal static bool IsUnlinkedCandidate(IReadOnlyList<string> segs, out PieceKind kind)
    {
        if (!TryDataRoot(segs, out int depth, out kind) || segs.Count - depth != 1) return false;
        var name = segs[depth];
        return kind switch
        {
            PieceKind.StoreData => false,
            PieceKind.Profile => name.Length > 1 && name[0] == '.',
            _ => !IsNotAppFolder(name),
        };
    }

    /// <summary>
    /// The program owning a folder or file: the deepest claimed folder on its path (a Steam game inside Steam belongs
    /// to the game). Returns the claim and the claimed folder, or null.
    /// </summary>
    public (AppClaim Claim, string Folder)? OwnerOf(string path)
    {
        var p = FsPaths.Real(path);
        if (p.Length < 3) return null;
        var segs = p.Split('\\');
        for (int n = Math.Min(segs.Length, MaxMatchDepth); n >= 2; n--)
        {
            var sub = new ArraySegment<string>(segs, 0, n);
            var folder = string.Join('\\', sub);
            if (Match(folder, sub) is { } c) return (c, folder);
        }
        return null;
    }

    // ------------------------------------------------------------------ this machine

    private static Task<AppCatalog>? _shared;
    private static bool _pinned;
    private static readonly object SharedSync = new();

    /// <summary>The installed programs of this machine, read once on a background thread (again with <paramref name="reload"/>).</summary>
    public static Task<AppCatalog> GetAsync(bool reload = false)
    {
        lock (SharedSync)
        {
            if (_shared == null || (reload && !_pinned) || _shared.IsFaulted) _shared = Task.Run(LoadFromSystem);
            return _shared;
        }
    }

    /// <summary>The machine catalog when it has been loaded already, else null (never blocks).</summary>
    public static AppCatalog? Loaded { get { lock (SharedSync) return _shared is { IsCompletedSuccessfully: true } t ? t.Result : null; } }

    /// <summary>Tests: use this catalog instead of the machine's (reloads keep it) until null is set.</summary>
    internal static void SetShared(AppCatalog? catalog) { lock (SharedSync) { _shared = catalog == null ? null : Task.FromResult(catalog); _pinned = catalog != null; } }

    public static AppCatalog LoadFromSystem()
    {
        var sw = Stopwatch.StartNew();
        var entries = ReadUninstallEntries();
        var packages = ReadStorePackages();
        var catalog = FromEntries(entries, packages);
        Log.Debug($"Installed programs: {catalog.Apps.Count} ({entries.Count} uninstall entries, {packages.Count} Store packages) read in {sw.ElapsedMilliseconds} ms");
        return catalog;
    }

    internal static List<UninstallEntry> ReadUninstallEntries()
    {
        var result = new List<UninstallEntry>();
        const string sub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var root = baseKey.OpenSubKey(sub);
                if (root == null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    try
                    {
                        using var k = root.OpenSubKey(name);
                        if (k == null) continue;
                        string S(string v) => k.GetValue(v) is { } o ? Convert.ToString(o, CultureInfo.InvariantCulture) ?? "" : "";
                        long size = k.GetValue("EstimatedSize") is int i ? (uint)i : 0;
                        result.Add(new UninstallEntry(name, S("DisplayName"))
                        {
                            Publisher = S("Publisher"), Version = S("DisplayVersion"), InstallLocation = S("InstallLocation"),
                            DisplayIcon = S("DisplayIcon"), UninstallString = S("UninstallString"), EstimatedSizeKb = size, InstallDate = S("InstallDate"),
                            SystemComponent = k.GetValue("SystemComponent") is int sc && sc != 0,
                            IsUpdate = S("ParentKeyName").Length > 0 || S("ReleaseType") is "Update" or "Hotfix" or "Security Update",
                        });
                    }
                    catch (Exception ex) { Log.Debug("Uninstall entry " + name + ": " + ex.Message); }
                }
            }
            catch (Exception ex) { Log.Debug("Uninstall registry " + hive + "/" + view + ": " + ex.Message); }
        }
        return result;
    }

    internal static List<StorePackage> ReadStorePackages()
    {
        var result = new List<StorePackage>();
        const string repo = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(repo);
            if (root == null) return result;
            foreach (var full in root.GetSubKeyNames())
            {
                try
                {
                    using var k = root.OpenSubKey(full);
                    if (k == null || FamilyOf(full) == null) continue;
                    var dn = k.GetValue("DisplayName") as string ?? "";
                    if (dn.StartsWith("@{", StringComparison.Ordinal) || dn.StartsWith("ms-resource:", OIC)) dn = Interop.Native.LoadIndirectString(dn) ?? "";
                    result.Add(new StorePackage(full, dn) { RootFolder = k.GetValue("PackageRootFolder") as string ?? "" });
                }
                catch (Exception ex) { Log.Debug("Store package " + full + ": " + ex.Message); }
            }
        }
        catch (Exception ex) { Log.Debug("Store package repository: " + ex.Message); }
        return result;
    }
}
