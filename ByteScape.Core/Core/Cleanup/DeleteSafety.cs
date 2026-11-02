using ByteScape.Core.Apps;
using ByteScape.Core.Knowledge;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using ByteScape.Core.Util;

namespace ByteScape.Core.Cleanup;

/// <summary>How safe deleting something is, from harmless to never.</summary>
public enum SafetyLevel
{
    /// <summary>A cache, temporary or rebuildable file: nothing is lost.</summary>
    Safe,
    /// <summary>Rebuilt too, but the program using it must be closed first.</summary>
    SafeWhenClosed,
    /// <summary>ByteScape does not know what it is: look first.</summary>
    Unknown,
    /// <summary>Something is lost: settings, program files, downloads kept only here, cloud copies...</summary>
    Caution,
    /// <summary>Your own files (documents, photos...): they cannot be recreated.</summary>
    Personal,
    /// <summary>Part of Windows or of an installed program, or a folder Windows needs: ByteScape does not delete it.</summary>
    Protected,
}

public enum WarningLevel { Info, Caution, Serious }

public sealed record SafetyWarning(WarningLevel Level, string Text);

/// <summary>"Is it safe to delete this?": the verdict, why, the proper way to clean it, and everything worth a word.</summary>
public sealed class Assessment
{
    public required FsNode Node { get; init; }
    /// <summary>Real path (no snapshot frame).</summary>
    public required string Path { get; init; }
    public SafetyLevel Level { get; internal set; } = SafetyLevel.Unknown;
    /// <summary>Short verdict: "Safe to delete", "Your own files", "Protected: part of Windows".</summary>
    public string Headline { get; internal set; } = "";
    /// <summary>What it is and what deleting it does.</summary>
    public string Why { get; internal set; } = "";
    /// <summary>The proper way to reclaim the space (a command, a Settings page, the program's uninstaller), empty when "just delete it".</summary>
    public string Cleanup { get; internal set; } = "";
    public List<SafetyWarning> Warnings { get; } = new();
    /// <summary>Protected by the always-on list (drive roots, Windows, profile roots...): the settings cannot unlock it.</summary>
    public bool AlwaysProtected { get; internal set; }
    /// <summary>A folder Windows or programs expect to exist (the Temp folder): what is inside is deleted, the folder stays.</summary>
    public bool ContentOnly { get; internal set; }
    public KnownLocation? Rule { get; internal set; }
    public FileKind Kind { get; internal set; }
    /// <summary>The installed program the element belongs to, when known.</summary>
    public InstalledApp? Program { get; internal set; }
    /// <summary>The element is the install folder of <see cref="Program"/>: uninstall instead.</summary>
    public bool IsProgramFolder { get; internal set; }
    /// <summary>Which part of <see cref="Program"/>'s footprint it lies in (install folder, app data...).</summary>
    public PieceKind? ProgramPiece { get; internal set; }
    /// <summary>In a Downloads folder: installers and unfinished downloads there are leftovers.</summary>
    internal bool InDownloads { get; set; }

    public bool Blocked => Level == SafetyLevel.Protected;
    /// <summary>Deleting it needs an explicit "I understand" (something is lost, or a serious warning).</summary>
    public bool NeedsConfirmation => Level is SafetyLevel.Caution or SafetyLevel.Personal
                                     || Warnings.Any(w => w.Level == WarningLevel.Serious)
                                     || Level == SafetyLevel.Unknown && Warnings.Any(w => w.Level == WarningLevel.Caution);

    public string LevelText => DeleteSafety.LevelText(Level);

    public override string ToString() => $"{LevelText}: {Path} — {Headline}";
}

/// <summary>The folders of this machine the protection rules are built on (tests build their own).</summary>
public sealed class SafetyEnvironment
{
    public required string WindowsDir { get; init; }
    public required string ProgramFiles { get; init; }
    public required string ProgramFilesX86 { get; init; }
    public required string ProgramData { get; init; }
    public required string UsersDir { get; init; }
    /// <summary>Where ByteScape runs from and keeps its settings: not deleted while it runs.</summary>
    public string[] OwnDirs { get; init; } = Array.Empty<string>();

    public string SystemDrive => WindowsDir.Length >= 2 ? WindowsDir.Substring(0, 2) : "C:";

    private static SafetyEnvironment? _current;
    public static SafetyEnvironment Current => _current ??= FromSystem();

    private static string Clean(string p) => p.TrimEnd('\\');

