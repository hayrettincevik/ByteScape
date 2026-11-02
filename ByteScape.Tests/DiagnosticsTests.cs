using System.IO.Compression;
using ByteScape.Core.Config;
using ByteScape.Core.Util;
using Xunit;

namespace ByteScape.Tests;

/// <summary>Crash reports and the "Report a problem" zip. Everything in a test folder (<see cref="TestSetup.CrashReports"/>).</summary>
[Collection("config")]
public class DiagnosticsTests
{
    private static void Clean()
    {
        try { if (Directory.Exists(TestSetup.CrashReports)) Directory.Delete(TestSetup.CrashReports, true); } catch { }
        CrashLog.ResetRunCount();
    }

    private static Exception Thrown()
    {
        try { Outer(); }
        catch (Exception ex) { return ex; }
        throw new InvalidOperationException("unreachable");
    }

    private static void Outer()
    {
        try { Inner(); }
        catch (Exception ex) { throw new InvalidOperationException("outer failure", ex); }
    }

    private static void Inner() => throw new FileNotFoundException("inner failure", "missing.dat");

    [Fact]
    public void CrashLog_WritesListsPrunesAndRemembersWhatWasOffered()
    {
        Clean();
        try
        {
            Log.Error("line before the crash");
            var path = CrashLog.Write(Thrown(), "unit test", fatal: true);
            Assert.NotNull(path);
            Assert.StartsWith(CrashLog.FatalPrefix, Path.GetFileName(path));
            var text = File.ReadAllText(path!);
            Assert.Contains("stopped because of an error", text);
            Assert.Contains("unit test", text);
            Assert.Contains("outer failure", text);
            Assert.Contains("inner failure", text);                 // the inner exception with its stack
            Assert.Contains(nameof(Inner), text);
            Assert.Contains("Windows", text);                       // system lines
            Assert.Contains("line before the crash", text);         // the last log lines

            var error = CrashLog.Write(new InvalidDataException("went on"), "ui", fatal: false);
            Assert.StartsWith(CrashLog.ErrorPrefix, Path.GetFileName(error));
            Assert.Equal(2, CrashLog.List().Count);

            // only the one that ended the program is offered on the next start, and only once
            Assert.Single(CrashLog.UnseenCrashes());
            CrashLog.MarkSeen();
            Assert.Empty(CrashLog.UnseenCrashes());
            Thread.Sleep(30);
            CrashLog.Write(Thrown(), "again", fatal: true);
            Assert.Single(CrashLog.UnseenCrashes());

            // an error in a loop writes at most MaxPerRun reports per run; the folder keeps the newest Keep
            CrashLog.ResetRunCount();
            int written = 0;
            for (int i = 0; i < CrashLog.MaxPerRun + 5; i++) if (CrashLog.Write(new Exception("loop " + i), "loop", false) != null) written++;
            Assert.Equal(CrashLog.MaxPerRun, written);
            CrashLog.ResetRunCount();
            for (int i = 0; i < CrashLog.MaxPerRun; i++) CrashLog.Write(new Exception("more " + i), "loop", false);
            var all = CrashLog.List();
            Assert.Equal(CrashLog.Keep, all.Count);
            Assert.Contains("more 9", File.ReadAllText(all[0].FullName)); // newest first

            CrashLog.DeleteAll();
            Assert.Empty(CrashLog.List());
        }
        finally { Clean(); }
    }

    [Fact]
    public void CrashLog_Hooks_BackgroundThreadAndForgottenTask()
    {
        Clean();
        try
        {
            CrashLog.OnUnhandled(null, new UnhandledExceptionEventArgs(new InvalidOperationException("thread died"), isTerminating: true));
            var agg = new AggregateException(new TimeoutException("task one"), new ArgumentException("task two"));
            var args = new UnobservedTaskExceptionEventArgs(agg);
            CrashLog.OnUnobservedTask(null, args);
            Assert.True(args.Observed);                              // does not end the program
            var reports = CrashLog.List();
            Assert.Equal(2, reports.Count);
            Assert.Single(reports, CrashLog.IsFatal);
            var taskReport = File.ReadAllText(reports.Single(r => !CrashLog.IsFatal(r)).FullName);
            Assert.Contains("task one", taskReport);
            Assert.Contains("task two", taskReport);
        }
        finally { Clean(); }
    }

