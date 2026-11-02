using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using ByteScape.Core.Util;

namespace ByteScape.Core.Compare;

public enum FolderDiffKind { OnlyLeft, OnlyRight, Different, Same }

/// <summary>One element of a folder comparison (a folder present on one side only is one entry with its whole content).</summary>
public sealed record FolderDiffEntry(string RelativePath, bool IsFolder, FolderDiffKind Kind, FsNode? Left, FsNode? Right, long LeftBytes, long RightBytes, DateTime LeftModified, DateTime RightModified, string Reason)
{
    public string Name => System.IO.Path.GetFileName(RelativePath);
}

public sealed class FolderCompareOptions
{
    /// <summary>Files of the same length and time are also read and compared (SHA-256): finds silent corruption and copies with a touched time.</summary>
    public bool CompareContents { get; init; }
    /// <summary>Times this close count as equal (FAT and exFAT keep 2 second steps, copies can round).</summary>
    public TimeSpan TimeTolerance { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>A time differing by whole hours only (up to 14) is a time zone / summer time shift of FAT drives, not a change.</summary>
    public bool IgnoreHourShifts { get; init; } = true;
}

public sealed class FolderCompareResult
{
    public required string LeftPath { get; init; }
    public required string RightPath { get; init; }
    public List<FolderDiffEntry> Entries { get; } = new();
    public bool ContentsCompared { get; init; }
    public TimeSpan Elapsed { get; internal set; }
    public int Count(FolderDiffKind k) => Entries.Count(e => e.Kind == k);
    public long Bytes(FolderDiffKind k) => Entries.Where(e => e.Kind == k).Sum(e => k == FolderDiffKind.OnlyRight ? e.RightBytes : e.LeftBytes);
    /// <summary>Files (not folders) compared on both sides and found equal.</summary>
    public int SameFiles => Entries.Count(e => e.Kind == FolderDiffKind.Same && !e.IsFolder);
}

/// <summary>
/// Compares two folders (two scanned trees, or two folders of one tree) by relative path: what is only on the left, only
/// on the right, different (length, time, or with <see cref="FolderCompareOptions.CompareContents"/> the content) and the
/// same. A folder on one side only is one entry holding its whole content. Meant for a background thread.
/// </summary>
public static class FolderCompare
{
    public static FolderCompareResult Compare(ContainerNode left, object? leftSync, ContainerNode right, object? rightSync, FolderCompareOptions? options = null, CancellationToken token = default)
    {
        var opt = options ?? new FolderCompareOptions();
        var sw = Stopwatch.StartNew();
        string lp = Real(left), rp = Real(right);
        var r = new FolderCompareResult { LeftPath = lp, RightPath = rp, ContentsCompared = opt.CompareContents };
        List<FsNode> Kids(ContainerNode c, object? sync)
        {
            if (sync != null) lock (sync) return c.Children.Where(k => !k.IsSynthetic).ToList();
            return c.Children.Where(k => !k.IsSynthetic).ToList();
        }
        void Walk(ContainerNode l, ContainerNode rt, string rel)
        {
            token.ThrowIfCancellationRequested();
            var rightByName = new Dictionary<string, FsNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in Kids(rt, rightSync)) rightByName.TryAdd(k.Name, k);
            foreach (var k in Kids(l, leftSync).OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
            {
                string kr = rel.Length == 0 ? k.Name : rel + "\\" + k.Name;
                if (!rightByName.Remove(k.Name, out var other) || (other is ContainerNode) != (k is ContainerNode))
                {
                    if (other != null) r.Entries.Add(Only(other, kr, FolderDiffKind.OnlyRight));   // same name, a file on one side and a folder on the other
                    r.Entries.Add(Only(k, kr, FolderDiffKind.OnlyLeft));
                    continue;
                }
                if (k is ContainerNode kc) { Walk(kc, (ContainerNode)other, kr); continue; }
                r.Entries.Add(Files((FileNode)k, (FileNode)other, kr, lp, rp, opt, token));
            }
            foreach (var other in rightByName.Values.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
                r.Entries.Add(Only(other, rel.Length == 0 ? other.Name : rel + "\\" + other.Name, FolderDiffKind.OnlyRight));
        }
        Walk(left, right, "");
        r.Elapsed = sw.Elapsed;
        return r;
    }

    private static string Real(ContainerNode c)
    {
        var p = c.RealPath().TrimEnd('\\');
        return p.Length == 2 ? p + "\\" : p;
    }

    private static FolderDiffEntry Only(FsNode n, string rel, FolderDiffKind kind)
    {
        bool left = kind == FolderDiffKind.OnlyLeft;
        DateTime m = n is FileNode f ? f.Modified : (n as FolderNode)?.Modified ?? default;
        string reason = n is ContainerNode c ? P(c.FileCount, "{0:N0} file inside", "{0:N0} files inside") : "";
        return new FolderDiffEntry(rel, n is ContainerNode, kind, left ? n : null, left ? null : n, left ? n.DiskSize : 0, left ? 0 : n.DiskSize, left ? m : default, left ? default : m, reason);
    }

    private static FolderDiffEntry Files(FileNode a, FileNode b, string rel, string lp, string rp, FolderCompareOptions opt, CancellationToken token)
    {
        FolderDiffEntry E(FolderDiffKind k, string why) => new(rel, false, k, a, b, a.DiskSize, b.DiskSize, a.Modified, b.Modified, why);
        if (a.LogicalSize != b.LogicalSize) return E(FolderDiffKind.Different, T(a.LogicalSize > b.LogicalSize ? "bigger on the left" : "bigger on the right"));
        var diff = (a.Modified - b.Modified).Duration();
        bool sameTime = diff <= opt.TimeTolerance
                        || opt.IgnoreHourShifts && diff < TimeSpan.FromHours(14.1) && (diff - TimeSpan.FromHours(Math.Round(diff.TotalHours))).Duration() <= opt.TimeTolerance;
        if (!opt.CompareContents)
            return sameTime ? E(FolderDiffKind.Same, "") : E(FolderDiffKind.Different, T(a.Modified > b.Modified ? "newer on the left" : "newer on the right"));
        var ha = Hash(Join(lp, rel), token);
        var hb = Hash(Join(rp, rel), token);
        if (ha == null || hb == null) return E(FolderDiffKind.Different, T(ha == null ? "could not be read on the left" : "could not be read on the right"));
        if (ha != hb) return E(FolderDiffKind.Different, T("same length, different content"));
        return E(FolderDiffKind.Same, sameTime ? "" : T("same content, different time"));
    }

    private static string Join(string root, string rel) => root.EndsWith('\\') ? root + rel : root + "\\" + rel;

    private static string? Hash(string path, CancellationToken token)
    {
        try
        {
            if ((File.GetAttributes(DirectoryScanner.IoPath(path)) & (FileAttributes.Offline | Cloud.CloudFiles.RecallOnDataAccess)) != 0) return null;   // reading would download it
            using var fs = new FileStream(DirectoryScanner.IoPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[1 << 20];
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0) { token.ThrowIfCancellationRequested(); sha.AppendData(buf, 0, n); }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public static string KindText(FolderDiffKind k) => k switch
    {
        FolderDiffKind.OnlyLeft => T("Only on the left"),
        FolderDiffKind.OnlyRight => T("Only on the right"),
        FolderDiffKind.Different => T("Different"),
        _ => T("Same"),
    };

    public static string ToText(FolderCompareResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(T("Left:") + "  " + r.LeftPath);
        sb.AppendLine(T("Right:") + " " + r.RightPath);
        foreach (var k in Enum.GetValues<FolderDiffKind>())
            sb.AppendLine($"{KindText(k),-18}: {r.Count(k),8:N0}  {Format.Size(r.Bytes(k)),10}");
        sb.AppendLine(T(r.ContentsCompared ? "Contents of files of equal length were compared." : "Files are compared by length and time."));
        foreach (var k in new[] { FolderDiffKind.OnlyLeft, FolderDiffKind.OnlyRight, FolderDiffKind.Different })
        {
            sb.AppendLine();
            sb.AppendLine("== " + KindText(k));
            foreach (var e in r.Entries.Where(e => e.Kind == k))
                sb.AppendLine("   " + e.RelativePath + (e.IsFolder ? "\\" : "") + "   " + (k == FolderDiffKind.Different ? Format.Size(e.LeftBytes) + " / " + Format.Size(e.RightBytes) + "   " + e.Reason : Format.Size(Math.Max(e.LeftBytes, e.RightBytes)) + (e.Reason.Length > 0 ? "   " + e.Reason : "")));
        }
        return sb.ToString();
    }

    public static string ToCsv(FolderCompareResult r, char separator = ',')
    {
        string Q(string s) => Util.Csv.Cell(s, separator);
        string T(DateTime d) => d == default ? "" : d.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append(string.Join(separator, "RelativePath", "Type", "Result", "LeftSize", "RightSize", "LeftModified", "RightModified", "Reason")).Append("\r\n");
        foreach (var e in r.Entries)
            sb.Append(string.Join(separator, Q(e.RelativePath), e.IsFolder ? "folder" : "file", e.Kind.ToString(), e.LeftBytes.ToString(), e.RightBytes.ToString(), T(e.LeftModified), T(e.RightModified), Q(e.Reason))).Append("\r\n");
        return sb.ToString();
    }
}
