using System.Globalization;
using System.Text;

namespace ByteScape.Core.Forecast;

/// <summary>A folder's size at each remembered complete scan of its drive (null where it was under the summary's threshold).</summary>
public sealed class FolderSeries
{
    public required string Path { get; init; }
    public required long?[] Bytes { get; init; }
    public long First => Bytes.FirstOrDefault(b => b.HasValue) ?? 0;
    public long Last => Bytes.LastOrDefault(b => b.HasValue) ?? 0;
    /// <summary>Size at the last scan minus size at the first (under the threshold at either end counts as 0 there).</summary>
    public long Change => (Bytes[^1] ?? 0) - (Bytes[0] ?? 0);
    public int Depth => Path.Count(c => c == '\\') + 1;
}

public sealed class FolderHistoryResult
{
    public required string Drive { get; init; }
    /// <summary>When each remembered complete scan was made, oldest first.</summary>
    public required IReadOnlyList<DateTime> Scans { get; init; }
    /// <summary>Every folder seen in a summary, biggest change first.</summary>
    public required IReadOnlyList<FolderSeries> Folders { get; init; }
    /// <summary>The drive's measured use over time (free space measurements).</summary>
    public required IReadOnlyList<DiskSample> Samples { get; init; }
    public FolderSeries? Find(string path) => Folders.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The folders of a drive over time, from the folder summaries kept after each complete scan (folders of 50 MB and more,
/// three levels deep) and the free space measurements: what grew and shrank, and each folder's line.
/// </summary>
public static class FolderHistory
{
    public static FolderHistoryResult Build(DiskHistory history, string drive)
    {
        drive = drive.TrimEnd('\\').ToUpperInvariant();
        var summaries = history.Summaries(drive);
        var maps = summaries.Select(s => DiskHistory.ReadSummary(s.File)).ToList();
        var paths = maps.SelectMany(m => m.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var folders = paths.Select(p => new FolderSeries { Path = p, Bytes = maps.Select(m => m.TryGetValue(p, out var b) ? b : (long?)null).ToArray() })
                           .OrderByDescending(f => Math.Abs(f.Change)).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var samples = history.Load().Where(s => s.Drive.Equals(drive, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Utc).ToList();
        return new FolderHistoryResult { Drive = drive, Scans = summaries.Select(s => s.Utc).ToList(), Folders = folders, Samples = samples };
    }

    /// <summary>One row per folder, one column per scan (bytes; empty where the folder was under the threshold).</summary>
    public static string ToCsv(FolderHistoryResult r, char separator = ',')
    {
        string Q(string s) => Util.Csv.Cell(s, separator);
        var sb = new StringBuilder();
        sb.Append("Folder").Append(separator).Append(string.Join(separator, r.Scans.Select(t => t.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)))).Append(separator).Append("Change").Append("\r\n");
        foreach (var f in r.Folders)
            sb.Append(Q(r.Drive + "\\" + f.Path)).Append(separator).Append(string.Join(separator, f.Bytes.Select(b => b?.ToString(CultureInfo.InvariantCulture) ?? ""))).Append(separator).Append(f.Change.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        return sb.ToString();
    }
}