    private static SafetyEnvironment FromSystem()
    {
        string win = Clean(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (win.Length == 0) win = @"C:\Windows";
        string drive = win.Substring(0, 2);
        string pf = Clean(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)), pf86 = Clean(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        string pd = Clean(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        string profile = Clean(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        string users = profile.Length > 3 ? System.IO.Path.GetDirectoryName(profile) ?? drive + @"\Users" : drive + @"\Users";
        var own = new List<string> { Clean(AppContext.BaseDirectory) };
        try { own.Add(Clean(System.IO.Path.GetDirectoryName(Config.AppConfig.ConfigPath) ?? "")); } catch { }
        return new SafetyEnvironment
        {
            WindowsDir = win, ProgramFiles = pf.Length > 0 ? pf : drive + @"\Program Files", ProgramFilesX86 = pf86.Length > 0 ? pf86 : drive + @"\Program Files (x86)",
            ProgramData = pd.Length > 0 ? pd : drive + @"\ProgramData", UsersDir = Clean(users), OwnDirs = own.Where(o => o.Length > 3).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }
}

/// <summary>What the assessment may look at besides the element: the tree lock, installed programs, hard links, the settings.</summary>
public sealed class SafetyContext
{
    public object? Sync { get; init; }
    public AppCatalog? Catalog { get; init; }
    public HardLinkRegistry? HardLinks { get; init; }
    public SafetyEnvironment Environment { get; init; } = SafetyEnvironment.Current;
    /// <summary>Protected folders stay protected (Settings: "Protect system folders"); the always-on list does not depend on it.</summary>
    public bool ProtectSystem { get; init; } = true;
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;
    /// <summary>Most elements looked at inside a folder (its content decides some warnings).</summary>
    public int ContentLimit { get; init; } = 200_000;

    public static SafetyContext For(ScanTree tree) => new()
    {
        Sync = tree.Sync, Catalog = AppCatalog.Loaded, HardLinks = tree.CountsLinksOnce ? tree.HardLinks : null,
        ProtectSystem = Config.AppConfig.Current.ProtectSystemFolders,
    };
}

/// <summary>
/// Decides how safe deleting a file or folder is: an always-on protection list (drive roots, Windows, the roots of Program
/// Files / ProgramData / Users, profile folders, the Recycle Bin...), protected folders the settings can unlock (what the
/// knowledge base calls part of Windows, Windows' user folders themselves, install folders of installed programs,
/// protected operating system files), then the knowledge base (caches, temp), where it lies (program data, your own
/// folders, Downloads, a cloud folder) and what it is (file types, and for folders what is inside). Pure: no file is
/// opened (access rights are <see cref="AccessProbe"/>'s job), so it is quick enough for tooltips and lists.
/// </summary>
public static class DeleteSafety
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    public static string LevelText(SafetyLevel l) => l switch
    {
        SafetyLevel.Safe => T("Safe"),
        SafetyLevel.SafeWhenClosed => T("Safe when closed"),
        SafetyLevel.Unknown => T("Unknown"),
        SafetyLevel.Caution => T("Caution"),
        SafetyLevel.Personal => T("Your files"),
        _ => T("Protected"),
    };

    /// <summary>Folders in a user profile Windows expects to exist: their content is yours, the folders themselves stay.</summary>
    private static readonly string[] ProfileFolders =
    {
        "Desktop", "Documents", "Downloads", "Music", "Pictures", "Videos", "Favorites", "Contacts", "Links", "Saved Games", "Searches", "3D Objects",
        "OneDrive", "AppData", @"AppData\Local", @"AppData\Roaming", @"AppData\LocalLow", @"AppData\Local\Microsoft", @"AppData\Roaming\Microsoft",
        @"AppData\Local\Packages", @"AppData\Local\Programs",
    };

    /// <summary>Folders of a profile that hold the user's own files.</summary>
    private static readonly string[] PersonalFolders = { "Desktop", "Documents", "Music", "Pictures", "Videos", "OneDrive", "Saved Games", "Favorites", "Contacts" };

    /// <summary>Folders at the root of any drive that Windows owns.</summary>
    private static readonly string[] RootSystemFolders = { "System Volume Information", "$Recycle.Bin", "Recovery", "Boot", "EFI", "$WinREAgent", "$SysReset", "Config.Msi", "Documents and Settings", "WindowsApps" };

    /// <summary>Files at the root of any drive that Windows owns.</summary>
    private static readonly string[] RootSystemFiles = { "pagefile.sys", "hiberfil.sys", "swapfile.sys", "bootmgr", "BOOTNXT", "BOOTSECT.BAK" };

    internal static bool IsUnder(string path, string dir) => path.Length > dir.Length && path.StartsWith(dir, OIC) && path[dir.Length] == '\\';
    internal static bool IsSameOrUnder(string path, string dir) => path.Equals(dir, OIC) || IsUnder(path, dir);

    public static Assessment Assess(FsNode node, SafetyContext? context = null)
    {
        var ctx = context ?? new SafetyContext();
        string path;
        try { path = node.RealPath().TrimEnd('\\'); } catch { path = node.Name; }
        if (path.Length == 2 && path[1] == ':') path += "\\";
        bool isFolder = node is ContainerNode;
        var a = new Assessment { Node = node, Path = path, Kind = isFolder ? FileKind.Other : FileKinds.Of(node.Name) };
        var env = ctx.Environment;
        var km = node.IsSynthetic ? null : KnowledgeBase.Classify(node);
        a.Rule = km?.Location;

        // 1. never: whatever the settings say
        if (Always(path, isFolder, node, km, env) is { } always)
        {
            a.Level = SafetyLevel.Protected; a.AlwaysProtected = true;
            (a.Headline, a.Why, a.Cleanup) = always;
            return a;
        }

        // the program the element belongs to (install folder, its data)
        (AppClaim Claim, string Folder)? owner = null;
        if (ctx.Catalog != null) { try { owner = ctx.Catalog.OwnerOf(path); } catch { } }
        if (owner is { } ow)
        {
            a.Program = ow.Claim.App;
            a.ProgramPiece = ow.Claim.Kind;
            a.IsProgramFolder = ow.Claim.Kind == PieceKind.Program && ow.Folder.Equals(path, OIC) && !ow.Claim.App.IsComponent;
        }

        // 2. protected unless the settings say otherwise
        if (Protected(path, isFolder, node, km, a, env, ctx) is { } prot)
        {
            if (ctx.ProtectSystem)
            {
                a.Level = SafetyLevel.Protected;
                (a.Headline, a.Why, a.Cleanup) = prot;
                return a;
            }
            a.Level = SafetyLevel.Caution;
            (a.Headline, a.Why, a.Cleanup) = prot;
            a.Headline = T("Normally protected:") + " " + a.Headline;
            a.Warnings.Add(new SafetyWarning(WarningLevel.Serious, T("Protection of system folders is off in the settings.") + " " + prot.Why));
        }
        else Classify(path, isFolder, node, km, a, env);

        // 3. what it is and what is inside
        if (isFolder) Content((ContainerNode)node, path, a, ctx);
        else FileWarnings((FileNode)node, a, ctx);
        CommonWarnings(node, path, a, ctx);
        return a;
    }

    // ------------------------------------------------------------------ the always-on list

    private static (string Headline, string Why, string Cleanup)? Always(string path, bool isFolder, FsNode node, KnowledgeMatch? km, SafetyEnvironment env)
    {
        if (path.Length == 3 && path[1] == ':')
            return (T("Protected: a whole drive"), T("The root of a drive. Delete what is inside instead, a folder at a time."), "");
        string name = System.IO.Path.GetFileName(path);
        string parent = System.IO.Path.GetDirectoryName(path)?.TrimEnd('\\') ?? "";
        bool atRoot = parent.Length == 2 && parent[1] == ':';
        if (atRoot && isFolder && RootSystemFolders.Contains(name, StringComparer.OrdinalIgnoreCase)
            || RootSystemFolders.Any(f => f != "WindowsApps" && parent.Length > 2 && IsSameOrUnder(parent, parent.Substring(0, 2) + "\\" + f)))
        {
            if (name.Equals("$Recycle.Bin", OIC) || path.Contains(@"\$Recycle.Bin\", OIC))
                return (T("Protected: the Recycle Bin"), T("Deleted files waiting to be restored. Windows keeps an index of them: empty or restore them from the Recycle Bin itself."), T("Right click the Recycle Bin on the desktop > Empty Recycle Bin"));
            return (T("Protected: kept by Windows"), T("A folder Windows keeps at the root of the drive (restore points, boot and recovery files, the Recycle Bin)."), T("Disk Cleanup or Settings > System > Storage; restore points: System Protection"));
        }
        if (!isFolder && atRoot && RootSystemFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
            return (T("Protected: a Windows system file"), km?.Location.Description ?? T("A file Windows needs to start or to manage memory."), km?.Location.Cleanup ?? "");
        if (IsSameOrUnder(path, env.WindowsDir) && km?.Location is not { IsReclaimable: true, Id: not "windows-dir" })
            return (T("Protected: part of Windows"), T("The running Windows installation. Only its temporary, log and download areas may be cleaned, and Windows' own tools do it best."), T("Settings > System > Storage > Temporary files, or Disk Cleanup"));
        foreach (var root in new[] { env.ProgramFiles, env.ProgramFilesX86, env.ProgramData, env.UsersDir })
            if (path.Equals(root, OIC)) return (T("Protected: a Windows folder"), F($"A folder Windows needs ({root}). Delete what is inside, and uninstall programs from Settings > Apps."), "");
        if (path.Contains(@"\WindowsApps", OIC) && (path.EndsWith(@"\WindowsApps", OIC) || path.Contains(@"\WindowsApps\", OIC)))
            return (T("Protected: a Store app"), T("Store apps are installed and removed by Windows only."), T("Settings > Apps > Installed apps > Uninstall"));
        if (IsUnder(path, env.UsersDir) && parent.Equals(env.UsersDir, OIC))
            return (T("Protected: a user account's folder"), F($"The whole profile of a user account ({name}). Delete what is inside, or remove the account in Settings > Accounts."), T("Settings > Accounts > Other users"));
        foreach (var root in new[] { env.WindowsDir, env.ProgramFiles, env.ProgramFilesX86, env.ProgramData, env.UsersDir })
            if (isFolder && IsUnder(root, path)) return (F($"Protected: holds {root}"), F($"It contains {root}, which Windows needs."), "");
        foreach (var own in env.OwnDirs)
            if (IsSameOrUnder(own, path) || IsUnder(path, own) && System.IO.Path.GetFileName(path).StartsWith("ByteScape", OIC))
                return (F($"Protected: {Branding.Name} runs from here"), F($"{Branding.Name} itself or its settings ({own})."), "");
        return null;
    }

    // ------------------------------------------------------------------ protected unless the settings say otherwise

    private static (string Headline, string Why, string Cleanup)? Protected(string path, bool isFolder, FsNode node, KnowledgeMatch? km, Assessment a, SafetyEnvironment env, SafetyContext ctx)
    {
        // a Windows user folder itself (Documents, AppData...): its content is fine to look at, the folder stays
        if (ProfileRoot(path, env) is { } profile)
        {
            var rel = path.Length > profile.Length ? path.Substring(profile.Length + 1) : "";
            if (rel.Length > 0 && ProfileFolders.Contains(rel, StringComparer.OrdinalIgnoreCase))
                return (T("Protected: a Windows user folder"), F($"Windows and programs expect \"{rel}\" to exist. Delete what is inside instead."), "");
        }
        if (a.IsProgramFolder)
            return (T("Protected: an installed program"), F($"The install folder of {a.Program!.Name}. Deleting it by hand leaves a broken entry in Settings > Apps, shortcuts and settings behind; the program's uninstaller removes it cleanly."), F($"Settings > Apps > Installed apps > {a.Program.Name} > Uninstall"));
        if (km?.Location is { Safety: Safety.System } sys)
        {
            // everything below Program Files inherits "Installed programs": decided by the program it belongs to instead
            if (sys.Id == "program-files" && km.Inherited) return null;
            return (T("Protected:") + " " + sys.Title, sys.Description + " " + T("If deleted:") + " " + sys.Consequence, sys.Cleanup);
        }
        // Windows' data folders, unless the knowledge base knows the place (crash reports, caches, Visual Studio's packages)
        bool known = km?.Location is { Safety: not Safety.System };
        if (IsUnder(path, env.ProgramData + @"\Microsoft") && !known)
            return (T("Protected: Windows data"), T("Data Windows keeps for all users (Start menu, Defender, certificates, search)."), T("Settings > System > Storage"));
        if (profileFolder(path, env, out _, out var relPath) && (relPath.StartsWith(@"AppData\Local\Microsoft\", OIC) || relPath.StartsWith(@"AppData\Roaming\Microsoft\", OIC)) && !known)
            return (T("Protected: Windows user data"), T("Data Windows keeps for this account (credentials, Start menu, Explorer, Office settings)."), "");
        if (!isFolder && node is FileNode f && (f.Attributes & (FileAttributes.System | FileAttributes.Hidden)) == (FileAttributes.System | FileAttributes.Hidden))
            return (T("Protected: a protected operating system file"), T("Hidden and marked as a system file: Windows or a program relies on it (desktop.ini files, boot and license files)."), "");
        if (node is ContainerNode wc && IsWindowsInstall(path, wc, ctx.Sync))
            return (T("Protected: a Windows installation"), T("A Windows folder on another drive (an older or second installation). If it is no longer used, Settings > System > Storage > Temporary files removes a previous Windows installation cleanly."), T("Settings > System > Storage > Temporary files > Previous Windows installation"));
        return null;

        static bool profileFolder(string p, SafetyEnvironment e, out string root, out string rel)
        {
            root = ProfileRoot(p, e) ?? "";
            rel = root.Length > 0 && p.Length > root.Length ? p.Substring(root.Length + 1) : "";
            return root.Length > 0;
        }
    }

    /// <summary>"D:\Windows" with System32 or WinSxS inside: a Windows installation, not the running one (that one is always protected).</summary>
    private static bool IsWindowsInstall(string path, ContainerNode c, object? sync)
    {
        if (path.Length <= 3 || !System.IO.Path.GetFileName(path).Equals("Windows", OIC)) return false;
        if (sync == null) return c.FindChild("System32") != null || c.FindChild("WinSxS") != null;
        lock (sync) return c.FindChild("System32") != null || c.FindChild("WinSxS") != null;
    }

    /// <summary>The profile folder a path lies in (C:\Users\name), or null.</summary>
    internal static string? ProfileRoot(string path, SafetyEnvironment env)
    {
        // profiles on the system drive and, like the knowledge base, "?:\Users\name" on any drive
        int users = -1;
        if (IsUnder(path, env.UsersDir)) users = env.UsersDir.Length;
        else if (path.Length > 9 && path[1] == ':' && path.Substring(2).StartsWith(@"\Users\", OIC)) users = 8;
        if (users < 0) return null;
        int end = path.IndexOf('\\', users + 1);
        return end < 0 ? path : path.Substring(0, end);
    }

    // ------------------------------------------------------------------ everything else

    private static void Classify(string path, bool isFolder, FsNode node, KnowledgeMatch? km, Assessment a, SafetyEnvironment env)
    {
        var loc = km?.Location;
        if (loc != null && !(loc.Id == "program-files" && km!.Inherited))
        {
            a.Why = loc.Description + " " + T("If deleted:") + " " + loc.Consequence;
            a.Cleanup = loc.Cleanup;
            switch (loc.Safety)
            {
                case Safety.Regenerable:
                    a.Level = SafetyLevel.Safe; a.Headline = T("Safe to delete:") + " " + loc.Title; break;
                case Safety.SafeWhenClosed:
                    a.Level = SafetyLevel.SafeWhenClosed; a.Headline = T("Safe to delete once its program is closed:") + " " + loc.Title; break;
                default:
                    a.Level = SafetyLevel.Caution; a.Headline = T("Something is lost:") + " " + loc.Title; break;
            }
            // the Temp folder itself: programs expect it, only what is inside goes
            if (isFolder && !km!.Inherited && loc.Category == LocationCategory.WindowsTemp && loc.Id == "user-temp")
            {
                a.ContentOnly = true;
                a.Warnings.Add(new SafetyWarning(WarningLevel.Info, T("Windows and programs need this folder: what is inside is deleted, the folder stays. Files a program has open are skipped.")));
            }
            return;
        }
        if (Dev.DevProjects.ArtifactOf(node) is { } dev)
        {
            a.Level = SafetyLevel.Safe;
            a.Headline = F($"Safe to delete: rebuilt from the project {dev.Project}");
            a.Why = F($"Build output or downloaded packages of a {dev.Ecosystem} project; the project rebuilds them.");
            a.Cleanup = dev.Rebuild.Length > 0 ? T("Rebuild with:") + " " + dev.Rebuild : "";
            return;
        }
        if (a.Program is { } app && a.ProgramPiece is { } kind)
        {
            switch (kind)
            {
                case PieceKind.Program:
                    a.Level = SafetyLevel.Caution;
                    a.Headline = F($"Part of the installed program {app.Name}");
                    a.Why = F($"{app.Name} may stop working. To remove it, uninstall it from Settings > Apps.");
                    a.Cleanup = F($"Settings > Apps > Installed apps > {app.Name} > Uninstall");
                    return;
                case PieceKind.StoreData:
                case PieceKind.LocalData:
                case PieceKind.RoamingData:
                case PieceKind.LocalLowData:
                case PieceKind.SharedData:
                case PieceKind.Profile:
                    a.Level = SafetyLevel.Caution;
                    a.Headline = F($"Data of {app.Name}");
                    a.Why = (kind == PieceKind.SharedData ? F($"{app.Name} keeps its settings, sign-ins, downloads or saved work here, for every user of this PC. It may lose them or start as if new.") : F($"{app.Name} keeps its settings, sign-ins, downloads or saved work here. It may lose them or start as if new."));
                    a.Cleanup = F($"Clean it from {app.Name}'s own settings, or uninstall {app.Name} (Settings > Apps) if you no longer use it");
                    return;
            }
        }
        foreach (var pf in new[] { env.ProgramFiles, env.ProgramFilesX86 })
            if (IsUnder(path, pf))
            {
                a.Level = SafetyLevel.Caution;
                a.Headline = T("In Program Files, no installed program claims it");
                a.Why = T("Probably left behind by a program that was removed. If a program still uses it, it stops working; check Settings > Apps first.");
                return;
            }
        if (IsUnder(path, env.ProgramData))
        {
            a.Level = SafetyLevel.Caution;
            a.Headline = T("Program data shared by all users");
            a.Why = T("A program keeps data here for every user of this PC (licenses, databases, updates). If the program is still installed it may stop working.");
            return;
        }
        if (ProfileRoot(path, env) is { } profile && path.Length > profile.Length)
        {
            var rel = path.Substring(profile.Length + 1);
            string top = rel.Split('\\')[0];
            if (top.Equals("Downloads", OIC))
            {
                a.InDownloads = true;
                a.Level = SafetyLevel.Caution;
                a.Headline = T("A download");
                a.Why = T("Something you downloaded. It can usually be downloaded again; keep it if this is the only copy.");
                return;
            }
            if (PersonalFolders.Contains(top, StringComparer.OrdinalIgnoreCase))
            {
                a.Level = SafetyLevel.Personal;
                a.Headline = F($"Your own files ({top})");
                a.Why = T("Files you made or saved. They cannot be recreated unless you have another copy.");
                return;
            }
            if (top.Equals("AppData", OIC))
            {
                a.Level = SafetyLevel.Caution;
                a.Headline = T("Program data of this account");
                a.Why = F($"A program keeps settings, sign-ins or saved work here. {Branding.Name} does not know which one; the program may lose them.");
                return;
            }
            if (top.StartsWith('.'))
            {
                a.Level = SafetyLevel.Caution;
                a.Headline = F($"Settings of a tool ({top})");
                a.Why = T("Folders starting with a dot hold the settings and caches of developer tools and command line programs.");
                return;
            }
        }
        a.Level = SafetyLevel.Unknown;
        a.Headline = F($"{Branding.Name} does not know what this is");
        a.Why = T("Check what it is before deleting it.");
    }

    /// <summary>A file: what its type means, and whether it looks like a leftover.</summary>
    private static void FileWarnings(FileNode f, Assessment a, SafetyContext ctx)
    {
        var meaning = FileKinds.Meaning(a.Kind);
        switch (a.Kind)
        {
            case FileKind.Temporary or FileKind.PartialDownload or FileKind.Dump or FileKind.Log when a.Level == SafetyLevel.Unknown || a.InDownloads:
                a.Level = SafetyLevel.Safe;
                a.Headline = T("Safe to delete:") + " " + FileKinds.Name(a.Kind);
                a.Why = meaning;
                break;
            case FileKind.Installer when (a.Level == SafetyLevel.Unknown || a.InDownloads) && a.Program == null:
                a.Level = SafetyLevel.Safe;
                a.Headline = T("Safe to delete: an installation file");
                a.Why = meaning;
                break;
            case FileKind.Program when a.Level == SafetyLevel.Unknown:
                a.Level = SafetyLevel.Caution;
                a.Headline = T("A program file");
                a.Why = meaning;
                break;
            case var k when FileKinds.IsIrreplaceable(k) && a.Level == SafetyLevel.Unknown:
                a.Level = SafetyLevel.Personal;
                a.Headline = T("Your own file:") + " " + FileKinds.Name(k);
                a.Why = meaning;
                break;
            default:
                if (meaning.Length > 0) a.Warnings.Add(new SafetyWarning(WarningLevel.Info, meaning));
                break;
        }
        if (a.Kind is FileKind.Secret or FileKind.VirtualDisk or FileKind.Database or FileKind.Backup)
            a.Warnings.Add(new SafetyWarning(WarningLevel.Serious, meaning));
        if (f.CountedElsewhere || ctx.HardLinks?.GroupOf(f) is { Length: > 1 })
        {
            var g = ctx.HardLinks?.GroupOf(f);
            if (f.CountedElsewhere)
                a.Warnings.Add(new SafetyWarning(WarningLevel.Caution, F($"A hard link: the same file also exists as {(g != null ? g[0].FullPath : T("another name"))}. Deleting this name frees no space.")));
            else if (g != null)
                a.Warnings.Add(new SafetyWarning(WarningLevel.Caution, P(g.Length - 1, "A hard link: the same file also exists under {0} other name, e.g. {1}. Its space is freed only when every name is deleted.", "A hard link: the same file also exists under {0} other names, e.g. {1}. Its space is freed only when every name is deleted.", g.First(x => !ReferenceEquals(x, f)).FullPath)));
        }
        if (Cloud.CloudFiles.StateOf(f) == Cloud.CloudState.OnlineOnly)
            a.Warnings.Add(new SafetyWarning(WarningLevel.Serious, T("Online-only: it takes no space on this PC. Deleting it only removes it from the cloud (and your other devices).")));
        if ((f.Attributes & FileAttributes.ReadOnly) != 0)
            a.Warnings.Add(new SafetyWarning(WarningLevel.Info, T("Marked read-only: someone wanted it kept.")));
        var age = ctx.NowUtc - f.Modified;
        if (f.Modified != default && age < TimeSpan.FromDays(2) && age > TimeSpan.Zero)
            a.Warnings.Add(new SafetyWarning(WarningLevel.Caution, F($"Changed {Format.Age(f.Modified, ctx.NowUtc)} ago: a program may still be using it.")));
    }

    /// <summary>A folder: what is inside decides some warnings (documents, keys, program files, recent changes, protected folders).</summary>
    private static void Content(ContainerNode c, string path, Assessment a, SafetyContext ctx)
    {
        var s = ContentStats.Of(c, path, ctx);
        if (s.ProtectedInside is { } inside && ctx.ProtectSystem && a.Level != SafetyLevel.Protected)
        {
            a.Level = SafetyLevel.Protected;
            a.Headline = F($"Protected: it holds {inside.Title}");
            a.Why = F($"Inside it: {inside.Path}.") + " " + inside.Why;
            a.Cleanup = inside.Cleanup;
            return;
        }
        var irreplaceable = s.Kinds.Where(k => FileKinds.IsIrreplaceable(k.Key) && k.Value > 0).OrderByDescending(k => k.Value).ToList();
        if (irreplaceable.Count > 0 && a.Level is not SafetyLevel.Safe and not SafetyLevel.SafeWhenClosed)
        {
            a.Warnings.Add(new SafetyWarning(s.Kinds.Keys.Any(k => k is FileKind.Secret or FileKind.VirtualDisk or FileKind.Database or FileKind.Backup) ? WarningLevel.Serious : WarningLevel.Caution,
                F($"It contains {string.Join(", ", irreplaceable.Take(4).Select(k => FileKinds.Count(k.Key, k.Value)))}.")));
            if (a.Level == SafetyLevel.Unknown && irreplaceable.Any(k => k.Key is FileKind.Document or FileKind.Photo or FileKind.Video or FileKind.Audio))
            {
                a.Level = SafetyLevel.Personal;
                a.Headline = T("Holds your own files");
                a.Why = T("Documents, photos, videos or music: they cannot be recreated unless you have another copy.");
            }
        }
        if (s.Kinds.TryGetValue(FileKind.Program, out int programs) && programs > 0 && a.Level == SafetyLevel.Unknown)
        {
            a.Level = SafetyLevel.Caution;
            a.Headline = T("Holds a program");
            a.Why = F($"It contains {FileKinds.Count(FileKind.Program, programs)}. If this is an installed program, uninstall it instead; a program started from here stops working.");
        }
        if (s.Files > 0 && s.Files == s.Kinds.Where(k => k.Key is FileKind.Temporary or FileKind.PartialDownload or FileKind.Log or FileKind.Dump).Sum(k => k.Value) && a.Level == SafetyLevel.Unknown)
        {
            a.Level = SafetyLevel.Safe;
            a.Headline = T("Safe to delete: only temporary files, logs and dumps");
            a.Why = T("Every file inside is temporary, a log, an unfinished download or a crash dump.");
        }
        if (s.Shadows > 0)
            a.Warnings.Add(new SafetyWarning(WarningLevel.Info, P(s.Shadows, "{0:N0} file inside is a hard link counted at another name: deleting it here frees no space.", "{0:N0} files inside are hard links counted at another name: deleting them here frees no space.")));
        if (s.SharedOutside > 0)
            a.Warnings.Add(new SafetyWarning(WarningLevel.Caution, P(s.SharedOutside, "{0:N0} file inside also exists under another name outside it (a hard link): its space stays in use.", "{0:N0} files inside also exist under another name outside it (hard links): their space stays in use.")));
        if (s.NewestWrite != default && ctx.NowUtc - s.NewestWrite < TimeSpan.FromDays(2) && ctx.NowUtc > s.NewestWrite)
            a.Warnings.Add(new SafetyWarning(WarningLevel.Caution, F($"Something inside changed {Format.Age(s.NewestWrite, ctx.NowUtc)} ago: a program may still be using it.")));
        if (s.Truncated)
            a.Warnings.Add(new SafetyWarning(WarningLevel.Info, F($"Very large folder: only the first {ctx.ContentLimit:N0} elements were looked at.")));
    }

    private static void CommonWarnings(FsNode node, string path, Assessment a, SafetyContext ctx)
    {
        if (node is FolderNode { Attributes: var fa } && (fa & FileAttributes.ReparsePoint) != 0)
            a.Warnings.Add(new SafetyWarning(WarningLevel.Info, T("A link (junction or cloud folder): deleting removes the link; what it points to is not touched.")));
        if (Cloud.CloudFiles.SyncRootOf(node) is { } root && a.Level != SafetyLevel.Protected)
        {
            a.Warnings.Add(new SafetyWarning(WarningLevel.Serious, F($"In {root.Provider}: deleting it here also deletes it in the cloud and on your other devices. To free space on this PC only, use \"Free up space\" (right click > Windows menu).")));
            if (a.Level < SafetyLevel.Caution) a.Level = SafetyLevel.Caution;
        }
        if (a.Program != null && a.Level != SafetyLevel.Protected && a.Headline.IndexOf(a.Program.Name, OIC) < 0)
            a.Warnings.Add(new SafetyWarning(WarningLevel.Info, F($"It belongs to {a.Program.Name}.")));
    }
}

/// <summary>What a folder holds, counted from the scanned tree (the lock is taken per folder).</summary>
internal sealed class ContentStats
{
    public int Files, Folders, Shadows, SharedOutside;
    public bool Truncated;
    public DateTime NewestWrite;
    public Dictionary<FileKind, int> Kinds { get; } = new();
    public (string Title, string Path, string Why, string Cleanup)? ProtectedInside;

    private static bool HasWindowsParts(ContainerNode c, object? sync)
    {
        if (sync == null) return c.FindChild("System32") != null || c.FindChild("WinSxS") != null;
        lock (sync) return c.FindChild("System32") != null || c.FindChild("WinSxS") != null;
    }

    public static ContentStats Of(ContainerNode root, string rootPath, SafetyContext ctx)
    {
        var s = new ContentStats();
        var stack = new Stack<(ContainerNode Node, string Path)>();
        stack.Push((root, rootPath));
        var kids = new List<FsNode>();
        var inside = new HashSet<FileNode>(ReferenceEqualityComparer.Instance);
        var owners = new List<FileNode>();
        int seen = 0;
        while (stack.Count > 0)
        {
            var (c, p) = stack.Pop();
            kids.Clear();
            if (ctx.Sync != null) lock (ctx.Sync) kids.AddRange(c.Children);
            else kids.AddRange(c.Children);
            foreach (var k in kids)
            {
                if (k.IsSynthetic) continue;
                if (++seen > ctx.ContentLimit) { s.Truncated = true; return s; }
                string kp = p.EndsWith('\\') ? p + k.Name : p + "\\" + k.Name;
                if (k is FileNode f)
                {
                    s.Files++;
                    var kind = FileKinds.Of(f.Name);
                    if (kind != FileKind.Other) s.Kinds[kind] = s.Kinds.GetValueOrDefault(kind) + 1;
                    if (f.Modified > s.NewestWrite && f.Modified <= ctx.NowUtc.AddDays(1)) s.NewestWrite = f.Modified;
                    if (f.CountedElsewhere) s.Shadows++;
                    else if (ctx.HardLinks != null && f.FileId != 0) owners.Add(f);
                    inside.Add(f);
                }
                else if (k is ContainerNode kc)
                {
                    s.Folders++;
                    if (s.ProtectedInside == null && KnowledgeBase.MatchFolderPath(kp, k.Name) is { Safety: Safety.System } sys)
                        s.ProtectedInside = (sys.Title, kp, sys.Description, sys.Cleanup);
                    else if (s.ProtectedInside == null && k.Name.Equals("Windows", StringComparison.OrdinalIgnoreCase) && HasWindowsParts(kc, ctx.Sync))
                        s.ProtectedInside = (T("a Windows installation"), kp, T("A Windows folder (System32, WinSxS) of an older or second installation."), T("Settings > System > Storage > Temporary files > Previous Windows installation"));
                    stack.Push((kc, kp));
                }
            }
        }
        // hard-linked files counted here whose other names lie outside: deleting the folder does not free their space
        if (ctx.HardLinks != null)
            foreach (var f in owners)
                if (ctx.HardLinks.GroupOf(f) is { Length: > 1 } g && g.Any(x => !inside.Contains(x))) s.SharedOutside++;
        return s;
    }
}
