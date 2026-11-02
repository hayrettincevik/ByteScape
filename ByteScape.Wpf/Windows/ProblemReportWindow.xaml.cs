using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using ByteScape.Core.Util;
using Microsoft.Win32;

namespace ByteScape.Wpf.Windows;

/// <summary>
/// Packs system information, the log, the error reports and the settings into a zip the user saves and sends themselves.
/// Nothing is sent from here: no network use at all.
/// </summary>
public partial class ProblemReportWindow : Window
{
    private readonly IReadOnlyList<FileInfo> _crashes;
    private string _system = "";
    /// <summary>The report saved from this window, attached by "Open an issue" (saved again when the options changed since).</summary>
    private string? _saved;
    private ProblemReportOptions? _savedWith;

    /// <summary>Tests: receives what would be started (Explorer, the browser) instead of starting it.</summary>
    internal static Action<ProcessStartInfo>? StartHook;

    public ProblemReportWindow()
    {
        InitializeComponent();
        Localizer.Apply(this);
        _crashes = CrashLog.List();
        IntroText.Text = F($"Saves a .zip file with what helps to find the cause of a problem. It holds plain text files: open it and look at everything before you pass it on. {Branding.Name} sends nothing itself")
            + (Branding.IssueUrl.Length > 0 ? T(" — attach the file to a new issue on the project page.") : T(" — send the file to the developer yourself."));
        LogOpt.Content = F($"The log of this session ({Log.Snapshot().Count} entries)");
        if (_crashes.Count == 0)
        {
            CrashOpt.Content = T("Error reports (there are none)");
            CrashOpt.IsChecked = false;
            CrashOpt.IsEnabled = false;
        }
        else
        {
            int fatal = _crashes.Count(CrashLog.IsFatal);
            CrashOpt.Content = F($"Error reports ({_crashes.Count}{(fatal > 0 ? F($", {fatal} of them ended the program") : "")}; the latest {_crashes[0].LastWriteTime:g})");
        }
        OpenFolderBtn.IsEnabled = Directory.Exists(CrashLog.Dir);
        if (Branding.IssueUrl.Length > 0)
        {
            // with a project page the issue is the way to go; saving stays for sending it some other way
            IssueBtn.Visibility = Visibility.Visible;
            IssueBtn.IsDefault = true;
            SaveBtn.IsDefault = false;
            SaveBtn.Style = (Style)FindResource(typeof(System.Windows.Controls.Button));
        }
        PreviewBox.Text = T("Collecting…");
        Loaded += async (_, _) =>
        {
            var extra = ScreenLines();
            _system = await Task.Run(() => ProblemReport.SystemText(includeDrives: true, extra)); // a network drive may take a moment to answer
            ShowPreview();
        };
    }

