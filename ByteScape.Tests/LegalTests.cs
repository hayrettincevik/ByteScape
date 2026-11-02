using System.Text.RegularExpressions;
using System.Windows;
using ByteScape.Core;
using ByteScape.Wpf.Windows;
using Xunit;

namespace ByteScape.Tests;

/// <summary>ByteScape is free and open source (MIT): the licence and third-party notices it ships and shows.</summary>
public class LegalTests
{
    private static readonly string[] Tokens = { "{Name}", "{Year}", "{Publisher}" };

    /// <summary>The program shows the repository's LICENSE file itself: the MIT licence naming the copyright holder.</summary>
    [Fact]
    public void LicenseTerms_AreTheRepositoryLicense()
    {
        var text = LegalTexts.LicenseTerms();
        Assert.StartsWith("MIT License", text);
        Assert.Contains("Copyright (c) " + Branding.CopyrightYear + " " + Branding.Publisher, text);
        Assert.Contains("Permission is hereby granted, free of charge", text);
        Assert.Contains("THE SOFTWARE IS PROVIDED \"AS IS\"", text);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "ByteScape.sln"))) root = root.Parent;
        Assert.NotNull(root);
        Assert.Equal(File.ReadAllText(Path.Combine(root!.FullName, "LICENSE")).Replace("\r\n", "\n"), text.Replace("\r\n", "\n"));
        // no line wider than the help window shows
        Assert.All(text.Split('\n'), l => Assert.True(l.TrimEnd('\r').Length <= 86, l));
    }

    /// <summary>No person's name or address in the program: the copyright holder is the project.</summary>
    [Fact]
    public void Publisher_IsTheProject()
    {
        Assert.Equal(Branding.Name + " contributors", Branding.Publisher);
        Assert.Equal(Branding.Publisher, LegalTexts.Publisher("en"));
        Assert.Equal(Branding.Publisher, LegalTexts.Publisher("tr"));
        foreach (var url in new[] { Branding.HomeUrl, Branding.UpdateUrl, Branding.IssueUrl })
            Assert.StartsWith("https://", url);
        Assert.StartsWith(Branding.HomeUrl + "/", Branding.IssueUrl);
    }

    /// <summary>The standalone exe carries .NET and WPF (MIT) and the tiling follows d3-hierarchy (ISC): their notices ship with it.</summary>
    [Fact]
    public void ThirdPartyNotices_CoverWhatTheExeCarries()
    {
        var n = LegalTexts.ThirdPartyNotices();
        foreach (var token in Tokens) Assert.DoesNotContain(token, n);
        Assert.Contains("Copyright (c) .NET Foundation and Contributors", n);
        Assert.True(Regex.Matches(n, Regex.Escape("The MIT License (MIT)")).Count >= 2);   // .NET runtime and WPF
        Assert.Contains("Copyright 2010-2021 Mike Bostock", n);
        Assert.Contains("License notice for", n);                              // the runtime's own notices, verbatim
        Assert.Contains("https://github.com/dotnet/wpf", n);
        Assert.True(n.Length > 50_000);
    }

    /// <summary>The About window says it is free and open source and opens both texts; the donation link stays hidden without an address.</summary>
    [Fact]
    public void AboutWindow_ShowsTheLicenseLinks() => WpfHost.Run(() =>
    {
        var w = new AboutWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false };
        w.Show();
        WpfHost.Pump();
        try
        {
            Assert.Equal(Visibility.Visible, w.LicenseBtn.Visibility);
            Assert.Equal(Visibility.Visible, w.NoticesBtn.Visibility);
            Assert.Equal(Branding.DonateUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed, w.DonateBtn.Visibility);
            Assert.Contains(UiTexts.Collect(w), t => t.StartsWith("Free and open source (MIT License)", StringComparison.Ordinal));
            Assert.Equal("© " + Branding.CopyrightYear + " " + Branding.Publisher, w.CopyrightText.Text);
        }
        finally { w.Close(); }
        foreach (var (topic, start) in new[] { (HelpWindow.Topic.License, "MIT License"), (HelpWindow.Topic.ThirdPartyNotices, Branding.Name + " - Third-Party Notices") })
        {
            var h = new HelpWindow(topic) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false };
            h.Show();
            WpfHost.Pump();
            Assert.StartsWith(start, h.Body.Text);
            h.Close();
        }
    });
}
