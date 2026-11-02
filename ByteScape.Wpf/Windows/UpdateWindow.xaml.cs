using System.Windows;
using ByteScape.Core.Config;
using ByteScape.Core.Update;
using ByteScape.Core.Util;

namespace ByteScape.Wpf.Windows;

/// <summary>A newer version: what it brings, and its download page (opened in the browser — the program downloads and runs nothing).</summary>
public partial class UpdateWindow : Window
{
    internal UpdateInfo Info { get; }

    /// <summary>Tests: gets the address instead of the browser.</summary>
    internal static Action<string>? OpenHook;

    internal UpdateWindow(UpdateInfo info)
    {
        InitializeComponent();
        Localizer.Apply(this);
        Info = info;
        HeadLine.Text = F($"{Branding.Name} {info.Version} is available");
        SubLine.Text = F($"You have version {Branding.Version}.")
            + (info.Published is { } p ? " " + F($"Released on {Format.Date(p)}.") : "")
            + " " + T("The download page opens in your browser; the program itself downloads and installs nothing.");
        bool notes = info.Notes.Length > 0;
        NotesHead.Visibility = NotesBox.Visibility = notes ? Visibility.Visible : Visibility.Collapsed;
        NotesBox.Text = info.Notes.Replace("\n", Environment.NewLine);
        ShaText.Text = info.Sha256.Length > 0 ? F($"SHA-256 of the new program file: {info.Sha256} (compare it with the file you download: Get-FileHash in PowerShell).") : "";
        ShaText.Visibility = info.Sha256.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        // the description was checked when read; checked again right before anything is opened
        if (UpdateCheck.IsSafeLink(Info.DownloadUrl, allowLoopbackHttp: false)) (OpenHook ?? MainWindow.OpenUrl)(Info.DownloadUrl);
        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        AppConfig.Current.SkippedVersion = Info.Version;
        AppConfig.Save();
        Close();
    }
}
