using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ByteScape.Core.Config;
using ByteScape.Core.Model;
using ByteScape.Core.Snapshot;
using ByteScape.Wpf;
using ByteScape.Wpf.Controls;
using ByteScape.Wpf.Views;
using ByteScape.Wpf.Windows;
using Microsoft.Win32;
using Xunit;
using Xunit.Abstractions;

namespace ByteScape.Tests;

/// <summary>
/// Presses every button, menu item and shortcut of the program, in every window it reaches, the way a user would: the
/// main window with three scans (plain files, games, projects), each tool window, every tab and settings page, the map's
/// right-click menu, the "More" menu, the ready-made filters. Message boxes, file pickers, modal windows, started programs,
/// the clipboard and the Windows context menu go through <see cref="Dialogs"/>: they are answered by the test and recorded,
/// nothing reaches the screen, the clipboard or another program, and deleting only pretends (<c>Deleter.Fake</c>). No
/// command may throw, and every window must be reached.
/// </summary>
[Collection("language")]
public class EveryCommandTests
{
    private readonly ITestOutputHelper _out;
    public EveryCommandTests(ITestOutputHelper o) => _out = o;

    private const int MaxDepth = 3;
    private readonly List<string> _pressed = new();
    private readonly List<string> _failures = new();
    private readonly HashSet<string> _windowsSeen = new();
    private readonly List<string> _deleted = new();
    private Dialogs.Script _script = null!;
    private string _out1 = "", _other = "", _snapshot = "";
    private int _saves;

