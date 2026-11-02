using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ByteScape.Core.Apps;
using ByteScape.Core.Compare;
using ByteScape.Core.Config;
using ByteScape.Core.Forecast;
using ByteScape.Core.Localization;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using ByteScape.Wpf;
using ByteScape.Wpf.Views;
using ByteScape.Wpf.Windows;
using Xunit;

namespace ByteScape.Tests;

/// <summary>
/// Store screenshots: BYTESCAPE_STORE_SHOTS=&lt;folder&gt; renders the main pictures of the listing in every interface
/// language at 1920×1080 from a made-up drive (<see cref="DemoDisk"/>), so no real names or folders of this machine show.
/// Without the variable the test does nothing.
/// </summary>
[Collection("language")]
public class StoreShotTests
{
    private const double Scale = 1.25;              // 1536×864 at 125 % = 1920×1080
    private const int W = 1536, H = 864;

    [Fact]
    public void StoreScreenshots()
    {
        var dir = Environment.GetEnvironmentVariable("BYTESCAPE_STORE_SHOTS");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        WpfHost.Run(() =>
        {
            var oldHistory = DiskHistory.Current;
            var (oldCulture, oldUi) = (Thread.CurrentThread.CurrentCulture, Thread.CurrentThread.CurrentUICulture);
            try
            {
                // the view a user gets after switching on colours by file type and a few more levels of detail
                AppConfig.Replace(new AppConfig { WatchDog = false, InitialStyle = ViewStyle.FileClasses, InitialDetailLevel = 7 });
                DiskHistory.Current = null;
                AppCatalog.SetShared(DemoDisk.Catalog());
                foreach (var code in Loc.Languages.Select(l => l.Code))
                {
                    Loc.SetLanguage(code);
                    // numbers and dates as a user of that language sees them (this machine's own settings are Turkish)
                    var culture = new System.Globalization.CultureInfo(code switch { "en" => "en-US", "zh-Hans" => "zh-CN", "tr" => "tr-TR", "ru" => "ru-RU", "fr" => "fr-FR", "es" => "es-ES", var c => c });
                    Thread.CurrentThread.CurrentCulture = culture;
                    Thread.CurrentThread.CurrentUICulture = culture;
                    var xml = XmlLanguage.GetLanguage(code == "zh-Hans" ? "zh-CN" : code);
                    Shoot(Path.Combine(dir, code), xml);
                }
            }
            finally
            {
                foreach (Window w in Application.Current.Windows) w.Close();
                Loc.SetLanguage(Loc.English);
                Thread.CurrentThread.CurrentCulture = oldCulture;
                Thread.CurrentThread.CurrentUICulture = oldUi;
                AppCatalog.SetShared(null);
                DiskHistory.Current = oldHistory;
                AppConfig.Replace(new AppConfig());
            }
        });
    }

    private static void Shoot(string dir, XmlLanguage xml)
    {
        Directory.CreateDirectory(dir);
        var now = DemoDisk.Build(earlier: false);
        var tree = new ScanTree(now, live: true);   // complete, nothing to scan: it reads as a finished scan of the drive
        var main = new MainWindow(new List<CommandLine.ViewRequest>(), false, openStartDialog: false) { Language = xml };
        Place(main);
        var view = (ScanView)typeof(MainWindow).GetMethod("AddView", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(main, new object[] { tree, now, "C:\\" })!;
        Pump(400);

        // 1 the map, a Windows Update download folder explained in the details panel, the reclaimable total below
        view.Model.Selected = tree.Find(@"C:\Windows\SoftwareDistribution\Download");
        view.ToggleDetails();
        view.UpdateStatusForTest();
        PumpUntil(() => view.Model.Reclaim.TotalBytes > 0 && !view.Model.IsSummarizing);
        view.UpdateStatusForTest();
        Pump(300);
        Save(main, Path.Combine(dir, "1-map.png"));

        // 2 what changed in three months: the same drive compared with its earlier state
        var earlier = DemoDisk.Build(earlier: true);
        var diff = TreeDiff.Compare(earlier, "C-drive.sns", DateTime.UtcNow.AddDays(-91), now, tree.Sync);
        view.Model.Diff = diff;
        view.Model.Selected = tree.Find(@"C:\Users\Sam\Videos\2025");
        view.ToggleDetails(); view.ToggleDetails();     // the details panel shows the new selection
        view.RefreshMap();
        Pump(300);
        Save(main, Path.Combine(dir, "2-compare.png"));
        view.Model.Diff = null;
        view.RefreshMap();

        // 3 programs on disk
        view.Model.Selected = null;
        view.ShowAppFootprint();
        var apps = WaitFor<AppsWindow>();
        PumpUntil(() => apps.AppList.Items.Count > 0);
        apps.Language = xml;
        Place(apps);
        apps.AppList.SelectedIndex = 0;
        Pump(300);
        Save(apps, Path.Combine(dir, "3-programs.png"));
        apps.Close();

        // 4 the disk forecast from half a year of measurements
        var utc = DateTime.UtcNow;
        var fw = new ForecastWindow(view) { Language = xml, Owner = main };
        Place(fw);
        fw.SetResult(DiskForecast.FromHistory(DemoDisk.Samples(utc, now.FreeBytes), "C:", now.TotalBytes, now.FreeBytes, utc),
            DiskForecast.RecentlyAdded(now, TimeSpan.FromDays(30), utc, tree.Sync), DemoDisk.Grew(earlier, now), utc.AddDays(-91), "");
        Pump(300);
        Save(fw, Path.Combine(dir, "4-forecast.png"));
        fw.Close();

        main.Close();
        Pump(100);
    }

    /// <summary>Off screen, without a frame, exactly the picture size.</summary>
    private static void Place(Window w)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.WindowStyle = WindowStyle.None;
        w.ResizeMode = ResizeMode.NoResize;
        w.Left = -10000; w.Top = -10000;
        w.ShowActivated = false;
        w.Width = W; w.Height = H;
        if (!w.IsVisible) w.Show();
        Pump(200);
    }

