// Crash reports end to end: a child process installs the same hooks as the app and really dies of an error on a background
// thread (and, separately, leaves a failed task nobody waits for); the parent checks the reports it left. Test folder only.
using System.Diagnostics;
using System.IO;
using ByteScape.Core.Util;

namespace ByteScape.Diag;

internal static partial class Program
{
    private static void CrashTest(string arg)
    {
        if (arg.StartsWith("child:", StringComparison.Ordinal))
        {
            var parts = arg.Split(':', 3);                   // child:<mode>:<folder>
            CrashLog.DirOverride = parts[2];
            CrashLog.Install();
            Log.Error("child is about to fail (" + parts[1] + ")");
            if (parts[1] == "thread")
            {
                var t = new Thread(() => throw new InvalidOperationException("diag background thread failure")) { IsBackground = true };
                t.Start();
                t.Join();
                Thread.Sleep(5000);                          // not reached: the failure ends the process
                Environment.Exit(3);
            }
            else
            {
                FailedTaskNobodyWaitsFor();
                for (int i = 0; i < 5; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(100); }
                Environment.Exit(0);                          // an unobserved task exception does not end the program
            }
            return;
        }

        Out("== CRASHTEST");
        var dir = Path.Combine(Path.GetTempPath(), "bytescape-crashtest");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        var exe = Environment.ProcessPath!;
        int Run(string mode)
        {
            var psi = new ProcessStartInfo(exe, $"crashtest child:{mode}:{dir}") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            psi.Environment["DIAG_OUT"] = dir;
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            p.WaitForExit(60000);
            return p.ExitCode;
        }
        CrashLog.DirOverride = dir;

        int code = Run("thread");
        var reports = CrashLog.List();
        var fatal = reports.FirstOrDefault(CrashLog.IsFatal);
        string text = fatal == null ? "" : File.ReadAllText(fatal.FullName);
        Out($"   background thread failure: exit code 0x{code:X8} (the process ended: {code != 0}); crash report written: {fatal != null}");
        Out($"     report has the error: {text.Contains("diag background thread failure")}, the stack: {text.Contains(" at ")}, the system: {text.Contains("Windows")}, the log line: {text.Contains("child is about to fail")}");
        Out($"     offered at the next start: {CrashLog.UnseenCrashes().Count == 1}");
        bool okThread = code != 0 && fatal != null && text.Contains("diag background thread failure") && text.Contains("child is about to fail");

        code = Run("task");
        var error = CrashLog.List().FirstOrDefault(f => !CrashLog.IsFatal(f));
        string etext = error == null ? "" : File.ReadAllText(error.FullName);
        Out($"   forgotten failed task: exit code {code} (the program went on: {code == 0}); error report written: {error != null}, has the error: {etext.Contains("diag task failure")}");
        bool okTask = code == 0 && etext.Contains("diag task failure");

        var zip = Path.Combine(dir, "report.zip");
        var names = ProblemReport.Build(zip, new ProblemReportOptions());
        Out($"   problem report of these: {names.Count} files ({string.Join(", ", names)}), {new FileInfo(zip).Length / 1024.0:0.0} KB");
        Out($"   RESULT: {(okThread && okTask && names.Count(n => n.StartsWith(ProblemReport.CrashFolder)) == 2 ? "OK" : "PROBLEM")}");
        CrashLog.DirOverride = null;
        try { Directory.Delete(dir, true); } catch { }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void FailedTaskNobodyWaitsFor() => Task.Run(() => throw new TimeoutException("diag task failure"));
}
