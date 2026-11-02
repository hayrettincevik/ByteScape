using System.Net;
using System.Text;
using System.Windows;
using ByteScape.Core.Config;
using ByteScape.Core.Update;
using ByteScape.Wpf;
using ByteScape.Wpf.Windows;
using Xunit;

namespace ByteScape.Tests;

/// <summary>The update check of the portable exe: what it reads, what it sends, when it runs, what it opens.</summary>
[Collection("config")]
public class UpdateTests
{
    /// <summary>Serves one canned answer and records the request (headers included).</summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly HttpListener _l = new();
        public string Body = "{}";
        public int Status = 200;
        public int DelayMs;
        public readonly List<(string Path, WebHeaderCollection Headers)> Requests = new();
        public string Prefix { get; }

        public FakeServer()
        {
            int port = 19400 + Random.Shared.Next(1000);
            Prefix = $"http://127.0.0.1:{port}/";
            _l.Prefixes.Add(Prefix);
            _l.Start();
            _ = Task.Run(async () =>
            {
                while (_l.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _l.GetContextAsync(); } catch { return; }
                    lock (Requests) Requests.Add((ctx.Request.Url!.AbsolutePath, (WebHeaderCollection)ctx.Request.Headers));
                    if (DelayMs > 0) await Task.Delay(DelayMs);
                    try
                    {
                        var bytes = Encoding.UTF8.GetBytes(Body);
                        ctx.Response.StatusCode = Status;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = bytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(bytes);
                        ctx.Response.Close();
                    }
                    catch { }
                }
            });
        }

        public string Url => Prefix + "update.json";
        public void Dispose() { try { _l.Stop(); _l.Close(); } catch { } }
    }

    private const string Newer = """{"version":"9.1.0","url":"https://example.com/bytescape/download","notes":"Faster.\nSafer.","published":"2026-10-01T12:00:00Z","sha256":"cc756f42f1124efc801bcfba902d6ac2e1271beb72c45e0e86782ee8a717ebb7"}""";

    [Fact]
    public void Description_OwnOrGitHub_IsRead_AndUnsafeLinksOrPreReleasesAreNot()
    {
        var own = UpdateCheck.Parse(Newer)!;
        Assert.Equal("9.1.0", own.Version);
        Assert.Equal("https://example.com/bytescape/download", own.DownloadUrl);
        Assert.Equal("Faster.\nSafer.", own.Notes);
        Assert.Equal(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), own.Published);
        Assert.Equal("CC756F42F1124EFC801BCFBA902D6AC2E1271BEB72C45E0E86782EE8A717EBB7", own.Sha256);

        var gh = UpdateCheck.Parse("""{"tag_name":"v2.0.1","html_url":"https://github.com/o/r/releases/tag/v2.0.1","body":"notes","published_at":"2026-10-02T08:00:00Z","prerelease":false,"draft":false,"assets":[]}""")!;
        Assert.Equal("2.0.1", gh.Version);
        Assert.Equal("https://github.com/o/r/releases/tag/v2.0.1", gh.DownloadUrl);
        Assert.Equal("", gh.Sha256);

        // what is never offered: pre-releases, drafts, links the program must not open, broken answers
        foreach (var bad in new[]
        {
            """{"tag_name":"v3.0.0","html_url":"https://github.com/x","prerelease":true}""",
            """{"tag_name":"v3.0.0","html_url":"https://github.com/x","draft":true}""",
            """{"version":"3.0.0-beta","url":"https://example.com"}""",
            """{"version":"3.0.0","url":"http://example.com/plain"}""",
            """{"version":"3.0.0","url":"file:///C:/Windows/System32/calc.exe"}""",
            """{"version":"3.0.0","url":"C:\\Windows\\System32\\calc.exe"}""",
            """{"version":"3.0.0","url":"javascript:alert(1)"}""",
            """{"version":"3.0.0","url":"ms-settings:"}""",
            """{"version":"three","url":"https://example.com"}""",
            """{"url":"https://example.com"}""",
            """[1,2,3]""",
            "not json at all",
            string.Concat(Enumerable.Repeat("[", 40)) + string.Concat(Enumerable.Repeat("]", 40)),
        })
            Assert.Null(UpdateCheck.Parse(bad));

        // long notes are cut, a bad hash is dropped
        var longOne = UpdateCheck.Parse("""{"version":"3.0.0","url":"https://example.com","notes":"%N%","sha256":"xyz"}""".Replace("%N%", new string('n', 5000)))!;
        Assert.True(longOne.Notes.Length <= 2001);
        Assert.Equal("", longOne.Sha256);
    }

    [Theory]
    [InlineData("1.0.1", "1.0.0", UpdateStatus.NewerAvailable)]
    [InlineData("1.0.10", "1.0.9", UpdateStatus.NewerAvailable)]
    [InlineData("1.1", "1.0.9", UpdateStatus.NewerAvailable)]
    [InlineData("1.0.0.1", "1.0.0", UpdateStatus.NewerAvailable)]
    [InlineData("1.0.0", "1.0.0", UpdateStatus.UpToDate)]
    [InlineData("1.0", "1.0.0", UpdateStatus.UpToDate)]
    [InlineData("0.9.9", "1.0.0", UpdateStatus.UpToDate)]
    public void Versions_Compare(string offered, string current, UpdateStatus expected)
        => Assert.Equal(expected, UpdateCheck.Compare(new UpdateInfo(offered, "https://x", "", null, ""), current).Status);

    [Fact]
    public async Task Check_AsksTheServer_SendsOnlyNameAndVersion_AndCopesWithBadAnswers()
    {
        using var srv = new FakeServer { Body = Newer };
        UpdateCheck.UrlOverride = srv.Url;
        try
        {
            Assert.True(UpdateCheck.Available);
            var r = await UpdateCheck.CheckAsync();
            Assert.Equal(UpdateStatus.NewerAvailable, r.Status);
            Assert.Equal("9.1.0", r.Info!.Version);
            var (path, headers) = srv.Requests.Single();
            Assert.Equal("/update.json", path);
            Assert.Equal($"{Branding.Name}/{Branding.Version}", headers["User-Agent"]);
            Assert.Null(headers["Cookie"]);
            Assert.Null(headers["Authorization"]);

            srv.Body = $$"""{"version":"{{Branding.Version}}","url":"https://example.com"}""";
            Assert.Equal(UpdateStatus.UpToDate, (await UpdateCheck.CheckAsync()).Status);

            srv.Status = 404;
            var notFound = await UpdateCheck.CheckAsync();
            Assert.Equal(UpdateStatus.Failed, notFound.Status);
            Assert.Contains("404", notFound.Message);

            srv.Status = 200;
            srv.Body = "{\"version\":\"9.0.0\",\"url\":\"https://example.com\",\"notes\":\"" + new string('x', 100_000) + "\"}";
            Assert.Equal(UpdateStatus.Failed, (await UpdateCheck.CheckAsync()).Status);         // too long: not read

            srv.Body = "<html>maintenance</html>";
            Assert.Equal(UpdateStatus.Failed, (await UpdateCheck.CheckAsync()).Status);

            srv.Body = Newer;
            srv.DelayMs = 3000;
            var slow = await UpdateCheck.CheckAsync(timeout: TimeSpan.FromMilliseconds(400));
            Assert.Equal(UpdateStatus.Failed, slow.Status);

            // only https addresses (plain http on this computer for tests), and nothing without an address
            UpdateCheck.UrlOverride = "http://example.com/update.json";
            Assert.False(UpdateCheck.Available);
            UpdateCheck.UrlOverride = "";
            Assert.False(UpdateCheck.Available);
            Assert.Equal(UpdateStatus.NotConfigured, (await UpdateCheck.CheckAsync()).Status);
        }
        finally { UpdateCheck.UrlOverride = null; }
    }

    [Fact]
    public void WeeklyCheck_OnlyWhenSwitchedOn_AndDue()
    {
        var now = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
        UpdateCheck.UrlOverride = "https://example.com/update.json";
        try
        {
            Assert.False(UpdateCheck.IsDue(new AppConfig(), now));                                   // off by default: nothing is contacted
            Assert.True(UpdateCheck.IsDue(new AppConfig { CheckForUpdates = true }, now));
            Assert.True(UpdateCheck.IsDue(new AppConfig { CheckForUpdates = true, LastUpdateCheckUtc = now.AddDays(-8) }, now));
            Assert.False(UpdateCheck.IsDue(new AppConfig { CheckForUpdates = true, LastUpdateCheckUtc = now.AddDays(-2) }, now));
            UpdateCheck.UrlOverride = "";
            Assert.False(UpdateCheck.IsDue(new AppConfig { CheckForUpdates = true }, now));            // no address, no check
        }
        finally { UpdateCheck.UrlOverride = null; }
        Assert.False(new AppConfig().CheckForUpdates);
        // kept in the settings file
        var c = AppConfig.FromXml(AppConfig.ToXml(new AppConfig { CheckForUpdates = true, LastUpdateCheckUtc = now, SkippedVersion = "9.1.0" }));
        Assert.True(c.CheckForUpdates);
        Assert.Equal(now, c.LastUpdateCheckUtc);
        Assert.Equal("9.1.0", c.SkippedVersion);
    }

    [Fact]
    public void Windows_ShowTheCheckOnlyWithAnAddress_AndOpenOnlyTheDownloadPage() => WpfHost.Run(() =>
    {
        using var srv = new FakeServer { Body = Newer };
        var opened = new List<string>();
        var told = new List<string>();
        try
        {
            AppConfig.Replace(new AppConfig());
            UpdateCheck.UrlOverride = "";
            var about = new AboutWindow();
            Assert.Equal(Visibility.Collapsed, about.UpdateBtn.Visibility);
            about.Close();
            var cfg = new ConfigWindow();
            Assert.Equal(Visibility.Collapsed, cfg.UpdatesPanel.Visibility);
            cfg.Close();

            UpdateCheck.UrlOverride = srv.Url;
            about = new AboutWindow();
            Assert.Equal(Visibility.Visible, about.UpdateBtn.Visibility);
            cfg = new ConfigWindow();
            Assert.Equal(Visibility.Visible, cfg.UpdatesPanel.Visibility);
            Assert.Contains(srv.Url, cfg.UpdatesHint.Text);
            cfg.Close();

            // asked: a newer version opens the update window
            UpdateUi.MessageHook = told.Add;
            UpdateWindow.OpenHook = opened.Add;
            about.WindowStartupLocation = WindowStartupLocation.Manual;
            about.Left = -10000; about.Top = -10000; about.ShowActivated = false;   // off screen
            about.Show();
            var check = UpdateUi.CheckNow(about);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!check.IsCompleted && sw.ElapsedMilliseconds < 10000) WpfHost.Pump();
            var w = Application.Current.Windows.OfType<UpdateWindow>().Single();
            Assert.Contains("9.1.0", w.HeadLine.Text);
            Assert.Contains("Safer.", w.NotesBox.Text);
            Assert.Contains("CC756F42", w.ShaText.Text);
            Assert.True(AppConfig.Current.LastUpdateCheckUtc > DateTime.UtcNow.AddMinutes(-1));
            w.DownloadBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(new[] { "https://example.com/bytescape/download" }, opened);

            // skipped: kept in the settings
            var w2 = new UpdateWindow(UpdateCheck.Parse(Newer)!);
            w2.SkipBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal("9.1.0", AppConfig.Current.SkippedVersion);

            // up to date and failures are told when asked
            srv.Body = $$"""{"version":"{{Branding.Version}}","url":"https://example.com"}""";
            check = UpdateUi.CheckNow(about);
            sw.Restart();
            while (!check.IsCompleted && sw.ElapsedMilliseconds < 10000) WpfHost.Pump();
            Assert.Contains("newest version", told.Single());
            about.Close();
        }
        finally
        {
            UpdateCheck.UrlOverride = null;
            UpdateUi.MessageHook = null;
            UpdateWindow.OpenHook = null;
            foreach (Window x in Application.Current.Windows) if (x is UpdateWindow or AboutWindow or ConfigWindow) x.Close();
            AppConfig.Replace(new AppConfig());
            try { File.Delete(AppConfig.ConfigPath); } catch { }
        }
    });
}
