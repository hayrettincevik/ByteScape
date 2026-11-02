using System.Drawing;
using System.Globalization;
using System.Xml.Linq;
using ByteScape.Core.Export;

namespace ByteScape.Core.Config;

/// <summary>A file class: description + extensions + colour (Styles / File Classes tab).</summary>
public sealed class FileClass
{
    public string Name { get; set; } = T("New file class");
    /// <summary>The name to show: a default class (as the program ships it) in the interface language, a name the user gave as it is.</summary>
    public string DisplayName => DefaultNames.Contains(Name) ? T(Name) : Name;
    private static readonly HashSet<string> DefaultNames = AppConfig.DefaultFileClasses().Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
    public string Extensions { get; set; } = "";
    public Color Color { get; set; } = Color.Gray;

    private HashSet<string>? _set;

    public bool Contains(string extension)
    {
        _set ??= new HashSet<string>(Extensions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(e => e.TrimStart('*', '.')), StringComparer.OrdinalIgnoreCase);
        return _set.Contains(extension);
    }

    public void Invalidate() => _set = null;

    public FileClass Clone() => new() { Name = Name, Extensions = Extensions, Color = Color };

    /// <summary>
    /// The file classes of earlier versions, by FNV-1a hash (the texts themselves are gone): (name) → the current default
    /// that replaced it, and (name|extensions) when a saved class was still exactly the earlier default.
    /// </summary>
    private static readonly Dictionary<ulong, int> EarlierNames = new()
    {
        [0x0042DD4C696068E8] = 1, [0xB486AD558B35C3AD] = 4, [0x0D91A8CF4EC7222C] = 3, [0x8FAE04090BA2E0C1] = 0, [0x6C66513D843B9211] = 2,
    };
    private static readonly HashSet<ulong> EarlierDefaults = new()
    {
        0x3F89CE74A3D639B5, 0xFE56DE99D0ACF0A4, 0xC613D8E43448E79C, 0x2844EFD36448D9AB, 0x9107A0EF454F894F,
    };

    /// <summary>Tests: one more earlier default (a made-up one: the real earlier texts are not kept anywhere).</summary>
    internal static void AddEarlierDefault(string name, string extensions, int current)
    {
        EarlierNames[Hash(name.ToLowerInvariant())] = current;
        EarlierDefaults.Add(Hash(name + "|" + extensions));
    }

    /// <summary>The current default class an earlier default name stands for, or -1.</summary>
    internal static int EarlierDefault(string name) => EarlierNames.TryGetValue(Hash(name.ToLowerInvariant()), out int i) ? i : -1;

    /// <summary>
    /// A settings file from an earlier version: a class that was still the earlier default becomes the current default
    /// (name, extensions and colour); a renamed default the user changed keeps the user's extensions and colour.
    /// </summary>
    internal static void UpdateEarlierDefaults(List<FileClass> classes)
    {
        var defaults = AppConfig.DefaultFileClasses();
        for (int k = 0; k < classes.Count; k++)
        {
            int i = EarlierDefault(classes[k].Name);
            if (i < 0) continue;
            if (EarlierDefaults.Contains(Hash(classes[k].Name + "|" + classes[k].Extensions))) classes[k] = defaults[i];
            else classes[k].Name = defaults[i].Name;
        }
    }

    private static ulong Hash(string s)
    {
        ulong h = 0xcbf29ce484222325;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 0x100000001b3; }
        return h;
    }
}

public enum ViewStyle { Flat, FileClasses }

/// <summary>
/// Application configuration. Persisted as ByteScapeConfig.xml next to the executable,
/// element names stay the same from version to version, so an existing file can be reused.
/// </summary>
public sealed class AppConfig
{
    public static AppConfig Current { get; private set; } = new();

    private static string? _configPath;

    /// <summary>
    /// ByteScapeConfig.xml next to the executable when that folder is writable (portable use), otherwise
    /// %LOCALAPPDATA%\ByteScape\ (Store / MSIX installs live under the read-only WindowsApps folder).
    /// A file that already exists next to the executable is always preferred.
    /// </summary>
    public static string ConfigPath => _configPath ??= ResolveConfigPath();

