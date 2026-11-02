using System.Globalization;
using System.Text;
using ByteScape.Core.Model;
using ByteScape.Core.Util;

namespace ByteScape.Core.Forecast;

/// <summary>One measurement of a drive: when, which, how big, how much free.</summary>
public readonly record struct DiskSample(DateTime Utc, string Drive, long Total, long Free)
{
    public long Used => Total - Free;
}

/// <summary>
/// The measurements behind the disk forecast, kept next to the configuration: a line per drive and measurement in
/// ByteScapeHistory.csv (taken when ByteScape starts, after every complete scan of a drive, and by "ByteScape measure"),
/// plus the sizes of the big folders of each complete drive scan (ByteScapeHistory\&lt;drive&gt;-&lt;time&gt;.tsv) for
/// "what grew since then". Everything stays on this computer.
/// </summary>
public sealed class DiskHistory
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly object _sync = new();

    public DiskHistory(string file)
    {
        FilePath = file;
        SummaryDir = Path.Combine(Path.GetDirectoryName(file) ?? ".", Path.GetFileNameWithoutExtension(file));
    }

    public string FilePath { get; }
    public string SummaryDir { get; }
    /// <summary>Lines kept at most (oldest dropped): years of hourly measurements of a few drives.</summary>
    public int MaxLines { get; init; } = 40_000;
    /// <summary>Folder summaries kept per drive.</summary>
    public int MaxSummaries { get; init; } = 60;

    /// <summary>The application's history (null when measuring is off, and in tests).</summary>
    public static DiskHistory? Current { get; set; }

    /// <summary>The history file next to the configuration file.</summary>
    public static DiskHistory ForConfig() => new(Path.Combine(Path.GetDirectoryName(Config.AppConfig.ConfigPath)!, "ByteScapeHistory.csv"));

    public List<DiskSample> Load()
    {
        var list = new List<DiskSample>();
        string[] lines;
        lock (_sync)
        {
            try { if (!File.Exists(FilePath)) return list; lines = File.ReadAllLines(FilePath); }
            catch (Exception ex) { Log.Debug("Disk history unreadable: " + ex.Message); return list; }
        }
        foreach (var line in lines)
        {
            var p = line.Split(',');
            if (p.Length < 4 || !DateTime.TryParse(p[0], Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                || !long.TryParse(p[2], NumberStyles.Integer, Inv, out var total) || !long.TryParse(p[3], NumberStyles.Integer, Inv, out var free) || total <= 0) continue;
            list.Add(new DiskSample(DateTime.SpecifyKind(t, DateTimeKind.Utc), p[1], total, free));
        }
        return list;
    }

    /// <summary>Adds a measurement unless the drive was measured less than <paramref name="minInterval"/> before.</summary>
    public bool Record(DiskSample s, TimeSpan minInterval)
    {
        lock (_sync)
        {
            try
            {
                var all = Load();
                var last = all.Where(x => x.Drive.Equals(s.Drive, StringComparison.OrdinalIgnoreCase)).Select(x => (DateTime?)x.Utc).LastOrDefault();
                if (last != null && (s.Utc - last.Value).Duration() < minInterval) return false;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, string.Create(Inv, $"{s.Utc:yyyy-MM-ddTHH:mm:ssZ},{s.Drive},{s.Total},{s.Free}") + Environment.NewLine);
                if (all.Count + 1 > MaxLines)
                {
                    var keep = File.ReadAllLines(FilePath);
                    File.WriteAllLines(FilePath, keep.Skip(keep.Length - MaxLines));
                }
                return true;
            }
            catch (Exception ex) { Log.Debug("Disk history not written: " + ex.Message); return false; }
        }
    }

    /// <summary>Measures every ready local drive (fixed and removable); returns how many were written.</summary>
    public int RecordDrives(DateTime nowUtc, TimeSpan minInterval)
    {
        int n = 0;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady) continue;
                if (Record(new DiskSample(nowUtc, d.Name.Substring(0, 2).ToUpperInvariant(), d.TotalSize, d.TotalFreeSpace), minInterval)) n++;
            }
            catch (Exception ex) { Log.Debug("Drive " + d.Name + " not measured: " + ex.Message); }
        }
        return n;
    }

    // ------------------------------------------------------------------ folder summaries

    /// <summary>Folders of a scanned drive down to <paramref name="depth"/> levels holding at least <paramref name="minBytes"/> (paths relative to the drive).</summary>
    public static List<(string Path, long Bytes)> SummaryOf(ContainerNode root, object? sync, int depth = 3, long minBytes = 50L << 20)
    {
        var list = new List<(string, long)>();
        void Walk(ContainerNode c, string rel, int level)
        {
            List<FsNode> kids;
            if (sync == null) kids = c.ChildrenSnapshot(); else lock (sync) kids = c.ChildrenSnapshot();
            foreach (var k in kids)
                if (k is ContainerNode kc && !kc.IsSynthetic && kc.DiskSize >= minBytes)
                {
                    var p = rel.Length == 0 ? kc.Name : rel + "\\" + kc.Name;
                    list.Add((p, kc.DiskSize));
                    if (level < depth) Walk(kc, p, level + 1);
                }
        }
        Walk(root, "", 1);
        return list;
    }

    public void SaveSummary(string drive, DateTime utc, IEnumerable<(string Path, long Bytes)> folders)
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(SummaryDir);
                var sb = new StringBuilder();
                foreach (var (p, b) in folders) sb.Append(p).Append('\t').Append(b.ToString(Inv)).Append('\n');
                File.WriteAllText(Path.Combine(SummaryDir, $"{drive.TrimEnd(':', '\\')}-{utc:yyyyMMdd-HHmmss}.tsv"), sb.ToString());
                // pruned without administrator rights only: the folder is the user's, a link could lead the delete elsewhere
                if (!Cleanup.AccessProbe.IsElevated) foreach (var old in Summaries(drive).SkipLast(MaxSummaries)) File.Delete(old.File);
            }
            catch (Exception ex) { Log.Debug("Folder summary not written: " + ex.Message); }
        }
    }

    /// <summary>The folder summaries of a drive, oldest first.</summary>
    public List<(DateTime Utc, string File)> Summaries(string drive)
    {
        var list = new List<(DateTime, string)>();
        try
        {
            if (!Directory.Exists(SummaryDir)) return list;
            var prefix = drive.TrimEnd(':', '\\') + "-";
            foreach (var f in Directory.EnumerateFiles(SummaryDir, prefix + "*.tsv"))
            {
                var stamp = Path.GetFileNameWithoutExtension(f).Substring(prefix.Length);
                if (DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
                    list.Add((DateTime.SpecifyKind(t, DateTimeKind.Utc), f));
            }
        }
        catch { }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list;
    }

    public static Dictionary<string, long> ReadSummary(string file)
    {
        var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in File.ReadAllLines(file))
            {
                int tab = line.LastIndexOf('\t');
                if (tab > 0 && long.TryParse(line.AsSpan(tab + 1), NumberStyles.Integer, Inv, out var b)) d[line.Substring(0, tab)] = b;
            }
        }
        catch { }
        return d;
    }
}