    [Fact]
    public void IssueLink_StaysShortEnough_ForTheBrowser()
    {
        var system = "Program     : ByteScape 1.0.0\r\nWindows     : Windows 10\r\n.NET        : 8\r\nProcessors  : 4\r\n";
        var shortOne = Wpf.Windows.ProblemReportWindow.IssueUri("It stopped.\r\nTwice.", system, @"C:\Desk\report.zip");
        Assert.StartsWith(Branding.IssueUrl + "?title=" + Uri.EscapeDataString(Branding.Name + " " + Branding.Version + " problem report") + "&body=", shortOne);
        var body = Uri.UnescapeDataString(shortOne.Substring(shortOne.IndexOf("&body=", StringComparison.Ordinal) + 6));
        Assert.StartsWith("It stopped.\nTwice.\n\n```\nProgram", body);                // the system lines as a code block
        Assert.DoesNotContain("Processors", body);                                           // the first three lines only
        Assert.DoesNotContain(@"C:\Desk", body);                                             // the file name, never the folder
        Assert.Contains("report.zip", body);
        var longOne = Wpf.Windows.ProblemReportWindow.IssueUri(string.Concat(Enumerable.Repeat("ğüşöç long text ", 400)), system, @"C:\Desk\report.zip");
        Assert.InRange(longOne.Length, 2000, Wpf.Windows.ProblemReportWindow.MaxIssueUri);
        body = Uri.UnescapeDataString(longOne.Substring(longOne.IndexOf("&body=", StringComparison.Ordinal) + 6));
        Assert.Contains("…\n\n```\nProgram", body);                                     // shortened, the rest is in README.txt
        Assert.Contains("report.zip", body);
    }

    [Fact]
    public void NameHider_ReplacesUserAndComputerAsWholeWords()
    {
        var h = new NameHider("john", "DESKTOP-AB12", @"D:\Users\john.smith");
        Assert.Equal(@"D:\Users\<user>\Documents\a.txt", h.Apply(@"d:\users\JOHN.SMITH\Documents\a.txt"));
        Assert.Equal(@"<user>'s files on <computer>", h.Apply(@"john's files on desktop-ab12"));
        Assert.Equal("johnny johnson", h.Apply("johnny johnson"));              // other words stay
        // the profile folder matches as a whole folder name: a longer name that starts the same stays
        Assert.Equal(@"D:\Users\jsmithers\x", new NameHider("jdoe", "PC-1234", @"D:\Users\jsmith").Apply(@"D:\Users\jsmithers\x"));
        Assert.Equal(@"D:\Users\<user>\x", new NameHider("jdoe", "PC-1234", @"D:\Users\jsmith").Apply(@"D:\Users\jsmith\x"));
        Assert.Equal(@"C:\Users\<user>\x", h.Apply(@"C:\Users\john\x"));
        Assert.Equal("al and bo", new NameHider("al", "bo", "").Apply("al and bo")); // too short to replace safely
        Assert.Equal("cost $5 <user>", h.Apply("cost $5 john"));
    }