    /// <summary>What only the window knows: the screen, the scaling and high contrast.</summary>
    private List<string> ScreenLines()
    {
        var lines = new List<string>();
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            lines.Add($"{"Screen",-12}: {SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX:0} x {SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY:0} at {dpi.DpiScaleX * 100:0} % scaling"
                + $" (all screens {SystemParameters.VirtualScreenWidth * dpi.DpiScaleX:0} x {SystemParameters.VirtualScreenHeight * dpi.DpiScaleY:0})");
            lines.Add($"{"High contrast",-12}: {(SystemParameters.HighContrast ? "on" : "off")}");
        }
        catch { }
        return lines;
    }

    internal ProblemReportOptions Options() => new()
    {
        IncludeLog = LogOpt.IsChecked == true,
        IncludeCrashReports = CrashOpt.IsChecked == true,
        IncludeSettings = SettingsOpt.IsChecked == true,
        HideNames = HideOpt.IsChecked == true,
        Description = DescriptionBox.Text,
        ExtraSystemLines = ScreenLines(),
    };

    private void ShowPreview()
    {
        if (_system.Length == 0) return;
        PreviewBox.Text = HideOpt.IsChecked == true ? NameHider.ForThisUser().Apply(_system) : _system;
    }

    private void Option_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) ShowPreview(); }

    /// <summary>Writes the report (tests call this without the file dialog).</summary>
    internal async Task<IReadOnlyList<string>> SaveTo(string path)
    {
        var options = Options();
        SaveBtn.IsEnabled = false;
        try
        {
            var files = await Task.Run(() => ProblemReport.Build(path, options));
            CrashLog.MarkSeen();
            _saved = path;
            _savedWith = options;
            ResultText.Text = F($"Saved {Path.GetFileName(path)} ({files.Count} files, {Format.Size(new FileInfo(path).Length)}) in {Path.GetDirectoryName(path)}.");
            ResultText.Foreground = (Brush)FindResource("Text");
            ResultText.Visibility = Visibility.Visible;
            return files;
        }
        finally { SaveBtn.IsEnabled = true; }
    }

    private void Start(ProcessStartInfo psi)
    {
        if (StartHook != null) { StartHook(psi); return; }
        Dialogs.Start(psi);
    }

    private void Reveal(string path)
    {
        try { Start(new ProcessStartInfo(SystemTools.Explorer, "/select,\"" + path + "\"") { UseShellExecute = true }); } catch { }
    }

    private string? AskWhereToSave()
    {
        var dlg = new SaveFileDialog
        {
            Title = T("Save problem report"),
            FileName = ProblemReport.SuggestedFileName(DateTime.Now),
            Filter = T("Zip file (*.zip)|*.zip"),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        return Dialogs.Pick(dlg, this) == true ? dlg.FileName : null;
    }

    private void ShowError(string text)
    {
        ResultText.Text = text;
        ResultText.Foreground = (Brush)FindResource("Error");
        ResultText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The new-issue page for the report (<see cref="Branding.IssueUrl"/>): the title naming the program and its version, the
    /// body the user's description, the first lines of the system information and the file to attach. A link cannot carry
    /// an attachment, so the file is shown in Explorer to drag in. Kept under <see cref="MaxIssueUri"/> characters, which
    /// browsers and the issue page take.
    /// </summary>
    internal static string IssueUri(string description, string system, string zipPath)
    {
        string title = $"{Branding.Name} {Branding.Version} problem report";
        var head = string.Join("\n", system.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).Take(3));
        string attach = F($"Please attach the file {Path.GetFileName(zipPath)} here: drag it from Explorer, where it is shown selected.");   // no folder: it would name the user
        string Build(string text) => Branding.IssueUrl + "?title=" + Uri.EscapeDataString(title) + "&body="
            + Uri.EscapeDataString((text.Length > 0 ? text + "\n\n" : "") + "```\n" + head + "\n```\n\n" + attach);
        var text = description.Trim().Replace("\r\n", "\n");
        var uri = Build(text);
        // a long description is shortened in the issue; the whole text is in the report's README.txt
        while (uri.Length > MaxIssueUri && text.Length > 0)
        {
            text = text.Substring(0, Math.Max(0, text.Length - Math.Max(20, (uri.Length - MaxIssueUri) / 3))).TrimEnd() + "…";
            if (text.Length <= 1) text = "";
            uri = Build(text);
        }
        return uri;
    }

    internal const int MaxIssueUri = 4000;

    /// <summary>Saves the report (or reuses the one saved with the same options), shows it in Explorer and opens the new-issue page.</summary>
    internal async Task OpenIssue(string? path)
    {
        var options = Options();
        if (_saved == null || !File.Exists(_saved) || !Same(options, _savedWith))
        {
            if (path == null) return;
            try { await SaveTo(path); }
            catch (Exception ex) { ShowError(T("The report could not be saved:") + " " + ex.Message); return; }
        }
        var zip = _saved!;
        Reveal(zip);
        string system = HideOpt.IsChecked == true ? NameHider.ForThisUser().Apply(_system) : _system;
        try
        {
            Start(new ProcessStartInfo(IssueUri(options.Description, system, zip)) { UseShellExecute = true });
            ResultText.Text = F($"A new issue opens in your browser: drag {Path.GetFileName(zip)} from Explorer into it and submit it. Everyone can read issues.");
            ResultText.Foreground = (Brush)FindResource("Text");
            ResultText.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            // no browser to open the page with: the address goes to the clipboard
            try { Dialogs.Copy(Branding.IssueUrl); } catch { }
            ShowError(F($"The browser could not be opened. Open {Branding.IssueUrl} and attach {Path.GetFileName(zip)} there; the address is copied to the clipboard."));
        }
    }

    private static bool Same(ProblemReportOptions a, ProblemReportOptions? b) => b != null
        && a.IncludeLog == b.IncludeLog && a.IncludeCrashReports == b.IncludeCrashReports && a.IncludeSettings == b.IncludeSettings
        && a.HideNames == b.HideNames && a.Description == b.Description;

    private async void Issue_Click(object sender, RoutedEventArgs e)
    {
        bool fresh = _saved != null && File.Exists(_saved) && Same(Options(), _savedWith);
        await OpenIssue(fresh ? _saved : AskWhereToSave());
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (AskWhereToSave() is not { } path) return;
        try
        {
            await SaveTo(path);
            Reveal(path);
        }
        catch (Exception ex) { ShowError(T("The report could not be saved:") + " " + ex.Message); }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Dialogs.Start(new ProcessStartInfo(SystemTools.Explorer, "\"" + CrashLog.Dir + "\"") { UseShellExecute = true }); } catch { }
    }
}
