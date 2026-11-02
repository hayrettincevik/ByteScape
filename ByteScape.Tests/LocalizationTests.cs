using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ByteScape.Core.Config;
using ByteScape.Core.Localization;
using ByteScape.Wpf;
using ByteScape.Wpf.Windows;
using Xunit;
using Xunit.Abstractions;

namespace ByteScape.Tests;

/// <summary>Tests that switch the interface language: run alone, after the others, so no other test sees a translated text.</summary>
[CollectionDefinition("language", DisableParallelization = true)]
public class LanguageCollection { }

/// <summary>The catalogs against the source, the format and plural rules, language choice and the text extractor.</summary>
public class LocalizationTests
{
    private static readonly Regex Placeholder = new(@"\{\d+(?:,[^}:]*)?(?::[^}]*)?\}");

    internal static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ByteScape.sln"))) return d.FullName;
        throw new InvalidOperationException("ByteScape.sln not found above " + AppContext.BaseDirectory);
    }

    private static readonly Lazy<List<LocKey>> Source = new(() => LocSource.Extract(RepoRoot()));
    private static IEnumerable<string> Translated => Loc.Languages.Select(l => l.Code).Where(c => c != Loc.English);

    public static IEnumerable<object[]> TranslatedLanguages() => Translated.Select(c => new object[] { c });

    [Fact]
    public void Languages_AreTheSevenOffered_EachWithACatalog()
    {
        Assert.Equal(new[] { "en", "tr", "ru", "zh-Hans", "fr", "es", "pt-BR" }, Loc.Languages.Select(l => l.Code));
        foreach (var code in Translated) Assert.NotEmpty(Loc.Load(code));
        Assert.Empty(Loc.Load("en"));
        Assert.Empty(Loc.Load("xx"));
    }

    [Theory]
    [MemberData(nameof(TranslatedLanguages))]
    public void Catalog_TranslatesEverySourceText_AndNothingElse(string code)
    {
        var cat = Loc.Load(code);
        var source = Source.Value;
        Assert.True(source.Count > 1000, "the extractor found only " + source.Count + " texts");
        var missing = source.Where(k => !cat.ContainsKey(k.Id)).Select(k => $"{k.Where}: {k.Key}").ToList();
        Assert.True(missing.Count == 0, $"{code}: {missing.Count} texts not translated:\n" + string.Join("\n", missing.Take(20)));
        var ids = source.Select(k => k.Id).ToHashSet();
        var stale = cat.Keys.Where(id => !ids.Contains(id)).Select(Loc.IdText).ToList();
        Assert.True(stale.Count == 0, $"{code}: {stale.Count} translations of texts the source no longer has: " + string.Join(", ", stale.Take(20)));
        // no two source texts share an id
        Assert.Equal(source.Count, ids.Count);
    }

    [Theory]
    [MemberData(nameof(TranslatedLanguages))]
    public void Catalog_KeepsThePlaceholders_AndThePluralForms(string code)
    {
        var cat = Loc.Load(code);
        var problems = new List<string>();
        var args = Enumerable.Range(0, 10).Select(i => (object)(i + 2)).ToArray();
        foreach (var key in Source.Value)
        {
            if (!cat.TryGetValue(key.Id, out var t)) continue;
            string english = key.IsPlural ? key.Key.Split('\u0001')[1] : key.Key;
            var want = Placeholders(english);
            var forms = key.IsPlural ? t.Split('|') : new[] { t };
            if (key.IsPlural && forms.Length != 1 && forms.Length != Loc.PluralForms(code))
                problems.Add($"{key.Where}: {forms.Length} plural forms, {Loc.PluralForms(code)} wanted: {t}");
            foreach (var f in forms)
            {
                if (!want.SetEquals(Placeholders(f))) problems.Add($"{key.Where}: placeholders {string.Join(" ", Placeholders(f))} instead of {string.Join(" ", want)}: {f}");
                // the translation must survive string.Format (a stray brace would show the raw text)
                if (want.Count > 0)
                    try { _ = string.Format(CultureInfo.InvariantCulture, f, args); }
                    catch (FormatException) { problems.Add($"{key.Where}: not a valid format: {f}"); }
                else if (f.Contains('{') && !english.Contains('{')) problems.Add($"{key.Where}: brace in a plain text: {f}");
            }
            if (english.Count(c => c == '\n') != t.Count(c => c == '\n') && !key.Where.Contains("HelpTexts", StringComparison.Ordinal) && !key.Where.Contains("Branding", StringComparison.Ordinal))
                problems.Add($"{key.Where}: {t.Count(c => c == '\n')} line breaks instead of {english.Count(c => c == '\n')}: {t}");
            if (t.Trim().Length == 0) problems.Add($"{key.Where}: empty translation");
        }
        Assert.True(problems.Count == 0, code + ":\n" + string.Join("\n", problems.Take(30)));
    }

    private static HashSet<string> Placeholders(string s) => Placeholder.Matches(s).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

    [Theory]
    [InlineData("", "tr-TR", "tr")]
    [InlineData("", "ru-RU", "ru")]
    [InlineData("", "zh-CN", "zh-Hans")]
    [InlineData("", "zh-TW", "zh-Hans")]
    [InlineData("", "fr-CA", "fr")]
    [InlineData("", "es-MX", "es")]
    [InlineData("", "pt-PT", "pt-BR")]
    [InlineData("", "en-GB", "en")]
    [InlineData("", "de-DE", "en")]
    [InlineData(null, "ja-JP", "en")]
    [InlineData("fr", "tr-TR", "fr")]
    [InlineData("ZH-HANS", "en-US", "zh-Hans")]
    [InlineData("pt-br", "en-US", "pt-BR")]
    [InlineData("en", "ru-RU", "en")]
    [InlineData("xx", "ru-RU", "ru")]
    [InlineData("qps", "en-US", "qps")]
    public void Resolve_TheSettingThenWindowsLanguage_ThenEnglish(string? setting, string windows, string expected)
        => Assert.Equal(expected, Loc.Resolve(setting, new CultureInfo(windows)));

    [Fact]
    public void PluralIndex_FollowsEachLanguage()
    {
        long[] n = { 0, 1, 2, 3, 4, 5, 11, 12, 14, 21, 22, 25, 101, 111, 1000 };
        int[] Of(string lang) => n.Select(x => Loc.PluralIndex(lang, x)).ToArray();
        Assert.Equal(new[] { 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 }, Of("en"));
        Assert.Equal(new[] { 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 }, Of("es"));
        Assert.Equal(new[] { 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 }, Of("fr"));
        Assert.Equal(new[] { 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 }, Of("pt-BR"));
        Assert.Equal(new[] { 2, 0, 1, 1, 1, 2, 2, 2, 2, 0, 1, 2, 0, 2, 2 }, Of("ru"));
        Assert.All(Of("tr"), i => Assert.Equal(0, i));
        Assert.All(Of("zh-Hans"), i => Assert.Equal(0, i));
        Assert.Equal(0, Loc.PluralIndex("ru", -1));
        foreach (var code in Translated)
            Assert.All(n, x => Assert.InRange(Loc.PluralIndex(code, x), 0, Loc.PluralForms(code) - 1));
    }

    [Fact]
    public void Catalog_ParsesItsOwnEscapes()
    {
        const string text = "line one\nline\ttwo \\ back";
        var escaped = Loc.Escape(text);
        Assert.DoesNotContain('\n', escaped);
        Assert.Equal(text, Loc.Unescape(escaped));
        Assert.Equal("a\nb", Loc.Unescape(Loc.Escape("a\r\nb")));
        var file = "# comment\r\n" + Loc.IdText(Loc.Id(text)) + "\t" + escaped + "\r\n\r\nnot an entry\n0000000g\tbad id\n0000000a\tten\n";
        var cat = Loc.Parse(file);
        Assert.Equal(2, cat.Count);
        Assert.Equal(text, cat[Loc.Id(text)]);
        Assert.Equal("ten", cat[10]);
        // the id: FNV-1a over UTF-16, \r ignored so a file's line ends do not matter
        Assert.Equal(2166136261u, Loc.Id(""));
        Assert.Equal(Loc.Id("a\nb"), Loc.Id("a\r\nb"));
        Assert.Equal("811c9dc5", Loc.IdText(Loc.Id("")));
    }

    [Fact]
    public void Extractor_FindsCallsMarksAndXaml_ButNotCodeValues()
    {
        var keys = new List<LocKey>();
        LocSource.FromCSharp("""
            var a = T("Delete");
            var b = F($"{count:N0} files in {name}");
            var c = P(n, "{0:N0} file", "{0:N0} files", name);
            var d = Fmt("Open {0}", path);
            const string e = /*loc*/"Marked elsewhere";
            var f = T(kind == "access" ? "Denied" : flag ? "Yes" : "No");
            var g = T(x switch { "create" => "Created", _ => "Other" });
            // T("In a comment")
            var h = Other.T("Not ours");
            var i = F($"{(big ? "large" : "small")} {size,8:N1} left");
            """, "Sample.cs", keys);
        Assert.Equal(new[] { "Delete", "{0:N0} files in {1}", "{0:N0} file\u0001{0:N0} files", "Open {0}", "Marked elsewhere", "Denied", "Yes", "No", "Created", "Other", "{0} {1,8:N1} left" },
                     keys.Select(k => k.Key));
        Assert.True(keys[2].IsPlural);
        Assert.Equal("Sample.cs:2", keys[1].Where);

        keys.Clear();
        LocSource.FromXaml("""
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:l="clr-namespace:ByteScape.Wpf" Title="Duplicate files">
              <StackPanel>
                <TextBlock Text="Files from" ToolTip="The smallest file" />
                <TextBox Text="typed" ToolTip="Search box" />
                <Button Content="{Binding Name}" />
                <TextBlock Text="{l:T 'Nothing to show'}" />
                <Border Title="not a window" />
              </StackPanel>
            </Window>
            """, "Sample.xaml", keys);
        Assert.Equal(new[] { "Duplicate files", "Files from", "The smallest file", "Search box", "Nothing to show" }, keys.Select(k => k.Key));
    }
}

