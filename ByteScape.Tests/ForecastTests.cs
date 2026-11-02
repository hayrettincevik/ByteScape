using ByteScape.Core.Forecast;
using ByteScape.Core.Model;
using Xunit;

namespace ByteScape.Tests;

/// <summary>Disk forecast: the measurement history, the robust trend, what was added lately, what grew.</summary>
[Collection("config")]
public class ForecastTests
{
    private const long GB = 1L << 30;
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "forecast");

    private static List<DiskSample> Daily(int days, Func<int, long> used, long total = 100 * GB, string drive = "C:") =>
        Enumerable.Range(0, days).Select(i => new DiskSample(Now.AddDays(i - days), drive, total, total - used(i))).ToList();

    [Fact]
    public void History_RecordsThrottlesTrimsAndSkipsBadLines()
    {
        try
        {
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
            Directory.CreateDirectory(Dir);
            var h = new DiskHistory(Path.Combine(Dir, "ByteScapeHistory.csv")) { MaxLines = 5, MaxSummaries = 2 };
            Assert.Empty(h.Load());
            Assert.True(h.Record(new DiskSample(Now, "C:", 100 * GB, 40 * GB), TimeSpan.FromHours(1)));
            Assert.False(h.Record(new DiskSample(Now.AddMinutes(30), "C:", 100 * GB, 39 * GB), TimeSpan.FromHours(1)));   // too soon
            Assert.True(h.Record(new DiskSample(Now.AddMinutes(30), "D:", 500 * GB, 300 * GB), TimeSpan.FromHours(1)));   // another drive
            File.AppendAllText(h.FilePath, "garbage line\n2026-01-01T00:00:00Z,C:,notanumber,1\n");
            var all = h.Load();
            Assert.Equal(2, all.Count);
            Assert.Equal(60 * GB, all[0].Used);
            Assert.Equal(DateTimeKind.Utc, all[0].Utc.Kind);
            Assert.Equal(Now, all[0].Utc);
            for (int i = 1; i <= 6; i++) h.Record(new DiskSample(Now.AddDays(i), "C:", 100 * GB, (40 - i) * GB), TimeSpan.FromHours(1));
            Assert.True(File.ReadAllLines(h.FilePath).Length <= 5);                  // trimmed, oldest first
            Assert.Equal(34 * GB, h.Load()[^1].Free);

            // folder summaries: written, listed oldest first, the oldest dropped beyond the limit
            h.SaveSummary("C:", Now.AddDays(-20), new[] { (@"Users\ali", 10 * GB), (@"Windows", 20 * GB) });
            h.SaveSummary("C:", Now.AddDays(-10), new[] { (@"Users\ali", 12 * GB) });
            h.SaveSummary("C:", Now, new[] { (@"Users\ali", 13 * GB) });
            h.SaveSummary("D:", Now, new[] { ("Games", 100 * GB) });
            var sums = h.Summaries("C:");
            Assert.Equal(new[] { Now.AddDays(-10), Now }, sums.Select(s => s.Utc));
            Assert.Equal(12 * GB, DiskHistory.ReadSummary(sums[0].File)[@"users\ALI"]);   // case insensitive paths
            Assert.Single(h.Summaries("D:"));
        }
        finally { try { Directory.Delete(Dir, true); } catch { } }
    }

    [Fact]
    public void Trend_OfSteadyGrowth_GivesTheDayTheDriveIsFull()
    {
        // 50 GB used 30 days ago, one more GB every day: 80 GB used now of 100 GB -> full in about 20 days
        var samples = Daily(30, i => 50 * GB + i * GB);
        var r = DiskForecast.FromHistory(samples, "C:", 100 * GB, 20 * GB, Now);
        Assert.True(r.HasTrend);
        Assert.InRange(r.BytesPerDay, 0.99 * GB, 1.01 * GB);
        Assert.Equal(31, r.Samples);                                              // + today
        Assert.Equal(31, r.Points.Count);                                            // + today
        Assert.InRange((r.FullDate!.Value - Now.ToLocalTime().Date).TotalDays, 19, 21);
        Assert.Equal("medium", r.Confidence);
        Assert.Equal("in about 3 weeks", DiskForecast.When(r.FullDate.Value, Now.ToLocalTime()));
        Assert.Equal("+7.0GB per week", DiskForecast.Rate(r.BytesPerDay));
        Assert.Contains("Full: in about 3 weeks", DiskForecast.ToText(r, null, Now.ToLocalTime()));
    }

    [Fact]
    public void Trend_IgnoresOneBigCleanup_AndOtherDrives()
    {
        // steady +0.5 GB a day, but 25 GB deleted on day 20 (the rest keeps the pace from the new level)
        var samples = Daily(40, i => 40 * GB + i * GB / 2 - (i >= 20 ? 25 * GB : 0));
        samples.AddRange(Daily(40, i => 10 * GB - i * GB / 10, drive: "D:"));        // another drive, shrinking
        samples.Add(new DiskSample(Now.AddDays(-400), "C:", 100 * GB, 99 * GB));    // outside the window
        long usedNow = 40 * GB + 40 * GB / 2 - 25 * GB;
        var r = DiskForecast.FromHistory(samples, "C:", 100 * GB, 100 * GB - usedNow, Now);
        Assert.InRange(r.BytesPerDay, 0.3 * GB, 0.6 * GB);                          // a least squares line would be dragged negative
        Assert.NotNull(r.FullDate);
        var d = DiskForecast.FromHistory(samples, "D:", 100 * GB, 100 * GB - 6 * GB, Now);
        Assert.True(d.BytesPerDay < 0);
        Assert.Null(d.FullDate);                                                    // not filling up
        Assert.StartsWith("−", DiskForecast.Rate(d.BytesPerDay));
    }

    [Fact]
    public void Trend_KeepsBurstsOfGrowth_AndGapsBetweenMeasurements()
    {
        // nothing for nine days, then a 10 GB download: that is 1 GB a day, not "not growing"
        var bursts = Daily(60, i => 20 * GB + (i / 10) * 10 * GB);
        long usedNow = 20 * GB + 5 * 10 * GB + 10 * GB;
        var r = DiskForecast.FromHistory(bursts, "C:", 200 * GB, 200 * GB - usedNow, Now);
        Assert.InRange(r.BytesPerDay, 0.9 * GB, 1.2 * GB);
        // measured every fifth day only: the changes are spread over the days between
        var sparse = Daily(60, i => 30 * GB + i * GB / 4).Where((_, i) => i % 5 == 0).ToList();
        var s = DiskForecast.FromHistory(sparse, "C:", 100 * GB, 100 * GB - (30 * GB + 60 * GB / 4), Now);
        Assert.InRange(s.BytesPerDay, 0.2 * GB, 0.3 * GB);
        Assert.Equal("good", DiskForecast.FromHistory(Daily(80, i => 10 * GB), "C:", 100 * GB, 90 * GB, Now).Confidence);
    }

    [Fact]
    public void Trend_NeedsTwoDays()
    {
        var r = DiskForecast.FromHistory(new[] { new DiskSample(Now.AddHours(-2), "C:", 100 * GB, 50 * GB) }, "C:", 100 * GB, 49 * GB, Now);
        Assert.False(r.HasTrend);
        Assert.Null(r.FullDate);
        Assert.Equal("low", r.Confidence);
        Assert.Contains("not enough measurements", DiskForecast.ToText(r, null, Now.ToLocalTime()));
    }

    [Fact]
    public void RecentlyAdded_ByFileDates_GroupedThreeLevelsDown_AndGrewSince()
    {
        var root = new DriveNode(@"C:\") { IsDrive = true, Enumerated = true, ScanComplete = true };
        FileNode F(string folder, string name, long bytes, int daysAgo)
        {
            var f = AppsTests.File(AppsTests.Path(root, folder), name, bytes);
            f.Created = Now.AddDays(-daysAgo);
            return f;
        }
        F(@"Users\ali\Downloads", "movie.mkv", 3 * GB, 2);
        F(@"Users\ali\Downloads\sub\deeper", "x.zip", 1 * GB, 10);                   // counted in Users\ali\Downloads
        F(@"Users\ali\Documents", "old.docx", 2 * GB, 400);                          // too old
        F(@"Windows\Temp", "setup.tmp", GB / 2, 1);
        F(@"Windows\Temp", "future.tmp", GB, -30);                                    // a date in the future: ignored
        var (bytes, files, where) = DiskForecast.RecentlyAdded(root, TimeSpan.FromDays(30), Now, null);
        Assert.Equal(4 * GB + GB / 2, bytes);
        Assert.Equal(3, files);
        Assert.Equal(new[] { @"C:\Users\ali\Downloads", @"C:\Windows\Temp" }, where.Select(w => w.Path));
        Assert.Equal(4 * GB, where[0].Bytes);

        // grew since a summary: the deepest folder that explains the growth
        var then = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["Users"] = 3 * GB, [@"Users\ali"] = 3 * GB, [@"Users\ali\Documents"] = 2 * GB, [@"Users\ali\Downloads"] = 1 * GB, ["Windows"] = GB / 2 + GB,
        };
        var grew = DiskForecast.GrewSince(then, root, null);
        Assert.Equal(@"Users\ali\Downloads", grew[0].Path);
        Assert.Equal(1 * GB, grew[0].Then);
        Assert.Equal(4 * GB, grew[0].Now);
        Assert.DoesNotContain(grew, g => g.Path == "Users" || g.Path == @"Users\ali");   // explained by Downloads
        Assert.DoesNotContain(grew, g => g.Path == "Windows");                          // did not grow
        var summary = DiskHistory.SummaryOf(root, null, depth: 2, minBytes: GB);
        Assert.Contains((@"Users\ali", 6 * GB), summary);
        Assert.DoesNotContain(summary, s => s.Path.Count(c => c == '\\') >= 2);        // two levels only
    }

    [Fact]
    public void MeasureCommandLine_IsRecognisedAlone()
    {
        Assert.True(ByteScape.CommandLine.IsMeasure(new[] { "measure" }));
        Assert.True(ByteScape.CommandLine.IsMeasure(new[] { "MEASURE" }));
        Assert.False(ByteScape.CommandLine.IsMeasure(new[] { "scan", @"C:\", "measure" }));
        Assert.False(ByteScape.CommandLine.IsMeasure(Array.Empty<string>()));
        var (req, _, _) = ByteScape.CommandLine.Parse(new[] { "scan", @"C:\", "forecast" });
        Assert.True(req.Single().ShowForecast);
    }
}
