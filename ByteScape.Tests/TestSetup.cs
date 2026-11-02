using System.Runtime.CompilerServices;

namespace ByteScape.Tests;

internal static class TestSetup
{
    /// <summary>Scans kept when test windows close (unfinished, or complete for quick rescans): a test folder emptied at start, never next to the configuration.</summary>
    internal static readonly string KeptScans = Path.Combine(Path.GetTempPath(), "bytescape-tests", "kept");

#pragma warning disable CA2255   // the test assembly's own setup
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init()
    {
        try { if (Directory.Exists(KeptScans)) Directory.Delete(KeptScans, true); } catch { }
        Core.Scanning.ScanResume.DirOverride = KeptScans;
        try { if (Directory.Exists(CrashReports)) Directory.Delete(CrashReports, true); } catch { }
        Core.Util.CrashLog.DirOverride = CrashReports;
    }

    /// <summary>Error reports written by tests: never into "Crash reports" next to the configuration.</summary>
    internal static readonly string CrashReports = Path.Combine(Path.GetTempPath(), "bytescape-tests", "crash-reports");
}
