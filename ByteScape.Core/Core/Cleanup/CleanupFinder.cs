using System.Diagnostics;
using System.Text;
using ByteScape.Core.Apps;
using ByteScape.Core.Knowledge;
using ByteScape.Core.Model;
using ByteScape.Core.Util;

namespace ByteScape.Core.Cleanup;

public enum CleanupCategory { LargeFiles, TemporaryFiles, OldInstallers, UnusedPrograms }

/// <summary>One line of a cleanup category: what it is, its size, how safe, and what a delete (or uninstall) would take.</summary>
public sealed class CleanupItem
{
    public required CleanupCategory Category { get; init; }
    public required string Title { get; init; }
    /// <summary>Where it is, how many elements, what it holds.</summary>
    public string Detail { get; init; } = "";
    public string Path { get; init; } = "";
    public long Bytes { get; internal set; }
    /// <summary>What a delete gets (a folder that must stay is emptied: its assessment says so).</summary>
    public List<FsNode> Nodes { get; } = new();
    public Assessment? Assessment { get; internal set; }
    public SafetyLevel Level { get; internal set; } = SafetyLevel.Unknown;
    /// <summary>Last change, or for programs the last use.</summary>
    public DateTime When { get; internal set; }
    public string WhenText { get; internal set; } = "";
    public AppUsage? Program { get; init; }
    /// <summary>Safe items start ticked.</summary>
    public bool Preselected { get; internal set; }
    /// <summary>Better done by Windows: a Settings page or shell folder to open (Storage settings, the Recycle Bin).</summary>
    public string WindowsAction { get; init; } = "";
    /// <summary>Text for the <see cref="WindowsAction"/> button.</summary>
    public string WindowsActionText { get; init; } = "";
    public bool CanDelete => Nodes.Count > 0 && Assessment is not { Blocked: true };
}

public sealed class CleanupOptions
{
    public long LargeFileBytes { get; init; } = 500L << 20;
    public int MaxLargeFiles { get; init; } = 200;
    /// <summary>Installers in Downloads and on the Desktop older than this are "old".</summary>
    public TimeSpan InstallerAge { get; init; } = TimeSpan.FromDays(30);
    /// <summary>Programs not started and whose data did not change for this long are "unused".</summary>
    public TimeSpan UnusedProgramAge { get; init; } = TimeSpan.FromDays(180);
    /// <summary>Smaller programs are not listed as unused: the list is about space (and keeps out Store extensions and language packs).</summary>
    public long MinProgramBytes { get; init; } = 10L << 20;
    /// <summary>Temporary files that changed more recently are left alone (a program may be using them).</summary>
    public TimeSpan TempAge { get; init; } = TimeSpan.FromDays(1);
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;

    public static CleanupOptions FromConfig(Config.AppConfig c) => new()
    {
        LargeFileBytes = Math.Max(1, c.LargeFileMB) * (1L << 20),
        InstallerAge = TimeSpan.FromDays(Math.Max(0, c.OldInstallerDays)),
        UnusedProgramAge = TimeSpan.FromDays(Math.Max(1, c.UnusedProgramDays)),
    };
}

public sealed class CleanupResult
{
    public required string RootPath { get; init; }
    public Dictionary<CleanupCategory, List<CleanupItem>> Items { get; } = Enum.GetValues<CleanupCategory>().ToDictionary(c => c, _ => new List<CleanupItem>());
    public List<string> Notes { get; } = new();
    /// <summary>Installed programs were matched (without them there is no "unused programs" list).</summary>
    public bool ProgramsIncluded { get; init; }
    public TimeSpan Elapsed { get; internal set; }

    public IReadOnlyList<CleanupItem> Of(CleanupCategory c) => Items[c];
    public long BytesOf(CleanupCategory c) => Items[c].Sum(i => i.Bytes);
}

