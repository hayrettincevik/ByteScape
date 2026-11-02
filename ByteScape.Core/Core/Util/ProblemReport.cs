using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ByteScape.Core.Util;

public sealed class ProblemReportOptions
{
    public bool IncludeLog { get; set; } = true;
    public bool IncludeCrashReports { get; set; } = true;
    /// <summary>The settings without the API key and without the list of recently opened folders.</summary>
    public bool IncludeSettings { get; set; } = true;
    /// <summary>The user and computer name become &lt;user&gt; / &lt;computer&gt; in every file of the report.</summary>
    public bool HideNames { get; set; } = true;
    /// <summary>What the user wrote about the problem (goes first in README.txt).</summary>
    public string Description { get; set; } = "";
    /// <summary>Lines only the window knows (screens, DPI), added to system.txt.</summary>
    public IReadOnlyList<string> ExtraSystemLines { get; set; } = Array.Empty<string>();
}

/// <summary>
/// "Report a problem": one zip with what a developer needs to understand a problem — system information, the log, the crash
/// reports and the settings. Built only when the user asks, saved where the user chooses; the program sends nothing. Plain
/// text files, so the user can see exactly what they would be handing over.
/// </summary>
public static class ProblemReport
{
    public const string ReadmeName = "README.txt", SystemName = "system.txt", LogName = "log.txt", SettingsName = "settings.xml", CrashFolder = "crash reports/";

