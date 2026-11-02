using System.Diagnostics;
using System.Text;

namespace ByteScape.Core.Util;

/// <summary>
/// A text file for every error nothing else caught: what happened (the full exception), where, on which system and the last
/// log lines. The files stay on this computer ("Crash reports" next to the configuration); nothing is sent anywhere.
/// <see cref="ProblemReport"/> packs them, when the user asks, into a zip the user can look into and send themselves.
/// </summary>
public static class CrashLog
{
    /// <summary>Tests: a folder of their own.</summary>
    internal static string? DirOverride;
    public static string Dir => DirOverride ?? Path.Combine(Path.GetDirectoryName(Config.AppConfig.ConfigPath)!, "Crash reports");

    /// <summary>Reports kept; older ones are deleted when a new one is written.</summary>
    public const int Keep = 20;
    /// <summary>An error repeating in a loop (e.g. on every frame) writes this many reports per run, not thousands.</summary>
    public const int MaxPerRun = 10;

    public const string FatalPrefix = "crash-", ErrorPrefix = "error-";
    private const string SeenMarker = ".seen";

    private static int _written;
    internal static void ResetRunCount() => _written = 0;

    /// <summary>Writes a report; null when it could not be written or the run already wrote <see cref="MaxPerRun"/>. Never throws.</summary>
    public static string? Write(Exception ex, string source, bool fatal)
    {
        try
        {
            if (Interlocked.Increment(ref _written) > MaxPerRun) return null;
            var now = DateTime.Now;
            Directory.CreateDirectory(Dir);
            var path = Path.Combine(Dir, $"{(fatal ? FatalPrefix : ErrorPrefix)}{now:yyyyMMdd-HHmmss-fff}.txt");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(Dir, $"{(fatal ? FatalPrefix : ErrorPrefix)}{now:yyyyMMdd-HHmmss-fff}-{i}.txt");
            File.WriteAllText(path, Format(ex, source, fatal, now), new UTF8Encoding(false));
            Prune();
            return path;
        }
        catch { return null; } // a crash report must never be the next crash
    }

    public static string Format(Exception ex, string source, bool fatal, DateTime when)
    {
        var sb = new StringBuilder();
        sb.AppendLine(fatal ? $"{Branding.Name} stopped because of an error." : $"{Branding.Name} caught an unexpected error and went on.");
        sb.AppendLine($"Time        : {when:yyyy-MM-dd HH:mm:ss.fff} (UTC{when:zzz})");
        sb.AppendLine($"Where       : {source}");
        try { sb.AppendLine($"Running for : {Format(DateTime.Now - Process.GetCurrentProcess().StartTime)}"); } catch { }
        sb.AppendLine();
        try { sb.Append(ProblemReport.SystemText(includeDrives: false)); } catch (Exception e) { sb.AppendLine("(system information unavailable: " + e.Message + ")"); }
        sb.AppendLine();
        sb.AppendLine("Error:");
        sb.AppendLine(ex.ToString());
        if (ex is AggregateException agg)
            foreach (var inner in agg.Flatten().InnerExceptions.Skip(1).Take(5))
                sb.AppendLine().AppendLine("Also:").AppendLine(inner.ToString());
        var log = Log.Snapshot();
        sb.AppendLine();
        sb.AppendLine($"Last log entries ({Math.Min(50, log.Count)} of {log.Count}):");
        foreach (var e in log.Skip(Math.Max(0, log.Count - 50))) sb.AppendLine(e.ToString());
        return sb.ToString();
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{t.Minutes} min {t.Seconds} s" : $"{t.TotalSeconds:0.0} s";

    /// <summary>Every report, newest first.</summary>
    public static IReadOnlyList<FileInfo> List()
    {
        try
        {
            var d = new DirectoryInfo(Dir);
            if (!d.Exists) return Array.Empty<FileInfo>();
            return d.GetFiles("*.txt").Where(IsReport).OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).ToList();
        }
        catch { return Array.Empty<FileInfo>(); }
    }

    public static bool IsFatal(FileInfo f) => f.Name.StartsWith(FatalPrefix, StringComparison.OrdinalIgnoreCase);
    private static bool IsReport(FileInfo f) => IsFatal(f) || f.Name.StartsWith(ErrorPrefix, StringComparison.OrdinalIgnoreCase);

    public static void Prune()
    {
        // not as administrator: the folder sits where the user can swap it for a link to a folder the user may not touch
        if (Cleanup.AccessProbe.IsElevated) return;
        foreach (var f in List().Skip(Keep))
            try { f.Delete(); } catch { }
    }

    /// <summary>Times the program stopped because of an error that nobody has been told about yet (offered on the next start).</summary>
    public static IReadOnlyList<FileInfo> UnseenCrashes()
    {
        DateTime seen = DateTime.MinValue;
        try { var m = new FileInfo(Path.Combine(Dir, SeenMarker)); if (m.Exists) seen = m.LastWriteTimeUtc; } catch { }
        return List().Where(f => IsFatal(f) && f.LastWriteTimeUtc > seen).ToList();
    }

    public static void MarkSeen()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var m = Path.Combine(Dir, SeenMarker);
            File.WriteAllText(m, "");
            File.SetLastWriteTimeUtc(m, DateTime.UtcNow);
        }
        catch { }
    }

    public static void DeleteAll()
    {
        foreach (var f in List())
            try { f.Delete(); } catch { }
    }

    // ------------------------------------------------------------------ hooks

    /// <summary>Errors of any thread that end the process, and exceptions of tasks nobody waited for (these do not).</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
    }

    internal static void OnUnhandled(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) Write(ex, "background thread (the program ended)", fatal: e.IsTerminating);
    }

    internal static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Write(e.Exception, "background task", fatal: false);
        Log.Error("Unexpected error in a background task: " + e.Exception.InnerException?.Message);
        e.SetObserved();
    }
}
