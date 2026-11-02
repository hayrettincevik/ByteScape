using System.Drawing;
using System.Reflection;
using System.Xml.Linq;
using ByteScape.Core.Config;
using ByteScape.Core.Export;
using ByteScape.Core.Util;
using Xunit;
using Xunit.Abstractions;

namespace ByteScape.Tests;

/// <summary>Configuration model, persistence and the configuration dialog (driven without a screen).</summary>
[Collection("config")]
public class ConfigTests
{
    private readonly ITestOutputHelper _out;
    public ConfigTests(ITestOutputHelper o) => _out = o;

    private static string TempFile(string name) => Path.Combine(Path.GetTempPath(), "bytescape-tests", name);

    static ConfigTests()
    {
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "bytescape-tests"));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Every settable public property of AppConfig (the model surface that must survive save/load and the dialog).</summary>
    private static IEnumerable<PropertyInfo> SettableProps() =>
        typeof(AppConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && p.SetMethod!.IsPublic);

    /// <summary>Builds a configuration where every property differs from its default (values stay inside the dialog's ranges).</summary>
    internal static AppConfig NonDefault()
    {
        var c = new AppConfig();
        var d = new AppConfig();
        foreach (var p in SettableProps())
        {
            object? v = p.GetValue(d);
            object? nv = v switch
            {
                bool b => !b,
                int i => p.Name switch
                {
                    nameof(AppConfig.FontSize) => 11,
                    nameof(AppConfig.MinElementSizePx) => 33,
                    nameof(AppConfig.Proportions) => 70,
                    nameof(AppConfig.InitialDetailLevel) => 5,
                    nameof(AppConfig.ZoomTimeMs) => 850,
                    nameof(AppConfig.RefreshDelayMs) => 250,
                    nameof(AppConfig.TooltipDelayMs) => 900,
                    nameof(AppConfig.BlinkDurationMs) => 1250,
                    nameof(AppConfig.MaxRecentPaths) => 7,
                    nameof(AppConfig.LogMaxEntries) => 1200,
                    nameof(AppConfig.LevelContrast) => 65,
                    nameof(AppConfig.BorderContrast) => 15,
                    nameof(AppConfig.HilightHaloLevels) => 3,
                    nameof(AppConfig.ScanDriveKind) => 3,
                    _ => i + 7,
                },
                string s => p.Name == nameof(AppConfig.ExcludePaths) ? "*\\WinSxS;node_modules;*temp*" : s + "_x",
                Color col => Color.FromArgb(col.R ^ 0x55, col.G ^ 0x33, col.B ^ 0x11),
                DateTime => new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc),
                ViewStyle st => st == ViewStyle.Flat ? ViewStyle.FileClasses : ViewStyle.Flat,
                List<FileClass> => new List<FileClass> { new() { Name = "Class A", Extensions = "aaa;bbb", Color = Color.Teal }, new() { Name = "Class B", Extensions = "*.ccc", Color = Color.Maroon } },
                List<string> => new List<string> { "C:\\one", "D:\\two" },
                List<ExportConfig> => new List<ExportConfig> { new() { Description = "My export", DetailFormat = "<%pathfile%>{&br}", FineSorting = FineSorting.Name, FineSortingDescending = false } },
                _ => throw new InvalidOperationException("unhandled property type " + p.Name),
            };
            p.SetValue(c, nv);
            Assert.False(Same(p.GetValue(c), v), "test data for " + p.Name + " must differ from the default");
        }
        return c;
    }

    private static bool Same(object? a, object? b)
    {
        switch (a)
        {
            case Color ca: return b is Color cb && ca.ToArgb() == cb.ToArgb();
            case List<FileClass> la: return b is List<FileClass> lb && la.Count == lb.Count && la.Zip(lb).All(t => t.First.Name == t.Second.Name && t.First.Extensions == t.Second.Extensions && t.First.Color.ToArgb() == t.Second.Color.ToArgb());
            case List<string> sa: return b is List<string> sb && sa.SequenceEqual(sb);
            case List<ExportConfig> ea: return b is List<ExportConfig> eb && ea.Count == eb.Count && ea.Zip(eb).All(t => t.First.Description == t.Second.Description && t.First.DetailFormat == t.Second.DetailFormat && t.First.FineSorting == t.Second.FineSorting && t.First.FineSortingDescending == t.Second.FineSortingDescending);
            default: return Equals(a, b);
        }
    }

    internal static void AssertSameConfig(AppConfig expected, AppConfig actual, params string[] ignore)
    {
        foreach (var p in SettableProps())
        {
            if (ignore.Contains(p.Name)) continue;
            Assert.True(Same(p.GetValue(expected), p.GetValue(actual)), $"{p.Name}: expected {Describe(p.GetValue(expected))} got {Describe(p.GetValue(actual))}");
        }
    }

    private static string Describe(object? v) => v switch
    {
        Color c => AppConfig.ColorText(c),
        List<FileClass> l => string.Join("|", l.Select(f => f.Name)),
        List<string> l => string.Join("|", l),
        List<ExportConfig> l => string.Join("|", l.Select(e => e.Description)),
        _ => v?.ToString() ?? "null",
    };

    // ------------------------------------------------------------------ model & persistence

    [Fact]
    public void SaveLoad_RoundTripsEveryProperty()
    {
        var path = TempFile("roundtrip.xml");
        var c = NonDefault();
        Assert.True(AppConfig.Save(c, path));
        var loaded = AppConfig.FromXml(XDocument.Load(path).Root!);
        AssertSameConfig(c, loaded);
        _out.WriteLine(File.ReadAllText(path));
    }

    [Fact]
    public void Load_MissingOrBrokenFile_GivesDefaults()
    {
        var missing = TempFile("does-not-exist.xml");
        File.Delete(missing);
        AppConfig.Load(missing);
        AssertSameConfig(new AppConfig(), AppConfig.Current);

        var broken = TempFile("broken.xml");
        File.WriteAllText(broken, "<ByteScapeConfig><fontSize>12</fontSize><colors>"); // truncated
        AppConfig.Load(broken);
        AssertSameConfig(new AppConfig(), AppConfig.Current);

        var empty = TempFile("empty.xml");
        File.WriteAllText(empty, "");
        AppConfig.Load(empty);
        AssertSameConfig(new AppConfig(), AppConfig.Current);
    }

    [Fact]
    public void Load_PartialFile_KeepsDefaultsForMissingAndIgnoresUnknown()
    {
        var xml = XElement.Parse("<ByteScapeConfig><fontSize>13</fontSize><bogus>1</bogus><colors><folderColor>16764108</folderColor><levelContrast>abc</levelContrast></colors></ByteScapeConfig>");
        var c = AppConfig.FromXml(xml);
        Assert.Equal(13, c.FontSize);
        Assert.Equal(new AppConfig().LevelContrast, c.LevelContrast);          // unparsable -> default
        Assert.Equal(Color.FromArgb(0xCC, 0xCC, 0xFF).ToArgb(), c.FolderColor.ToArgb()); // Delphi TColor integer (BGR): 16764108 = 0xFFCCCC
        Assert.Equal(new AppConfig().FileClasses.Count, c.FileClasses.Count);     // element absent -> defaults kept
        Assert.Equal(Color.FromArgb(0x12, 0x34, 0x56).ToArgb(), AppConfig.ParseColor("#123456")!.Value.ToArgb());
        Assert.Equal(Color.Red.ToArgb(), AppConfig.ParseColor("Red")!.Value.ToArgb());
        Assert.Null(AppConfig.ParseColor("not a colour"));
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var a = NonDefault();
        var b = a.Clone();
        b.FileClasses[0].Name = "changed";
        b.RecentPaths.Add("E:\\three");
        b.ExportConfigs[0].Description = "changed";
        Assert.Equal("Class A", a.FileClasses[0].Name);
        Assert.Equal(2, a.RecentPaths.Count);
        Assert.Equal("My export", a.ExportConfigs[0].Description);
    }

    [Fact]
    public void RecentPaths_DedupeAndLimit()
    {
        var c = new AppConfig { MaxRecentPaths = 3 };
        c.AddRecentPath("C:\\a"); c.AddRecentPath("C:\\b"); c.AddRecentPath("c:\\A"); c.AddRecentPath("C:\\c"); c.AddRecentPath("C:\\d");
        Assert.Equal(new[] { "C:\\d", "C:\\c", "c:\\A" }, c.RecentPaths);
        c.RememberRecentPaths = false;
        c.AddRecentPath("C:\\e");
        Assert.Equal(3, c.RecentPaths.Count);
    }

    [Fact]
    public void ExcludeMasks_MatchNameOrFullPath()
    {
        var c = new AppConfig { ExcludePaths = "*\\WinSxS; node_modules ;*\\Temp\\*" };
        Assert.True(c.IsExcluded(@"C:\Windows\WinSxS"));
        Assert.True(c.IsExcluded(@"C:\proj\node_modules"));
        Assert.True(c.IsExcluded(@"C:\Users\x\Temp\cache"));
        Assert.False(c.IsExcluded(@"C:\Windows\System32"));
        Assert.False(c.IsExcluded(@"C:\proj\node_modules_backup"));
        c.ExcludePaths = "";
        Assert.False(c.IsExcluded(@"C:\Windows\WinSxS"));
    }

    [Fact]
    public void FileClass_ExtensionMatching()
    {
        var f = new FileClass { Extensions = "jpg; *.PNG ;.gif" };
        Assert.True(f.Contains("jpg")); Assert.True(f.Contains("JPG")); Assert.True(f.Contains("png")); Assert.True(f.Contains("gif"));
        Assert.False(f.Contains("jpeg"));
        f.Extensions = "jpeg"; f.Invalidate();
        Assert.True(f.Contains("jpeg")); Assert.False(f.Contains("jpg"));
        var cfg = new AppConfig();
        Assert.Equal("Pictures", cfg.ClassOf("jpg")!.Name);
        Assert.Equal("Pictures", cfg.ClassOf("heic")!.Name);
        Assert.Equal("Music and audio", cfg.ClassByName("music and audio")!.Name);
        Assert.Null(cfg.ClassOf("xyz"));
    }

    /// <summary>
    /// A settings file of an earlier version kept the earlier default classes: untouched ones become today's defaults,
    /// changed ones keep the user's extensions under today's name, and a filter may still use an earlier name.
    /// </summary>
    [Fact]
    public void FileClasses_OfAnEarlierVersion_BecomeTodaysDefaults()
    {
        // the program knows the earlier defaults only by their hashes; the test adds two made-up ones the same way
        const string video = "Old films", videoExt = "film1;film2", music = "Old sounds";
        FileClass.AddEarlierDefault(video, videoExt, 1);
        FileClass.AddEarlierDefault(music, "sound1", 2);
        var cfg = AppConfig.FromXml(XElement.Parse("<ByteScapeConfig><fileGroups>" +
            $"<fileGroup><name>{video}</name><extensions>{videoExt}</extensions><color>#C080E0</color></fileGroup>" +
            $"<fileGroup><name>{music}</name><extensions>mp3;flac</extensions><color>#112233</color></fileGroup>" +
            "<fileGroup><name>Mine</name><extensions>abc</extensions><color>#445566</color></fileGroup>" +
            "</fileGroups></ByteScapeConfig>"));
        Assert.Equal(new[] { "Videos", "Music and audio", "Mine" }, cfg.FileClasses.Select(c => c.Name));
        Assert.Equal(AppConfig.DefaultFileClasses()[1].Extensions, cfg.FileClasses[0].Extensions);   // was the default: now today's list
        Assert.Equal("mp3;flac", cfg.FileClasses[1].Extensions);                                      // changed by the user: kept
        Assert.Equal(Color.FromArgb(0x11, 0x22, 0x33).ToArgb(), cfg.FileClasses[1].Color.ToArgb());
        Assert.Same(cfg.FileClasses[1], cfg.ClassByName(music.ToLowerInvariant()));                  // an earlier filter still works
    }

    [Fact]
    public void Format_DecimalVersusBinaryUnits()
    {
        try
        {
            Format.DecimalUnits = false;
            Assert.Equal("1KB", Format.Size(1024));
            Assert.Equal("1023b", Format.Size(1023));   // bytes below 1 KB: "0b", "512b"
            Assert.Equal("1.0MB", Format.Size(1024 * 1024));
            Format.DecimalUnits = true;
            Assert.Equal("1KB", Format.Size(1000));
            Assert.Equal("1.0MB", Format.Size(1_000_000));
            Assert.Equal("1.5GB", Format.Size(1_500_000_000));
        }
        finally { Format.DecimalUnits = false; }
    }
}