    /// <summary>Writes the report to <paramref name="zipPath"/> and returns the names of the files in it.</summary>
    public static IReadOnlyList<string> Build(string zipPath, ProblemReportOptions options)
    {
        var files = new List<(string Name, string Text)>();
        var crashes = options.IncludeCrashReports ? CrashLog.List() : Array.Empty<FileInfo>();
        files.Add((SystemName, SystemText(includeDrives: true, options.ExtraSystemLines)));
        if (options.IncludeLog) files.Add((LogName, LogText()));
        if (options.IncludeSettings) files.Add((SettingsName, SettingsText(Config.AppConfig.Current)));
        foreach (var f in crashes)
        {
            try { files.Add((CrashFolder + f.Name, File.ReadAllText(f.FullName))); }
            catch (Exception ex) { files.Add((CrashFolder + f.Name, "(could not be read: " + ex.Message + ")")); }
        }
        files.Insert(0, (ReadmeName, Readme(options, files.Select(f => f.Name).ToList(), crashes)));

        var hide = options.HideNames ? NameHider.ForThisUser() : null;
        var tmp = zipPath + ".part";
        using (var fs = File.Create(tmp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in files)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                w.Write(hide == null ? text : hide.Apply(text));
            }
        }
        File.Move(tmp, zipPath, overwrite: true);
        return files.Select(f => f.Name).ToList();
    }

    /// <summary>A file name for the report: ByteScape-problem-report-20260926-1432.zip.</summary>
    public static string SuggestedFileName(DateTime now) => $"{Branding.Name}-problem-report-{now:yyyyMMdd-HHmm}.zip";

    private static string Readme(ProblemReportOptions o, IReadOnlyList<string> names, IReadOnlyList<FileInfo> crashes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Branding.Name} {Branding.Version} problem report, created {DateTime.Now:yyyy-MM-dd HH:mm} by the user.");
        sb.AppendLine();
        if (o.Description.Trim().Length > 0)
        {
            sb.AppendLine("What happened (in the user's words):");
            sb.AppendLine(o.Description.Trim());
            sb.AppendLine();
        }
        sb.AppendLine("Contents:");
        foreach (var n in names)
        {
            string what = n switch
            {
                SystemName => "Windows version, .NET, processor and memory, drives (type, file system, size, free space)",
                LogName => "the program's log of this session",
                SettingsName => "the settings, without the API key and without the recently opened folders",
                _ => "",
            };
            if (what.Length > 0) sb.AppendLine($"  {n,-13} {what}");
        }
        if (crashes.Count > 0)
            sb.AppendLine($"  {CrashFolder,-13} {crashes.Count} error report(s), {crashes.Count(CrashLog.IsFatal)} of them ended the program");
        sb.AppendLine();
        sb.AppendLine(o.HideNames ? "The Windows user name and the computer name have been replaced by <user> and <computer>."
                                  : "The Windows user name and the computer name were left as they are.");
        sb.AppendLine("File and folder names from the log and the error reports are included as they are. No file contents are included.");
        sb.AppendLine(Branding.Name + " did not send this file anywhere: it was saved where the user chose.");
        return sb.ToString();
    }

    public static string LogText()
    {
        var log = Log.Snapshot();
        if (log.Count == 0) return "(empty)\r\n";
        var sb = new StringBuilder();
        foreach (var e in log) sb.Append(e.Time.ToString("yyyy-MM-dd ")).AppendLine(e.ToString());
        return sb.ToString();
    }

    /// <summary>The settings as saved, minus what is private: the API key and the recently opened folders.</summary>
    public static string SettingsText(Config.AppConfig current)
    {
        var c = current.Clone();
        c.AiApiKey = "";
        c.RecentPaths = new List<string>();
        return Config.AppConfig.ToXml(c).ToString();
    }

    // ------------------------------------------------------------------ system

    /// <summary>The small portable exe runs on the .NET installed on the PC (its runtime lives in a shared folder); the standalone one carries its own.</summary>
    internal static bool UsesInstalledDotNet()
    {
        try
        {
            var dir = RuntimeEnvironment.GetRuntimeDirectory();
            return dir.Contains(@"\shared\Microsoft.NETCore.App\", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static string SystemText(bool includeDrives, IReadOnlyList<string>? extra = null)
    {
        var sb = new StringBuilder();
        void Line(string name, string value) => sb.AppendLine($"{name,-12}: {value}");
        Line("Program", $"{Branding.Name} {Branding.Version} ({(Config.AppConfig.IsPackaged() ? "Store package" : UsesInstalledDotNet() ? "portable exe using the installed .NET" : "portable exe, .NET included")}, {RuntimeInformation.ProcessArchitecture})");
        Line("Windows", WindowsVersion());
        Line(".NET", RuntimeInformation.FrameworkDescription);
        Line("Processors", Environment.ProcessorCount.ToString());
        try { Line("Memory", Format.Size(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes) + " available to the program"); } catch { }
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            Line("Using", $"{Format.Size(p.WorkingSet64)} working set, {Format.Size(GC.GetTotalMemory(false))} managed, {p.HandleCount} handles, {p.Threads.Count} threads");
        }
        catch { }
        Line("Rights", Environment.IsPrivilegedProcess ? "administrator" : "standard (not elevated)");
        Line("Language", $"{System.Globalization.CultureInfo.CurrentCulture.Name} (formats), {System.Globalization.CultureInfo.CurrentUICulture.Name} (Windows display)");
        foreach (var l in extra ?? Array.Empty<string>()) sb.AppendLine(l);
        if (includeDrives)
        {
            sb.AppendLine();
            sb.AppendLine("Drives:");
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    string kind = d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable
                        ? Scanning.ScanStrategy.KindName(Scanning.DriveProfile.Detect(d.Name).Kind) : d.DriveType.ToString();
                    sb.AppendLine(d.IsReady
                        ? $"  {d.Name,-4} {kind,-18} {d.DriveFormat,-6} {Format.Size(d.TotalSize),10} total, {Format.Size(d.AvailableFreeSpace),10} free"
                        : $"  {d.Name,-4} {kind,-18} (not ready)");
                }
                catch (Exception ex) { sb.AppendLine($"  {d.Name,-4} ({ex.Message})"); }
            }
        }
        return sb.ToString();
    }

    /// <summary>"Windows 11 Pro 23H2 (build 22631.4037)": the registry's product name still says Windows 10 on 11, the build tells.</summary>
    public static string WindowsVersion()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string product = k?.GetValue("ProductName") as string ?? "Windows";
            string display = k?.GetValue("DisplayVersion") as string ?? k?.GetValue("ReleaseId") as string ?? "";
            int.TryParse(k?.GetValue("CurrentBuild") as string, out int build);
            var ubr = k?.GetValue("UBR");
            if (build >= 22000) product = product.Replace("Windows 10", "Windows 11");
            return $"{product} {display} (build {build}{(ubr != null ? "." + ubr : "")})".Replace("  ", " ");
        }
        catch { return RuntimeInformation.OSDescription; }
    }
}

/// <summary>Replaces the Windows user name and the computer name (and the profile folder) in report text.</summary>
public sealed class NameHider
{
    private readonly List<(Regex Pattern, string Replacement)> _rules = new();

    public NameHider(string userName, string computerName, string profileFolder)
    {
        // the profile folder first (its last part may differ from the user name, e.g. "john.smith" for "john")
        profileFolder = profileFolder.TrimEnd('\\');
        if (profileFolder.Length > 3 && Path.GetDirectoryName(profileFolder) is { } parent)
            _rules.Add((new Regex(Regex.Escape(profileFolder) + @"(?![\p{L}\p{N}_.-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), Path.Combine(parent, "<user>")));
        // whole words only, so that a short name does not eat parts of other words; very short names are left alone
        if (userName.Length >= 3) _rules.Add((Word(userName), "<user>"));
        if (computerName.Length >= 3) _rules.Add((Word(computerName), "<computer>"));
    }

    public static NameHider ForThisUser() => new(Environment.UserName, Environment.MachineName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private static Regex Word(string s) => new(@"(?<![\p{L}\p{N}_])" + Regex.Escape(s) + @"(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string Apply(string text)
    {
        foreach (var (p, r) in _rules) text = p.Replace(text, r.Replace("$", "$$"));
        return text;
    }
}
