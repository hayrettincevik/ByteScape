using ByteScape.Core.Config;
using ByteScape.Core.Filtering;
using ByteScape.Core.Model;
using Xunit;

namespace ByteScape.Tests;

/// <summary>Plain language → filter language (Turkish and English), and the produced filters really parse and match.</summary>
[Collection("config")]
public class NaturalFilterTests
{
    private static string T(string text) => NaturalFilter.Translate(text).Filter;
    private static HashSet<string> Parts(string text) => new(T(text).Split(';', StringSplitOptions.RemoveEmptyEntries));

    [Theory]
    [InlineData("6 aydır açılmamış 100 MB üstü videolar", new[] { "*.mp4", "*.mkv", ">100mb", "access>6months" })]
    [InlineData("videos larger than 100 MB not opened for 6 months", new[] { "*.mp4", ">100mb", "access>6months" })]
    [InlineData("1 yıldan eski büyük ISO dosyaları", new[] { "*.iso", ">100mb", "modify>1years" })]
    [InlineData("son 7 günde oluşturulan dosyalar", new[] { "create<7days" })]
    [InlineData("files created in the last two weeks", new[] { "create<2weeks" })]
    [InlineData("1 GB'dan büyük dosyalar", new[] { ">1gb" })]
    [InlineData("500 mb altındaki fotoğraflar", new[] { "*.jpg", "*.png", "<500mb" })]
    [InlineData("pictures smaller than 2 MB", new[] { "*.jpg", "<2mb" })]
    [InlineData("gizli dosyalar", new[] { ":attr:h" })]
    [InlineData("kırmızı etiketli belgeler", new[] { "*.pdf", "*.docx", ":red" })]
    [InlineData("önbellek dosyaları", new[] { ":reclaimable" })]
    [InlineData("junk", new[] { ":reclaimable" })]
    [InlineData("büyük müzik dosyaları", new[] { "*.mp3", "*.flac", ">100mb" })]
    [InlineData("psd dosyaları", new[] { "*.psd" })]
    [InlineData(".mkv files over 4 gb", new[] { "*.mkv", ">4gb" })]
    [InlineData("Downloads klasöründeki videolar", new[] { "*.mp4", "\\Downloads*" })]
    [InlineData("videos in Downloads folder", new[] { "*.mp4", "\\Downloads*" })]
    [InlineData("node_modules klasörleri", new[] { "\\node_modules*" })]
    [InlineData("bugün değişen dosyalar", new[] { "modify<1day" })]
    [InlineData("dev dosyalar", new[] { ">1gb" })]
    [InlineData("boş dosyalar", new[] { "<1" })]
    [InlineData("videolar hariç 100 mb üstü", new[] { "|*.mp4", ">100mb" })]
    [InlineData("large files except videos", new[] { "|*.mp4", ">100mb" })]
    public void Translates(string text, string[] expectedParts)
    {
        var parts = Parts(text);
        foreach (var p in expectedParts) Assert.Contains(p, parts);
        // whatever comes out must be valid filter syntax
        AppConfig.Replace(new AppConfig());
        var f = new Filter(T(text));
        Assert.False(f.IsEmpty);
    }

    [Fact]
    public void Explanation_DescribesWhatWasUnderstood()
    {
        var r = NaturalFilter.Translate("6 aydır açılmamış 100 MB üstü videolar");
        Assert.Contains("videos", r.Explanation);
        Assert.Contains("larger than 100 MB", r.Explanation);
        Assert.Contains("not opened for 6 months", r.Explanation);
        Assert.True(NaturalFilter.Translate("lorem ipsum").IsEmpty);
    }

    [Fact]
    public void LooksNatural_LeavesFilterSyntaxAlone()
    {
        Assert.False(NaturalFilter.LooksNatural("*.jpg"));
        Assert.False(NaturalFilter.LooksNatural(">100mb"));
        Assert.False(NaturalFilter.LooksNatural(":red"));
        Assert.False(NaturalFilter.LooksNatural("\\temp"));
        Assert.False(NaturalFilter.LooksNatural("report"));            // a plain name mask stays a name mask
        Assert.True(NaturalFilter.LooksNatural("videolar"));           // a known noun alone is natural
        Assert.True(NaturalFilter.LooksNatural("büyük videolar"));
        Assert.True(NaturalFilter.LooksNatural("files bigger than 1 gb"));
    }

    [Fact]
    public void TranslatedFilter_MatchesTheRightFiles()
    {
        AppConfig.Replace(new AppConfig());
        var root = new DriveNode("C:\\");
        FileNode F(string name, long mb, int accessDaysAgo)
        {
            var f = new FileNode(name) { LogicalSize = mb * 1048576L, DiskSize = mb * 1048576L, Modified = DateTime.UtcNow.AddDays(-accessDaysAgo), Created = DateTime.UtcNow.AddDays(-accessDaysAgo * 2), Accessed = DateTime.UtcNow.AddDays(-accessDaysAgo) };
            root.AddChild(f); return f;
        }
        var oldBigVideo = F("holiday.mp4", 700, 400);
        var newBigVideo = F("clip.mkv", 900, 3);
        var oldSmallVideo = F("meme.mp4", 5, 400);
        var oldBigIso = F("win.iso", 4000, 400);
        var f = new Filter(T("6 aydır açılmamış 100 MB üstü videolar"));
        var now = DateTime.UtcNow;
        Assert.True(f.Matches(oldBigVideo, Filter.FolderMaskState.Root, now));
        Assert.False(f.Matches(newBigVideo, Filter.FolderMaskState.Root, now));
        Assert.False(f.Matches(oldSmallVideo, Filter.FolderMaskState.Root, now));
        Assert.False(f.Matches(oldBigIso, Filter.FolderMaskState.Root, now));
    }
}