/// <summary>The interface in another language: lookups, counts, and every window built in the test ("pseudo") language.</summary>
[Collection("language")]
public class LanguageSwitchTests
{
    private readonly ITestOutputHelper _out;
    public LanguageSwitchTests(ITestOutputHelper o) => _out = o;

    private sealed class Using : IDisposable
    {
        public Using(string code) => Loc.SetLanguage(code);
        public void Dispose() => Loc.SetLanguage(Loc.English);
    }

    [Fact]
    public void Lookups_UseTheCatalog_AndFallBackToEnglish()
    {
        using (new Using("tr"))
        {
            Assert.Equal("tr", Loc.Language);
            Assert.Equal("Kalıcı olarak sil…", Loc.T("Delete permanently…"));
            Assert.Equal("Not in any catalog", Loc.T("Not in any catalog"));
            Assert.Equal("Diskteki programlar", Loc.T("Programs on disk"));
        }
        using (new Using("ru"))
        {
            string Files(long n) => Loc.P(n, "{0:N0} file", "{0:N0} files");
            var forms = Loc.Load("ru")[Loc.Id(Loc.PluralKey("{0:N0} file", "{0:N0} files"))].Split('|');
            Assert.Equal(3, forms.Length);
            Assert.Equal(string.Format(forms[0], 21), Files(21));
            Assert.Equal(string.Format(forms[1], 3), Files(3));
            Assert.Equal(string.Format(forms[2], 11), Files(11));
            Assert.Equal("7 things", Loc.P(7, "{0} thing", "{0} things"));      // not in the catalog: English forms
        }
        using (new Using(Loc.Pseudo))
        {
            Assert.Equal("⟦Delete⟧", Loc.T("Delete"));
            Assert.Equal("…", Loc.T("…"));                                     // no letter: left alone
            Assert.Equal("⟦2 files⟧", Loc.P(2, "{0} file", "{0} files"));
        }
        Assert.Equal("Delete", Loc.T("Delete"));
        Assert.Equal("1 file", Loc.P(1, "{0} file", "{0} files"));
    }