    [Fact]
    public void EveryButtonMenuItemAndShortcut_Works() => WpfHost.Run(() =>
    {
        var app = Application.Current;
        void Unhandled(object s, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) { _failures.Add("unhandled: " + e.Exception.GetType().Name + ": " + e.Exception.Message); e.Handled = true; }
        app.DispatcherUnhandledException += Unhandled;
        var oldHistory = Core.Forecast.DiskHistory.Current;
        MainWindow? main = null;
        try
        {
            // data: plain files with a duplicate, a game library, developer projects; a snapshot and a second folder to compare
            ToolsTests.Clean();
            var dir = ToolsTests.Dir;
            var data = new byte[1 << 20]; new Random(3).NextBytes(data);
            Directory.CreateDirectory(Path.Combine(dir, "L")); Directory.CreateDirectory(Path.Combine(dir, "R"));
            File.WriteAllBytes(Path.Combine(dir, "L", "photo.jpg"), data);
            File.WriteAllBytes(Path.Combine(dir, "R", "photo.jpg"), data);
            File.WriteAllBytes(Path.Combine(dir, "L", "only.tmp"), new byte[5000]);
            _other = Path.Combine(dir, "R");
            _out1 = Path.Combine(dir, "out"); Directory.CreateDirectory(_out1);
            _snapshot = Path.Combine(_out1, "before.sns");
            Core.Apps.AppCatalog.SetShared(AppsTests.Catalog());
            Core.Games.GameLibrary.SetShared(Core.Games.GameLibrary.Load(GameTests.BuildLayout()));
            DevTests.BuildLayout();
            Core.Forecast.DiskHistory.Current = new Core.Forecast.DiskHistory(Path.Combine(_out1, "history.csv"));
            AppConfig.Replace(new AppConfig { WatchDog = false, LargeFileMB = 1, ZoomTimeMs = 0 });

            Core.Cleanup.Deleter.Fake = (path, recycle) => { _deleted.Add((recycle ? "recycle " : "delete ") + path); return null; };
            _script = new Dialogs.Script { Pick = Pick, Modal = Modal };
            Dialogs.Test = _script;

            main = new MainWindow(new List<CommandLine.ViewRequest>(), false, openStartDialog: false)
            { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false, Width = 1300, Height = 850 };
            main.Show();
            foreach (var path in new[] { dir, GameTests.Dir, DevTests.Dir })
            {
                var v = main.OpenScan(path)!;
                PumpUntil(() => v.Tree.Root.ScanComplete && !v.Tree.IsMasterScanning);
            }
            var tabs = main.Tabs;
            var views = tabs.Items.OfType<TabItem>().Select(t => (ScanView)t.Content).ToList();
            // the snapshot of the first folder, from before only.tmp was there: comparing with it finds that file added
            SnsFormat.Write(_snapshot, views[0].Tree.Root, n => n.Name != "only.tmp");
            views[0].Model.Selected = views[0].Tree.Find(Path.Combine(dir, "L", "photo.jpg"));

            // 1. every view: its right-click menu (a file and a folder), then every button of the window
            for (int i = 0; i < views.Count; i++)
            {
                tabs.SelectedIndex = i;
                Settle();
                var v = views[i];
                var file = v.Tree.Root.Descendants().OfType<FileNode>().FirstOrDefault();
                var folder = v.Tree.Root.Children.OfType<ContainerNode>().FirstOrDefault();
                foreach (var node in new FsNode?[] { file, folder })
                    if (node != null) PressMenu(v.BuildContextMenu(node, new Point()), $"view {i + 1} right-click menu of {node.Name}", 1);
                PressWindow(main, $"main window, view {i + 1}", 0);
            }

            // 2. the "More" menu (its items, without opening it)
            tabs.SelectedIndex = 0;
            Settle();
            PressMenu(main.MoreMenu, "More menu", 1);

            // 3. every shortcut of the window
            foreach (var (key, mods) in Shortcuts)
            {
                if (main.Tabs.Items.Count == 0) break;
                RunCommand($"shortcut {mods}+{key}".Replace("None+", ""), () => main.HandleShortcut(key, mods, null), 1);
            }
            // the map's own keys
            if (main.Tabs.SelectedItem is TabItem { Content: ScanView sv })
                foreach (var key in new[] { Key.Down, Key.Right, Key.Left, Key.Up, Key.End, Key.Home })
                    RunCommand("map key " + key, () => sv.Map.HandleKey(key, ModifierKeys.None), 1);

            // 4. the window of a newer version (the update check itself asks no server here)
            RunCommand("a newer version: open the download page", () =>
            {
                var u = new UpdateWindow(new Core.Update.UpdateInfo("9.9.9", "https://example.org/bytescape", "Faster scans.\nNew colours.", DateTime.Now, new string('a', 64))) { Owner = main };
                u.Show();
                Click(u.DownloadBtn);
                Assert.False(u.IsVisible);
            }, 1);
            RunCommand("a newer version, skipped", () => new UpdateWindow(new Core.Update.UpdateInfo("9.9.9", "https://example.org/bytescape", "", null, "")) { Owner = main }.Show(), 1);

            // 5. last: restart as administrator (the new process is only recorded) closes the window
            CloseOthers(main);
            RunCommand("Restart as administrator", () => main.RestartAsAdministrator(), 1);
            Assert.Contains(_script.Started, p => p.Verb == "runas");
            Assert.False(main.IsLoaded, "the window stays open after a restart");
        }
        finally
        {
            Dialogs.Test = null;
            Core.Cleanup.Deleter.Fake = null;
            app.DispatcherUnhandledException -= Unhandled;
            foreach (Window w in app.Windows) w.Close();
            Core.Forecast.DiskHistory.Current = oldHistory;
            Core.Apps.AppCatalog.SetShared(null);
            Core.Games.GameLibrary.SetShared(null);
            AppConfig.Replace(new AppConfig());
            ToolsTests.Clean();
            try { Directory.Delete(GameTests.Dir, true); } catch { }
            try { Directory.Delete(DevTests.Dir, true); } catch { }
        }

        foreach (var p in _pressed) _out.WriteLine("pressed  " + p);
        foreach (var l in _script.Log) _out.WriteLine("answered " + l);
        foreach (var d in _deleted) _out.WriteLine("fake     " + d);
        _out.WriteLine($"{_pressed.Count} commands, {_script.Log.Count} answered, {_script.Started.Count} programs started, {_deleted.Count} fake deletes, windows: {string.Join(", ", _windowsSeen.OrderBy(x => x))}");

        Assert.True(_failures.Count == 0, _failures.Count + " commands failed:\n" + string.Join("\n", _failures));
        foreach (var w in new[] { "StartWindow", "ConfigWindow", "ExportWindow", "AboutWindow", "HelpWindow", "LogWindow", "ProblemReportWindow",
                                  "AppsWindow", "GamesWindow", "DevWindow", "ForecastWindow", "CleanupWindow", "DuplicatesWindow", "DeleteWindow",
                                  "FolderCompareWindow", "HistoryWindow", "CompareWindow", "LayoutDebugWindow", "UpdateWindow" })
            Assert.True(_windowsSeen.Contains(w), w + " was never reached");
        Assert.True(_pressed.Count > 400, "only " + _pressed.Count + " commands pressed");
        Assert.NotEmpty(_deleted);                                                          // a delete really went through the dialog
        Assert.Contains(_script.Started, p => p.FileName.StartsWith(Branding.IssueUrl + "?title=", StringComparison.Ordinal));   // "Open an issue"
        Assert.Contains(_script.Started, p => p.FileName.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(_script.Copied);
        Assert.Contains(_script.Started, p => p.FileName == "https://example.org/bytescape");
    });

    private static readonly (Key, ModifierKeys)[] Shortcuts =
    {
        (Key.T, ModifierKeys.Control), (Key.T, ModifierKeys.Control), (Key.F, ModifierKeys.Control), (Key.F, ModifierKeys.Control),
        (Key.U, ModifierKeys.Control), (Key.U, ModifierKeys.Control), (Key.OemPlus, ModifierKeys.Control), (Key.OemMinus, ModifierKeys.Control),
        (Key.L, ModifierKeys.Control), (Key.D1, ModifierKeys.Control), (Key.D2, ModifierKeys.Control), (Key.D3, ModifierKeys.Control),
        (Key.D4, ModifierKeys.Control), (Key.D0, ModifierKeys.Control), (Key.C, ModifierKeys.Control), (Key.Up, ModifierKeys.Control),
        (Key.Home, ModifierKeys.Control), (Key.D1, ModifierKeys.None), (Key.D0, ModifierKeys.None), (Key.Back, ModifierKeys.None),
        (Key.Back, ModifierKeys.Shift), (Key.Enter, ModifierKeys.None), (Key.Pause, ModifierKeys.None), (Key.Pause, ModifierKeys.None),
        (Key.P, ModifierKeys.Control), (Key.G, ModifierKeys.Control), (Key.B, ModifierKeys.Control), (Key.K, ModifierKeys.Control),
        (Key.H, ModifierKeys.Control), (Key.D, ModifierKeys.Control), (Key.D, ModifierKeys.Control | ModifierKeys.Shift),
        (Key.C, ModifierKeys.Control | ModifierKeys.Shift), (Key.Delete, ModifierKeys.Control | ModifierKeys.Shift),
        (Key.Delete, ModifierKeys.None), (Key.Delete, ModifierKeys.Shift), (Key.E, ModifierKeys.Control), (Key.N, ModifierKeys.Control),
        (Key.W, ModifierKeys.Control),
    };

    // ------------------------------------------------------------------ answers

    private bool? Pick(CommonDialog d)
    {
        switch (d)
        {
            case OpenFolderDialog f: f.FolderName = _other; return true;
            case SaveFileDialog s:
                var ext = Path.GetExtension(s.FileName);
                if (ext.Length == 0) ext = System.Text.RegularExpressions.Regex.Match(s.Filter ?? "", @"\*(\.\w+)").Groups[1].Value;
                s.FileName = Path.Combine(_out1, "save" + ++_saves + (ext.Length > 0 ? ext : ".txt"));
                return true;
            case OpenFileDialog o when (o.Filter ?? "").Contains("*.sns"):
                o.FileName = _snapshot; return true;
            default: return false;   // imports of settings: cancelled (their own tests cover them)
        }
    }

    private int _depth;

    private bool? Modal(Window w)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -10000; w.Top = -10000; w.ShowActivated = false;
        w.Show();
        Settle();
        if (_depth < MaxDepth) PressWindow(w, "modal " + w.GetType().Name, _depth + 1);
        if (w.IsLoaded) w.Close();
        return false;
    }

