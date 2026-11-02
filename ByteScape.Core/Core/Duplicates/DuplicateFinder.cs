using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using ByteScape.Core.Util;

namespace ByteScape.Core.Duplicates;

/// <summary>One copy of a duplicated file (a physical file: its hard-linked names are listed with it, they take no extra space).</summary>
public sealed class DuplicateCopy
{
    public required FileNode Node { get; init; }
    public required string Path { get; init; }
    public DateTime Modified => Node.Modified;
    public long DiskSize => Node.DiskSize;
    /// <summary>Other names of the same physical file (hard links): deleting this copy frees nothing unless they go too.</summary>
    public List<string> OtherNames { get; } = new();
}

/// <summary>Files with the same content (same length and SHA-256).</summary>
public sealed class DuplicateGroup
{
    public required long Length { get; init; }
    public required string Hash { get; init; }
    public List<DuplicateCopy> Copies { get; } = new();
    /// <summary>Space the extra copies take: everything but the biggest copy on disk.</summary>
    public long Wasted => Copies.Sum(c => c.DiskSize) - Copies.Max(c => c.DiskSize);
    public string Name => Copies[0].Node.Name;
}

public sealed class DuplicateOptions
{
    /// <summary>Smaller files are not compared (many tiny duplicates free little and cost a lot of reading).</summary>
    public long MinBytes { get; init; } = 1L << 20;
    /// <summary>Windows' own folder (its duplicates are hard links and system files) is left out unless this is set.</summary>
    public bool IncludeWindows { get; init; }
    /// <summary>Files hashed at the same time (0 = as the drive's scan strategy says).</summary>
    public int Threads { get; init; }
    internal int EdgeBytes { get; init; } = 64 * 1024;
}

public readonly record struct DuplicateProgress(string Phase, long Done, long Total);

public sealed class DuplicateResult
{
    public required string RootPath { get; init; }
    public List<DuplicateGroup> Groups { get; } = new();
    public long WastedBytes => Groups.Sum(g => g.Wasted);
    public int Candidates { get; internal set; }
    public long BytesRead { get; internal set; }
    /// <summary>Online-only cloud files: never read (reading would download them).</summary>
    public int SkippedOnline { get; internal set; }
    /// <summary>Files that could not be read (no rights, open without sharing).</summary>
    public int SkippedUnreadable { get; internal set; }
    /// <summary>Names that are hard links of another name found: the same file, not a copy.</summary>
    public int HardLinkNames { get; internal set; }
    public TimeSpan Elapsed { get; internal set; }
}

/// <summary>
/// Finds files with the same content in a scanned tree without reading more than needed: files are grouped by length,
/// names of one physical file (hard links, same NTFS id) count once, then the first and last 64 KB decide which files
/// can still be equal, and only those are read whole (SHA-256). Online-only cloud files are never read. Meant for a
/// background thread; the tree lock is taken per folder.
/// </summary>
public static class DuplicateFinder
{
    private const FileAttributes NotHere = FileAttributes.Offline | Cloud.CloudFiles.RecallOnDataAccess;

    public static DuplicateResult Find(ContainerNode root, object? sync, DuplicateOptions opt, IProgress<DuplicateProgress>? progress, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        string rootPath = root.RealPath().TrimEnd('\\');
        if (rootPath.Length == 2) rootPath += "\\";
        var result = new DuplicateResult { RootPath = rootPath };
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');

        // 1. every file big enough, by length; hard-linked names of one file folded into one physical file
        var byLength = new Dictionary<long, List<DuplicateCopy>>();
        var byId = new Dictionary<long, DuplicateCopy>();
        var stack = new Stack<(ContainerNode Node, string Path)>();
        stack.Push((root, rootPath));
        var kids = new List<FsNode>();
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (c, p) = stack.Pop();
            kids.Clear();
            if (sync != null) lock (sync) kids.AddRange(c.Children);
            else kids.AddRange(c.Children);
            foreach (var k in kids)
            {
                if (k.IsSynthetic) continue;
                string kp = p.EndsWith('\\') ? p + k.Name : p + "\\" + k.Name;
                if (k is ContainerNode kc)
                {
                    if (!opt.IncludeWindows && windows.Length > 0 && kp.Equals(windows, StringComparison.OrdinalIgnoreCase)) continue;
                    stack.Push((kc, kp));
                    continue;
                }
                var f = (FileNode)k;
                if (f.LogicalSize < Math.Max(1, opt.MinBytes)) continue;
                if ((f.Attributes & NotHere) != 0) { result.SkippedOnline++; continue; }
                if (f.FileId != 0 && byId.TryGetValue(f.FileId, out var same)) { same.OtherNames.Add(kp); result.HardLinkNames++; continue; }
                var copy = new DuplicateCopy { Node = f, Path = kp };
                if (f.FileId != 0) byId[f.FileId] = copy;
                if (!byLength.TryGetValue(f.LogicalSize, out var list)) byLength[f.LogicalSize] = list = new List<DuplicateCopy>();
                list.Add(copy);
            }
        }
        var candidates = byLength.Values.Where(l => l.Count > 1).ToList();
        result.Candidates = candidates.Sum(l => l.Count);
        int threads = opt.Threads > 0 ? opt.Threads : Math.Clamp(DriveProfile.For(rootPath).Workers, 1, 8);
        long read = 0, unreadable = 0;

