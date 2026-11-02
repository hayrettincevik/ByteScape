using System.Windows;
using ByteScape.Core.Config;
using ByteScape.Core.Util;

namespace ByteScape.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CrashLog.Install(); // errors that end the program leave a report in "Crash reports" (nothing is sent)
        if (Shell.ShellMenu.IsServerStart(e.Args))
        {
            // started by COM for Explorer's "Open in ByteScape" (Store package): no window; the class opens new ByteScape
            // processes and this one exits once Explorer has not asked anything for a while
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AppConfig.Load();
            UseLanguage(AppConfig.Current.Language);            // the menu entry speaks the user's language
            uint cookie = Shell.ShellMenu.Register();
            var idle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            idle.Tick += (_, _) => { if (cookie == 0 || Shell.ShellMenu.Idle > TimeSpan.FromSeconds(30)) { idle.Stop(); Shell.ShellMenu.Revoke(cookie); Shutdown(); } };
            idle.Start();
            return;
        }
        AppConfig.Load();
        UseLanguage(AppConfig.Current.Language);
        var (requests, autoClose, help) = CommandLine.Parse(e.Args);
        if (help)
        {
            Dialogs.Show(T(HelpTexts.CommandLine), Branding.Name + " - " + T("Command line options"), MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        if (CommandLine.IsMeasure(e.Args))
        {
            // Task Scheduler: one measurement of every drive for the disk forecast, no window
            Core.Forecast.DiskHistory.ForConfig().RecordDrives(DateTime.UtcNow, TimeSpan.FromMinutes(1));
            Shutdown();
            return;
        }
        if (AppConfig.Current.RecordDiskHistory)
        {
            var history = Core.Forecast.DiskHistory.Current = Core.Forecast.DiskHistory.ForConfig();
            System.Threading.Tasks.Task.Run(() => history.RecordDrives(DateTime.UtcNow, TimeSpan.FromHours(1)));
        }
        ThemeManager.Initialize();
        if (!Interop.Native.TryEnableBackupPrivilege())
            Log.Debug("SeBackupPrivilege not available - HINT: Try running " + Branding.Name + " as Administrator");
        DispatcherUnhandledException += (_, ex) =>
        {
            CrashLog.Write(ex.Exception, "user interface (the program went on)", fatal: false);
            Log.Error("Unexpected error: " + ex.Exception.Message);
            ex.Handled = true;
        };
        var main = new MainWindow(requests, autoClose);
        MainWindow = main;
        main.Show();
        if (requests.Count == 0 && !autoClose) OfferCrashReport(main);
        // the weekly update check, only when the user switched it on (nothing is contacted otherwise)
        if (!autoClose && Core.Update.UpdateCheck.IsDue(AppConfig.Current, DateTime.UtcNow))
            main.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => UpdateUi.CheckInBackground(main));
        System.Threading.Tasks.Task.Run(CleanStaleExtractions);
    }

    /// <summary>
    /// The language of every text from here on: the translation catalog, and WPF's own language (formats in bindings, and
    /// the font fallback picks Chinese glyphs for Chinese). Windows already built keep their texts: a change needs a restart.
    /// </summary>
    internal static void UseLanguage(string setting)
    {
        Core.Localization.Loc.SetLanguage(setting);
        var code = Core.Localization.Loc.Language switch { "zh-Hans" => "zh-CN", Core.Localization.Loc.Pseudo => "en-US", var c => c };
        try
        {
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
                new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage(code)));
        }
        catch (ArgumentException) { /* already set once in this process */ }
    }

    /// <summary>After the program stopped because of an error, the next interactive start offers a problem report (once).</summary>
    private static void OfferCrashReport(Window owner)
    {
        var crashes = CrashLog.UnseenCrashes();
        if (crashes.Count == 0) return;
        owner.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            CrashLog.MarkSeen();
            var answer = Dialogs.Show(owner,
                F($"{Branding.Name} stopped because of an error the last time it ran ({crashes[0].LastWriteTime:g}).") + "\n\n"
                + T("Would you like to create a problem report? It is saved as a file you can look into and send to the developer; nothing is sent automatically."),
                Branding.Name, MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes) Dialogs.ShowModal(new Windows.ProblemReportWindow { Owner = owner });
        });
    }

    /// <summary>
    /// The single-file build extracts its native libraries to %TEMP%\.net\ByteScape\&lt;hash&gt;\ and .NET never
    /// removes the folders of previous versions. Delete every sibling of the folder this instance runs from
    /// (a folder still in use by another version simply fails to delete and is left alone).
    /// </summary>
    private static void CleanStaleExtractions()
    {
        // not as administrator: %TEMP% is the user's, a folder swapped for a link would lead the delete elsewhere
        if (Core.Cleanup.AccessProbe.IsElevated) return;
        try
        {
            // AppContext.BaseDirectory is the exe folder; the extraction folder is where the native modules were loaded from
            string? mine = null;
            foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
            {
                var p = m.FileName;
                if (p.Contains(@"\.net\", StringComparison.OrdinalIgnoreCase)) { mine = Path.GetDirectoryName(p)?.TrimEnd('\\'); break; }
            }
            var parent = mine == null ? null : Path.GetDirectoryName(mine);
            if (mine == null || parent == null || !string.Equals(Path.GetFileName(parent), Branding.Name, StringComparison.OrdinalIgnoreCase)) return;
            foreach (var dir in Directory.GetDirectories(parent))
            {
                if (string.Equals(dir.TrimEnd('\\'), mine, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(dir, true); Log.Debug("Removed stale extraction folder " + dir); }
                catch { /* in use by another running version */ }
            }
        }
        catch (Exception ex) { Log.Debug("Extraction cleanup skipped: " + ex.Message); }
    }

}
