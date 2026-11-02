using System.Windows;

namespace ByteScape.Wpf.Windows;

public partial class HelpWindow : Window
{
    public enum Topic { Filtering, Tagging, CommandLine, License, ThirdPartyNotices }

    public HelpWindow(Topic topic)
    {
        InitializeComponent();
        Localizer.Apply(this);
        (Title, Heading.Text, Body.Text) = topic switch
        {
            Topic.Filtering => (T("How to filter"), T("Filters"), T(HelpTexts.Filtering)),
            Topic.Tagging => (T("How to use colour tags"), T("Colour tags"), T(HelpTexts.Tagging)),
            // legal texts are shown as written (English), never through the catalogs
            Topic.License => (T("License terms"), T("License terms"), Core.LegalTexts.LicenseTerms()),
            Topic.ThirdPartyNotices => (T("Third-party notices"), T("Third-party notices"), Core.LegalTexts.ThirdPartyNotices()),
            _ => (T("Command line options"), T("Command line"), T(HelpTexts.CommandLine)),
        };
        // the legal texts are prose: wrapped to the window instead of cut off at its edge
        if (topic is Topic.License or Topic.ThirdPartyNotices) Body.TextWrapping = TextWrapping.Wrap;
    }
}