    [Fact]
    public void BrokenTranslation_ShowsTheRawText_InsteadOfFailing()
        => Assert.Equal("{0 broken", Loc.Fmt("{0 broken", 5));

    /// <summary>
    /// Opens every window with data in the test language, where each translated text shows as ⟦…⟧, and looks at what is
    /// on screen: no text may be an English catalog text shown as it is (a text that skipped the translation). The texts
    /// with letters and no ⟦ are listed in the output, to review (they should be data: names, paths, sizes).
    /// </summary>
    [Fact]
    public void EveryWindow_InThePseudoLanguage_ShowsNoUntranslatedText() => WpfHost.Run(() =>
    {
        var english = LocSource.Extract(LocalizationTests.RepoRoot()).SelectMany(k => k.IsPlural ? k.Key.Split('\u0001') : new[] { k.Key })
                                .Where(k => !k.Contains('{')).ToHashSet(StringComparer.Ordinal);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);   // text → where
        using (new Using(Loc.Pseudo))
            OpenEveryWindow((root, where) =>
            {
                foreach (var t in UiTexts.Collect(root))
                    if (!seen.ContainsKey(t)) seen[t] = where;
            });
        var plain = seen.Where(p => !p.Key.Contains('⟦') && p.Key.Any(char.IsLetter)).OrderBy(p => p.Value).ThenBy(p => p.Key).ToList();
        foreach (var (text, where) in plain) _out.WriteLine($"{where}: {text.Replace("\n", "⏎")}");
        var leaks = plain.Where(p => english.Contains(p.Key.Trim()) && p.Key != Branding.Name).Select(p => $"{p.Value}: {p.Key}").ToList();
        Assert.True(leaks.Count == 0, "texts shown untranslated:\n" + string.Join("\n", leaks));
        Assert.True(seen.Count > 500, "only " + seen.Count + " texts seen");
    });

    /// <summary>
    /// Translations run longer than English (French, Russian) or use other fonts (Chinese): in no language may a fixed
    /// text be cut off where the English one fits. BYTESCAPE_LOC_SHOTS=1 saves a picture of every window per language.
    /// </summary>
    [Fact]
    public void EveryLanguage_FitsWhereEnglishFits() => WpfHost.Run(() =>
    {
        var shots = Environment.GetEnvironmentVariable("BYTESCAPE_LOC_SHOTS") == "1" ? Path.Combine(Path.GetTempPath(), "bytescape-tests", "loc") : null;
        if (shots != null) Directory.CreateDirectory(shots);
        Dictionary<string, string> CutOff(string code)
        {
            var found = new Dictionary<string, string>(StringComparer.Ordinal);   // window + place of the text → text
            using (new Using(code))
                OpenEveryWindow((root, where) =>
                {
                    if (root is not Window w) return;
                    foreach (var (path, text) in UiTexts.CutOff(w)) found[where + " " + path] = text;
                    if (shots != null) WpfHost.Snapshot(w, Path.Combine(shots, code + " - " + where + ".png"));
                });
            return found;
        }
        var english = CutOff(Loc.English);
        foreach (var (key, text) in english) _out.WriteLine($"cut off in English too: {key}: {text}");
        var problems = new List<string>();
        foreach (var code in Loc.Languages.Select(l => l.Code).Where(c => c != Loc.English))
            foreach (var (key, text) in CutOff(code))
                if (!english.ContainsKey(key)) problems.Add($"{code}  {key}: {text}");
        Assert.True(problems.Count == 0, problems.Count + " texts cut off in a translation only:\n" + string.Join("\n", problems));
    });

    /// <summary>Every window of the program with data, shown off screen and closed again; <paramref name="look"/> sees each (and a context menu).</summary>
    internal static void OpenEveryWindow(Action<DependencyObject, string> look)
    {
        void Look(DependencyObject root, string where) { WpfHost.Pump(); look(root, where); }
        var oldHistory = Core.Forecast.DiskHistory.Current;
        Core.Update.UpdateCheck.UrlOverride = "https://example.com/update.json";   // the update check's texts show too
        try
        {
            AppConfig.Replace(new AppConfig { WatchDog = false });
            // plain windows
            foreach (var w in new Window[] { new StartWindow(), new AboutWindow(), new LogWindow(), new ProblemReportWindow() })
            { Show(w); Look(w, w.GetType().Name); w.Close(); }
            var uw = new UpdateWindow(new Core.Update.UpdateInfo("9.1.0", "https://example.com/download", "Faster scans.\nSeven languages.", DateTime.UtcNow, new string('A', 64)));
            Show(uw); Look(uw, "UpdateWindow"); uw.Close();
            foreach (HelpWindow.Topic topic in Enum.GetValues(typeof(HelpWindow.Topic)))
            { var w = new HelpWindow(topic); Show(w); Look(w, "Help " + topic); w.Close(); }
            var cw = new ConfigWindow();
            Show(cw);
            for (int i = 0; i < 7; i++) { cw.SelectPage(i); Look(cw, "Settings page " + i); }
            cw.Close();

            // the main window with a scan, its menus, tool windows and the delete dialog
            ToolsTests.Clean();
            var dir = ToolsTests.Dir;
            var data = new byte[2 << 20]; new Random(7).NextBytes(data);
            void Make(string rel, byte[] bytes)
            {
                var p = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllBytes(p, bytes);
            }
            Make(@"L\photo.jpg", data);
            Make(@"R\photo.jpg", data);
            Make(@"L\only.tmp", new byte[5000]);
            AppConfig.Replace(new AppConfig { WatchDog = false, LargeFileMB = 1 });
            var main = new MainWindow(new List<CommandLine.ViewRequest>(), false, openStartDialog: false)
            { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false, Width = 1300, Height = 850 };
            main.Show();
            var view = main.OpenScan(dir)!;
            PumpUntil(() => view.Tree.Root.ScanComplete && !view.Tree.IsMasterScanning);
            view.Model.Selected = view.Tree.Find(Path.Combine(dir, @"L\photo.jpg"));
            view.ToggleDetails();
            Look(main, "Main window");
            // the tour over the main window, step by step
            if (main.StartTour() is { } tour)
                for (int i = 0, n = tour.Count; i < n; i++) { Look(main, "Tour " + (i + 1)); tour.Next(); }
            Look(view.BuildContextMenu(view.Model.Selected!, new Point()), "Context menu");
            var export = new ExportWindow(view.Model);
            Show(export);
            Look(export, "Export");
            export.Close();

            view.ShowCleanup();
            var cleanup = WaitFor<CleanupWindow>();
            PumpUntil(() => cleanup.List.Items.Count > 0);
            foreach (var tab in new[] { cleanup.TabLarge, cleanup.TabTemp, cleanup.TabPrograms }) { cleanup.Tabs.SelectedItem = tab; Look(cleanup, "Cleanup " + cleanup.Tabs.SelectedIndex); }
            cleanup.Close();

            view.ShowDuplicates();
            var dup = WaitFor<DuplicatesWindow>();
            PumpUntil(() => dup.Groups.Items.Count == 1);
            Look(dup, "Duplicates");
            dup.TickAllButOldest();
            var del = dup.DeleteTicked(permanent: true, modal: false)!;
            PumpUntil(() => del.Ready.IsCompleted);
            Look(del, "Delete dialog");
            del.Close();
            dup.Close();

            view.CompareWithFolder(Path.Combine(dir, "R"), (Core.Model.ContainerNode)view.Tree.Find(Path.Combine(dir, "L"))!);
            var fc = WaitFor<FolderCompareWindow>();
            PumpUntil(() => fc.HeadLine.Text.Length > 0 && !fc.HeadLine.Text.Contains("Comparing", StringComparison.Ordinal) && !fc.HeadLine.Text.EndsWith("…⟧", StringComparison.Ordinal));
            Look(fc, "Folder compare");
            fc.Close();

            var h = new Core.Forecast.DiskHistory(Path.Combine(dir, "history.csv"));
            var drive = view.Tree.Root.FullPath.Substring(0, 2);
            var t0 = DateTime.UtcNow.AddDays(-90);
            for (int k = 0; k < 4; k++)
            {
                h.SaveSummary(drive, t0.AddDays(30 * k), new[] { ("Users", (40L + 5 * k) << 30), ("SteamLibrary", (60L - 6 * k) << 30) });
                h.Record(new Core.Forecast.DiskSample(t0.AddDays(30 * k), drive, 500L << 30, (300L - 8 * k) << 30), TimeSpan.Zero);
            }
            Core.Forecast.DiskHistory.Current = h;
            view.ShowHistory();
            var hw = WaitFor<HistoryWindow>();
            PumpUntil(() => hw.List.Items.Count == 2);
            hw.List.SelectAll();
            Look(hw, "History");
            hw.Close();
            view.ShowForecast();
            var fw = WaitFor<ForecastWindow>();
            Look(fw, "Forecast");
            fw.Close();

            main.Close();

            // a snapshot comparison (the sample tree against itself plus one new file)
            var sample = Wpf.Controls.SampleTree.Create();
            var sampleView = new Wpf.Views.ScanView(sample, sample.Root, "sample");
            ((Core.Model.ContainerNode)sample.Root.FindChild("Users")!).AddChild(new Core.Model.FileNode("new.iso") { LogicalSize = 1 << 28, DiskSize = 1 << 28 });
            var diff = Core.Compare.TreeDiff.Compare(Wpf.Controls.SampleTree.Create().Root, "baseline.sns", DateTime.UtcNow, sample.Root, sample.Sync);
            sampleView.Model.Diff = diff;
            var cmp = new CompareWindow(sampleView, diff);
            Show(cmp);
            Look(cmp, "Compare");
            cmp.Close();

            // programs, games and projects, each with the data its tests use
            Core.Apps.AppCatalog.SetShared(AppsTests.Catalog());
            var appDrive = AppsTests.BuildDrive();
            var appView = new Wpf.Views.ScanView(new Core.Scanning.ScanTree(appDrive, live: false), appDrive, "C:\\");
            var host = new Window { Content = appView, Width = 1000, Height = 640, Title = "host" };
            Show(host);
            appView.ShowAppFootprint();
            var apps = WaitFor<AppsWindow>();
            PumpUntil(() => apps.AppList.Items.Count > 0);
            apps.AppList.SelectedIndex = 0;
            Look(apps, "Programs on disk");
            apps.Close(); host.Close();

            Core.Games.GameLibrary.SetShared(Core.Games.GameLibrary.Load(GameTests.BuildLayout()));
            var gameTree = GameTests.Scan();
            var gameView = new Wpf.Views.ScanView(gameTree, gameTree.Root, "games");
            host = new Window { Content = gameView, Width = 1000, Height = 640, Title = "host" };
            Show(host);
            gameView.ShowGames();
            var games = WaitFor<GamesWindow>();
            PumpUntil(() => games.GameList.Items.Count > 0);
            games.GameList.SelectedIndex = 0;
            Look(games, "Games");
            games.Close(); host.Close();

            DevTests.BuildLayout();
            var devTree = DevTests.Scan();
            var devView = new Wpf.Views.ScanView(devTree, devTree.Root, "dev");
            host = new Window { Content = devView, Width = 1000, Height = 640, Title = "host" };
            Show(host);
            devView.ShowDev();
            var dev = WaitFor<DevWindow>();
            PumpUntil(() => dev.ProjectList.Items.Count > 0);
            dev.ProjectList.SelectedIndex = 0;
            Look(dev, "Developer leftovers");
            dev.Close(); host.Close();
        }
        finally
        {
            foreach (Window w in Application.Current.Windows) w.Close();
            Core.Update.UpdateCheck.UrlOverride = null;
            Core.Forecast.DiskHistory.Current = oldHistory;
            Core.Apps.AppCatalog.SetShared(null);
            Core.Games.GameLibrary.SetShared(null);
            AppConfig.Replace(new AppConfig());
            ToolsTests.Clean();
            try { Directory.Delete(GameTests.Dir, true); } catch { }
            try { Directory.Delete(DevTests.Dir, true); } catch { }
        }
    }

    private static void Show(Window w)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -10000; w.Top = -10000; w.ShowActivated = false;
        w.Show();
        WpfHost.Pump();
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