    // ------------------------------------------------------------------ pressing

    /// <summary>Runs one command; then presses the items of the menus it wanted to open and the buttons of the windows it opened (and closes those).</summary>
    private void RunCommand(string what, Action command, int depth)
    {
        var before = Application.Current.Windows.OfType<Window>().ToHashSet();
        int menus = _script.Menus.Count;
        _pressed.Add(what);
        int saved = _depth;
        _depth = depth;
        try { command(); }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
            _failures.Add($"{what}: {inner.GetType().Name}: {inner.Message}");
        }
        Settle();
        // the tour: every step, forwards and one back, to its end
        for (int step = 0; Tour.Running is { } tour && step < 40; step++)
        {
            _pressed.Add($"{what} > tour step {tour.Index + 1}/{tour.Count}");
            if (step == 1) Click(tour.BackBtn); else Click(tour.NextBtn);
            Settle();
        }
        if (Tour.Running != null) _failures.Add(what + ": the tour does not end");
        for (int m = menus; m < _script.Menus.Count; m++) PressMenu(_script.Menus[m], what + " > menu", depth + 1);
        foreach (var w in Application.Current.Windows.OfType<Window>().Where(w => !before.Contains(w)).ToList())
        {
            if (!w.IsLoaded) continue;
            _windowsSeen.Add(w.GetType().Name);
            if (depth < MaxDepth) PressWindow(w, what + " > " + w.GetType().Name, depth + 1);
            if (w.IsLoaded && w is not MainWindow) w.Close();
        }
        _depth = saved;
    }

    private void PressWindow(Window w, string where, int depth)
    {
        _windowsSeen.Add(w.GetType().Name);
        var done = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        bool Open() => w.IsLoaded && w.IsVisible;
        // closers: false on the pages and tabs before the last, so the window stays open for them
        void PressVisible(bool closers)
        {
            SelectFirstItems(w);
            // every choice of every drop-down list, then back to the one it had
            foreach (var cb in Descendants<ComboBox>(w).Where(c => !done.Contains(c) && c.TemplatedParent == null && Ancestor<ColorPicker>(c) == null).ToList())
            {
                if (!Open()) return;
                if (!cb.IsVisible || !cb.IsEnabled) continue;
                done.Add(cb);
                int original = cb.SelectedIndex;
                for (int i = 0; i < Math.Min(cb.Items.Count, 12) && Open(); i++)
                {
                    int k = i;
                    RunCommand($"{where}: {Describe(cb)} = {ItemText(cb.Items[k])}", () => cb.SelectedIndex = k, depth);
                }
                if (Open()) cb.SelectedIndex = original;
            }
            var buttons = Descendants<ButtonBase>(w).Where(b => !done.Contains(b) && Pressable(b, w)).ToList();
            // plain buttons first, then switches (pressed twice: on and back), the ones that close the window last
            foreach (var b in buttons.OrderBy(b => ClosesWindow(b) ? 2 : b is ToggleButton ? 1 : 0))
            {
                if (!Open()) return;
                if (!closers && ClosesWindow(b)) continue;
                done.Add(b);
                if (!b.IsVisible || !b.IsEnabled) continue;
                RunCommand($"{where}: {Describe(b)}", () => Click(b), depth);
                if (b is ToggleButton and not RadioButton && Open() && b.IsVisible && b.IsEnabled)
                    RunCommand($"{where}: {Describe(b)} (back)", () => Click(b), depth);
            }
        }
        if (w is ConfigWindow cw)
            for (int page = 0; page < 7 && Open(); page++) { cw.SelectPage(page); Settle(); PressVisible(false); }
        // every tab of every tab control of the window
        foreach (var tc in Descendants<TabControl>(w).Where(t => !ReferenceEquals(t, (w as MainWindow)?.Tabs)).ToList())
            for (int t = 0; t < tc.Items.Count && Open(); t++) { tc.SelectedIndex = t; Settle(); PressVisible(false); }
        if (Open()) PressVisible(true);
    }

    private static string ItemText(object item) => item switch
    {
        ContentControl c => c.Content?.ToString() ?? "",
        _ => item?.ToString() ?? "",
    };

    private void PressMenu(ItemsControl menu, string where, int depth)
    {
        foreach (var item in menu.Items.OfType<MenuItem>().ToList())
        {
            string name = Header(item);
            if (!item.IsEnabled || item.Visibility != Visibility.Visible || Skip(name)) continue;
            if (item.Items.Count > 0) { PressMenu(item, where + " > " + name, depth); continue; }
            RunCommand($"{where}: {name}", () => Invoke(item, "OnClick"), depth);
        }
    }

    private static void Click(ButtonBase b) => Invoke(b, "OnClick");

    private static void Invoke(object target, string method)
    {
        for (var t = target.GetType(); t != null; t = t.BaseType)
        {
            var m = t.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly, Type.EmptyTypes);
            if (m != null) { m.Invoke(target, null); return; }
        }
        throw new MissingMethodException(target.GetType().Name, method);
    }

    /// <summary>Buttons a user can press: not parts of a control's template, not the colour popup, not the ones that change Windows itself.</summary>
    private static bool Pressable(ButtonBase b, Window w)
    {
        if (b.TemplatedParent != null || Ancestor<ColorPicker>(b) != null) return false;
        if (b.Name is "ExplorerMenuBtn") return false;                   // writes Explorer's menu to the registry: only when the user asks
        if (b.Style == (Style)Application.Current.TryFindResource("CardButton")) return false;   // a drive card would scan a whole drive
        return !Skip(Describe(b));
    }

    private static bool Skip(string name) =>
        name.Contains("administrator", StringComparison.OrdinalIgnoreCase) || name is "Exit";

    private static bool ClosesWindow(ButtonBase b) =>
        b is Button { IsCancel: true } or Button { IsDefault: true } || Describe(b) is "Close" or "Close view" or "OK" or "Done" or "Cancel" or "Later";

    private static string Describe(FrameworkElement b)
    {
        var name = AutomationProperties.GetName(b);
        if (string.IsNullOrEmpty(name)) name = (b as ContentControl)?.Content as string ?? b.ToolTip as string ?? b.Name;
        if (string.IsNullOrEmpty(name)) name = b.GetType().Name;
        return name.Length > 60 ? name[..60] : name;
    }

    private static string Header(MenuItem m) => (m.Header as string ?? m.Header?.ToString() ?? "").Trim();

    private static void SelectFirstItems(DependencyObject root)
    {
        foreach (var s in Descendants<Selector>(root))
            if (s is not TabControl and not ComboBox && s.Items.Count > 0 && s.SelectedIndex < 0) s.SelectedIndex = 0;
    }

    private void CloseOthers(Window keep)
    {
        foreach (var w in Application.Current.Windows.OfType<Window>().Where(w => !ReferenceEquals(w, keep)).ToList()) w.Close();
        Settle();
    }

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is T t) yield return t;
            int n = d is Visual ? VisualTreeHelper.GetChildrenCount(d) : 0;
            for (int i = n - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
    }

    private static T? Ancestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var x = VisualTreeHelper.GetParent(d); x != null; x = VisualTreeHelper.GetParent(x))
            if (x is T t) return t;
        return null;
    }

    /// <summary>Lets the dispatcher and the background work of a command finish (scans, filters, exports of the tiny test data).</summary>
    private static void Settle()
    {
        for (int i = 0; i < 4; i++) { WpfHost.Pump(); Thread.Sleep(15); }
    }

    private static void PumpUntil(Func<bool> done, int ms = 30000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < ms) { WpfHost.Pump(); Thread.Sleep(10); }
        Assert.True(done(), "timed out");
    }
}