    private static string ResolveConfigPath()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "ByteScapeConfig.xml");
        if (File.Exists(portable)) return portable;
        if (!IsPackaged() && IsWritable(AppContext.BaseDirectory)) return portable;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Branding.Name);
        try { Directory.CreateDirectory(dir); } catch { }
        return Path.Combine(dir, "ByteScapeConfig.xml");
    }

    /// <summary>True when running from an MSIX / Store package (identity present).</summary>
    public static bool IsPackaged()
    {
        try
        {
            int len = 0;
            // APPMODEL_ERROR_NO_PACKAGE (15700) when the process has no package identity
            return Interop.Native.GetCurrentPackageFullName(ref len, null) != 15700;
        }
        catch { return false; }
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".bytescape-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    // Geometry
    public int FontSize { get; set; } = 8;
    public int MinElementSizePx { get; set; } = 20;
    /// <summary>0..100, 50 = square, lower = horizontal, higher = vertical.</summary>
    public int Proportions { get; set; } = 50;
    public int InitialDetailLevel { get; set; } = 3;
    public bool ShowFreeSpace { get; set; } = false;
    public bool ShowUnknownSpace { get; set; } = false;
    public bool UseSorting { get; set; } = true;

    // Animation
    public int ZoomTimeMs { get; set; } = 400;
    public bool UseRealZoom { get; set; } = true;       // DynaZoom
    public int RefreshDelayMs { get; set; } = 100;      // animation smoothness (frame period)
    public bool UseDynaSmooth { get; set; } = true;

    // Behavior
    public bool ScanADS { get; set; } = false;
    public bool WatchDog { get; set; } = true;
    public bool FlashWindow { get; set; } = true;
    public bool KeepTooltip { get; set; } = true;
    public bool ShowFileTooltipDateTime { get; set; } = true;
    public bool ShowFileTooltipAge { get; set; } = true;
    public bool LogPopup { get; set; } = true;
    public bool LogWarning { get; set; } = true;
    public bool LogDebug { get; set; } = false;
    public bool LogDebugFine { get; set; } = false;
    public bool LogIssue { get; set; } = true;

    // Look
    public int LevelContrast { get; set; } = 40;   // 0..100
    public int BorderContrast { get; set; } = 50;  // 0..100
    public int HilightHaloLevels { get; set; } = 1;
    public bool MouseHover { get; set; } = true;
    public bool HilightShadow { get; set; } = true;
    public bool MouseTrail { get; set; } = false;

    // Styles
    public ViewStyle InitialStyle { get; set; } = ViewStyle.Flat;
    public Color DriveColor { get; set; } = Color.FromArgb(0xE9, 0xF0, 0xF9);
    public Color FolderColor { get; set; } = Color.FromArgb(0xD3, 0xE2, 0xF4);
    public Color FileColor { get; set; } = Color.FromArgb(0x5E, 0x9E, 0xE8);
    public Color FreeColor { get; set; } = Color.FromArgb(0xC6, 0xE9, 0xF3);
    public Color UnknownColor { get; set; } = Color.FromArgb(0xDF, 0xE4, 0xED);
    public List<FileClass> FileClasses { get; set; } = DefaultFileClasses();

    // Tags
    public Color TagRedColor { get; set; } = Color.Red;
    public Color TagYellowColor { get; set; } = Color.Gold;
    public Color TagGreenColor { get; set; } = Color.LimeGreen;
    public Color TagBlueColor { get; set; } = Color.DodgerBlue;

    // Interaction
    /// <summary>Delay before the element tooltip appears.</summary>
    public int TooltipDelayMs { get; set; } = 500;
    /// <summary>How long an externally changed element keeps blinking.</summary>
    public int BlinkDurationMs { get; set; } = 2000;
    /// <summary>Elements smaller than the minimum size are gathered into one "N items" block (true) or simply hidden (false).</summary>
    public bool AggregateSmallElements { get; set; } = false;
    /// <summary>Mark known caches / temp / protected folders on the map (knowledge base badges).</summary>
    public bool ShowKnowledgeBadges { get; set; } = true;
    /// <summary>Show sizes with 1000-based units (kB, MB) instead of 1024-based.</summary>
    public bool DecimalUnits { get; set; } = false;

    /// <summary>Language of the user interface: a code of <see cref="Localization.Loc.Languages"/>, or "" for Windows' display language.</summary>
    public string Language { get; set; } = "";

    // Scanning
    /// <summary>Directories to skip while scanning: ';' separated wildcard masks matched against the full path or the folder name (e.g. "*\WinSxS;*\node_modules").</summary>
    public string ExcludePaths { get; set; } = "";
    /// <summary>Descend into junctions / symbolic links (off by default: they usually point to already counted space).</summary>
    public bool FollowReparsePoints { get; set; } = false;
    /// <summary>Read the NTFS master file table directly when running as administrator (much faster full scans of local NTFS drives).</summary>
    public bool UseMftScan { get; set; } = true;
    /// <summary>Count a hard-linked file's space once, at the first of its names (WinSxS, pnpm stores); off = once per name like Explorer.</summary>
    public bool CountHardLinksOnce { get; set; } = true;
    /// <summary>Measure the drives at start and after complete drive scans for the disk forecast (kept next to the configuration).</summary>
    public bool RecordDiskHistory { get; set; } = true;
    /// <summary>Folders listed in parallel by a scan; 0 = automatic (by drive type: SSD, network, spinning disk).</summary>
    public int ScanThreads { get; set; } = 0;
    /// <summary>Scan strategy by drive kind: 0 = what the disk says, 1 = HDD, 2 = SSD, 3 = NVMe SSD, 4 = USB flash / memory card.</summary>
    public int ScanDriveKind { get; set; } = 0;
    /// <summary>Keep the last scanned paths for the start dialog.</summary>
    public bool RememberRecentPaths { get; set; } = true;
    /// <summary>Keep a stopped scan (and one running when ByteScape closes) to continue it where it stopped, next time the path is opened.</summary>
    public bool KeepUnfinishedScans { get; set; } = true;
    /// <summary>Rescans of local NTFS drives list only the folders the NTFS change journal says changed; a complete scan is kept when ByteScape closes so the next start is quick too.</summary>
    public bool QuickRescan { get; set; } = true;

    // Deleting and cleanup
    /// <summary>Delete moves to the Recycle Bin (Shift+Delete deletes permanently); off = permanently by default.</summary>
    public bool DeleteToRecycleBin { get; set; } = true;
    /// <summary>Folders of Windows and installed programs, Windows' user folders and protected system files cannot be deleted from ByteScape (drive roots, Windows itself and profile roots never can).</summary>
    public bool ProtectSystemFolders { get; set; } = true;
    /// <summary>Cleanup: files at least this big (MB) are "large".</summary>
    public int LargeFileMB { get; set; } = 500;
    /// <summary>Cleanup: installers in Downloads and on the Desktop older than this many days are "old".</summary>
    public int OldInstallerDays { get; set; } = 30;
    /// <summary>Cleanup: programs not used for this many days are "unused".</summary>
    public int UnusedProgramDays { get; set; } = 180;
    public int MaxRecentPaths { get; set; } = 10;
    public List<string> RecentPaths { get; set; } = new();

    // Log
    public int LogMaxEntries { get; set; } = 5000;

    // Local AI (optional): a model the user runs themselves (Ollama or an OpenAI compatible server). Off by default.
    public bool AiEnabled { get; set; } = false;
    public string AiEndpoint { get; set; } = "http://localhost:11434";
    public string AiModel { get; set; } = "";
    public string AiApiKey { get; set; } = "";
    public int AiTimeoutSeconds { get; set; } = 60;

    /// <summary>The first-start tour was seen (finished or skipped): it does not start by itself again.</summary>
    public bool TourShown { get; set; } = false;

    // Updates of the portable exe (Core/Update/UpdateCheck): off by default, nothing is contacted unless switched on or asked
    public bool CheckForUpdates { get; set; } = false;
    public DateTime LastUpdateCheckUtc { get; set; }
    /// <summary>A version the user chose to skip: the weekly check does not show it again.</summary>
    public string SkippedVersion { get; set; } = "";


    // Export configurations stored in the config file (builtins are in ExportConfig.Builtins)
    public List<ExportConfig> ExportConfigs { get; set; } = new();

    public void AddRecentPath(string path)
    {
        if (!RememberRecentPaths || string.IsNullOrWhiteSpace(path)) return;
        RecentPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentPaths.Insert(0, path);
        if (RecentPaths.Count > Math.Max(1, MaxRecentPaths)) RecentPaths.RemoveRange(MaxRecentPaths, RecentPaths.Count - MaxRecentPaths);
    }

    /// <summary>Wildcard masks from <see cref="ExcludePaths"/>, compiled once.</summary>
    public IReadOnlyList<Util.Wildcard> ExcludeMasks
    {
        get
        {
            if (_excludeMasks == null || _excludeSource != ExcludePaths)
            {
                _excludeSource = ExcludePaths;
                _excludeMasks = ExcludePaths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(m => new Util.Wildcard(m)).ToList();
            }
            return _excludeMasks;
        }
    }
    private List<Util.Wildcard>? _excludeMasks;
    private string? _excludeSource;

    /// <summary>True when a directory (full path) matches one of the exclude masks (by full path or by name).</summary>
    public bool IsExcluded(string fullPath)
    {
        var masks = ExcludeMasks;
        if (masks.Count == 0) return false;
        string name = Path.GetFileName(fullPath.TrimEnd('\\'));
        foreach (var m in masks)
            if (m.IsMatch(fullPath) || m.IsMatch(name)) return true;
        return false;
    }

    /// <summary>The file classes a new settings file starts with (their order is used by <see cref="FileClass.UpdateEarlierDefaults"/>).</summary>
    public static List<FileClass> DefaultFileClasses() => new()
    {
        new FileClass { Name = /*loc*/"Pictures", Extensions = "arw;avif;bmp;cr2;cr3;dng;gif;heic;heif;ico;jpeg;jpg;nef;orf;png;psd;raf;raw;rw2;svg;tif;tiff;webp", Color = Color.FromArgb(0x80, 0xE0, 0x80) },
        new FileClass { Name = /*loc*/"Videos", Extensions = "3gp;avi;flv;m2ts;m4v;mkv;mov;mp4;mpeg;mpg;mts;ogv;vob;webm;wmv", Color = Color.FromArgb(0xC0, 0x80, 0xE0) },
        new FileClass { Name = /*loc*/"Music and audio", Extensions = "aac;aif;aiff;amr;ape;flac;m4a;mid;midi;mp3;oga;ogg;opus;wav;wma", Color = Color.FromArgb(0x80, 0xC0, 0xE0) },
        new FileClass { Name = /*loc*/"Archives and disk images", Extensions = "7z;bz2;cab;dmg;gz;img;iso;rar;tar;tgz;vhd;vhdx;wim;xz;zip;zst", Color = Color.FromArgb(0xE0, 0xC0, 0x60) },
        new FileClass { Name = /*loc*/"Programs and system files", Extensions = "bat;cmd;cpl;dll;drv;exe;msi;msp;ocx;ps1;scr;sys", Color = Color.FromArgb(0xE0, 0x80, 0x80) },
    };

    public FileClass? ClassOf(string extension)
    {
        foreach (var c in FileClasses)
            if (c.Contains(extension)) return c;
        return null;
    }

    public FileClass? ClassByName(string name)
    {
        foreach (var c in FileClasses)
            if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(c.DisplayName, name, StringComparison.OrdinalIgnoreCase)) return c;
        // a filter written for an earlier version names a default class by its earlier name
        int i = FileClass.EarlierDefault(name);
        return i >= 0 ? ClassByName(DefaultFileClasses()[i].Name) : null;
    }

    public AppConfig Clone()
    {
        var c = (AppConfig)MemberwiseClone();
        c.FileClasses = FileClasses.Select(f => f.Clone()).ToList();
        c.ExportConfigs = ExportConfigs.Select(e => e.Clone()).ToList();
        c.RecentPaths = new List<string>(RecentPaths);
        c._excludeMasks = null;
        return c;
    }

    public static AppConfig Defaults() => new();

    public static void Replace(AppConfig cfg) { Current = cfg; Activate(); }

    /// <summary>Pushes settings that live outside the config object (static runtime switches).</summary>
    public static void Activate() { Util.Format.DecimalUnits = Current.DecimalUnits; }

    // ------------------------------------------------------------------ persistence

    public static void Load() => Load(ConfigPath);

    /// <summary>Loads <paramref name="path"/> into <see cref="Current"/>; a missing or unreadable file yields the defaults.</summary>
    public static void Load(string path)
    {
        try
        {
            if (!File.Exists(path)) { Current = new AppConfig(); return; }
            var root = XDocument.Load(path).Root ?? throw new InvalidDataException("empty document");
            Current = FromXml(root);
            // a key an earlier version wrote as plain text is encrypted at once, not only when the settings are next saved
            if (HasPlainSecret(root) && Save(Current, path))
                Util.Log.Debug("The saved API key of the local AI was plain text in the settings file; it is now encrypted for this Windows user.");
        }
        catch (Exception ex)
        {
            Current = new AppConfig();
            Util.Log.Error("Error loading configuration:\n\n" + ex.Message);
        }
    }

    public static void Save() => Save(Current, ConfigPath);

    /// <summary>Writes a configuration to <paramref name="path"/>. Returns false when it could not be written (e.g. on read-only media; the program keeps running without saving).</summary>
    public static bool Save(AppConfig cfg, string path)
    {
        try
        {
            new XDocument(new XDeclaration("1.0", "UTF-8", null), ToXml(cfg)).Save(path);
            return true;
        }
        catch (Exception ex)
        {
            Util.Log.Warn("Error saving configuration:\n\n" + ex.Message);
            return false;
        }
    }

    public static AppConfig FromXml(XElement root)
    {
        var c = new AppConfig();
        int I(string n, int d) => int.TryParse((string?)root.Element(n), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : d;
        bool B(string n, bool d) => bool.TryParse((string?)root.Element(n), out var v) ? v : d;
        string S(string n, string d) => (string?)root.Element(n) ?? d;

        c.RefreshDelayMs = I("refreshDelayMs", c.RefreshDelayMs);
        c.ZoomTimeMs = I("zoomTimeMs", c.ZoomTimeMs);
        c.MinElementSizePx = I("minElementSizePx", c.MinElementSizePx);
        c.ShowFreeSpace = B("showFreeSpace", c.ShowFreeSpace);
        c.ShowUnknownSpace = B("showUnknownSpace", c.ShowUnknownSpace);
        var style = (string?)root.Element("initialStyle");
        if (style != null) c.InitialStyle = string.Equals(style, "flat", StringComparison.OrdinalIgnoreCase) ? ViewStyle.Flat : ViewStyle.FileClasses;
        c.ShowFileTooltipDateTime = B("showFileTooltipDateTime", c.ShowFileTooltipDateTime);
        c.ShowFileTooltipAge = B("showFileTooltipAge", c.ShowFileTooltipAge);
        c.UseRealZoom = B("useRealZoom", c.UseRealZoom);
        c.UseDynaSmooth = B("useDynaSmooth", c.UseDynaSmooth);
        c.UseSorting = B("useSorting", c.UseSorting);
        c.ScanADS = B("scanADS", c.ScanADS);
        c.WatchDog = B("watchDog", c.WatchDog);
        c.LogPopup = B("logPopup", c.LogPopup);
        c.LogWarning = B("logWarning", c.LogWarning);
        c.LogDebug = B("logDebug", c.LogDebug);
        c.LogDebugFine = B("logDebugFine", c.LogDebugFine);
        c.LogIssue = B("logIssue", c.LogIssue);
        c.FlashWindow = B("flashWindow", c.FlashWindow);
        c.KeepTooltip = B("keepTooltip", c.KeepTooltip);
        c.Proportions = I("proportions", c.Proportions);
        c.InitialDetailLevel = I("initialDetailLevel", c.InitialDetailLevel);
        c.FontSize = I("fontSize", c.FontSize);
        c.TooltipDelayMs = I("tooltipDelayMs", c.TooltipDelayMs);
        c.BlinkDurationMs = I("blinkDurationMs", c.BlinkDurationMs);
        c.AggregateSmallElements = B("aggregateSmallElements", c.AggregateSmallElements);
        c.ShowKnowledgeBadges = B("showKnowledgeBadges", c.ShowKnowledgeBadges);
        c.DecimalUnits = B("decimalUnits", c.DecimalUnits);
        c.Language = S("language", c.Language);
        c.ExcludePaths = S("excludePaths", c.ExcludePaths);
        c.FollowReparsePoints = B("followReparsePoints", c.FollowReparsePoints);
        c.UseMftScan = B("useMftScan", c.UseMftScan);
        c.CountHardLinksOnce = B("countHardLinksOnce", c.CountHardLinksOnce);
        c.RecordDiskHistory = B("recordDiskHistory", c.RecordDiskHistory);
        c.ScanThreads = Math.Clamp(I("scanThreads", c.ScanThreads), 0, 32);
        c.ScanDriveKind = Math.Clamp(I("scanDriveKind", c.ScanDriveKind), 0, 4);
        c.RememberRecentPaths = B("rememberRecentPaths", c.RememberRecentPaths);
        c.KeepUnfinishedScans = B("keepUnfinishedScans", c.KeepUnfinishedScans);
        c.QuickRescan = B("quickRescan", c.QuickRescan);
        c.DeleteToRecycleBin = B("deleteToRecycleBin", c.DeleteToRecycleBin);
        c.ProtectSystemFolders = B("protectSystemFolders", c.ProtectSystemFolders);
        c.LargeFileMB = Math.Clamp(I("largeFileMB", c.LargeFileMB), 1, 1_000_000);
        c.OldInstallerDays = Math.Clamp(I("oldInstallerDays", c.OldInstallerDays), 0, 3650);
        c.UnusedProgramDays = Math.Clamp(I("unusedProgramDays", c.UnusedProgramDays), 1, 3650);
        c.MaxRecentPaths = I("maxRecentPaths", c.MaxRecentPaths);
        c.LogMaxEntries = I("logMaxEntries", c.LogMaxEntries);
        c.AiEnabled = B("aiEnabled", c.AiEnabled);
        c.AiEndpoint = S("aiEndpoint", c.AiEndpoint);
        c.AiModel = S("aiModel", c.AiModel);
        c.AiApiKey = ReadSecret(root.Element("aiApiKey"), "The saved API key of the local AI");
        c.AiTimeoutSeconds = I("aiTimeoutSeconds", c.AiTimeoutSeconds);
        c.TourShown = B("tourShown", c.TourShown);
        c.CheckForUpdates = B("checkForUpdates", c.CheckForUpdates);
        c.LastUpdateCheckUtc = DateTime.TryParse((string?)root.Element("lastUpdateCheck"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var lastCheck) ? lastCheck : default;
        c.SkippedVersion = S("skippedVersion", c.SkippedVersion);
        var recent = root.Element("recentPaths");
        if (recent != null) c.RecentPaths = recent.Elements("path").Select(e => (string)e).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

        var colors = root.Element("colors");
        if (colors != null)
        {
            Color C(string n, Color d) => ParseColor((string?)colors.Element(n)) ?? d;
            int CI(string n, int d) => int.TryParse((string?)colors.Element(n), out var v) ? v : d;
            bool CB(string n, bool d) => bool.TryParse((string?)colors.Element(n), out var v) ? v : d;
            c.DriveColor = C("driveColor", c.DriveColor);
            c.FolderColor = C("folderColor", c.FolderColor);
            c.FileColor = C("fileColor", c.FileColor);
            c.FreeColor = C("freeColor", c.FreeColor);
            c.UnknownColor = C("unknownColor", c.UnknownColor);
            c.TagRedColor = C("tagRedColor", c.TagRedColor);
            c.TagYellowColor = C("tagYellowColor", c.TagYellowColor);
            c.TagGreenColor = C("tagGreenColor", c.TagGreenColor);
            c.TagBlueColor = C("tagBlueColor", c.TagBlueColor);
            c.LevelContrast = CI("levelContrast", c.LevelContrast);
            c.BorderContrast = CI("borderContrast", c.BorderContrast);
            c.MouseHover = CB("mouseHover", c.MouseHover);
            c.MouseTrail = CB("mouseTrail", c.MouseTrail);
            c.HilightShadow = CB("hilightShadow", c.HilightShadow);
            c.HilightHaloLevels = CI("hilightHaloLevels", c.HilightHaloLevels);
        }

        var groups = root.Element("fileGroups") ?? root.Element("fileClasses");
        if (groups != null)
        {
            var list = new List<FileClass>();
            foreach (var g in groups.Elements())
            {
                list.Add(new FileClass
                {
                    Name = (string?)g.Element("name") ?? "",
                    Extensions = (string?)g.Element("extensions") ?? "",
                    Color = ParseColor((string?)g.Element("color")) ?? Color.Gray,
                });
            }
            FileClass.UpdateEarlierDefaults(list);
            c.FileClasses = list; // an explicitly empty list is honoured too
        }

        var exports = root.Element("exportConfigs");
        if (exports != null)
            foreach (var e in exports.Elements("exportConfig"))
                c.ExportConfigs.Add(ExportConfig.FromXml(e));
        return c;
    }

    public static XElement ToXml(AppConfig c) => new("ByteScapeConfig",
        new XElement("refreshDelayMs", c.RefreshDelayMs),
        new XElement("zoomTimeMs", c.ZoomTimeMs),
        new XElement("minElementSizePx", c.MinElementSizePx),
        new XElement("showFreeSpace", c.ShowFreeSpace),
        new XElement("showUnknownSpace", c.ShowUnknownSpace),
        new XElement("initialStyle", c.InitialStyle == ViewStyle.Flat ? "flat" : "fileClasses"),
        new XElement("showFileTooltipDateTime", c.ShowFileTooltipDateTime),
        new XElement("showFileTooltipAge", c.ShowFileTooltipAge),
        new XElement("useRealZoom", c.UseRealZoom),
        new XElement("useDynaSmooth", c.UseDynaSmooth),
        new XElement("useSorting", c.UseSorting),
        new XElement("scanADS", c.ScanADS),
        new XElement("watchDog", c.WatchDog),
        new XElement("logPopup", c.LogPopup),
        new XElement("logWarning", c.LogWarning),
        new XElement("logDebug", c.LogDebug),
        new XElement("logDebugFine", c.LogDebugFine),
        new XElement("logIssue", c.LogIssue),
        new XElement("flashWindow", c.FlashWindow),
        new XElement("keepTooltip", c.KeepTooltip),
        new XElement("proportions", c.Proportions),
        new XElement("initialDetailLevel", c.InitialDetailLevel),
        new XElement("fontSize", c.FontSize),
        new XElement("tooltipDelayMs", c.TooltipDelayMs),
        new XElement("blinkDurationMs", c.BlinkDurationMs),
        new XElement("aggregateSmallElements", c.AggregateSmallElements),
        new XElement("showKnowledgeBadges", c.ShowKnowledgeBadges),
        new XElement("decimalUnits", c.DecimalUnits),
        new XElement("language", c.Language),
        new XElement("excludePaths", c.ExcludePaths),
        new XElement("followReparsePoints", c.FollowReparsePoints),
        new XElement("useMftScan", c.UseMftScan),
        new XElement("countHardLinksOnce", c.CountHardLinksOnce),
        new XElement("recordDiskHistory", c.RecordDiskHistory),
        new XElement("scanThreads", c.ScanThreads),
        new XElement("scanDriveKind", c.ScanDriveKind),
        new XElement("rememberRecentPaths", c.RememberRecentPaths),
        new XElement("keepUnfinishedScans", c.KeepUnfinishedScans),
        new XElement("quickRescan", c.QuickRescan),
        new XElement("deleteToRecycleBin", c.DeleteToRecycleBin),
        new XElement("protectSystemFolders", c.ProtectSystemFolders),
        new XElement("largeFileMB", c.LargeFileMB),
        new XElement("oldInstallerDays", c.OldInstallerDays),
        new XElement("unusedProgramDays", c.UnusedProgramDays),
        new XElement("maxRecentPaths", c.MaxRecentPaths),
        new XElement("logMaxEntries", c.LogMaxEntries),
        new XElement("aiEnabled", c.AiEnabled),
        new XElement("aiEndpoint", c.AiEndpoint),
        new XElement("aiModel", c.AiModel),
        SecretElement("aiApiKey", c.AiApiKey),
        new XElement("aiTimeoutSeconds", c.AiTimeoutSeconds),
        new XElement("tourShown", c.TourShown),
        new XElement("checkForUpdates", c.CheckForUpdates),
        new XElement("lastUpdateCheck", c.LastUpdateCheckUtc == default ? "" : c.LastUpdateCheckUtc.ToString("o", CultureInfo.InvariantCulture)),
        new XElement("skippedVersion", c.SkippedVersion),
        new XElement("recentPaths", c.RecentPaths.Select(p => new XElement("path", p))),
        new XElement("colors",
            new XElement("driveColor", ColorText(c.DriveColor)),
            new XElement("folderColor", ColorText(c.FolderColor)),
            new XElement("fileColor", ColorText(c.FileColor)),
            new XElement("freeColor", ColorText(c.FreeColor)),
            new XElement("unknownColor", ColorText(c.UnknownColor)),
            new XElement("tagRedColor", ColorText(c.TagRedColor)),
            new XElement("tagYellowColor", ColorText(c.TagYellowColor)),
            new XElement("tagGreenColor", ColorText(c.TagGreenColor)),
            new XElement("tagBlueColor", ColorText(c.TagBlueColor)),
            new XElement("levelContrast", c.LevelContrast),
            new XElement("borderContrast", c.BorderContrast),
            new XElement("mouseHover", c.MouseHover),
            new XElement("mouseTrail", c.MouseTrail),
            new XElement("hilightShadow", c.HilightShadow),
            new XElement("hilightHaloLevels", c.HilightHaloLevels)),
        new XElement("fileGroups", c.FileClasses.Select(g => new XElement("fileGroup",
            new XElement("name", g.Name),
            new XElement("extensions", g.Extensions),
            new XElement("color", ColorText(g.Color))))),
        new XElement("exportConfigs", c.ExportConfigs.Select(e => e.ToXml())));

    /// <summary>A secret is written encrypted for the current Windows user (<see cref="Util.Secrets"/>), never as plain text.</summary>
    private static XElement SecretElement(string name, string value) => value.Length == 0 ? new XElement(name)
        : new XElement(name, new XAttribute("protection", Util.Secrets.Scheme), Util.Secrets.Protect(value));

    /// <summary>A secret stored as plain text (settings files of earlier versions).</summary>
    internal static bool HasPlainSecret(XElement root)
        => root.Element("aiApiKey") is { } e && e.Value.Length > 0 && (string?)e.Attribute("protection") != Util.Secrets.Scheme;

    /// <summary>Plain text (files of earlier versions) is still read and encrypted when the file is loaded; a value protected by
    /// another Windows user or on another computer (a portable copy carried over) reads as empty, with a note in the log.</summary>
    private static string ReadSecret(XElement? e, string what)
    {
        if (e == null) return "";
        if ((string?)e.Attribute("protection") != Util.Secrets.Scheme) return e.Value;
        if (Util.Secrets.TryUnprotect(e.Value, out var plain)) return plain;
        Util.Log.Warn(what + " could not be read: it was saved by another Windows user or on another computer. Enter it again in Settings.");
        return "";
    }

    public static string ColorText(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color? ParseColor(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        try
        {
            if (s.StartsWith('#') && s.Length == 7)
                return Color.FromArgb(Convert.ToInt32(s.Substring(1, 2), 16), Convert.ToInt32(s.Substring(3, 2), 16), Convert.ToInt32(s.Substring(5, 2), 16));
            if (int.TryParse(s, out var v)) return Color.FromArgb(v & 0xFF, (v >> 8) & 0xFF, (v >> 16) & 0xFF); // Delphi TColor (BGR)
            var named = Color.FromName(s);
            return named.IsKnownColor ? named : null;
        }
        catch { return null; }
    }
}
