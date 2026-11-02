using System.Text;
using ByteScape.Core.Model;
using ByteScape.Core.Util;

namespace ByteScape.Core.Forecast;

/// <summary>When a drive fills up at the pace of the last weeks, and on what that is based.</summary>
public sealed class ForecastResult
{
    public required string Drive { get; init; }
    public long Total { get; init; }
    public long Free { get; init; }
    public long Used => Total - Free;
    /// <summary>One point per day (the day's last measurement), oldest first, local days.</summary>
    public IReadOnlyList<(DateTime Day, long Used)> Points { get; init; } = Array.Empty<(DateTime, long)>();
    public int Samples { get; init; }
    public double SpanDays { get; init; }
    /// <summary>Growth of the used space per day (negative: shrinking); NaN without enough history.</summary>
    public double BytesPerDay { get; init; } = double.NaN;
    /// <summary>Used space today, where the forecast line starts (for the chart).</summary>
    public double TrendToday { get; init; }
    /// <summary>When the trend reaches the drive's size; null when it does not grow (or no trend).</summary>
    public DateTime? FullDate { get; init; }
    public bool HasTrend => !double.IsNaN(BytesPerDay);
    /// <summary>"low" (under two weeks of history), "medium" (under two months), "good".</summary>
    public string Confidence => SpanDays < 14 ? /*loc*/ "low" : SpanDays < 60 ? /*loc*/ "medium" : /*loc*/ "good";
}

/// <summary>
/// Disk-full forecast. From the measurements (<see cref="DiskHistory"/>): one point per day, the pace the used space grows
/// at (the day-to-day changes over the whole span, one-off big drops such as a cleanup left out) and the day the free
/// space runs out at that pace. Without history, the current scan says how much was added lately (files created in the
/// window) and where it went.
/// </summary>
public static class DiskForecast
{
    public static ForecastResult FromHistory(IEnumerable<DiskSample> samples, string drive, long total, long free, DateTime nowUtc, int windowDays = 90)
    {
        var mine = samples.Where(s => s.Drive.Equals(drive, StringComparison.OrdinalIgnoreCase) && s.Utc <= nowUtc.AddMinutes(5) && s.Utc >= nowUtc.AddDays(-windowDays))
                          .OrderBy(s => s.Utc).ToList();
        mine.Add(new DiskSample(nowUtc, drive, total, free));   // now is a measurement too
        var points = mine.GroupBy(s => s.Utc.ToLocalTime().Date).Select(g => (Day: g.Key, Used: g.Last().Used)).OrderBy(p => p.Day).ToList();
        double span = points.Count > 1 ? (points[^1].Day - points[0].Day).TotalDays : 0;
        if (points.Count < 2 || span < 1)
            return new ForecastResult { Drive = drive, Total = total, Free = free, Points = points, Samples = mine.Count, SpanDays = span };

        // the pace data is added: the changes from one measured day to the next, without the one-off drops (a cleanup is
        // a new level, not a pace); a burst of downloads stays in, it is growth. Time passes through the dropped intervals.
        var rates = new List<double>(points.Count - 1);
        var changes = new List<(double Change, double Days)>(points.Count - 1);
        for (int k = 0; k + 1 < points.Count; k++)
        {
            double dt = (points[k + 1].Day - points[k].Day).TotalDays;
            double d = points[k + 1].Used - points[k].Used;
            changes.Add((d, dt));
            rates.Add(d / dt);
        }
        double median = Median(new List<double>(rates));
        double mad = Median(rates.Select(x => Math.Abs(x - median)).ToList());
        double cut = median - Math.Max(5 * mad, 200.0 * (1 << 20));   // at least 200 MB a day below the usual change
        double kept = 0;
        for (int k = 0; k < changes.Count; k++) if (rates[k] >= cut) kept += changes[k].Change;
        double slope = kept / span;
        double trendToday = total - free;
        DateTime? full = null;
        if (slope > 1)   // a byte a day is not growth
        {
            full = nowUtc.ToLocalTime().Date.AddDays(Math.Max(0, free / slope));
            if (full > DateTime.Now.AddYears(50)) full = null;
        }
        return new ForecastResult
        {
            Drive = drive, Total = total, Free = free, Points = points, Samples = mine.Count, SpanDays = span,
            BytesPerDay = slope, TrendToday = trendToday, FullDate = full,
        };
    }

    private static double Median(List<double> v)
    {
        if (v.Count == 0) return 0;
        v.Sort();
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }

