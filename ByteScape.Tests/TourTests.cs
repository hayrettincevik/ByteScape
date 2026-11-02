using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ByteScape.Core.Config;
using ByteScape.Wpf;
using Xunit;
using Xunit.Abstractions;

namespace ByteScape.Tests;

/// <summary>The first-start tour over the main window.</summary>
[Collection("config")]
public class TourTests
{
    private readonly ITestOutputHelper _out;
    public TourTests(ITestOutputHelper o) => _out = o;

    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "tour");
    private static readonly string Shots = Path.Combine(Path.GetTempPath(), "bytescape-tests", "wpf");

    private static string Data()
    {
        Directory.CreateDirectory(Path.Combine(Dir, "a"));
        File.WriteAllBytes(Path.Combine(Dir, "a", "big.bin"), new byte[300_000]);
        File.WriteAllBytes(Path.Combine(Dir, "small.txt"), new byte[20_000]);
        return Dir;
    }

    private static MainWindow Main()
    {
        var m = new MainWindow(new List<CommandLine.ViewRequest>(), false, openStartDialog: false)
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false, Width = 1300, Height = 850 };
        m.Show();
        WpfHost.Pump();
        return m;
    }

    private static void PumpUntil(Func<bool> done, int ms = 20000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < ms) { WpfHost.Pump(); Thread.Sleep(10); }
    }

    [Fact]
    public void Tour_OutlinesEachThing_KeepsItsCardOnScreen_AndEndsForGood() => WpfHost.Run(() =>
    {
        try
        {
            AppConfig.Replace(new AppConfig { WatchDog = false });
            var main = Main();
            Assert.False(main.AutoTour);                                   // test windows (no start dialog) never start it
            var view = main.OpenScan(Data())!;
            PumpUntil(() => view.Tree.Root.ScanComplete && !view.Tree.IsMasterScanning);
            var tour = main.StartTour()!;
            Assert.Same(tour, Tour.Running);
            Assert.Equal(11, tour.Count);
            Directory.CreateDirectory(Shots);
            for (int i = 0; i < tour.Count; i++)
            {
                WpfHost.Pump();
                Assert.Equal(i, tour.Index);
                var a = tour.Adorner!;
                var room = new Rect(((FrameworkElement)main.Content).RenderSize);
                Assert.Equal(tour.Current.NeedsTarget, a.Hole != null);
                Assert.True(room.Contains(a.CardBounds), $"step {i + 1}: the card {a.CardBounds} leaves the window {room}");
                // a toolbar button stays visible next to its card; over the map the card sits on the map
                if (a.Hole is { Height: < 120 } h) Assert.False(h.IntersectsWith(a.CardBounds), $"step {i + 1}: the card covers what it explains");
                Assert.False(string.IsNullOrWhiteSpace(tour.BodyText.Text));
                var bmp = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(main);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var fs = File.Create(Path.Combine(Shots, $"tour_{i + 1:00}.png"))) enc.Save(fs);
                tour.Next();                                               // the last step ends it
            }
            Assert.Null(Tour.Running);
            Assert.True(AppConfig.Current.TourShown);
            Assert.Null(AdornerLayer.GetAdornerLayer((Visual)main.Content)!.GetAdorners((UIElement)main.Content));

            // keys: arrows move, Esc ends, any other key waits for the tour
            var again = main.StartTour()!;
            Assert.True(again.HandleKey(Key.Right));
            Assert.Equal(1, again.Index);
            again.HandleKey(Key.Left);
            Assert.Equal(0, again.Index);
            Assert.True(again.HandleKey(Key.Delete));
            Assert.False(again.HandleKey(Key.Tab));
            again.HandleKey(Key.Escape);
            Assert.Null(Tour.Running);
            main.Close();

            // no view yet: the steps about the map, the filter and the details are left out
            var bare = Main();
            var small = bare.StartTour()!;
            Assert.True(small.Count < 11 && small.Count >= 6, "steps without a view: " + small.Count);
            small.Close();
            bare.Close();
            _out.WriteLine("images in " + Shots);
        }
        finally
        {
            Tour.Running?.Close();
            AppConfig.Replace(new AppConfig());
            try { File.Delete(AppConfig.ConfigPath); } catch { }
            try { Directory.Delete(Dir, true); } catch { }
        }
    });

    [Fact]
    public void Tour_StartsByItself_OnlyTheFirstTime() => WpfHost.Run(() =>
    {
        try
        {
            AppConfig.Replace(new AppConfig { WatchDog = false });
            var main = Main();
            main.AutoTour = true;                                          // as when started the usual way
            main.OpenScan(Data());
            PumpUntil(() => Tour.Running != null, 8000);
            Assert.NotNull(Tour.Running);
            Tour.Running!.Close();
            Assert.True(AppConfig.Current.TourShown);
            main.Close();

            var second = Main();
            second.AutoTour = true;
            second.OpenScan(Dir);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2500) { WpfHost.Pump(); Thread.Sleep(20); }
            Assert.Null(Tour.Running);                                     // seen once: not again by itself
            second.Close();
        }
        finally
        {
            Tour.Running?.Close();
            AppConfig.Replace(new AppConfig());
            try { File.Delete(AppConfig.ConfigPath); } catch { }
            try { Directory.Delete(Dir, true); } catch { }
        }
    });
}