    private static void Save(Window w, string file)
    {
        w.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Round(W * Scale), (int)Math.Round(H * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(w.Background ?? Brushes.White, null, new Rect(0, 0, W, H));
            dc.DrawRectangle(new VisualBrush(w) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, W, H));
        }
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bmp, PixelFormats.Bgr24, null, 0)));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    private static void Pump(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { WpfHost.Pump(); Thread.Sleep(10); }
    }

    private static T WaitFor<T>() where T : Window
    {
        PumpUntil(() => Application.Current.Windows.OfType<T>().Any());
        return Application.Current.Windows.OfType<T>().Single();
    }

    private static void PumpUntil(Func<bool> done, int ms = 30000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < ms) { WpfHost.Pump(); Thread.Sleep(10); }
        Assert.True(done(), "timed out");
    }
}

/// <summary>
/// A made-up, lived-in 512 GB system drive for the Store pictures: made-up programs, games, people and files, the same
/// every time. <c>earlier</c> gives the drive three months before (a game still installed, fewer videos and downloads).
/// </summary>
internal static class DemoDisk
{
    private const long KB = 1024, MB = KB * 1024, GB = MB * 1024;
    private const long Total = 512_110_190_592;

    private sealed class Builder
    {
        public readonly DateTime Now = DateTime.UtcNow;
        public FolderNode Dir(ContainerNode parent, string name)
        {
            if (parent.FindChild(name) is FolderNode existing) return existing;
            var f = new FolderNode(name) { Enumerated = true, ScanComplete = true, Attributes = FileAttributes.Directory, Created = Now.AddDays(-900), Modified = Now.AddDays(-20) };
            parent.AddChild(f);
            return f;
        }
        public FolderNode Path(ContainerNode root, string path)
        {
            var c = root;
            foreach (var part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries)) c = Dir(c, part);
            return (FolderNode)c;
        }
        public void File(ContainerNode parent, string name, long bytes, double ageDays, FileAttributes extra = 0)
        {
            if (parent.FindChild(name) != null) return;
            long disk = (bytes + 4095) / 4096 * 4096;
            var t = Now.AddDays(-ageDays);
            parent.AddChild(new FileNode(name) { LogicalSize = bytes, DiskSize = disk, Attributes = FileAttributes.Archive | extra, Created = t, Modified = t, Accessed = Now.AddDays(-Math.Min(ageDays, 3)) });
        }
        /// <summary>count files with log-uniform sizes; names from <paramref name="name"/> (made unique).</summary>
        public void Many(ContainerNode parent, int count, long min, long max, Func<Random, int, string> name, double maxAge = 900, double minAge = 30)
        {
            var r = Rng(parent.FullPath);
            double a = Math.Log(min), b = Math.Log(max);
            for (int i = 0; i < count; i++)
            {
                var n = name(r, i);
                if (parent.FindChild(n) != null) n = System.IO.Path.GetFileNameWithoutExtension(n) + "_" + i + System.IO.Path.GetExtension(n);
                File(parent, n, (long)Math.Exp(a + r.NextDouble() * (b - a)), minAge + r.NextDouble() * (maxAge - minAge));
            }
        }
    }

    private static Random Rng(string key)
    {
        uint h = 2166136261;
        foreach (char ch in key) { h ^= ch; h *= 16777619; }
        return new Random((int)(h & 0x7FFFFFFF));
    }

    private static string Pick(Random r, string[] a) => a[r.Next(a.Length)];

    private static readonly string[] Syl = { "win", "ms", "nt", "d3d", "dx", "shell", "net", "crypt", "user", "gdi", "core", "audio", "media", "print", "wlan", "bth", "usb", "hid", "font", "sec", "time", "task", "search", "sync", "input", "view", "data", "cfg" };
    private static readonly string[] Suf = { "svc", "api", "host", "ext", "ui", "res", "mgr", "srv", "prov", "base", "cli", "lib", "dev", "com" };
    private static string SysName(Random r, string ext) => Pick(r, Syl) + Pick(r, Suf) + (r.Next(3) == 0 ? r.Next(2, 64).ToString() : "") + ext;

    public static DriveNode Build(bool earlier)
    {
        var b = new Builder();
        var root = new DriveNode("C:\\") { IsDrive = true, TotalBytes = Total, Enumerated = true, ScanComplete = true };

        // ---------------------------------------------------------------- root files
        b.File(root, "hiberfil.sys", 6_420 * MB, 200, FileAttributes.Hidden | FileAttributes.System);
        b.File(root, "pagefile.sys", 9_216 * MB, 200, FileAttributes.Hidden | FileAttributes.System);
        b.File(root, "swapfile.sys", 256 * MB, 200, FileAttributes.Hidden | FileAttributes.System);
        b.File(root, "DumpStack.log.tmp", 8 * KB, 200, FileAttributes.Hidden | FileAttributes.System);

        // ---------------------------------------------------------------- Windows
        var win = b.Dir(root, "Windows");
        var components = new[] { "shell32", "font-fonts", "media-player", "printing", "networking-tcpip", "directx-direct3d", "audio-engine", "bluetooth-stack", "search-indexer", "defender-engine", "speech-voices", "handwriting", "ocr-languages", "hyperv-platform", "photos-codecs", "wmi-provider", "update-agent", "store-runtime", "edge-webview", "ink-workspace" };
        var sxs = b.Dir(win, "WinSxS");
        var rs = Rng("sxs");
        for (int i = 0; i < 520; i++)
        {
            var c = b.Dir(sxs, $"amd64_microsoft-windows-{Pick(rs, components)}_31bf3856ad364e35_10.0.19041.{rs.Next(1, 5500)}_none_{rs.NextInt64(0x1000_0000_0000, 0xFFFF_FFFF_FFFF):x12}");
            b.Many(c, rs.Next(2, 9), 12 * KB, 18 * MB, (r, k) => SysName(r, Pick(r, new[] { ".dll", ".dll", ".exe", ".mui", ".manifest" })));
        }
        var sys32 = b.Dir(win, "System32");
        b.Many(sys32, 2600, 16 * KB, 14 * MB, (r, _) => SysName(r, Pick(r, new[] { ".dll", ".dll", ".dll", ".exe", ".sys", ".cpl", ".mui" })));
        var drv = b.Path(sys32, @"DriverStore\FileRepository");
        var rd = Rng("drivers");
        foreach (var d in new[] { "display", "audio", "net", "wifi", "bt", "usb", "storage", "chipset", "camera", "touchpad", "printer", "hid" })
            for (int k = 0; k < 9; k++)
                b.Many(b.Dir(drv, $"{d}{k:00}.inf_amd64_{rd.NextInt64(0x1000_0000_0000, 0xFFFF_FFFF_FFFF):x12}"), rd.Next(3, 14), 8 * KB, d == "display" ? 180 * MB : 12 * MB, (r, _) => SysName(r, Pick(r, new[] { ".sys", ".dll", ".inf", ".cat", ".bin" })));
        b.Many(b.Dir(win, "SysWOW64"), 1500, 16 * KB, 12 * MB, (r, _) => SysName(r, Pick(r, new[] { ".dll", ".dll", ".exe", ".ocx" })));
        b.Many(b.Dir(win, "Installer"), 120, 2 * MB, 160 * MB, (r, _) => $"{r.Next(0x100000, 0xFFFFFF):x}.{Pick(r, new[] { "msi", "msp", "msp" })}");
        var download = b.Path(win, @"SoftwareDistribution\Download");
        var ru = Rng("updates");
        for (int i = 0; i < 34; i++)
            b.Many(b.Dir(download, $"{ru.NextInt64(0x1000_0000_0000, 0xFFFF_FFFF_FFFF):x12}{ru.Next(0x10000, 0xFFFFF):x5}"), ru.Next(1, 5), 3 * MB, 160 * MB,
                (r, _) => Pick(r, new[] { "Windows10.0-KB5", "windows10.0-kb5", "SSU-19041." }) + r.Next(10000, 99999) + Pick(r, new[] { "-x64.cab", ".psf", "-x64.cab", ".esd" }), 60, 2);
        b.Many(b.Dir(win, "Temp"), 160, 4 * KB, 40 * MB, (r, _) => $"{Pick(r, new[] { "MpCmdRun", "dism", "setup", "cab_" })}{r.Next(1000, 9999)}.{Pick(r, new[] { "log", "tmp", "etl" })}", 60, 1);
        b.Many(b.Dir(win, "Fonts"), 420, 20 * KB, 22 * MB, (r, _) => $"{Pick(r, new[] { "seg", "ari", "cal", "cam", "con", "tim", "ver", "mal", "yu", "ms", "nir", "gad" })}{Pick(r, new[] { "", "b", "i", "z", "l", "sb" })}{r.Next(10, 99)}.{Pick(r, new[] { "ttf", "ttf", "ttc", "fon" })}");
        b.Many(b.Path(win, @"Microsoft.NET\Framework64\v4.0.30319"), 700, 8 * KB, 9 * MB, (r, _) => "System." + Pick(r, new[] { "Web", "Data", "Xml", "Net", "IO", "Runtime", "Drawing", "Windows", "Linq", "Security" }) + "." + Pick(r, new[] { "Core", "Forms", "Http", "Design", "Services", "Extensions", "Primitives" }) + ".dll");
        b.Many(b.Path(win, @"assembly\NativeImages_v4.0.30319_64"), 600, 60 * KB, 40 * MB, (r, _) => "System." + Pick(r, new[] { "Web", "Data", "Xml", "Net", "IO", "Runtime", "Drawing", "Windows", "Linq", "Security" }) + r.Next(1, 999) + ".ni.dll");
        b.Many(b.Path(win, @"SystemApps"), 260, 20 * KB, 30 * MB, (r, _) => SysName(r, Pick(r, new[] { ".dll", ".pri", ".winmd", ".png", ".js" })));

        // ---------------------------------------------------------------- programs (made-up names)
        var pf = b.Dir(root, "Program Files");
        App(b, pf, "PixelForge Studio", 4_300, new[] { "bin", "Presets", "Plug-ins", "Brushes", "Locales" });
        App(b, pf, "Nimbus Office", 3_100, new[] { "root", "Templates", "Fonts", "Proofing", "Updates" });
        App(b, pf, "Atlas CAD", 5_600, new[] { "bin", "Libraries", "Materials", "Samples" });
        App(b, pf, "Lumen Video", 2_700, new[] { "bin", "Effects", "Transitions", "Codecs" });
        App(b, pf, "Harbor Browser", 640, new[] { "Application", "Extensions" });
        App(b, pf, "Tempo Player", 290, new[] { "bin", "Skins" });
        App(b, pf, "Vault Backup", 190, new[] { "bin", "Drivers" });
        App(b, pf, "Common Files", 950, new[] { "System", "Services", "Shared" });
        App(b, pf, "WindowsApps", 6_200, StoreApps.Select(a => a.Folder).ToArray());
        var pf86 = b.Dir(root, "Program Files (x86)");
        App(b, pf86, "Game Hub Launcher", 1_250, new[] { "bin", "webhelper", "logs", "appcache" });
        App(b, pf86, "PrintMate Suite", 420, new[] { "Drivers", "Scan", "Help" });
        App(b, pf86, "Common Files", 310, new[] { "Setup", "Shared" });
        var pd = b.Dir(root, "ProgramData");
        b.Many(b.Dir(pd, "Package Cache"), 90, 400 * KB, 190 * MB, (r, _) => $"{{{Guid(r)}}}.{Pick(r, new[] { "msi", "exe", "cab" })}", 700, 60);
        App(b, pd, "Atlas CAD", 2_100, new[] { "Content", "Cache" });
        App(b, pd, "Vault Backup", 1_400, new[] { "Catalog", "Logs" });

        // ---------------------------------------------------------------- games (made-up)
        var games = b.Dir(root, "Games");
        Game(b, games, "Starfall Odyssey", 44, 47);
        Game(b, games, "Hollow Crown", 27, 30);
        Game(b, games, "Pixel Racers", 5, 7);
        if (earlier) Game(b, games, "Iron Tide", 24, 26);
        else Game(b, games, "Deep Harbor", 17, 19, recent: true);

        // ---------------------------------------------------------------- the user
        var home = b.Path(root, @"Users\Sam");
        b.Path(root, @"Users\Public\Documents");
        b.Many(b.Path(root, @"Users\Public\Pictures"), 12, 200 * KB, 3 * MB, (r, i) => $"Sample {i + 1}.jpg");
        // videos by year; 2025 is new since the earlier state
        var videos = b.Dir(home, "Videos");
        foreach (var year in new[] { 2019, 2020, 2021, 2022, 2023, 2024 })
            b.Many(b.Dir(videos, year.ToString()), 8 + year % 5, 150 * MB, 2_800 * MB, (r, i) => Pick(r, new[] { $"VID_{year}{r.Next(1, 13):00}{r.Next(1, 29):00}_{r.Next(100000, 235959)}.mp4", $"GX01{r.Next(1000, 9999)}.MP4", $"Holiday {year} part {i + 1}.mp4", $"Birthday {year}.mov" }), 365 * (2026 - year), 365 * (2025 - year) + 30);
        if (!earlier)
            b.Many(b.Dir(videos, "2025"), 14, 400 * MB, 3_800 * MB, (r, i) => Pick(r, new[] { $"GX01{r.Next(1000, 9999)}.MP4", $"VID_2025{r.Next(6, 10):00}{r.Next(1, 29):00}_{r.Next(100000, 235959)}.mp4", $"Wedding - camera {i % 3 + 1} - clip {i + 1}.mp4" }), 80, 1);
        b.Many(b.Path(videos, @"Projects\Summer film"), 14, 40 * MB, 2_500 * MB, (r, i) => Pick(r, new[] { $"render_v{i + 1}.mov", $"proxy_{i:000}.mp4", $"Summer film final {i + 1}.mp4" }), 300, 120);
        // photos by year
        var pics = b.Dir(home, "Pictures");
        foreach (var year in new[] { 2018, 2019, 2020, 2021, 2022, 2023, 2024, 2025 })
        {
            int count = 280 + (year - 2018) * 70;
            if (year == 2025 && earlier) count = 260;
            b.Many(b.Dir(pics, year.ToString()), count, 1_800 * KB, 9 * MB, (r, i) => $"IMG_{year}{r.Next(1, 13):00}{r.Next(1, 29):00}_{i:0000}.jpg", 365.0 * (2026 - year), 365.0 * (2025 - year) + 20);
            if (year >= 2021) b.Many(b.Path(pics, year + @"\RAW"), 60 + (year - 2021) * 25, 22 * MB, 48 * MB, (r, i) => $"DSC{i + 1000:00000}.ARW", 365.0 * (2026 - year), 365.0 * (2025 - year) + 20);
        }
        b.Many(b.Dir(pics, "Screenshots"), 240, 150 * KB, 4 * MB, (r, i) => $"Screenshot {i + 1}.png", 600, 2);
        // music: made-up artists and albums
        var music = b.Dir(home, "Music");
        var artists = new[] { "Northern Lights Trio", "Maya Rowe", "The Quiet Harbor", "Solen", "Kite Parade", "Jonas Vale", "Amber Coast", "Echo Garden", "Paper Lanterns", "Lina Ostrova" };
        var albums = new[] { "First Light", "Night Drive", "Blue Rooms", "Low Tide", "Glass Cities", "Evergreen", "Slow Motion", "Afterglow" };
        var ra = Rng("music");
        foreach (var artist in artists)
            for (int k = 0, n = ra.Next(1, 4); k < n; k++)
            {
                var album = b.Path(music, artist + "\\" + albums[(artist.Length + k) % albums.Length]);
                bool flac = ra.Next(3) > 0;
                b.Many(album, ra.Next(9, 15), flac ? 18 * MB : 5 * MB, flac ? 52 * MB : 12 * MB, (r, i) => $"{i + 1:00} Track {i + 1}.{(flac ? "flac" : "mp3")}", 1500, 200);
            }
        // documents
        var docs = b.Dir(home, "Documents");
        b.Many(b.Dir(docs, "Invoices"), 140, 60 * KB, 900 * KB, (r, i) => $"Invoice {2019 + i / 24}-{i % 12 + 1:00}-{i:000}.pdf", 2000, 5);
        b.Many(b.Dir(docs, "Taxes"), 30, 40 * KB, 4 * MB, (r, i) => Pick(r, new[] { $"Tax return {2016 + i % 9}.pdf", $"Receipts {2016 + i % 9}.xlsx", $"Statement {i + 1}.pdf" }), 3000, 60);
        b.Many(b.Dir(docs, "Thesis"), 45, 30 * KB, 60 * MB, (r, i) => Pick(r, new[] { $"Chapter {i % 9 + 1} draft {i / 9 + 1}.docx", $"Figures {i}.pptx", $"Data {i}.csv", $"Sources {i}.pdf" }), 1400, 400);
        b.Many(b.Dir(docs, "Work"), 260, 20 * KB, 45 * MB, (r, i) => Pick(r, new[] { $"Report Q{i % 4 + 1} {2021 + i % 5}.docx", $"Budget {2021 + i % 5}.xlsx", $"Meeting notes {i + 1}.docx", $"Slides {i + 1}.pptx", $"Scan {i + 1}.pdf" }), 1500, 3);
        b.Many(b.Path(docs, @"Backups\Phone"), 3, 1_200 * MB, 4_800 * MB, (r, i) => $"Phone backup {2020 + i}.zip", 1800, 200);
        // downloads: more of them now
        var dl = b.Dir(home, "Downloads");
        b.Many(dl, 70, 200 * KB, 900 * MB, (r, i) => Pick(r, new[] { $"PixelForgeSetup-5.{i % 9}.exe", $"NimbusOffice_x64_{i}.exe", $"photos-{2020 + i % 5}.zip", $"manual-{i + 1}.pdf", $"driver_gpu_{550 + i}.exe", $"brochure {i + 1}.pdf", $"export ({i}).csv" }), 900, 95);
        b.File(dl, "linux-desktop-24.04-amd64.iso", 5_900 * MB, 300);
        if (!earlier)
        {
            b.File(dl, "Wedding photos - all.zip", 8_400 * MB, 12);
            b.File(dl, "windows-recovery-image.iso", 6_300 * MB, 20);
            b.Many(dl, 28, 300 * KB, 700 * MB, (r, i) => Pick(r, new[] { $"DeepHarbor_Setup_{i}.exe", $"invoice-{i + 400}.pdf", $"video-call-recording-{i + 1}.mp4" }), 60, 1);
        }
        b.Many(b.Dir(home, "Desktop"), 24, 2 * KB, 80 * MB, (r, i) => Pick(r, new[] { $"New Text Document ({i}).txt", $"todo {i}.txt", $"poster {i}.png", $"contract {i}.pdf" }), 400, 1);
        // developer projects with their node_modules (a new one since the earlier state)
        var repos = b.Path(home, @"source\repos");
        Project(b, repos, "family-website", 900);
        Project(b, repos, "budget-app", 1_200);
        if (!earlier) Project(b, repos, "webshop", 1_600, recent: true);
        // AppData
        var local = b.Path(home, @"AppData\Local");
        b.Many(b.Dir(local, "Temp"), earlier ? 240 : 560, 2 * KB, 160 * MB, (r, i) => Pick(r, new[] { $"~DF{r.Next(0x1000, 0xFFFF):X}.tmp", $"{Guid(r)}.tmp", $"setup_{i}.log", $"chrome_drag{i}.tmp", $"TCD{r.Next(0x100, 0xFFF):X}.tmp" }), earlier ? 400 : 300, 1);
        b.Many(b.Dir(local, "CrashDumps"), 7, 90 * MB, 700 * MB, (r, i) => $"LumenVideo.exe.{r.Next(1000, 99999)}.dmp", 200, 3);
        b.Many(b.Path(local, @"Harbor Browser\User Data\Default\Cache\Cache_Data"), 900, 1 * KB, 6 * MB, (r, i) => $"f_{i:x6}", 60, 1);
        b.Many(b.Path(local, @"Harbor Browser\User Data\Default\Service Worker\CacheStorage"), 300, 2 * KB, 12 * MB, (r, i) => $"{r.Next():x8}_{i}", 120, 2);
        App(b, local, @"Programs\CodeNest", 820, new[] { "resources", "locales", "bin" });
        b.Many(b.Path(local, @"Packages\Brightside.Photos_k2v7m9q3x1a8w\LocalCache"), 400, 20 * KB, 12 * MB, (r, i) => $"thumb_{i:0000}.dat", 200, 2);
        b.Many(b.Path(local, @"Microsoft\Windows\Explorer"), 12, 1 * MB, 180 * MB, (r, i) => $"thumbcache_{Pick(r, new[] { "16", "32", "96", "256", "1280", "idx" })}.db", 30, 1);
        var roaming = b.Path(home, @"AppData\Roaming");
        App(b, roaming, "PixelForge", 900, new[] { "Cache", "Settings" });
        App(b, roaming, "Tempo Player", 1_300, new[] { "Offline" });
        App(b, roaming, "CodeNest", 450, new[] { "User", "CachedExtensions" });

        // ---------------------------------------------------------------- Recycle Bin
        var bin = b.Path(root, @"$Recycle.Bin\S-1-5-21-1004336348-1177238915-682003330-1001");
        b.Many(bin, 22, 300 * KB, 900 * MB, (r, i) => $"$R{r.Next(0x100000, 0xFFFFFF):X}.{Pick(r, new[] { "mp4", "jpg", "zip", "docx" })}", 400, 35);

        root.FreeBytes = Math.Max(0, Total - root.DiskSize - 900 * MB);
        return root;
    }

    private static string Guid(Random r)
    {
        var bytes = new byte[16];
        r.NextBytes(bytes);
        return new System.Guid(bytes).ToString().ToUpperInvariant();
    }

    private static void App(Builder b, ContainerNode parent, string name, long megabytes, string[] parts)
    {
        var app = b.Path(parent, name);
        var r = Rng(app.FullPath);
        long left = megabytes * MB;
        for (int i = 0; i < parts.Length; i++)
        {
            var part = b.Dir(app, parts[i]);
            long share = i == parts.Length - 1 ? left : (long)(left * (0.35 + r.NextDouble() * 0.3));
            left -= share;
            int files = 20 + r.Next(260);
            long avg = Math.Max(4 * KB, share / files);
            b.Many(part, files, Math.Max(2 * KB, avg / 12), Math.Max(8 * KB, avg * 6), (rr, k) => Pick(rr, new[] { $"{name.Split(' ')[0]}{Pick(rr, Suf)}.dll", $"{Pick(rr, Syl)}{k}.pak", $"{Pick(rr, Syl)}_{k}.dat", $"{Pick(rr, Syl)}{Pick(rr, Suf)}.json", $"icon{k}.png" }), 600, 20);
        }
    }

    private static void Game(Builder b, ContainerNode games, string name, int minGb, int maxGb, bool recent = false)
    {
        var g = b.Dir(games, name);
        var r = Rng(g.FullPath);
        double oldest = recent ? 25 : 700, newest = recent ? 2 : 120;
        var paks = b.Path(g, @"Content\Paks");
        long target = (long)((minGb + r.NextDouble() * (maxGb - minGb)) * GB);
        for (int i = 0; target > 0; i++)
        {
            long size = Math.Min(target, (long)((0.4 + r.NextDouble() * 3.6) * GB));
            b.File(paks, $"pakchunk{i}-Windows.pak", size, newest + r.NextDouble() * (oldest - newest));
            target -= size;
        }
        b.Many(b.Path(g, @"Content\Movies"), 10, 30 * MB, 400 * MB, (rr, i) => $"cinematic_{i:00}.bk2", oldest, newest);
        b.Many(b.Path(g, @"Binaries\Win64"), 30, 40 * KB, 160 * MB, (rr, i) => i == 0 ? name.Replace(" ", "") + ".exe" : Pick(rr, Syl) + Pick(rr, Suf) + ".dll", oldest, newest);
        b.Many(b.Path(g, "Saved"), 12, 40 * KB, 30 * MB, (rr, i) => $"save_{i:00}.sav", 30, 1);
    }

    private static void Project(Builder b, ContainerNode repos, string name, int modules, bool recent = false)
    {
        var p = b.Dir(repos, name);
        double oldest = recent ? 40 : 500, newest = recent ? 1 : 200;
        b.Many(b.Dir(p, "src"), 140, 1 * KB, 60 * KB, (r, i) => $"{Pick(r, new[] { "app", "page", "cart", "user", "api", "util", "theme" })}{i}.{Pick(r, new[] { "ts", "tsx", "css", "json" })}", oldest, newest);
        b.Many(b.Path(p, @".git\objects\pack"), 6, 2 * MB, 220 * MB, (r, i) => $"pack-{r.NextInt64():x16}.pack", oldest, newest);
        b.Many(b.Dir(p, "dist"), 40, 4 * KB, 3 * MB, (r, i) => $"chunk-{r.Next():x8}.js", oldest, newest);
        var nm = b.Dir(p, "node_modules");
        var pkgs = new[] { "typescript", "webpack", "esbuild", "lodash", "react", "react-dom", "eslint", "@babel", "rxjs", "sass", "vite", "jest", "prettier", "date-fns", "core-js", "caniuse-lite", "@types", ".cache" };
        var r0 = Rng(nm.FullPath);
        for (int i = 0; i < modules; )
        {
            var pkg = b.Dir(nm, Pick(r0, pkgs));
            int n = r0.Next(20, 120);
            b.Many(b.Dir(pkg, Pick(r0, new[] { "lib", "dist", "src", "cjs", "esm" })), n, 400, 900 * KB, (r, k) => $"{Pick(r, Syl)}{k}.{Pick(r, new[] { "js", "js", "d.ts", "map", "json" })}", oldest, newest);
            i += n;
        }
    }

    public static AppCatalog Catalog()
    {
        static UninstallEntry E(string name, string publisher, string loc, string version) =>
            new(name.Replace(' ', '_'), name) { Publisher = publisher, InstallLocation = loc, Version = version };
        return AppCatalog.FromEntries(new[]
        {
            E("PixelForge Studio", "PixelForge Labs", @"C:\Program Files\PixelForge Studio", "5.2.1"),
            E("Nimbus Office", "Nimbus Software", @"C:\Program Files\Nimbus Office", "2025.4"),
            E("Atlas CAD", "Atlas Design Tools", @"C:\Program Files\Atlas CAD", "12.0"),
            E("Lumen Video", "Lumen Creative", @"C:\Program Files\Lumen Video", "3.8.0"),
            E("Harbor Browser", "Harbor Web", @"C:\Program Files\Harbor Browser", "131.0.2"),
            E("Tempo Player", "Tempo Audio", @"C:\Program Files\Tempo Player", "7.1"),
            E("Vault Backup", "Vault Data", @"C:\Program Files\Vault Backup", "4.4"),
            E("Game Hub Launcher", "Game Hub", @"C:\Program Files (x86)\Game Hub Launcher", "2.19"),
            E("PrintMate Suite", "PrintMate", @"C:\Program Files (x86)\PrintMate Suite", "1.6"),
            E("CodeNest", "CodeNest Team", @"C:\Users\Sam\AppData\Local\Programs\CodeNest", "1.94.0"),
            E("Starfall Odyssey", "Nova Forge Games", @"C:\Games\Starfall Odyssey", "1.12"),
            E("Hollow Crown", "Ninefold Studio", @"C:\Games\Hollow Crown", "2.0.3"),
            E("Pixel Racers", "Tiny Engine", @"C:\Games\Pixel Racers", "0.98"),
            E("Deep Harbor", "Bright Current", @"C:\Games\Deep Harbor", "1.0.1"),
        }, StoreApps.Select(a => new StorePackage(a.Folder, a.Name) { RootFolder = @"C:\Program Files\WindowsApps\" + a.Folder }));
    }

    /// <summary>Made-up Store apps (made-up publisher ids, so no real publisher is named).</summary>
    private static readonly (string Folder, string Name)[] StoreApps =
    {
        ("Brightside.Notes_4.2.0.0_x64__k2v7m9q3x1a8w", "Brightside Notes"),
        ("Brightside.Photos_2025.3.0.0_x64__k2v7m9q3x1a8w", "Brightside Photos"),
        ("Waypoint.Maps_11.0.0.0_x64__p5r8t2y6u4i0z", "Waypoint Maps"),
        ("Chatterbox.Messenger_3.1.0.0_x64__c3d5f7g9h1j2k", "Chatterbox"),
        ("Loopwave.Music_8.0.0.0_x64__m6n8b4v2c0x7z", "Loopwave Music"),
    };

    /// <summary>Half a year of weekly measurements of the drive: steady filling, one cleanup, a big download lately.</summary>
    public static List<DiskSample> Samples(DateTime nowUtc, long freeNow)
    {
        var r = Rng("samples");
        var days = new List<(int Day, double Free)>();
        double free = 0;
        for (int d = 182; d >= 3; d -= 7)
        {
            free -= 2.1 + r.NextDouble() * 1.4;
            if (d == 98) free += 21;            // a cleanup
            if (d == 17) free -= 9;             // the wedding videos
            days.Add((d, free));
        }
        // the last measurement a few days ago, a little above today's free space
        double shift = (freeNow + 1.5 * GB) / GB - free;
        return days.Select(x => new DiskSample(nowUtc.AddDays(-x.Day), "C:", Total, (long)((x.Free + shift) * GB))).ToList();
    }

    /// <summary>Folders two levels deep that grew between the two states, largest growth first.</summary>
    public static List<(string Path, long Then, long Now)> Grew(DriveNode earlier, DriveNode now)
    {
        static Dictionary<string, long> Sizes(DriveNode root)
        {
            var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in root.Descendants())
                if (n is ContainerNode c && c.Level <= 3) d[c.FullPath] = c.DiskSize;
            return d;
        }
        var then = Sizes(earlier);
        return Sizes(now)
            .Select(kv => (Path: kv.Key, Then: then.TryGetValue(kv.Key, out var t) ? t : 0, Now: kv.Value))
            .Where(x => x.Now - x.Then > 200 * MB && x.Path.Count(ch => ch == '\\') >= 2)
            .OrderByDescending(x => x.Now - x.Then).Take(8).ToList();
    }
}