    /// <summary>
    /// Files created in the last <paramref name="window"/> (copied, downloaded, extracted, written): their size on disk and
    /// the folders they went to, grouped <paramref name="depth"/> levels below the view. What was deleted meanwhile is not
    /// known, so this is the most the drive can have grown.
    /// </summary>
    public static (long Bytes, int Files, List<(FsNode Folder, string Path, long Bytes)> Where) RecentlyAdded(ContainerNode viewRoot, TimeSpan window, DateTime nowUtc, object? sync, int depth = 3, int top = 12)
    {
        var root = viewRoot is DriveNode { IsSnapshotFrame: true } f && f.ChildCount == 1 && f.Children[0] is ContainerNode inner ? inner : viewRoot;
        var since = nowUtc - window;
        var byFolder = new Dictionary<FsNode, long>(ReferenceEqualityComparer.Instance);
        long total = 0;
        int files = 0;
        void Walk(ContainerNode c, int level, FsNode bucket)
        {
            List<FsNode> kids;
            if (sync == null) kids = c.ChildrenSnapshot(); else lock (sync) kids = c.ChildrenSnapshot();
            foreach (var k in kids)
            {
                if (k is FileNode fn)
                {
                    if (fn.Created < since || fn.Created > nowUtc.AddDays(1) || fn.DiskSize <= 0) continue;
                    total += fn.DiskSize;
                    files++;
                    byFolder[bucket] = (byFolder.TryGetValue(bucket, out var b) ? b : 0) + fn.DiskSize;
                }
                else if (k is ContainerNode kc && !kc.IsSynthetic)
                    Walk(kc, level + 1, level < depth ? kc : bucket);
            }
        }
        Walk(root, 0, root);
        string rootPath;
        try { rootPath = root.RealPath(); } catch { rootPath = root.FullPath; }
        var where = byFolder.OrderByDescending(kv => kv.Value).Take(top)
                            .Select(kv => (kv.Key, SafePath(kv.Key, rootPath), kv.Value)).ToList();
        return (total, files, where);
    }

    private static string SafePath(FsNode n, string fallback)
    {
        try { return n.RealPath(); } catch { return fallback; }
    }

    /// <summary>Folders that grew since a folder summary (paths relative to the drive), most growth first.</summary>
    public static List<(string Path, long Then, long Now)> GrewSince(Dictionary<string, long> then, ContainerNode driveRoot, object? sync, int top = 12)
    {
        var now = DiskHistory.SummaryOf(driveRoot, sync, depth: 3, minBytes: 0);
        var nowMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (p, b) in now) nowMap[p] = b;
        var list = new List<(string, long, long)>();
        foreach (var (p, b) in nowMap)
        {
            long before = then.TryGetValue(p, out var t) ? t : 0;
            if (b - before > 0) list.Add((p, before, b));
        }
        // a folder that grew because a sub folder grew is listed with the sub folder only when that explains most of it
        list.Sort((a, b) => (b.Item3 - b.Item2).CompareTo(a.Item3 - a.Item2));
        var result = new List<(string, long, long)>();
        foreach (var e in list)
        {
            if (result.Count >= top) break;
            long grew = e.Item3 - e.Item2;
            // keep the deepest folder that carries the growth: skip a parent when a listed child explains 80 % of it
            if (list.Any(c => c.Item1.StartsWith(e.Item1 + "\\", StringComparison.OrdinalIgnoreCase) && (c.Item3 - c.Item2) >= grew * 0.8)) continue;
            result.Add(e);
        }
        return result;
    }

    /// <summary>"in about 5 weeks", "in 3 days", "within a day".</summary>
    public static string When(DateTime full, DateTime nowLocal)
    {
        double days = (full.Date - nowLocal.Date).TotalDays;
        if (days < 1) return T("within a day");
        if (days < 14) return P((int)Math.Round(days), "in {0} day", "in {0} days");
        if (days < 60) return P((int)Math.Round(days / 7), "in about {0} week", "in about {0} weeks");
        if (days < 730) return P((int)Math.Round(days / 30.44), "in about {0} month", "in about {0} months");
        return P((long)Math.Round(days / 365.25), "in about {0} year", "in about {0} years");
    }

    public static string Rate(double bytesPerDay)
    {
        double week = bytesPerDay * 7;
        return (week < 0 ? "−" : "+") + F($"{Format.Size((long)Math.Abs(week))} per week");
    }

    /// <summary>Plain text report (window "Copy").</summary>
    public static string ToText(ForecastResult r, (long Bytes, int Files, List<(FsNode Folder, string Path, long Bytes)> Where)? added, DateTime nowLocal)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Branding.Name + ": " + F($"disk forecast for {r.Drive}"));
        sb.AppendLine(T("Free:") + " " + F($"{Format.Size(r.Free)} of {Format.Size(r.Total)}"));
        if (r.HasTrend)
        {
            sb.AppendLine(T("Trend:") + " " + F($"{Rate(r.BytesPerDay)} ({r.Samples} measurements over {r.SpanDays:0} days, confidence {T(r.Confidence)})"));
            sb.AppendLine(T("Full:") + " " + (r.FullDate is { } f ? When(f, nowLocal) + " (" + F($"around {Format.Date(f)}") + ")" : T("not filling up")));
        }
        else sb.AppendLine(T("Trend:") + " " + T("not enough measurements yet"));
        if (added is { } a)
        {
            sb.AppendLine(P(a.Files, "Added in the last 30 days (file dates): {1} in {0:N0} file", "Added in the last 30 days (file dates): {1} in {0:N0} files", Format.Size(a.Bytes)));
            foreach (var (_, p, b) in a.Where) sb.Append("  ").Append(Format.Size(b)).Append('\t').AppendLine(p);
        }
        return sb.ToString();
    }
}