/// <summary>The texts a window shows or can show: its visual tree, plus menus, context menus, tool tips and list columns.</summary>
internal static class UiTexts
{
    public static List<string> Collect(DependencyObject root)
    {
        var list = new List<string>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Walk(root, list, seen);
        return list;
    }

    /// <summary>
    /// Fixed one-line texts (no wrapping, no "…" trimming) that do not fit: wider than the room layout gave them or one
    /// of their containers, unless that container scrolls sideways. Rows of lists are left out (their names and paths are
    /// data). The place is the text's position in the visual tree, the same in every language.
    /// </summary>
    public static List<(string Path, string Text)> CutOff(Window w)
    {
        var list = new List<(string, string)>();
        void Walk(DependencyObject d, string path)
        {
            if (d is ListBoxItem or TreeViewItem or DataGridRow) return;
            if (d is TextBlock tb && IsCut(tb)) list.Add((path, tb.Text));
            for (int i = 0, n = VisualTreeHelper.GetChildrenCount(d); i < n; i++) Walk(VisualTreeHelper.GetChild(d, i), path + "/" + i);
        }
        Walk(w, "");
        return list;
    }

    private static bool IsCut(TextBlock tb)
    {
        if (!tb.IsVisible || tb.ActualWidth <= 0 || string.IsNullOrWhiteSpace(tb.Text)) return false;
        if (tb.TextTrimming != TextTrimming.None || tb.TextWrapping != TextWrapping.NoWrap) return false;
        var text = new FormattedText(tb.Text, CultureInfo.CurrentUICulture, tb.FlowDirection, new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
                                     tb.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(tb).PixelsPerDip);
        double width = text.WidthIncludingTrailingWhitespace + tb.Padding.Left + tb.Padding.Right;
        for (DependencyObject? d = tb; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ScrollContentPresenter { CanHorizontallyScroll: true }) return false;
            if (d is not FrameworkElement fe) continue;
            if (System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(fe) is { } clip && !clip.Bounds.IsEmpty)
            {
                var r = tb.TransformToAncestor(fe).TransformBounds(new Rect(0, 0, width, tb.ActualHeight));
                if (r.Right > clip.Bounds.Right + 1.5 || r.Left < clip.Bounds.Left - 1.5) return true;
            }
            if (d is Window) break;
        }
        return false;
    }

    private static void Add(List<string> list, object? o)
    {
        if (o is string s && s.Length > 0) list.Add(s);
    }

    private static void Walk(object o, List<string> list, HashSet<object> seen)
    {
        if (o is string s) { Add(list, s); return; }
        if (o is not DependencyObject d || !seen.Add(d)) return;
        switch (d)
        {
            case TextBox or PasswordBox: return;                   // what the user typed, or a report (reports stay English)
            case Window w: Add(list, w.Title); break;
            case TextBlock tb: Add(list, tb.Text); break;
            case HeaderedItemsControl hi: Add(list, hi.Header); break;
            case HeaderedContentControl hc: Add(list, hc.Header); Add(list, hc.Content); break;
            case ContentControl cc: Add(list, cc.Content); break;
            case System.Windows.Documents.Run r: Add(list, r.Text); break;
        }
        if (d is FrameworkElement fe)
        {
            if (fe.ToolTip is string tip) Add(list, tip);
            else if (fe.ToolTip is DependencyObject tipObject) Walk(tipObject, list, seen);
            if (fe.ContextMenu != null) Walk(fe.ContextMenu, list, seen);
        }
        if (d is ListView { View: GridView gv }) foreach (var col in gv.Columns) Add(list, col.Header);
        if (d is ItemsControl ic && d is MenuItem or Menu or ContextMenu or ComboBox or TabControl)
            foreach (var item in ic.Items) Walk(item, list, seen);
        foreach (var child in LogicalTreeHelper.GetChildren(d)) Walk(child, list, seen);
        if (d is Visual or System.Windows.Media.Media3D.Visual3D)
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i), list, seen);
    }
}
