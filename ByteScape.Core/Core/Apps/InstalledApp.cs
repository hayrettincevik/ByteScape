namespace ByteScape.Core.Apps;

public enum AppSource { Desktop, Store }

/// <summary>Where a piece of a program's footprint lives.</summary>
public enum PieceKind
{
    /// <summary>The install folder (Program Files, AppData\Local\Programs or wherever the installer put it).</summary>
    Program,
    /// <summary>AppData\Local\&lt;program&gt;: caches and machine-bound data.</summary>
    LocalData,
    /// <summary>AppData\Roaming\&lt;program&gt;: settings and data that follow the user.</summary>
    RoamingData,
    /// <summary>AppData\LocalLow\&lt;program&gt;: low integrity data (games, browsers' sandboxes).</summary>
    LocalLowData,
    /// <summary>ProgramData\&lt;program&gt;: data shared by all users.</summary>
    SharedData,
    /// <summary>AppData\Local\Packages\&lt;package&gt;: everything a Store app keeps.</summary>
    StoreData,
    /// <summary>A dot folder in the user profile (.vscode, .docker...).</summary>
    Profile,
    /// <summary>Anywhere else.</summary>
    Other,
}

/// <summary>One installed program as Windows lists it; uninstall entries with the same name (x86 + x64, per user + per machine) are merged.</summary>
public sealed class InstalledApp
{
    public required string Name { get; init; }
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public AppSource Source { get; init; }
    /// <summary>Hidden from Settings &gt; Apps (runtimes, SDK parts, Windows' own packages); such entries only claim their install folder.</summary>
    public bool IsComponent { get; init; }
    /// <summary>Store package family ("Name_publisherid") for Store apps.</summary>
    public string? PackageFamily { get; init; }
    /// <summary>Size reported by the installer (EstimatedSize), 0 when unknown.</summary>
    public long EstimatedBytes { get; internal set; }
    /// <summary>Install folders (no trailing backslash).</summary>
    public List<string> InstallDirs { get; } = new();
    /// <summary>The command Settings > Apps runs to uninstall it (desktop programs), empty when unknown.</summary>
    public string UninstallCommand { get; internal set; } = "";
    /// <summary>When it was installed (the installer's InstallDate), default when unknown.</summary>
    public DateTime InstallDate { get; internal set; }

    /// <summary>Normalised names that identify the program's data folders (see <see cref="AppCatalog.Key"/>).</summary>
    internal readonly HashSet<string> Keys = new(StringComparer.Ordinal);
    internal readonly HashSet<string> PublisherKeys = new(StringComparer.Ordinal);
    /// <summary>Store package families that are this desktop program's (Edge's identity package): their data folders are its data.</summary>
    internal readonly List<string> ExtraFamilies = new();

    public string TypeText => T(Source == AppSource.Store ? (IsComponent ? "Windows" : "Store") : IsComponent ? "Component" : "Desktop");

    public override string ToString() => Name;
}

/// <summary>Raw uninstall registry entry (HKLM / HKLM WOW6432Node / HKCU ...\Uninstall\*).</summary>
public sealed record UninstallEntry(string KeyName, string DisplayName)
{
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    public string DisplayIcon { get; init; } = "";
    public string UninstallString { get; init; } = "";
    public long EstimatedSizeKb { get; init; }
    /// <summary>"yyyyMMdd" as installers write it, empty when not written.</summary>
    public string InstallDate { get; init; } = "";
    public bool SystemComponent { get; init; }
    /// <summary>Updates and patches point at their parent entry and are skipped.</summary>
    public bool IsUpdate { get; init; }
}

/// <summary>Store (MSIX / AppX) package registered for the current user.</summary>
public sealed record StorePackage(string FullName, string DisplayName)
{
    public string RootFolder { get; init; } = "";
    public string Family => AppCatalog.FamilyOf(FullName) ?? FullName;
}

/// <summary>"This folder belongs to that program": the claim of one folder.</summary>
public sealed record AppClaim(InstalledApp App, PieceKind Kind);