        // 2. the start and end of each candidate: most files of one length already differ there
        var edges = new List<List<DuplicateCopy>>();
        long edgeTotal = candidates.Sum(l => l.Count), edgeDone = 0;
        var hashes = new System.Collections.Concurrent.ConcurrentDictionary<DuplicateCopy, string>();
        Parallel.ForEach(candidates.SelectMany(l => l), new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = token }, copy =>
        {
            var h = Hash(copy, opt.EdgeBytes, edgesOnly: true, ref read, token);
            if (h == null) Interlocked.Increment(ref unreadable); else hashes[copy] = h;
            long d = Interlocked.Increment(ref edgeDone);
            if ((d & 0xFF) == 0) progress?.Report(new DuplicateProgress(/*loc*/ "Comparing the start and end of files", d, edgeTotal));
        });
        foreach (var l in candidates)
            foreach (var g in l.Where(hashes.ContainsKey).GroupBy(c => hashes[c]).Where(g => g.Count() > 1))
                edges.Add(g.ToList());

        // 3. whole files, only where the edges matched (files no longer than both edges are already fully read)
        long fullTotal = edges.Sum(g => g.Count == 0 ? 0 : g.Count * g[0].Node.LogicalSize), fullDone = 0;
        var full = new System.Collections.Concurrent.ConcurrentDictionary<DuplicateCopy, string>();
        Parallel.ForEach(edges.SelectMany(g => g), new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = token }, copy =>
        {
            string? h = copy.Node.LogicalSize <= 2L * opt.EdgeBytes ? hashes[copy] : Hash(copy, opt.EdgeBytes, edgesOnly: false, ref read, token);
            if (h == null) Interlocked.Increment(ref unreadable); else full[copy] = h;
            long d = Interlocked.Add(ref fullDone, copy.Node.LogicalSize);
            progress?.Report(new DuplicateProgress(/*loc*/ "Comparing whole files", d, fullTotal));
        });
        foreach (var g in edges)
            foreach (var same in g.Where(full.ContainsKey).GroupBy(c => full[c]).Where(x => x.Count() > 1))
            {
                var group = new DuplicateGroup { Length = same.First().Node.LogicalSize, Hash = same.Key };
                group.Copies.AddRange(same.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase));
                result.Groups.Add(group);
            }
        result.Groups.Sort((a, b) => b.Wasted.CompareTo(a.Wasted));
        result.BytesRead = read;
        result.SkippedUnreadable = (int)unreadable;
        result.Elapsed = sw.Elapsed;
        return result;
    }

    /// <summary>SHA-256 of the whole file, or (<paramref name="edgesOnly"/>) of its first and last <paramref name="edge"/> bytes; null when it cannot be read.</summary>
    private static string? Hash(DuplicateCopy copy, int edge, bool edgesOnly, ref long read, CancellationToken token)
    {
        try
        {
            using var fs = new FileStream(DirectoryScanner.IoPath(copy.Path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[edgesOnly ? edge : 1 << 20];
            long length = fs.Length;
            if (length != copy.Node.LogicalSize) return null;   // changed since the scan
            if (edgesOnly)
            {
                int n = ReadFull(fs, buf, (int)Math.Min(edge, length));
                sha.AppendData(buf, 0, n);
                Interlocked.Add(ref read, n);
                if (length > 2L * edge)
                {
                    fs.Seek(-edge, SeekOrigin.End);
                    n = ReadFull(fs, buf, edge);
                    sha.AppendData(buf, 0, n);
                    Interlocked.Add(ref read, n);
                }
                else if (length > edge)
                {
                    n = ReadFull(fs, buf, (int)(length - edge));
                    sha.AppendData(buf, 0, n);
                    Interlocked.Add(ref read, n);
                }
            }
            else
            {
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    sha.AppendData(buf, 0, n);
                    Interlocked.Add(ref read, n);
                }
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Fine("Duplicates: cannot read " + copy.Path + " - " + ex.Message); return null; }
    }

    private static int ReadFull(Stream s, byte[] buf, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buf, total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>
    /// The copies to delete so that one per group stays: the one to keep is the oldest (<paramref name="keep"/> = Oldest),
    /// the newest, or the first in path order. Copies <paramref name="cannot"/> delete (protected) are always kept.
    /// </summary>
    public static List<DuplicateCopy> AllButOne(DuplicateGroup g, KeepRule keep, Func<DuplicateCopy, bool>? cannot = null)
    {
        var deletable = g.Copies.Where(c => cannot == null || !cannot(c)).ToList();
        if (deletable.Count < g.Copies.Count) return deletable;           // a protected copy stays anyway: every other copy can go
        var keeper = keep switch
        {
            KeepRule.Oldest => g.Copies.OrderBy(c => c.Modified).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).First(),
            KeepRule.Newest => g.Copies.OrderByDescending(c => c.Modified).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).First(),
            _ => g.Copies[0],
        };
        return g.Copies.Where(c => !ReferenceEquals(c, keeper)).ToList();
    }

    public enum KeepRule { Oldest, Newest, FirstPath }

    /// <summary>Plain text (or, for a .csv file name, CSV; for .json, JSON) report of the groups.</summary>
    public static string ToText(DuplicateResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(F($"Duplicate files in {r.RootPath}"));
        sb.AppendLine(P(r.Groups.Count, "{0:N0} group, {1} in extra copies;", "{0:N0} groups, {1} in extra copies;", Format.Size(r.WastedBytes)) + " " + F($"{r.Candidates:N0} files of equal length compared, {Format.Size(r.BytesRead)} read in {r.Elapsed.TotalSeconds:0.0} s."));
        if (r.HardLinkNames > 0) sb.AppendLine(P(r.HardLinkNames, "{0:N0} name is a hard link of another name (the same file, no extra space): not counted as a copy.", "{0:N0} names are hard links of another name (the same file, no extra space): not counted as copies."));
        if (r.SkippedOnline > 0) sb.AppendLine(P(r.SkippedOnline, "{0:N0} online-only cloud file was not read (reading would download it).", "{0:N0} online-only cloud files were not read (reading would download them)."));
        if (r.SkippedUnreadable > 0) sb.AppendLine(P(r.SkippedUnreadable, "{0:N0} file could not be read.", "{0:N0} files could not be read."));
        foreach (var g in r.Groups)
        {
            sb.AppendLine();
            sb.AppendLine(F($"{Format.Size(g.Length)} × {g.Copies.Count} copies — {Format.Size(g.Wasted)} extra — {g.Name}"));
            foreach (var c in g.Copies)
                sb.AppendLine("   " + c.Path + "   (" + Format.Date(c.Modified) + ")" + (c.OtherNames.Count > 0 ? "   + " + P(c.OtherNames.Count, "{0} hard link", "{0} hard links") : ""));
        }
        return sb.ToString();
    }

    public static string ToCsv(DuplicateResult r, char separator = ',')
    {
        string Q(string s) => Util.Csv.Cell(s, separator);
        var sb = new StringBuilder();
        sb.Append(string.Join(separator, "Group", "Hash", "Length", "Copies", "Wasted", "Path", "SizeOnDisk", "Modified", "HardLinks")).Append("\r\n");
        int i = 0;
        foreach (var g in r.Groups)
        {
            i++;
            foreach (var c in g.Copies)
                sb.Append(string.Join(separator, i.ToString(), g.Hash, g.Length.ToString(), g.Copies.Count.ToString(), g.Wasted.ToString(), Q(c.Path), c.DiskSize.ToString(),
                    c.Modified == default ? "" : c.Modified.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture), c.OtherNames.Count.ToString())).Append("\r\n");
        }
        return sb.ToString();
    }

    public static string ToJson(DuplicateResult r) => System.Text.Json.JsonSerializer.Serialize(new
    {
        path = r.RootPath, wasted = r.WastedBytes, groups = r.Groups.Select(g => new
        {
            length = g.Length, hash = g.Hash, wasted = g.Wasted,
            copies = g.Copies.Select(c => new { path = c.Path, sizeOnDisk = c.DiskSize, modified = c.Modified, hardLinks = c.OtherNames }),
        }),
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
}
