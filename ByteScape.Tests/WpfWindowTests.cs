using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ByteScape.Core.Config;
using ByteScape.Core.Model;
using ByteScape.Wpf;
using ByteScape.Wpf.Windows;
using Xunit;
using Xunit.Abstractions;

namespace ByteScape.Tests;

/// <summary>Hosts one WPF Application on a dedicated STA thread for all WPF tests.</summary>
internal static class WpfHost
{
    private static Dispatcher? _dispatcher;
    private static readonly object Sync = new();

    public static Dispatcher Dispatcher
    {
        get
        {
            lock (Sync)
            {
                if (_dispatcher != null) return _dispatcher;
                var ready = new ManualResetEventSlim();
                var t = new Thread(() =>
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/ByteScape;component/Themes/Theme.xaml") });
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/ByteScape;component/Themes/Styles.xaml") });
                    _dispatcher = Dispatcher.CurrentDispatcher;
                    ready.Set();
                    app.Run();
                });
                t.SetApartmentState(ApartmentState.STA);
                t.IsBackground = true;
                t.Start();
                ready.Wait();
                return _dispatcher!;
            }
        }
    }

    public static void Run(Action a)
    {
        Exception? error = null;
        Dispatcher.Invoke(() => { try { a(); } catch (Exception ex) { error = ex; } });
        if (error != null) throw new Xunit.Sdk.XunitException("WPF thread failed: " + error);
    }

    /// <summary>Shows the window off screen, pumps layout and saves a PNG.</summary>
    public static void Snapshot(Window w, string file)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -10000; w.Top = -10000;
        w.ShowActivated = false;
        w.Show();
        Pump();
        var bmp = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(w);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    public static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}