    [Fact]
    public void ProblemReport_ZipHasWhatItSays_AndNothingPrivate()
    {
        Clean();
        var zipPath = Path.Combine(Path.GetTempPath(), "bytescape-tests", "problem-report.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        try
        {
            string user = Environment.UserName, profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Log.Error($"Could not read {Path.Combine(profile, "Documents", "tax.pdf")} on {Environment.MachineName}");
            CrashLog.Write(Thrown(), "report test", fatal: true);

            var files = ProblemReport.Build(zipPath, new ProblemReportOptions { Description = "It froze when I pressed Delete.", ExtraSystemLines = new[] { "Screen      : 1920 x 1080" } });
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var names = zip.Entries.Select(e => e.FullName).ToList();
                Assert.Equal(files, names);
                Assert.Equal(ProblemReport.ReadmeName, names[0]);
                Assert.Contains(ProblemReport.SystemName, names);
                Assert.Contains(ProblemReport.LogName, names);
                Assert.Contains(ProblemReport.SettingsName, names);
                Assert.Single(names, n => n.StartsWith(ProblemReport.CrashFolder));
                string Read(string n) { using var r = new StreamReader(zip.GetEntry(n)!.Open()); return r.ReadToEnd(); }
                var all = string.Concat(names.Select(Read));
                Assert.Contains("It froze when I pressed Delete.", Read(ProblemReport.ReadmeName));
                Assert.Contains("1 error report(s), 1 of them ended the program", Read(ProblemReport.ReadmeName));
                Assert.Contains("Screen      : 1920 x 1080", Read(ProblemReport.SystemName));
                Assert.Contains("Drives:", Read(ProblemReport.SystemName));
                Assert.Contains(@"<user>\Documents\tax.pdf", Read(ProblemReport.LogName));
                Assert.Contains("outer failure", all);
                Assert.DoesNotContain(profile, all, StringComparison.OrdinalIgnoreCase);
                if (user.Length >= 3) Assert.DoesNotMatch(@"(?i)(?<![\p{L}\p{N}_])" + System.Text.RegularExpressions.Regex.Escape(user) + @"(?![\p{L}\p{N}_])", all);
                if (Environment.MachineName.Length >= 3) Assert.DoesNotContain(Environment.MachineName, all, StringComparison.OrdinalIgnoreCase);
            }

            // without hiding and without the optional parts
            ProblemReport.Build(zipPath, new ProblemReportOptions { HideNames = false, IncludeLog = false, IncludeSettings = false, IncludeCrashReports = false });
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                Assert.Equal(new[] { ProblemReport.ReadmeName, ProblemReport.SystemName }, zip.Entries.Select(e => e.FullName));
                using var r = new StreamReader(zip.GetEntry(ProblemReport.ReadmeName)!.Open());
                Assert.Contains("were left as they are", r.ReadToEnd());
            }
            Assert.False(File.Exists(zipPath + ".part"));
        }
        finally { Clean(); File.Delete(zipPath); }
    }

    [Fact]
    public void ProblemReport_SettingsLeaveOutKeyAndRecentFolders()
    {
        var c = new AppConfig { AiApiKey = "sk-secret-123", AiEndpoint = "http://box:1234/v1", FontSize = 15 };
        c.RecentPaths.Add(@"C:\Private\Taxes");
        var xml = ProblemReport.SettingsText(c);
        Assert.DoesNotContain("Taxes", xml);
        Assert.DoesNotContain("protection", xml);                   // no key, not even encrypted
        Assert.Contains("http://box:1234/v1", xml);
        Assert.Contains("<fontSize>15</fontSize>", xml);
        Assert.Equal("sk-secret-123", c.AiApiKey);                  // the live settings are untouched
        Assert.Single(c.RecentPaths);
        var parsed = AppConfig.FromXml(System.Xml.Linq.XElement.Parse(xml));
        Assert.Equal(15, parsed.FontSize);
    }

    [Fact]
    public void ProblemReport_SystemText_NamesWindowsAndDrives()
    {
        var text = ProblemReport.SystemText(includeDrives: true);
        Assert.Contains("Windows 1", text);
        Assert.Contains("build ", ProblemReport.WindowsVersion());
        Assert.Contains(".NET 8", text);
        Assert.Contains(@"C:\", text);
        Assert.DoesNotContain("Drives:", ProblemReport.SystemText(includeDrives: false));
        Assert.EndsWith(".zip", ProblemReport.SuggestedFileName(new DateTime(2026, 9, 26, 14, 32, 0)));
        Assert.Contains("20260926-1432", ProblemReport.SuggestedFileName(new DateTime(2026, 9, 26, 14, 32, 0)));
    }
}
