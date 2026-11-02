using System.Windows;

namespace ByteScape.Wpf.Windows;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Localizer.Apply(this);
        Title = F($"About {Branding.Name}");
        NameText.Text = Branding.Name;
        VersionText.Text = F($"Version {Branding.Version}");
        CopyrightText.Text = "© " + Branding.CopyrightYear + " " + Core.LegalTexts.Publisher(Loc.Language);
        SloganText.Text = "“" + T(Branding.Slogan) + "”";
        CreditsText.Text = T(Branding.Description);
        LinkText.Content = Branding.HomeUrl;
        LinkText.Visibility = Branding.HomeUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateBtn.Visibility = Core.Update.UpdateCheck.Available ? Visibility.Visible : Visibility.Collapsed;
        // a donation page only when there is one, and not in the Store package (the Store has its own rules for payments)
        DonateBtn.Content = F($"Support {Branding.Name} with a donation…");
        DonateBtn.Visibility = Branding.DonateUrl.Length > 0 && !Core.Config.AppConfig.IsPackaged() ? Visibility.Visible : Visibility.Collapsed;
    }

    private void License_Click(object sender, RoutedEventArgs e) => Dialogs.ShowModal(new HelpWindow(HelpWindow.Topic.License) { Owner = this });

    private void Notices_Click(object sender, RoutedEventArgs e) => Dialogs.ShowModal(new HelpWindow(HelpWindow.Topic.ThirdPartyNotices) { Owner = this });

    private void Donate_Click(object sender, RoutedEventArgs e) => MainWindow.OpenUrl(Branding.DonateUrl);

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateBtn.IsEnabled = false;
        try { await UpdateUi.CheckNow(this); }
        finally { UpdateBtn.IsEnabled = true; }
    }

    private void Link_Click(object sender, RoutedEventArgs e) => MainWindow.OpenUrl(Branding.HomeUrl);
}