[Collection("config")]
public class WpfWindowTests
{
    private readonly ITestOutputHelper _out;
    public WpfWindowTests(ITestOutputHelper o) => _out = o;
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "wpf");

    static WpfWindowTests() => Directory.CreateDirectory(Dir);

    [Fact]
    public void ConfigWindow_RendersEveryPage_AndRoundTrips() => WpfHost.Run(() =>
    {
        AppConfig.Replace(ConfigTests.NonDefault());
        var w = new ConfigWindow();
        // every property of the working copy equals Current (deep clone)
        ConfigTests.AssertSameConfig(AppConfig.Current, w.Vm.Model);
        WpfHost.Snapshot(w, Path.Combine(Dir, "config_0.png"));
        for (int i = 1; i < 7; i++)
        {
            w.SelectPage(i);
            WpfHost.Pump();
            var bmp = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(w);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(Dir, $"config_{i}.png"));
            enc.Save(fs);
        }
        // edits through the view model reach the working model, not Current, until Apply
        w.Vm.FontSize = 15;
        w.Vm.ExcludePathsText = "*\\WinSxS\r\nnode_modules";
        Assert.Equal(15, w.Vm.Model.FontSize);
        Assert.Equal("*\\WinSxS;node_modules", w.Vm.Model.ExcludePaths);
        Assert.NotEqual(15, AppConfig.Current.FontSize);
        // restore defaults keeps data, resets look
        w.RestoreDefaults();
        ConfigTests.AssertSameConfig(AppConfig.Defaults(), w.Vm.Model, nameof(AppConfig.ExportConfigs), nameof(AppConfig.RecentPaths), nameof(AppConfig.AiApiKey));
        // the API key box (a PasswordBox, not bound) writes the working model
        w.AiKeyBox.Password = "typed-key";
        Assert.Equal("typed-key", w.Vm.Model.AiApiKey);
        // export / import round trip; a profile never carries the API key, importing one keeps the key entered here
        var file = Path.Combine(Dir, "profile.xml");
        w.Vm.Reset(ConfigTests.NonDefault());
        Assert.True(w.ExportTo(file));
        var keyElement = System.Xml.Linq.XDocument.Load(file).Root!.Element("aiApiKey")!;
        Assert.Equal("", keyElement.Value);
        Assert.Equal(ConfigTests.NonDefault().AiApiKey, w.Vm.Model.AiApiKey); // exporting does not clear the window's key
        w.Vm.Reset(new AppConfig { AiApiKey = "kept-key" });
        // the profile switches the local AI on (every setting differs from the defaults): taken over when confirmed
        ConfigWindow.ConfirmAiHook = _ => true;
        try { w.ImportFrom(file); }
        finally { ConfigWindow.ConfirmAiHook = null; }
        Assert.Equal("kept-key", w.Vm.Model.AiApiKey);
        Assert.Equal("kept-key", w.AiKeyBox.Password);
        ConfigTests.AssertSameConfig(ConfigTests.NonDefault(), w.Vm.Model, nameof(AppConfig.AiApiKey));
        // apply writes Current and the file
        File.Delete(AppConfig.ConfigPath);
        w.Vm.FontSize = 9;
        Assert.True(w.ApplyChanges());
        Assert.Equal(9, AppConfig.Current.FontSize);
        Assert.True(File.Exists(AppConfig.ConfigPath));
        w.Close();
        AppConfig.Replace(new AppConfig());
        File.Delete(AppConfig.ConfigPath);
        _out.WriteLine("images in " + Dir);
    });

    [Fact]
    public void ConfigWindow_Validation()
    {
        var c = new AppConfig { ExcludePaths = "ok;bad|mask" };
        Assert.Contains("invalid characters", ConfigWindow.Validate(c)!);
        c.ExcludePaths = "";
        c.FileClasses = new List<FileClass> { new() { Name = "Same" }, new() { Name = "same" } };
        Assert.Contains("unique", ConfigWindow.Validate(c)!);
        c.FileClasses = AppConfig.DefaultFileClasses();
        Assert.Null(ConfigWindow.Validate(c));
    }

    [Fact]
    public void OtherWindows_Render() => WpfHost.Run(() =>
    {
        AppConfig.Replace(new AppConfig());
        WpfHost.Snapshot(new StartWindow(), Path.Combine(Dir, "start.png"));
        WpfHost.Snapshot(new HelpWindow(HelpWindow.Topic.Filtering), Path.Combine(Dir, "help.png"));
        WpfHost.Snapshot(new AboutWindow(), Path.Combine(Dir, "about.png"));
        WpfHost.Snapshot(new LogWindow(), Path.Combine(Dir, "log.png"));
        var tree = Wpf.Controls.SampleTree.Create();
        var vm = new Treemap.ViewModel(tree, tree.Root);
        WpfHost.Snapshot(new ExportWindow(vm), Path.Combine(Dir, "export.png"));
        WpfHost.Snapshot(new LayoutDebugWindow(vm), Path.Combine(Dir, "debug.png"));
        foreach (Window w in Application.Current.Windows) w.Close();
    });

    [Fact]
    public void MainWindow_WithView_Renders() => WpfHost.Run(() =>
    {
        AppConfig.Replace(new AppConfig());
        var main = new MainWindow(new List<CommandLine.ViewRequest>(), false, openStartDialog: false);
        main.WindowStartupLocation = WindowStartupLocation.Manual;
        main.Left = -10000; main.Top = -10000; main.ShowActivated = false;
        main.Show();
        WpfHost.Pump();
        var view = main.OpenScan(AppContext.BaseDirectory);
        Assert.NotNull(view);
        Thread.Sleep(800);
        WpfHost.Pump();
        var bmp = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(main);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(Path.Combine(Dir, "main.png"))) enc.Save(fs);
        Assert.True(view!.Tree.Root.DiskSize > 0);
        main.Close();
    });


    [Fact]
    public void DetailsPanel_ShowsKnowledgeBlock_ForKnownLocation() => WpfHost.Run(() =>
    {
        AppConfig.Replace(new AppConfig());
        // %TEMP%\bytescape-tests lives inside the "Temporary files" rule: every node inherits it
        var dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "wpf");
        var main = new MainWindow(new List<CommandLine.ViewRequest>(), false, openStartDialog: false);
        main.WindowStartupLocation = WindowStartupLocation.Manual;
        main.Left = -10000; main.Top = -10000; main.ShowActivated = false; main.Width = 1300;
        main.Show();
        WpfHost.Pump();
        var view = main.OpenScan(dir)!;
        Thread.Sleep(500);
        WpfHost.Pump();
        var km = Core.Knowledge.KnowledgeBase.Classify(view.Tree.Root);
        Assert.NotNull(km);
        Assert.Equal("user-temp", km!.Location.Id);
        Assert.True(km.Inherited);
        view.Model.Selected = view.Tree.Root;
        view.ToggleDetails();
        WpfHost.Pump();
        var facts = (System.Windows.Controls.StackPanel)view.GetType().GetField("DetFacts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!;
        Assert.Contains(facts.Children.OfType<System.Windows.Controls.Border>(), b => b.Child is System.Windows.Controls.StackPanel);
        WpfHost.Pump();
        var bmp = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(main);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(Path.Combine(Dir, "main_details_knowledge.png"))) enc.Save(fs);
        // the context menu offers "What is this?" for known locations
        var menu = view.BuildContextMenu(view.Tree.Root, new Point());
        Assert.Contains(menu.Items.OfType<System.Windows.Controls.MenuItem>(), i => (i.Header as string)?.StartsWith("What is this?") == true);
        main.Close();
    });

    [Fact]
    public void CompareWindow_Renders_AndOverlayFollowsIt() => WpfHost.Run(() =>
    {
        AppConfig.Replace(new AppConfig());
        var tree = Wpf.Controls.SampleTree.Create();
        var view = new Wpf.Views.ScanView(tree, tree.Root, "sample");
        var baseline = Wpf.Controls.SampleTree.Create().Root;
        var users = (Core.Model.ContainerNode)tree.Root.FindChild("Users")!;
        users.AddChild(new Core.Model.FileNode("new.iso") { LogicalSize = 1 << 28, DiskSize = 1 << 28 });
        var diff = Core.Compare.TreeDiff.Compare(baseline, "baseline.sns", DateTime.UtcNow, tree.Root, tree.Sync);
        view.Model.Diff = diff;
        var w = new CompareWindow(view, diff);
        WpfHost.Snapshot(w, Path.Combine(Dir, "compare.png"));
        Assert.True(w.List.Items.Count >= 2);                 // Users (grown) + new.iso (added)
        Assert.Same(diff, view.Model.Diff);
        w.Close();
        Assert.Null(view.Model.Diff);                          // closing the window removes the overlay
    });

    [Fact]
    public void CompareFromASnapshotView_AlignsThroughTheFrame() => WpfHost.Run(() =>
    {
        AppConfig.Replace(new AppConfig());
        var dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "wpf");
        var file = Path.Combine(dir, "view.sns");
        Core.Snapshot.SnsFormat.Write(file, Wpf.Controls.SampleTree.Create().Root, _ => true);
        var main = new MainWindow(new List<CommandLine.ViewRequest>(), false, openStartDialog: false);
        main.WindowStartupLocation = WindowStartupLocation.Manual;
        main.Left = -10000; main.Top = -10000; main.ShowActivated = false;
        main.Show();
        WpfHost.Pump();
        var view = main.OpenSnapshot(file)!;
        Assert.True(((Core.Model.DriveNode)view.Model.ViewRoot).IsSnapshotFrame);
        // same file: must align (no "does not overlap" error) and find no changes
        view.CompareWithSnapshot(file);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (view.Model.Diff == null && sw.ElapsedMilliseconds < 10000) { WpfHost.Pump(); Thread.Sleep(20); }
        Assert.NotNull(view.Model.Diff);
        Assert.Empty(view.Model.Diff!.Entries);
        Assert.Equal("C:", view.Model.Diff.CurrentPath);
        foreach (Window w in Application.Current.Windows) if (w is CompareWindow cw) cw.Close();
        main.Close();
    });

    [Fact]
    public void SlowSnapshotRead_ShowsProgress_KeepsTheWindowAlive_AndCancels() => WpfHost.Run(() =>
    {
        AppConfig.Replace(new AppConfig());
        var main = new MainWindow(new List<CommandLine.ViewRequest>(), false, openStartDialog: false)
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false };
        main.Show();
        WpfHost.Pump();
        Window? Dialog() => Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.Owner == main && w.Content is System.Windows.Controls.StackPanel);
        object? Slow(Action<long, long> progress, CancellationToken token)
        {
            for (int i = 1; i <= 40; i++) { token.ThrowIfCancellationRequested(); progress(i, 40); Thread.Sleep(25); }
            return "tree";
        }
        // a quick read: no dialog at all
        Assert.Equal("fast", main.ReadResponsive<object>("x.sns", (_, _) => "fast", out var c0));
        Assert.False(c0);
        // a slow one: the dialog appears, the window's own messages keep being handled, the result comes back
        int ticks = 0; bool sawDialog = false;
        var probe = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        probe.Tick += (_, _) => { ticks++; sawDialog |= Dialog() != null; };
        probe.Start();
        Assert.Equal("tree", main.ReadResponsive<object>("big.sns", Slow, out var c1));
        probe.Stop();
        Assert.False(c1);
        Assert.True(sawDialog);
        Assert.True(ticks >= 5, "the message loop ran " + ticks + " times while reading");
        Assert.Null(Dialog());
        // closing the dialog cancels the read
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var closer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        closer.Tick += (_, _) => { if (Dialog() is { } d) { closer.Stop(); d.Close(); } };
        closer.Start();
        Assert.Null(main.ReadResponsive<object>("big.sns", Slow, out var c2));
        Assert.True(c2);
        Assert.True(sw.ElapsedMilliseconds < 950, "cancelled after " + sw.ElapsedMilliseconds + " ms");
        main.Close();
    });

    [Fact]
    public void TreemapContextMenu_HasExplorerActions_AndRenders() => WpfHost.Run(() =>
    {
        AppConfig.Replace(new AppConfig());
        var main = new MainWindow(new List<CommandLine.ViewRequest>(), false, openStartDialog: false);
        main.WindowStartupLocation = WindowStartupLocation.Manual;
        main.Left = -10000; main.Top = -10000; main.ShowActivated = false;
        main.Show();
        WpfHost.Pump();
        var view = main.OpenScan(AppContext.BaseDirectory)!;
        Thread.Sleep(800);
        WpfHost.Pump();

        static List<string> Headers(System.Windows.Controls.ItemsControl m) =>
            m.Items.OfType<System.Windows.Controls.MenuItem>().Select(i => i.Header is string s ? s : ((System.Windows.Controls.TextBlock)i.Header).Text).ToList();

        // a real file of the live scan: Explorer actions enabled
        var file = view.Tree.Root.Children.OfType<Core.Model.FileNode>().First();
        var menu = view.BuildContextMenu(file, new Point());
        var headers = Headers(menu);
        Assert.Contains("Open in Explorer", headers);
        Assert.Contains("Open", headers);
        Assert.Contains("Copy path", headers);
        Assert.Contains("Zoom into folder", headers);
        Assert.Contains("Tag", headers);
        Assert.Contains("Windows menu…", headers);
        var explorer = menu.Items.OfType<System.Windows.Controls.MenuItem>().First(i => (i.Header as string) == "Open in Explorer");
        Assert.True(explorer.IsEnabled);
        var tag = menu.Items.OfType<System.Windows.Controls.MenuItem>().First(i => (i.Header as string) == "Tag");
        Assert.Equal(new[] { "Red", "Yellow", "Green", "Blue", "Clear tags in this zoom" }, Headers(tag));

        // the menu renders with the app theme (template applies outside a visual tree through Application resources)
        menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        menu.Arrange(new Rect(menu.DesiredSize));
        WpfHost.Pump();
        Assert.True(menu.DesiredSize.Height > 200, "menu height " + menu.DesiredSize.Height);
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(menu.DesiredSize.Width), (int)Math.Ceiling(menu.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(menu);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(Path.Combine(Dir, "contextmenu_file.png"))) enc.Save(fs);

        // a folder: zoom / sub scan instead of Open, and everything stays available for snapshot trees except file system actions
        var folder = view.Tree.Root.Children.OfType<Core.Model.FolderNode>().FirstOrDefault();
        if (folder != null)
        {
            var fm = view.BuildContextMenu(folder, new Point());
            var fh = Headers(fm);
            Assert.Contains("Zoom into", fh);
            Assert.Contains("Scan this folder now", fh);
            Assert.DoesNotContain("Open", fh);
        }
        var sample = Wpf.Controls.SampleTree.Create();
        var snapView = new Wpf.Views.ScanView(sample, sample.Root, "sample");
        var sm = snapView.BuildContextMenu(sample.Root.Children.First(), new Point());
        var snapExplorer = sm.Items.OfType<System.Windows.Controls.MenuItem>().First(i => (i.Header as string) == "Open in Explorer");
        Assert.False(snapExplorer.IsEnabled);
        Assert.Contains("Copy path", Headers(sm));
        // keyboard twins of the menu: Ctrl+C copies the selected path, Enter is a no-op on snapshots
        snapView.Model.Selected = sample.Root.Children.First(c => c.Name == "movie.mkv");
        snapView.CopySelectedPath();
        Assert.Equal("C:\\movie.mkv", Clipboard.GetText());
        snapView.OpenSelectedInExplorer();
        main.Close();
    });

    [Fact]
    public void CloudFiles_ShowInStatusDetailsAndMenu() => WpfHost.Run(() =>
    {
        AppConfig.Replace(new AppConfig());
        var old = Core.Cloud.CloudFiles.ProviderByPath;
        Core.Cloud.CloudFiles.ProviderByPath = p => p.StartsWith(@"C:\Users\Ali\OneDrive", StringComparison.OrdinalIgnoreCase) ? "OneDrive" : null;
        Core.Cloud.CloudFiles.SetKnownRoots(Array.Empty<Core.Cloud.SyncRoot>());
        try
        {
            Directory.CreateDirectory(Dir);
            var c = new Core.Model.DriveNode("C:\\") { IsDrive = true, Enumerated = true, ScanComplete = true, TotalBytes = 1L << 36, FreeBytes = 1L << 33 };
            var ali = AppsTests.Path(c, @"Users\Ali");
            var od = new Core.Model.FolderNode("OneDrive") { Enumerated = true, ScanComplete = true, Attributes = FileAttributes.Directory | FileAttributes.ReparsePoint };
            ali.AddChild(od);
            var local = AppsTests.File(AppsTests.Path(od, "Documents"), "report.docx", 3 << 20);
            var online = new Core.Model.FileNode("trip.mp4") { LogicalSize = 900L << 20, DiskSize = 0, Attributes = FileAttributes.Archive | Core.Cloud.CloudFiles.RecallOnDataAccess };
            AppsTests.Path(od, "Photos").AddChild(online);
            AppsTests.File(AppsTests.Path(ali, "Videos"), "home.mkv", 40 << 20);
            var tree = new Core.Scanning.ScanTree(c, live: false);
            var view = new Wpf.Views.ScanView(tree, c, "C:\\");
            var host = new Window { Content = view, Width = 1000, Height = 640, Title = "host" };
            WpfHost.Snapshot(host, Path.Combine(Dir, "cloud_plain.png"));
            // status line: the background summary finds the OneDrive folder and its local copies
            view.Model.RequestReclaimSummary(force: true);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while ((view.Model.IsSummarizing || view.Model.Cloud.Roots.Count == 0) && sw.ElapsedMilliseconds < 10000) { WpfHost.Pump(); Thread.Sleep(20); }
            sw.Restart();
            while (!((view.CloudText.Content as string) ?? "").StartsWith("☁") && sw.ElapsedMilliseconds < 5000) { WpfHost.Pump(); Thread.Sleep(50); }
            Assert.Equal("☁ 3.0MB can be freed (OneDrive)", view.CloudText.Content);
            Assert.Contains("Online-only (no space here): 900.0MB in 1 file", (string)view.CloudText.ToolTip);

            static List<string> Texts(DependencyObject d)
            {
                var list = new List<string>();
                foreach (var ch in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
                {
                    if (ch is System.Windows.Controls.TextBlock tb) list.Add(tb.Text);
                    list.AddRange(Texts(ch));
                }
                return list;
            }
            // details: a file's state, a folder's breakdown
            view.Model.Selected = local;
            view.ToggleDetails();
            var t = Texts(view.DetFacts);
            Assert.Contains("☁ OneDrive", t);
            Assert.Contains("Local copy", t);
            view.Model.Selected = online;
            view.ToggleDetails(); view.ToggleDetails();
            Assert.Contains("Online-only", Texts(view.DetFacts));
            view.Model.Selected = od;
            view.ToggleDetails(); view.ToggleDetails();
            Assert.Contains(Texts(view.DetFacts), s => s.StartsWith("Local copies: 3.0MB (1 file)"));
            WpfHost.Snapshot(host, Path.Combine(Dir, "cloud_details.png"));
            // context menu: the provider's own actions through the Windows menu (disabled on snapshot trees)
            var menu = view.BuildContextMenu(local, new Point());
            var item = menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(i => i.Header is string h && h.StartsWith("OneDrive (local copy): free up space"));
            Assert.False(item.IsEnabled);
            Assert.DoesNotContain(view.BuildContextMenu(AppsTests.Path(ali, "Videos"), new Point()).Items.OfType<System.Windows.Controls.MenuItem>(), i => i.Header is string h && h.StartsWith("OneDrive"));
            host.Close();
        }
        finally
        {
            Core.Cloud.CloudFiles.ProviderByPath = old;
            Core.Cloud.CloudFiles.SetKnownRoots(Array.Empty<Core.Cloud.SyncRoot>());
        }
    });

    [Fact]
    public void AppsWindow_ListsPrograms_HighlightsThem_AndIsLinkedFromTheMap() => WpfHost.Run(() =>
    {
        AppConfig.Replace(new AppConfig());
        Core.Apps.AppCatalog.SetShared(AppsTests.Catalog());
        try
        {
            Directory.CreateDirectory(Dir);
            var drive = AppsTests.BuildDrive();
            var tree = new Core.Scanning.ScanTree(drive, live: false);
            var view = new Wpf.Views.ScanView(tree, drive, "C:\\");
            var host = new Window { Content = view, Width = 1000, Height = 640, Title = "host" };
            WpfHost.Snapshot(host, Path.Combine(Dir, "apps_map_plain.png"));
            view.ShowAppFootprint();
            AppsWindow? w = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (((w = Application.Current.Windows.OfType<AppsWindow>().FirstOrDefault()) == null || w.AppList.Items.Count == 0) && sw.ElapsedMilliseconds < 10000) { WpfHost.Pump(); Thread.Sleep(20); }
            Assert.NotNull(w);
            var rows = w!.AppList.Items.OfType<AppsWindow.AppRow>().ToList();
            Assert.Equal("Alba: A Wildlife Adventure", rows[0].Name);            // largest first
            Assert.True(rows[^1].IsUnlinked);                                     // leftovers pinned at the bottom
            Assert.Equal(2, rows[^1].Pieces.Count);
            Assert.StartsWith("1.8GB used by 5 programs", w.HeadLine.Text);
            // selecting a program lists its folders and spotlights them on the map, carved out games excluded
            w.AppList.SelectedItem = rows.Single(r => r.Name == "Steam");
            WpfHost.Pump();
            Assert.Single(w.PieceList.Items);
            Assert.Contains("Steam", w.PieceHead.Text);
            var steam = (Core.Model.ContainerNode)AppsTests.Path(drive, @"Program Files (x86)\Steam");
            Assert.NotNull(view.Model.Spotlight);
            Assert.True(view.Model.InSpotlight(steam));
            Assert.False(view.Model.InSpotlight(AppsTests.Path(steam, @"steamapps\common\Alba")));
            Assert.False(view.Model.InSpotlight(AppsTests.Path(drive, "Windows")));
            WpfHost.Snapshot(host, Path.Combine(Dir, "apps_map_spotlight.png"));
            WpfHost.Snapshot(w, Path.Combine(Dir, "apps_window.png"));
            // search looks at names, publishers and folders
            w.Search.Text = "steam";
            WpfHost.Pump();
            var found = w.AppList.Items.OfType<AppsWindow.AppRow>().Select(r => r.Name).ToList();
            Assert.Equal(new[] { "Alba: A Wildlife Adventure", "Steam" }, found);     // the game by its folder, no unlinked row
            w.Search.Text = "valve";
            WpfHost.Pump();
            Assert.Equal("Steam", Assert.Single(w.AppList.Items.OfType<AppsWindow.AppRow>()).Name);
            w.Search.Text = "";
            // highlight can be switched off; closing the window clears it too
            w.AppList.SelectedItem = w.AppList.Items.OfType<AppsWindow.AppRow>().Single(r => r.Name == "Steam");
            w.Spot.IsChecked = false;
            w.Spot.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Null(view.Model.Spotlight);
            w.Spot.IsChecked = true;
            w.Spot.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.NotNull(view.Model.Spotlight);
            w.Close();
            Assert.Null(view.Model.Spotlight);

            // the details panel and the context menu name the owning program
            var cache = AppsTests.Path(drive, @"Users\Ali\AppData\Local\Google\Chrome\User Data\Default\Cache");
            view.Model.Selected = cache;
            view.ToggleDetails();
            Assert.Contains(view.DetFacts.Children.OfType<System.Windows.Controls.Grid>(), g =>
                g.Children.OfType<System.Windows.Controls.TextBlock>().Any(t => t.Text == "Program")
                && g.Children.OfType<System.Windows.Controls.Button>().Any(b => b.Content is System.Windows.Controls.TextBlock { Text: "Google Chrome" }));
            var menu = view.BuildContextMenu(cache, new Point());
            Assert.Contains(menu.Items.OfType<System.Windows.Controls.MenuItem>(), i => i.Header is string h && h.StartsWith("Program: Google Chrome"));
            var windows = AppsTests.Path(drive, "Windows");
            Assert.DoesNotContain(view.BuildContextMenu(windows, new Point()).Items.OfType<System.Windows.Controls.MenuItem>(), i => i.Header is string h && h.StartsWith("Program:"));
            host.Close();
        }
        finally { Core.Apps.AppCatalog.SetShared(null); }
    });
    [Fact]
    public void GamesWindow_ListsGames_FiltersIdleOnes_AndIsLinkedFromTheMap() => WpfHost.Run(() =>
    {
        var lib = Core.Games.GameLibrary.Load(GameTests.BuildLayout());
        Core.Games.GameLibrary.SetShared(lib);
        try
        {
            var tree = GameTests.Scan();
            var view = new Wpf.Views.ScanView(tree, tree.Root, "games");
            var host = new Window { Content = view, Width = 1000, Height = 640, Title = "host" };
            WpfHost.Snapshot(host, Path.Combine(Dir, "games_map.png"));
            view.ShowGames();
            GamesWindow? w = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (((w = Application.Current.Windows.OfType<GamesWindow>().FirstOrDefault()) == null || w.GameList.Items.Count == 0) && sw.ElapsedMilliseconds < 10000) { WpfHost.Pump(); Thread.Sleep(20); }
            Assert.NotNull(w);
            var rows = w!.GameList.Items.OfType<GamesWindow.GameRow>().ToList();
            Assert.Equal(new[] { "ELDEN RING", "Florence", "Fortnite", "The Witcher: Enhanced Edition", "Left behind by uninstalled games" }, rows.Select(r => r.Name));   // tools hidden, leftovers last
            Assert.Equal(5, rows[^1].Pieces.Count);
            Assert.True(rows.Single(r => r.Name == "Florence").IsIdle);
            Assert.False(rows.Single(r => r.Name == "ELDEN RING").IsIdle);
            Assert.Equal("8 h", rows.Single(r => r.Name == "Florence").PlaytimeText);
            Assert.Equal("—", rows.Single(r => r.Name == "Fortnite").PlayedText);     // Epic keeps no play dates
            Assert.StartsWith("1 · ", w.IdleText.Text);
            Assert.Contains("4 games", w.HeadLine.Text);
            // selecting a game lists its folders and spotlights them on the map
            w.GameList.SelectedItem = rows.Single(r => r.Name == "Florence");
            WpfHost.Pump();
            Assert.Equal(3, w.PieceList.Items.Count);
            Assert.Contains("played 8 h", w.PieceHead.Text);
            Assert.True(w.UninstallBtn.IsEnabled);
            Assert.True(w.StoreBtn.IsEnabled);
            var cache = (Core.Model.ContainerNode)GameTests.Node(tree.Root, @"Steam\steamapps\shadercache\1102130");
            Assert.True(view.Model.InSpotlight(cache));
            Assert.False(view.Model.InSpotlight(GameTests.Node(tree.Root, @"Lib2")));
            WpfHost.Snapshot(w, Path.Combine(Dir, "games_window.png"));
            WpfHost.Snapshot(host, Path.Combine(Dir, "games_map_spotlight.png"));
            // Epic games have no Steam buttons
            w.GameList.SelectedItem = w.GameList.Items.OfType<GamesWindow.GameRow>().Single(r => r.Name == "Fortnite");
            WpfHost.Pump();
            Assert.False(w.UninstallBtn.IsEnabled);
            Assert.False(w.StoreBtn.IsEnabled);
            // "not played for a year" keeps the idle Steam games only
            w.Idle.SelectedIndex = 4;
            WpfHost.Pump();
            Assert.Equal("Florence", Assert.Single(w.GameList.Items.OfType<GamesWindow.GameRow>()).Name);
            w.Idle.SelectedIndex = 0;
            w.Search.Text = "overcooked";
            WpfHost.Pump();
            var hit = Assert.Single(w.GameList.Items.OfType<GamesWindow.GameRow>());
            Assert.True(hit.IsLeftovers);
            Assert.Single(hit.Pieces);
            w.Search.Text = "";
            WpfHost.Pump();
            w.Close();
            Assert.Null(view.Model.Spotlight);

            // details panel, context menu and tooltip name the game of a numbered folder
            view.Model.Selected = cache;
            view.ToggleDetails();
            Assert.Contains(view.DetFacts.Children.OfType<System.Windows.Controls.Grid>(), g =>
                g.Children.OfType<System.Windows.Controls.TextBlock>().Any(t => t.Text == "Game")
                && g.Children.OfType<System.Windows.Controls.Button>().Any(b => b.Content is System.Windows.Controls.TextBlock { Text: "Florence (shader cache)" }));
            Assert.Contains(view.BuildContextMenu(cache, new Point()).Items.OfType<System.Windows.Controls.MenuItem>(), i => i.Header is string h && h.StartsWith("Game: Florence"));
            Assert.DoesNotContain(view.BuildContextMenu(GameTests.Node(tree.Root, "Lib2"), new Point()).Items.OfType<System.Windows.Controls.MenuItem>(), i => i.Header is string h && h.StartsWith("Game:"));
            // zoomed into the shader cache folder, the map caption carries the game's name
            view.ZoomTo((Core.Model.ContainerNode)cache.Parent!);
            WpfHost.Pump();
            WpfHost.Snapshot(host, Path.Combine(Dir, "games_map_labels.png"));
            host.Close();
            view.Close();
        }
        finally { Core.Games.GameLibrary.SetShared(null); AppConfig.Replace(new AppConfig()); try { Directory.Delete(GameTests.Dir, true); } catch { } }
    });
    [Fact]
    public void DevWindow_ListsProjects_FiltersStaleOnes_AndIsLinkedFromTheMap() => WpfHost.Run(() =>
    {
        DevTests.BuildLayout();
        try
        {
            var tree = DevTests.Scan();
            var view = new Wpf.Views.ScanView(tree, tree.Root, "dev");
            var host = new Window { Content = view, Width = 1000, Height = 640, Title = "host" };
            WpfHost.Snapshot(host, Path.Combine(Dir, "dev_map.png"));   // the owner window must be shown
            view.ShowDev();
            DevWindow? w = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (((w = Application.Current.Windows.OfType<DevWindow>().FirstOrDefault()) == null || w.ProjectList.Items.Count == 0) && sw.ElapsedMilliseconds < 10000) { WpfHost.Pump(); Thread.Sleep(20); }
            Assert.NotNull(w);
            var rows = w!.ProjectList.Items.OfType<DevWindow.ProjectRow>().ToList();
            Assert.Equal(7, rows.Count);
            Assert.Equal("rust", rows[0].Name);                                   // most rebuildable first
            Assert.True(rows.Single(r => r.Name == "webapp").IsStale);
            Assert.False(rows.Single(r => r.Name == "rust").IsStale);
            Assert.StartsWith("1 · ", w.StaleText.Text);
            Assert.Contains("7 projects", w.HeadLine.Text);
            // a project lists its folders and spotlights them
            w.ProjectList.SelectedItem = rows.Single(r => r.Name == "dotnet");
            WpfHost.Pump();
            Assert.Equal(3, w.PieceList.Items.Count);
            Assert.Contains("git repository", w.PieceHead.Text);
            Assert.True(w.CommandsBtn.IsEnabled);
            var appBin = GameTests.Node(tree.Root, @"dotnet\App\bin");
            Assert.True(view.Model.InSpotlight(appBin));
            Assert.False(view.Model.InSpotlight(GameTests.Node(tree.Root, @"dotnet\App\Program.cs")));
            WpfHost.Snapshot(w, Path.Combine(Dir, "dev_window.png"));
            WpfHost.Snapshot(host, Path.Combine(Dir, "dev_map_spotlight.png"));
            // "untouched for 1 year" keeps the old one
            w.Stale.SelectedIndex = 4;
            WpfHost.Pump();
            Assert.Equal("webapp", Assert.Single(w.ProjectList.Items.OfType<DevWindow.ProjectRow>()).Name);
            w.Stale.SelectedIndex = 0;
            w.Search.Text = "cargo";
            WpfHost.Pump();
            Assert.Empty(w.ProjectList.Items);
            w.Search.Text = "Rust";
            WpfHost.Pump();
            Assert.Equal("rust", Assert.Single(w.ProjectList.Items.OfType<DevWindow.ProjectRow>()).Name);
            w.Search.Text = "";
            // selecting from a rebuildable folder finds its project (a nested one inside the repository)
            w.Select(Path.Combine(DevTests.Dir, @"dotnet\Lib\bin"));
            Assert.Equal("dotnet", ((DevWindow.ProjectRow)w.ProjectList.SelectedItem).Name);
            w.Close();
            Assert.Null(view.Model.Spotlight);

            // details panel, tooltip text and menu name the project of a rebuildable folder
            var nm = GameTests.Node(tree.Root, @"webapp\node_modules");
            view.Model.Selected = nm;
            view.ToggleDetails();
            Assert.Contains(view.DetFacts.Children.OfType<System.Windows.Controls.Grid>(), g =>
                g.Children.OfType<System.Windows.Controls.TextBlock>().Any(t => t.Text == "Project")
                && g.Children.OfType<System.Windows.Controls.Button>().Any(b => b.Content is System.Windows.Controls.TextBlock { Text: "webapp (Node.js)" }));
            Assert.Contains(view.BuildContextMenu(nm, new Point()).Items.OfType<System.Windows.Controls.MenuItem>(), i => i.Header is string h && h.StartsWith("Project: webapp"));
            Assert.DoesNotContain(view.BuildContextMenu(GameTests.Node(tree.Root, @"webapp\src"), new Point()).Items.OfType<System.Windows.Controls.MenuItem>(), i => i.Header is string h && h.StartsWith("Project:"));
            host.Close();
            view.Close();
        }
        finally { AppConfig.Replace(new AppConfig()); try { Directory.Delete(DevTests.Dir, true); } catch { } }
    });
    [Fact]
    public void HardLinks_InDetailsPanel_AndStatusLine() => WpfHost.Run(() =>
    {
        HardLinkTests.BuildLayout();
        try
        {
            var tree = HardLinkTests.Scan(once: true);
            var view = new Wpf.Views.ScanView(tree, tree.Root, "links");
            var host = new Window { Content = view, Width = 1000, Height = 640, Title = "host" };
            WpfHost.Snapshot(host, Path.Combine(Dir, "links_map.png"));
            Assert.Contains("hard links counted once: 332KB", view.StatusText.Text);
            var shadow = GameTests.Node(tree.Root, @"z\zcopy.bin");
            view.Model.Selected = shadow;
            view.ToggleDetails();
            var row = view.DetFacts.Children.OfType<System.Windows.Controls.Grid>().Single(g => g.Children.OfType<System.Windows.Controls.TextBlock>().Any(t => t.Text == "Hard link"));
            var link = row.Children.OfType<System.Windows.Controls.Button>().Single();
            Assert.Equal("4 names, space counted at target.bin", ((System.Windows.Controls.TextBlock)link.Content).Text);
            // the link shows the name that carries the space
            link.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal("target.bin", view.Model.Selected!.Name);
            WpfHost.Snapshot(host, Path.Combine(Dir, "links_details.png"));
            var own = view.DetFacts.Children.OfType<System.Windows.Controls.Grid>().Single(g => g.Children.OfType<System.Windows.Controls.TextBlock>().Any(t => t.Text == "Hard link"));
            Assert.Equal("4 names, space counted here", ((System.Windows.Controls.TextBlock)own.Children.OfType<System.Windows.Controls.Button>().Single().Content).Text);
            view.Model.Selected = GameTests.Node(tree.Root, @"other\plain.txt");
            view.ToggleDetails(); view.ToggleDetails();
            Assert.DoesNotContain(view.DetFacts.Children.OfType<System.Windows.Controls.Grid>(), g => g.Children.OfType<System.Windows.Controls.TextBlock>().Any(t => t.Text == "Hard link"));
            host.Close();
            view.Close();
        }
        finally { AppConfig.Replace(new AppConfig()); try { Directory.Delete(HardLinkTests.Dir, true); } catch { } }
    });
    [Fact]
    public void ForecastWindow_DrawsTheTrend_AndOpensFromAView() => WpfHost.Run(() =>
    {
        const long GB = 1L << 30;
        var histDir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "forecastui");
        var old = Core.Forecast.DiskHistory.Current;
        try
        {
            if (Directory.Exists(histDir)) Directory.Delete(histDir, true);
            AppConfig.Replace(new AppConfig());
            // a synthetic 60 day history: steady growth with one cleanup, shown in a window of its own
            var drive = AppsTests.BuildDrive();
            var tree = new Core.Scanning.ScanTree(drive, live: false);
            var view = new Wpf.Views.ScanView(tree, drive, "C:\\");
            var host = new Window { Content = view, Width = 1000, Height = 640, Title = "host" };
            WpfHost.Snapshot(host, Path.Combine(Dir, "forecast_host.png"));
            var now = DateTime.UtcNow;
            var samples = Enumerable.Range(0, 60).Select(i => new Core.Forecast.DiskSample(now.AddDays(i - 60), "C:", 120 * GB, 120 * GB - (60 * GB + i * GB / 2 - (i >= 40 ? 12 * GB : 0)))).ToList();
            long used = 60 * GB + 30 * GB - 12 * GB;
            var r = Core.Forecast.DiskForecast.FromHistory(samples, "C:", 120 * GB, 120 * GB - used, now);
            var w = new ForecastWindow(view) { Owner = host };
            var added = Core.Forecast.DiskForecast.RecentlyAdded(drive, TimeSpan.FromDays(30), now, tree.Sync);
            w.SetResult(r, added, new List<(string, long, long)> { (@"Users\Ali\Downloads", 2 * GB, 7 * GB) }, now.AddDays(-20), "history note");
            WpfHost.Snapshot(w, Path.Combine(Dir, "forecast_window.png"));
            Assert.StartsWith("C: will be full in about", w.HeadLine.Text);
            Assert.Contains("per week", w.RateText.Text);
            Assert.True(w.Chart.Children.Count > 10);                            // lines, measurement dots, labels
            Assert.Single(w.GrewList.Items);
            Assert.Equal("history note", w.HistoryHint.Text);
            w.Close();

            // from the view: this live temp folder's drive, the measurements from the history file
            var tempTree = HardLinkTests.Scan(once: true, Path.GetTempPath().TrimEnd('\\') + "\\bytescape-tests\\wpf");
            var hist = Core.Forecast.DiskHistory.Current = new Core.Forecast.DiskHistory(Path.Combine(histDir, "ByteScapeHistory.csv"));
            var info = new DriveInfo("C");
            hist.Record(new Core.Forecast.DiskSample(now.AddDays(-10), "C:", info.TotalSize, info.TotalFreeSpace + 5 * GB), TimeSpan.Zero);
            var tempView = new Wpf.Views.ScanView(tempTree, tempTree.Root, "temp");
            var host2 = new Window { Content = tempView, Width = 900, Height = 600, Title = "host2" };
            WpfHost.Snapshot(host2, Path.Combine(Dir, "forecast_host2.png"));
            tempView.ShowForecast();
            ForecastWindow? f = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while ((f = Application.Current.Windows.OfType<ForecastWindow>().FirstOrDefault()) == null && sw.ElapsedMilliseconds < 10000) { WpfHost.Pump(); Thread.Sleep(20); }
            Assert.NotNull(f);
            WpfHost.Pump();
            Assert.StartsWith("C: ", f!.HeadLine.Text);
            Assert.Contains("per week", f.RateText.Text);                         // two days of measurements: a pace
            Assert.Contains(hist.FilePath, f.HistoryHint.Text);
            WpfHost.Snapshot(f, Path.Combine(Dir, "forecast_live.png"));
            f.Close(); host.Close(); host2.Close(); view.Close(); tempView.Close();
        }
        finally
        {
            Core.Forecast.DiskHistory.Current = old;
            AppConfig.Replace(new AppConfig());
            try { Directory.Delete(histDir, true); } catch { }
        }
    });
    [Fact]
    public void ClosingDuringAScan_KeepsIt_AndReopeningContinuesIt() => WpfHost.Run(() =>
    {
        Core.Scanning.ScanResume.DirOverride = ResumeTests.Store;
        try
        {
            if (Directory.Exists(ResumeTests.Store)) Directory.Delete(ResumeTests.Store, true);
            ResumeTests.BuildLayout();
            AppConfig.Replace(new AppConfig { WatchDog = false });
            var reference = ResumeTests.Reference();
            AppConfig.Replace(new AppConfig { WatchDog = false, ScanThreads = 1 });
            MainWindow Open()
            {
                var m = new MainWindow(new List<CommandLine.ViewRequest>(), false, openStartDialog: false);
                m.WindowStartupLocation = WindowStartupLocation.Manual;
                m.Left = -10000; m.Top = -10000; m.ShowActivated = false;
                m.Show();
                WpfHost.Pump();
                return m;
            }
            void WaitFor(Func<bool> done)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!done() && sw.ElapsedMilliseconds < 20000) { WpfHost.Pump(); Thread.Sleep(10); }
                Assert.True(done(), "timed out");
            }

            // a scan paused half way, the pause button's state, then ByteScape is closed
            int listed = 0;
            Core.Scanning.ScanTree? tree = null;
            Core.Scanning.DirectoryScanner.Listed = _ => { if (Interlocked.Increment(ref listed) == 50) tree!.PauseMasterScan(); };
            var main = Open();
            var view = main.OpenScan(ResumeTests.Dir)!;
            tree = view.Tree;
            WaitFor(() => tree.IsMasterPaused);
            Core.Scanning.DirectoryScanner.Listed = null;
            main.UpdateToolbar();
            Assert.True(main.BtnPause.IsEnabled);
            Assert.Equal("Resume the paused scan (Pause)", main.BtnPause.ToolTip);
            view.UpdateStatusForTest();
            Assert.StartsWith("Master scan: paused", view.StatusText.Text);
            main.Close();
            var kept = Core.Scanning.ScanResume.Find(ResumeTests.Dir);
            Assert.NotNull(kept);
            Assert.True(kept!.PendingFolders > 0);

            // started again: the same path continues where it stopped
            var second = new List<string>();
            Core.Scanning.DirectoryScanner.Listed = n => { lock (second) second.Add(n.FullPath); };
            var main2 = Open();
            var view2 = main2.OpenScan(ResumeTests.Dir)!;
            Assert.True(view2.Tree.MasterScan!.Resume);
            view2.UpdateStatusForTest();
            Assert.Contains("Continuing the scan stopped on", view2.StatusText.Text);
            WaitFor(() => !view2.Tree.IsMasterScanning);
            Core.Scanning.DirectoryScanner.Listed = null;
            Assert.Equal(Core.Scanning.ScanState.Completed, view2.Tree.MasterState);
            Assert.Equal(301 - 50, second.Count);                                   // only the folders not reached before
            Assert.Equal(reference, ResumeTests.Lines(view2.Tree));
            WaitFor(() => Core.Scanning.ScanResume.Find(ResumeTests.Dir) == null);  // done: nothing kept any more
            main2.Close();
        }
        finally
        {
            Core.Scanning.DirectoryScanner.Listed = null;
            Core.Scanning.ScanResume.DirOverride = TestSetup.KeptScans;
            AppConfig.Replace(new AppConfig());
            try { Directory.Delete(ResumeTests.Dir, true); } catch { }
            try { Directory.Delete(ResumeTests.Store, true); } catch { }
        }
    });
    [Fact]
    public void QuickRescan_FromTheScanButton_AndAfterClosingByteScape() => WpfHost.Run(() =>
    {
        if (Core.Scanning.ChangeJournal.Mark(Path.GetTempPath()) == null) return;   // no change journal on this drive
        var store = Path.Combine(Path.GetTempPath(), "bytescape-tests", "journal-wpf-store");
        Core.Scanning.ScanResume.DirOverride = store;
        try
        {
            if (Directory.Exists(store)) Directory.Delete(store, true);
            ChangeJournalTests.BuildLayout();
            AppConfig.Replace(new AppConfig { WatchDog = false, ScanThreads = 1 });
            MainWindow Open()
            {
                var m = new MainWindow(new List<CommandLine.ViewRequest>(), false, openStartDialog: false);
                m.WindowStartupLocation = WindowStartupLocation.Manual;
                m.Left = -10000; m.Top = -10000; m.ShowActivated = false;
                m.Show();
                WpfHost.Pump();
                return m;
            }
            void WaitFor(Func<bool> done)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!done() && sw.ElapsedMilliseconds < 20000) { WpfHost.Pump(); Thread.Sleep(10); }
                Assert.True(done(), "timed out");
            }

            // a complete scan: the scan button offers the quick rescan
            var main = Open();
            var view = main.OpenScan(ChangeJournalTests.Dir)!;
            WaitFor(() => view.Tree.Root.ScanComplete && !view.Tree.IsMasterScanning);
            main.UpdateToolbar();
            Assert.Equal("Rescan what changed since the last scan, from the NTFS change journal (Shift+click: scan everything again)", main.BtnScan.ToolTip);
            ChangeJournalTests.Change();
            var reference = ChangeJournalTests.Lines(ChangeJournalTests.FullScan());
            view.ToggleMasterScan();
            Assert.NotNull(view.Tree.MasterScan!.Since);
            WaitFor(() => !view.Tree.IsMasterScanning);
            WpfHost.Pump();
            view.UpdateStatusForTest();
            Assert.Contains("complete (quick rescan: ", view.StatusText.Text);
            Assert.Equal(reference, ChangeJournalTests.Lines(view.Tree));

            // closing keeps it; a change while ByteScape is closed; opening the path again lists only that folder
            main.Close();
            var kept = Core.Scanning.ScanResume.Find(ChangeJournalTests.Dir);
            Assert.NotNull(kept);
            Assert.True(kept!.Complete);
            Assert.NotNull(kept.Mark);
            System.IO.File.WriteAllBytes(Path.Combine(ChangeJournalTests.Dir, @"b\later.bin"), new byte[12_345]);
            reference = ChangeJournalTests.Lines(ChangeJournalTests.FullScan());
            var listed = new List<string>();
            Core.Scanning.DirectoryScanner.Listed = n => { lock (listed) listed.Add(n.FullPath); };
            var main2 = Open();
            var view2 = main2.OpenScan(ChangeJournalTests.Dir)!;
            Assert.NotNull(view2.Tree.MasterScan!.Since);
            view2.UpdateStatusForTest();
            Assert.StartsWith("Bringing the scan of", view2.StatusText.Text);
            WaitFor(() => !view2.Tree.IsMasterScanning);
            Core.Scanning.DirectoryScanner.Listed = null;
            Assert.Equal(Core.Scanning.ScanState.Completed, view2.Tree.MasterState);
            Assert.Equal(new[] { Path.Combine(ChangeJournalTests.Dir, "b") }, listed);
            Assert.Equal(reference, ChangeJournalTests.Lines(view2.Tree));

            // Shift+click: everything again
            view2.ToggleMasterScan(fullRescan: true);
            Assert.Null(view2.Tree.MasterScan!.Since);
            WaitFor(() => !view2.Tree.IsMasterScanning);
            Assert.Equal(reference, ChangeJournalTests.Lines(view2.Tree));
            main2.Close();
        }
        finally
        {
            Core.Scanning.DirectoryScanner.Listed = null;
            Core.Scanning.ScanResume.DirOverride = TestSetup.KeptScans;
            AppConfig.Replace(new AppConfig());
            ChangeJournalTests.Clean();
            try { Directory.Delete(store, true); } catch { }
        }
    });
    private static readonly string DeleteDir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "delete-wpf");

    private static void CleanDeleteDir()
    {
        if (!Directory.Exists(DeleteDir)) return;
        foreach (var f in Directory.EnumerateFiles(DeleteDir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        try { Directory.Delete(DeleteDir, true); } catch { }
    }

    private static MainWindow OpenMain()
    {
        var m = new MainWindow(new List<CommandLine.ViewRequest>(), false, openStartDialog: false);
        m.WindowStartupLocation = WindowStartupLocation.Manual;
        m.Left = -10000; m.Top = -10000; m.ShowActivated = false; m.Width = 1300; m.Height = 850;
        m.Show();
        WpfHost.Pump();
        return m;
    }

    private static void PumpUntil(Func<bool> done, int ms = 20000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < ms) { WpfHost.Pump(); Thread.Sleep(10); }
        Assert.True(done(), "timed out");
    }

    private static void Render(Window w, string name)
    {
        WpfHost.Pump();
        var bmp = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(w);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(Dir, name));
        enc.Save(fs);
    }

    [Fact]
    public void ProblemReportWindow_ShowsWhatGoesIn_AndSaves() => WpfHost.Run(() =>
    {
        void CleanReports() { try { if (Directory.Exists(TestSetup.CrashReports)) Directory.Delete(TestSetup.CrashReports, true); } catch { } Core.Util.CrashLog.ResetRunCount(); }
        var zip = Path.Combine(Dir, "problem-report.zip");
        CleanReports();
        try
        {
            var w = new ProblemReportWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false };
            w.Show();
            PumpUntil(() => !w.PreviewBox.Text.StartsWith("Collecting"));
            Assert.False(w.CrashOpt.IsEnabled);                       // no error reports yet
            Assert.Contains("there are none", (string)w.CrashOpt.Content);
            Assert.Contains("Windows 1", w.PreviewBox.Text);
            Assert.Contains("scaling", w.PreviewBox.Text);            // the screen line only the window knows
            Assert.Contains("attach the file to a new issue", w.IntroText.Text);
            Assert.Equal(Visibility.Visible, w.IssueBtn.Visibility);
            Assert.True(w.IssueBtn.IsDefault);
            w.DescriptionBox.Text = "The map stayed empty.";
            w.SettingsOpt.IsChecked = false;
            var save = w.SaveTo(zip);
            PumpUntil(() => save.IsCompleted);
            Assert.Equal(new[] { Core.Util.ProblemReport.ReadmeName, Core.Util.ProblemReport.SystemName, Core.Util.ProblemReport.LogName }, save.Result);
            Assert.Equal(Visibility.Visible, w.ResultText.Visibility);
            Assert.Contains("3 files", w.ResultText.Text);
            using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
            using (var r = new StreamReader(z.GetEntry(Core.Util.ProblemReport.ReadmeName)!.Open()))
                Assert.Contains("The map stayed empty.", r.ReadToEnd());
            Render(w, "problem_report.png");

            // "Open an issue": the report just saved with the same options is reused, Explorer shows it, the browser gets the
            // project's new-issue page (nothing is started for real here)
            var started = new List<System.Diagnostics.ProcessStartInfo>();
            ProblemReportWindow.StartHook = started.Add;
            try
            {
                var issue = w.OpenIssue(null);
                PumpUntil(() => issue.IsCompleted);
                Assert.Equal(2, started.Count);
                Assert.Equal(Core.Util.SystemTools.Explorer, started[0].FileName);
                Assert.Contains(zip, started[0].Arguments);
                var uri = started[1].FileName;
                Assert.StartsWith(Branding.IssueUrl + "?title=" + Uri.EscapeDataString(Branding.Name + " " + Branding.Version + " problem report") + "&body=", uri);
                var body = Uri.UnescapeDataString(uri.Substring(uri.IndexOf("&body=", StringComparison.Ordinal) + 6));
                Assert.StartsWith("The map stayed empty.\n\n```\nProgram", body);
                Assert.Contains(Path.GetFileName(zip), body);
                Assert.DoesNotContain(Environment.UserName, body.Replace("<user>", ""), StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Everyone can read issues", w.ResultText.Text);
                // changed options: a new report is saved first, where the user chose
                started.Clear();
                w.DescriptionBox.Text = "Now it is empty again.";
                var zip2 = Path.Combine(Dir, "problem-report-2.zip");
                issue = w.OpenIssue(zip2);
                PumpUntil(() => issue.IsCompleted);
                Assert.True(File.Exists(zip2));
                Assert.Contains(zip2, started[0].Arguments);
                Assert.Contains(Uri.EscapeDataString("Now it is empty again."), started[1].FileName);
                File.Delete(zip2);
            }
            finally { ProblemReportWindow.StartHook = null; }
            w.Close();

            // after an error report the option is on and says what it holds
            Core.Util.CrashLog.Write(new InvalidOperationException("boom"), "wpf test", fatal: true);
            var w2 = new ProblemReportWindow();
            Assert.True(w2.CrashOpt.IsEnabled);
            Assert.True(w2.CrashOpt.IsChecked);
            Assert.Contains("(1, 1 of them ended the program", (string)w2.CrashOpt.Content);
            w2.Close();
        }
        finally { CleanReports(); File.Delete(zip); }
    });

    [Fact]
    public void DeleteDialog_ShowsVerdicts_AsksBeforeDeletingForGood_AndUpdatesTheMap() => WpfHost.Run(() =>
    {
        try
        {
            CleanDeleteDir();
            Directory.CreateDirectory(Path.Combine(DeleteDir, "sub"));
            var doc = Path.Combine(DeleteDir, "notes.docx");
            var tmp = Path.Combine(DeleteDir, "old.tmp");
            var ini = Path.Combine(DeleteDir, "desktop.ini");
            File.WriteAllBytes(doc, new byte[30_000]);
            File.WriteAllBytes(tmp, new byte[20_000]);
            File.WriteAllText(ini, "[.ShellClassInfo]");
            File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
            File.WriteAllText(Path.Combine(DeleteDir, "sub", "a.txt"), "a");
            AppConfig.Replace(new AppConfig { WatchDog = false });
            var main = OpenMain();
            var view = main.OpenScan(DeleteDir)!;
            PumpUntil(() => view.Tree.Root.ScanComplete && !view.Tree.IsMasterScanning);
            var tree = view.Tree;
            FsNode N(string path) => tree.Find(path)!;

            // right click: the verdict, and the delete commands (disabled with the reason when protected)
            var menu = view.BuildContextMenu(N(doc), new Point());
            var items = menu.Items.OfType<System.Windows.Controls.MenuItem>().ToList();
            Assert.Contains(items, i => (i.Header as string)?.StartsWith("Is it safe to delete?") == true);
            Assert.True(items.Single(i => (i.Header as string)?.StartsWith("Move to the Recycle Bin") == true).IsEnabled);
            var iniMenu = view.BuildContextMenu(N(ini), new Point()).Items.OfType<System.Windows.Controls.MenuItem>().ToList();
            var locked = iniMenu.Single(i => (i.Header as string)?.StartsWith("Move to the Recycle Bin") == true);
            Assert.False(locked.IsEnabled);
            Assert.EndsWith("(protected)", (string)locked.Header);

            // details panel: "Is it safe to delete?" with the rights checked in the background
            view.Model.Selected = N(doc);
            view.ToggleDetails();
            var facts = (System.Windows.Controls.StackPanel)view.GetType().GetField("DetFacts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!;
            string FactsText() => string.Join("\n", Texts(facts));
            PumpUntil(() => FactsText().Contains("Can be deleted"));
            Assert.Contains("Is it safe to delete?", FactsText());
            Render(main, "delete_details.png");

            // for good: "I understand" first; the protected file is shown and left alone
            var w = view.DeleteNodes(new[] { N(doc), N(tmp), N(ini) }, permanent: true, modal: false)!;
            PumpUntil(() => w.Ready.IsCompleted);
            WpfHost.Pump();
            Assert.StartsWith("Delete 2 elements", w.HeadLine.Text);
            Assert.Contains("1 protected element is left out", w.SubLine.Text);
            Assert.Equal(Visibility.Visible, w.Understand.Visibility);
            Assert.Contains("cannot be undone", w.UnderstandText.Text);
            Assert.False(w.DeleteBtn.IsEnabled);
            Assert.Contains("Not deleted — protected (1)", string.Join("\n", Texts(w.ItemsPanel)));
            Assert.Contains("Can be deleted", string.Join("\n", Texts(w.ItemsPanel)));
            Render(w, "delete_dialog.png");
            w.Understand.IsChecked = true;
            Assert.True(w.DeleteBtn.IsEnabled);
            var run = w.RunDelete();
            PumpUntil(() => run.IsCompleted);
            Assert.StartsWith("Deleted 2 elements permanently", w.HeadLine.Text);
            Assert.False(File.Exists(doc));
            Assert.False(File.Exists(tmp));
            Assert.True(File.Exists(ini));
            PumpUntil(() => tree.Find(doc) == null && tree.Find(tmp) == null);
            Assert.NotNull(tree.Find(ini));
            Render(w, "delete_done.png");
            w.Close();

            // to the Recycle Bin (the shell replaced here: the tests leave nothing in the Recycle Bin)
            var recycled = new List<string>();
            Core.Cleanup.Deleter.Fake = (path, recycle) => { lock (recycled) recycled.Add((recycle ? "R " : "F ") + path); return null; };
            var w2 = view.DeleteNodes(new[] { N(Path.Combine(DeleteDir, "sub")) }, permanent: false, modal: false)!;
            PumpUntil(() => w2.Ready.IsCompleted);
            Assert.Equal(Visibility.Collapsed, w2.Understand.Visibility);          // safe, restorable: no extra question
            Assert.True(w2.DeleteBtn.IsEnabled);
            var run2 = w2.RunDelete();
            PumpUntil(() => run2.IsCompleted);
            Assert.Equal(new[] { "R " + Path.Combine(DeleteDir, "sub") }, recycled);
            Assert.StartsWith("Moved 1 element", w2.HeadLine.Text);
            Assert.Contains("until the Recycle Bin is emptied", w2.SubLine.Text);
            w2.Close();

            // the view root itself cannot be deleted from inside
            Assert.Null(view.DeleteNodes(new[] { (FsNode)tree.Root }, permanent: false, modal: false));
            main.Close();
        }
        finally
        {
            Core.Cleanup.Deleter.Fake = null;
            AppConfig.Replace(new AppConfig());
            CleanDeleteDir();
        }
    });

    [Fact]
    public void CleanupWindow_ListsTheCategories_AndDeletesWhatIsTicked() => WpfHost.Run(() =>
    {
        try
        {
            CleanDeleteDir();
            Directory.CreateDirectory(DeleteDir);
            var old = DateTime.UtcNow.AddDays(-20);
            void Make(string rel, int bytes, DateTime when)
            {
                var p = Path.Combine(DeleteDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllBytes(p, new byte[bytes]);
                File.SetLastWriteTimeUtc(p, when);
            }
            Make("big.bin", 3 << 20, old);
            Make(@"work\left1.tmp", 40_000, old);
            Make(@"work\left2.tmp", 50_000, old);
            Make(@"work\fresh.tmp", 60_000, DateTime.UtcNow);
            Make(@"dl\movie.crdownload", 70_000, old);
            AppConfig.Replace(new AppConfig { WatchDog = false, LargeFileMB = 1 });
            var main = OpenMain();
            var view = main.OpenScan(DeleteDir)!;
            PumpUntil(() => view.Tree.Root.ScanComplete && !view.Tree.IsMasterScanning);
            view.ShowCleanup();
            PumpUntil(() => Application.Current.Windows.OfType<CleanupWindow>().Any(), 60000);
            var w = Application.Current.Windows.OfType<CleanupWindow>().Single();
            PumpUntil(() => w.List.Items.Count > 0);
            Assert.Contains("Large files", (string)w.TabLarge.Header);
            Assert.Contains(w.List.Items.OfType<CleanupWindow.Row>(), r => r.Name == "big.bin");
            Render(w, "cleanup_large.png");
            w.Tabs.SelectedItem = w.TabTemp;
            WpfHost.Pump();
            var rows = w.List.Items.OfType<CleanupWindow.Row>().ToList();
            var leftovers = rows.Single(r => r.Name == "Leftover temporary files");
            long onDisk = view.Tree.Find(Path.Combine(DeleteDir, "work", "left1.tmp"))!.DiskSize + view.Tree.Find(Path.Combine(DeleteDir, "work", "left2.tmp"))!.DiskSize;
            Assert.Equal(onDisk, leftovers.Item.Bytes);                           // fresh.tmp is too new
            Assert.True(leftovers.Checked);                                       // safe: ticked from the start
            Assert.Contains(rows, r => r.Name == "Unfinished downloads" && r.Checked);
            Render(w, "cleanup_temp.png");
            w.Tabs.SelectedItem = w.TabPrograms;
            WpfHost.Pump();
            Render(w, "cleanup_programs.png");

            // the ticked ones go through the delete dialog; the window is refreshed afterwards
            var d = w.DeleteChecked(permanent: true, modal: false)!;
            PumpUntil(() => d.Ready.IsCompleted);
            Assert.Equal(3, d.Plan!.TargetCount);
            d.Understand.IsChecked = true;
            var run = d.RunDelete();
            PumpUntil(() => run.IsCompleted);
            Assert.False(File.Exists(Path.Combine(DeleteDir, @"work\left1.tmp")));
            Assert.False(File.Exists(Path.Combine(DeleteDir, @"dl\movie.crdownload")));
            Assert.True(File.Exists(Path.Combine(DeleteDir, @"work\fresh.tmp")));
            Assert.True(File.Exists(Path.Combine(DeleteDir, "big.bin")));        // not ticked
            d.Close();
            w.Tabs.SelectedItem = w.TabTemp;
            PumpUntil(() => { WpfHost.Pump(); return !w.List.Items.OfType<CleanupWindow.Row>().Any(r => r.Name == "Leftover temporary files"); });
            w.Close();
            main.Close();
        }
        finally
        {
            AppConfig.Replace(new AppConfig());
            CleanDeleteDir();
        }
    });

    private static IEnumerable<string> Texts(DependencyObject root)
    {
        if (root is System.Windows.Controls.TextBlock tb) yield return tb.Text;
        int n = VisualTreeHelper.GetChildrenCount(root);
        if (n == 0 && root is System.Windows.Controls.ContentControl { Content: DependencyObject content }) foreach (var t in Texts(content)) yield return t;
        if (n == 0 && root is System.Windows.Controls.Decorator { Child: { } child }) foreach (var t in Texts(child)) yield return t;
        if (n == 0 && root is System.Windows.Controls.Panel panel) foreach (UIElement c in panel.Children) foreach (var t in Texts(c)) yield return t;
        for (int i = 0; i < n; i++) foreach (var t in Texts(VisualTreeHelper.GetChild(root, i))) yield return t;
    }
    [Fact]
    public void ToolWindows_Duplicates_FolderCompare_History_Filters_Start() => WpfHost.Run(() =>
    {
        var oldHistory = Core.Forecast.DiskHistory.Current;
        try
        {
            ToolsTests.Clean();
            var dir = ToolsTests.Dir;
            var data = new byte[2 << 20]; new Random(7).NextBytes(data);
            void Make(string rel, byte[] bytes, DateTime when)
            {
                var p = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllBytes(p, bytes);
                File.SetLastWriteTimeUtc(p, when);
            }
            Make(@"L\photo.jpg", data, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Make(@"R\photo.jpg", data, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Make(@"R\extra\copy.jpg", data, new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Make(@"L\only.txt", new byte[5000], DateTime.UtcNow.AddDays(-3));
            AppConfig.Replace(new AppConfig { WatchDog = false });
            var main = OpenMain();
            var view = main.OpenScan(dir)!;
            PumpUntil(() => view.Tree.Root.ScanComplete && !view.Tree.IsMasterScanning);

            // duplicates: find, keep the oldest, delete the rest for good, the window follows
            view.ShowDuplicates();
            PumpUntil(() => Application.Current.Windows.OfType<DuplicatesWindow>().Any());
            var dw = Application.Current.Windows.OfType<DuplicatesWindow>().Single();
            PumpUntil(() => dw.Groups.Items.Count == 1);
            Assert.Contains("extra copies", dw.HeadLine.Text);
            dw.TickAllButOldest();
            WpfHost.Pump();
            Assert.Equal(2, dw.Ticked.Count());
            Assert.DoesNotContain(dw.Ticked, c => c.Path.EndsWith(@"L\photo.jpg"));
            Render(dw, "duplicates_window.png");
            var del = dw.DeleteTicked(permanent: true, modal: false)!;
            PumpUntil(() => del.Ready.IsCompleted);
            del.Understand.IsChecked = true;
            var run = del.RunDelete();
            PumpUntil(() => run.IsCompleted);
            Assert.True(File.Exists(Path.Combine(dir, @"L\photo.jpg")));
            Assert.False(File.Exists(Path.Combine(dir, @"R\photo.jpg")));
            Assert.False(File.Exists(Path.Combine(dir, @"R\extra\copy.jpg")));
            del.Close();
            PumpUntil(() => dw.Groups.Items.Count == 0);
            dw.Close();

            // two folders side by side
            view.CompareWithFolder(Path.Combine(dir, "R"), (Core.Model.ContainerNode)view.Tree.Find(Path.Combine(dir, "L"))!);
            PumpUntil(() => Application.Current.Windows.OfType<FolderCompareWindow>().Any());
            var fw = Application.Current.Windows.OfType<FolderCompareWindow>().Single();
            PumpUntil(() => fw.HeadLine.Text.Contains("difference"));
            Assert.Contains("Only on the left  ·  2", (string)fw.TabLeft.Header);
            Assert.Contains("Only on the right  ·  1", (string)fw.TabRight.Header);
            Render(fw, "folder_compare.png");
            fw.Close();

            // history: a history of its own (the user's is not touched)
            var h = new Core.Forecast.DiskHistory(Path.Combine(dir, "history.csv"));
            var drive = view.Tree.Root.FullPath.Substring(0, 2);
            var t0 = DateTime.UtcNow.AddDays(-90);
            for (int k = 0; k < 4; k++)
            {
                h.SaveSummary(drive, t0.AddDays(30 * k), new[] { ("Users", (40L + 5 * k) << 30), ("Users\\ali\\Videos", (10L + 4 * k) << 30), ("Games", (60L - 6 * k) << 30), ("Windows", 30L << 30) });
                h.Record(new Core.Forecast.DiskSample(t0.AddDays(30 * k), drive, 500L << 30, (300L - 8 * k) << 30), TimeSpan.Zero);
            }
            Core.Forecast.DiskHistory.Current = h;
            view.ShowHistory();
            PumpUntil(() => Application.Current.Windows.OfType<HistoryWindow>().Any());
            var hw = Application.Current.Windows.OfType<HistoryWindow>().Single();
            PumpUntil(() => hw.List.Items.Count == 4);
            hw.List.SelectAll();
            WpfHost.Pump();
            Assert.Contains("4 complete scans", hw.HeadLine.Text);
            Assert.True(hw.FolderChart.Children.Count > 10);
            Assert.True(hw.ChangeChart.Children.Count > 3);
            Render(hw, "history_window.png");
            hw.Close();

            // a ready-made filter goes into the filter box and applies
            view.AddFilterCondition(">1mb");
            view.AddFilterCondition("<7days");
            Assert.Equal(">1mb;<7days", view.FilterBox.Text);
            Assert.False(view.Model.Filter.IsEmpty);
            main.Close();

            // the start dialog tells the drive kinds
            var sw = new StartWindow();
            WpfHost.Snapshot(sw, Path.Combine(Dir, "start_window.png"));
            sw.Close();
        }
        finally
        {
            Core.Forecast.DiskHistory.Current = oldHistory;
            AppConfig.Replace(new AppConfig());
            ToolsTests.Clean();
        }
    });
}