/// <summary>
/// Finds what can usually go, by category, in a scanned tree: large files, temporary files (Temp folders, crash dumps,
/// logs, and stray .tmp / unfinished downloads elsewhere; what changed in the last day stays), old installation files
/// (installers in Downloads and on the Desktop, Windows upgrade and update leftovers, driver packages) and programs
/// not used for months (Windows' record of program starts and the programs' data folders). Every item carries the
/// <see cref="DeleteSafety"/> verdict; nothing is deleted here. Meant for a background thread (the lock is taken per folder).
/// </summary>
public static class CleanupFinder
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    /// <summary>Knowledge base locations that are temporary files.</summary>
    private static readonly HashSet<string> TempRules = new(StringComparer.Ordinal) { "user-temp", "crash-dumps", "windows-logs" };

    /// <summary>Knowledge base locations that are installation leftovers.</summary>
    private static readonly HashSet<string> InstallerRules = new(StringComparer.Ordinal) { "wu-download", "delivery-optimization", "nvidia-downloads", "windows-old", "vs-installer-packages" };

    /// <summary>Folders at the root of a drive that Windows setup, upgrades and driver installers leave behind.</summary>
    private static readonly (string Name, string Title, string Why)[] RootLeftovers =
    {
        ("$WINDOWS.~BT", /*loc*/ "Windows upgrade files", /*loc*/ "Left by a Windows feature upgrade. Remove them with Settings > System > Storage > Temporary files (\"Windows upgrade log files\", \"Temporary Windows installation files\")."),
        ("$Windows.~WS", /*loc*/ "Windows upgrade files", /*loc*/ "Left by the Windows upgrade assistant or Media Creation Tool. Settings > System > Storage > Temporary files removes them."),
        ("$GetCurrent", /*loc*/ "Windows upgrade files", /*loc*/ "Left by a Windows upgrade. Safe to delete once the upgrade is done."),
        ("ESD", /*loc*/ "Windows installation image", /*loc*/ "A Windows installation image downloaded for an upgrade or a reset."),
        ("NVIDIA", /*loc*/ "NVIDIA driver installer", /*loc*/ "Driver files unpacked by an NVIDIA installer. The installed driver keeps working."),
        ("AMD", /*loc*/ "AMD driver installer", /*loc*/ "Driver files unpacked by an AMD installer. The installed driver keeps working."),
        ("MSOCache", /*loc*/ "Office installation source", /*loc*/ "Kept by older Office versions to repair or change the installation without the disc; Office asks for the setup files if it is gone."),
    };

    public static string Title(CleanupCategory c) => c switch
    {
        CleanupCategory.LargeFiles => T("Large files"),
        CleanupCategory.TemporaryFiles => T("Temporary files"),
        CleanupCategory.OldInstallers => T("Old installation files"),
        _ => T("Unused programs"),
    };

    public static CleanupResult Find(ContainerNode root, SafetyContext ctx, CleanupOptions opt, FootprintResult? programs, AppLastUse? starts, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        string rootPath;
        try { rootPath = root.RealPath().TrimEnd('\\'); } catch { rootPath = root.Name.TrimEnd('\\'); }
        if (rootPath.Length == 2) rootPath += "\\";
        var result = new CleanupResult { RootPath = rootPath, ProgramsIncluded = programs != null };
        var env = ctx.Environment;
        var large = new List<(FileNode Node, string Path)>();
        var stray = new Dictionary<FileKind, (List<FsNode> Nodes, long Bytes, DateTime Newest)>();
        var stack = new Stack<(ContainerNode Node, string Path, bool Windows)>();
        stack.Push((root, rootPath, DeleteSafety.IsSameOrUnder(rootPath, env.WindowsDir)));
        var kids = new List<FsNode>();
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (c, p, inWindows) = stack.Pop();
            kids.Clear();
            if (ctx.Sync != null) lock (ctx.Sync) kids.AddRange(c.Children);
            else kids.AddRange(c.Children);
            string? profileTop = ProfileTop(p, env);
            // installers people download: Downloads and the Desktop (not inside tools' dot folders such as a .venv)
            bool userFiles = profileTop is not null && (profileTop.Equals("Downloads", OIC) || profileTop.Equals("Desktop", OIC)) && !p.Contains(@"\.", StringComparison.Ordinal);
            bool programArea = DeleteSafety.IsSameOrUnder(p, env.ProgramFiles) || DeleteSafety.IsSameOrUnder(p, env.ProgramFilesX86);
            foreach (var k in kids)
            {
                if (k.IsSynthetic) continue;
                if (k is ContainerNode kc)
                {
                    string kp = DeletePlan.Join(p, k.Name);
                    if (k.Name.Equals("$Recycle.Bin", OIC) && p.Length == 3) { RecycleBinItem(result, kc, kp, ctx); continue; }
                    var rule = KnowledgeBase.MatchFolderPath(kp, k.Name);
                    if (rule != null && TempRules.Contains(rule.Id)) { TempLocation(result, kc, kp, rule, ctx, opt); continue; }
                    if (rule != null && InstallerRules.Contains(rule.Id)) { InstallerLocation(result, kc, kp, rule.Title, rule.Description, ctx, rule.Id == "windows-old"); continue; }
                    if (p.Length == 3 && RootLeftovers.FirstOrDefault(l => l.Name.Equals(k.Name, OIC)) is { Name: not null } left)
                    { InstallerLocation(result, kc, kp, T(left.Title), T(left.Why), ctx, left.Name.StartsWith('$')); continue; }
                    if (kp.Equals(env.ProgramData + @"\Package Cache", OIC))
                    { InstallerLocation(result, kc, kp, T("Installer package cache"), T("Installers kept by Visual Studio, .NET, the C++ runtimes and other programs to repair, update and uninstall themselves. Deleting it breaks their repair and uninstall."), ctx, false, caution: true); continue; }
                    stack.Push((kc, kp, inWindows || DeleteSafety.IsSameOrUnder(kp, env.WindowsDir)));
                    continue;
                }
                // a file's path only when it is listed: most files are not, and a drive has millions of them
                var f = (FileNode)k;
                if (f.DiskSize >= opt.LargeFileBytes) large.Add((f, DeletePlan.Join(p, f.Name)));
                if (inWindows || programArea) continue;
                var kind = FileKinds.Of(f.Name);
                if (kind is FileKind.Temporary or FileKind.PartialDownload or FileKind.Dump && opt.NowUtc - f.Modified > opt.TempAge && !DeleteSafety.Assess(f, ctx).Blocked)
                {
                    stray.TryGetValue(kind, out var s);
                    s.Nodes ??= new List<FsNode>();
                    s.Nodes.Add(f); s.Bytes += f.DiskSize; if (f.Modified > s.Newest) s.Newest = f.Modified;
                    stray[kind] = s;
                }
                else if (kind == FileKind.Installer && userFiles && opt.NowUtc - f.Modified > opt.InstallerAge)
                    InstallerFile(result, f, DeletePlan.Join(p, f.Name), ctx, opt);
            }
        }

        // large files: the biggest first; protected ones (page file...) are only counted
        int hidden = 0;
        foreach (var (f, path) in large.OrderByDescending(x => x.Node.DiskSize))
        {
            if (result.Items[CleanupCategory.LargeFiles].Count >= opt.MaxLargeFiles) break;
            var a = DeleteSafety.Assess(f, ctx);
            if (a.Blocked) { hidden++; continue; }
            var item = new CleanupItem
            {
                Category = CleanupCategory.LargeFiles, Title = f.Name, Path = path, Detail = System.IO.Path.GetDirectoryName(path) ?? "",
                Bytes = f.DiskSize, Assessment = a, Level = a.Level, When = f.Modified, WhenText = F($"changed {Format.Age(f.Modified, opt.NowUtc)} ago"),
            };
            item.Nodes.Add(f);
            result.Items[CleanupCategory.LargeFiles].Add(item);
        }
        if (hidden > 0) result.Notes.Add(P(hidden, "{0} large file Windows needs (page file, hibernation file...) is not listed.", "{0} large files Windows needs (page file, hibernation file...) are not listed."));

        foreach (var (kind, s) in stray.Where(s => s.Value.Bytes > 0).OrderByDescending(s => s.Value.Bytes))
        {
            var item = new CleanupItem
            {
                Category = CleanupCategory.TemporaryFiles,
                Title = T(kind switch { FileKind.PartialDownload => "Unfinished downloads", FileKind.Dump => "Crash dumps", _ => "Leftover temporary files" }),
                Detail = F($"{FileKinds.Count(kind, s.Nodes.Count)} older than a day, e.g. {s.Nodes.OrderByDescending(n => n.DiskSize).First().RealPath()}"),
                Bytes = s.Bytes, Level = SafetyLevel.Safe, When = s.Newest, WhenText = F($"newest {Format.Age(s.Newest, opt.NowUtc)} old"), Preselected = true,
            };
            item.Nodes.AddRange(s.Nodes);
            item.Assessment = DeleteSafety.Assess(s.Nodes[0], ctx);
            result.Items[CleanupCategory.TemporaryFiles].Add(item);
        }

        if (programs != null) UnusedPrograms(result, programs, starts, opt);
        foreach (var list in result.Items.Values) list.Sort((x, y) => y.Bytes.CompareTo(x.Bytes));
        result.Elapsed = sw.Elapsed;
        return result;
    }

    /// <summary>The first folder below the user profile a path lies in ("Downloads"), or null outside a profile.</summary>
    private static string? ProfileTop(string path, SafetyEnvironment env)
    {
        var profile = DeleteSafety.ProfileRoot(path, env);
        if (profile == null || path.Length <= profile.Length) return null;
        var rel = path.Substring(profile.Length + 1);
        int i = rel.IndexOf('\\');
        return i < 0 ? rel : rel.Substring(0, i);
    }

    private static void TempLocation(CleanupResult result, ContainerNode folder, string path, KnownLocation rule, SafetyContext ctx, CleanupOptions opt)
    {
        var a = DeleteSafety.Assess(folder, ctx);
        // what a delete would take: the content that did not change lately (the Temp folder itself stays)
        var plan = DeletePlan.Build(new[] { folder }, ctx, opt.TempAge);
        var pi = plan.Items[0];
        long bytes = a.ContentOnly ? pi.Bytes : folder.DiskSize;
        if (bytes <= 0 && pi.KeptRecent == 0) return;
        var item = new CleanupItem
        {
            Category = CleanupCategory.TemporaryFiles, Title = rule.Title, Path = path,
            Detail = path + (a.ContentOnly ? "  ·  " + P(pi.Targets.Count, "{0:N0} element", "{0:N0} elements") + (pi.KeptRecent > 0 ? ", " + P(pi.KeptRecent, "{0:N0} changed in the last day is left alone", "{0:N0} changed in the last day are left alone") : "") : ""),
            Bytes = bytes, Assessment = a, Level = a.Level, Preselected = a.Level == SafetyLevel.Safe && !a.Blocked,
        };
        item.Nodes.Add(folder);
        result.Items[CleanupCategory.TemporaryFiles].Add(item);
    }

    private static void RecycleBinItem(CleanupResult result, ContainerNode bin, string path, SafetyContext ctx)
    {
        long bytes = bin.DiskSize;
        if (bytes <= 0) return;
        result.Items[CleanupCategory.TemporaryFiles].Add(new CleanupItem
        {
            Category = CleanupCategory.TemporaryFiles, Title = T("Recycle Bin"), Path = path, Bytes = bytes, Level = SafetyLevel.Caution,
            Detail = T("Deleted files still take their space until the Recycle Bin is emptied. Check it, then empty it from Windows."),
            WindowsAction = "shell:RecycleBinFolder", WindowsActionText = T("Open the Recycle Bin"),
            Assessment = DeleteSafety.Assess(bin, ctx),
        });
    }

    private static void InstallerLocation(CleanupResult result, ContainerNode folder, string path, string title, string why, SafetyContext ctx, bool windowsTool, bool caution = false)
    {
        long bytes = folder.DiskSize;
        if (bytes <= 0) return;
        var a = DeleteSafety.Assess(folder, ctx);
        var item = new CleanupItem
        {
            Category = CleanupCategory.OldInstallers, Title = title, Path = path, Detail = path + "  ·  " + why, Bytes = bytes, Assessment = a,
            Level = caution && a.Level < SafetyLevel.Caution ? SafetyLevel.Caution : a.Level,
            Preselected = !caution && !windowsTool && a.Level == SafetyLevel.Safe,
            WindowsAction = windowsTool ? "ms-settings:storagesense" : "", WindowsActionText = windowsTool ? T("Open Storage settings") : "",
        };
        if (!a.Blocked) item.Nodes.Add(folder);
        result.Items[CleanupCategory.OldInstallers].Add(item);
    }

    private static void InstallerFile(CleanupResult result, FileNode f, string path, SafetyContext ctx, CleanupOptions opt)
    {
        var a = DeleteSafety.Assess(f, ctx);
        var item = new CleanupItem
        {
            Category = CleanupCategory.OldInstallers, Title = f.Name, Path = path, Detail = System.IO.Path.GetDirectoryName(path) ?? "",
            Bytes = f.DiskSize, Assessment = a, Level = a.Level, When = f.Modified, WhenText = F($"downloaded {Format.Age(f.Modified, opt.NowUtc)} ago"),
            Preselected = a.Level == SafetyLevel.Safe,
        };
        if (!a.Blocked) item.Nodes.Add(f);
        result.Items[CleanupCategory.OldInstallers].Add(item);
    }

    private static void UnusedPrograms(CleanupResult result, FootprintResult programs, AppLastUse? starts, CleanupOptions opt)
    {
        if (starts is not { Entries: > 0 })
            result.Notes.Add(T("Windows keeps no record of program starts for this account (Settings > Privacy > General: \"Let Windows improve Start and search results by tracking app launches\"): only the programs' data folders tell when they were used."));
        foreach (var u in programs.Apps)
        {
            if (u.App.IsComponent || u.Bytes < opt.MinProgramBytes) continue;
            var run = starts?.LastRun(u.App);
            DateTime last = run?.When ?? default;
            string how = run?.How ?? "";
            if (u.LastDataWrite > last) { last = u.LastDataWrite; how = "its data last changed"; }
            var installed = u.App.InstallDate;
            if (installed != default && opt.NowUtc - installed < opt.UnusedProgramAge) continue;   // new: not unused yet
            if (last != default && opt.NowUtc - last < opt.UnusedProgramAge) continue;
            // no start recorded and no data written: nothing to go by (command line tools, runtimes) - not called unused
            if (last == default) continue;
            string when = how == "its data last changed" ? F($"data last changed {Format.Age(last, opt.NowUtc)} ago") : F($"last started {Format.Age(last, opt.NowUtc)} ago");
            result.Items[CleanupCategory.UnusedPrograms].Add(new CleanupItem
            {
                Category = CleanupCategory.UnusedPrograms, Title = u.App.Name, Program = u,
                Detail = (u.App.Publisher.Length > 0 ? u.App.Publisher + "  ·  " : "") + u.App.TypeText + "  ·  " + P(u.Pieces.Count, "{0} folder: program {1}, data {2}", "{0} folders: program {1}, data {2}", Format.Size(u.ProgramBytes), Format.Size(u.DataBytes)),
                Path = u.App.InstallDirs.FirstOrDefault() ?? "", Bytes = u.Bytes, Level = SafetyLevel.Caution, When = last, WhenText = when,
                WindowsAction = u.App.Source == AppSource.Store || u.App.UninstallCommand.Length == 0 ? "ms-settings:appsfeatures" : "", WindowsActionText = T("Uninstall…"),
            });
        }
    }

    /// <summary>Plain text report (command line "cleanupreport").</summary>
    public static string ToText(CleanupResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(F($"Cleanup candidates in {r.RootPath}"));
        sb.AppendLine(T("Nothing was deleted.") + " " + T("Safety:") + " " + string.Join(" / ", Enum.GetValues<SafetyLevel>().Select(DeleteSafety.LevelText)) + ".");
        foreach (var c in Enum.GetValues<CleanupCategory>())
        {
            var items = r.Of(c);
            sb.AppendLine();
            sb.AppendLine($"== {Title(c)}: " + P(items.Count, "{0} item, {1}", "{0} items, {1}", Format.Size(r.BytesOf(c))));
            if (c == CleanupCategory.UnusedPrograms && !r.ProgramsIncluded) sb.AppendLine("   (" + T("installed programs were not read") + ")");
            foreach (var i in items.Take(100))
            {
                sb.Append("   ").Append(Format.Size(i.Bytes).PadLeft(9)).Append("  [").Append(DeleteSafety.LevelText(i.Level)).Append("]  ").Append(i.Title);
                if (i.WhenText.Length > 0) sb.Append("  (").Append(i.WhenText).Append(')');
                sb.AppendLine();
                if (i.Detail.Length > 0) sb.Append("              ").AppendLine(i.Detail);
            }
            if (items.Count > 100) sb.AppendLine("   … " + P(items.Count - 100, "{0} more", "{0} more"));
        }
        if (r.Notes.Count > 0) { sb.AppendLine(); foreach (var n in r.Notes) sb.AppendLine(T("Note:") + " " + n); }
        return sb.ToString();
    }
}
