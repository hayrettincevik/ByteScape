using System.Globalization;
using System.Text;
using System.Text.Json;
using ByteScape.Core.Model;
using ByteScape.Core.Util;

namespace ByteScape.Core.Export;

public enum ReportFormat { Csv, Json, Html }

/// <summary>
/// Reports for other programs and people: a CSV list of every element (spreadsheets), a JSON tree (scripts), and a
/// self-contained HTML page (summary, largest folders and files, file types, ages, with bars). They follow the view's
/// filter (<c>include</c>) and size mode (<c>sizeOf</c>); the tree lock is taken per folder.
/// </summary>
public static class Reports
{
    public const string CsvName = "CSV list (spreadsheet)", JsonName = "JSON tree", HtmlName = "HTML report";

    public static ReportFormat? FormatOfName(string name) => name switch
    {
        CsvName => ReportFormat.Csv,
        JsonName => ReportFormat.Json,
        HtmlName => ReportFormat.Html,
        _ => null,
    };

    /// <summary>The children of a folder the report shows, biggest first, copied under the lock.</summary>
    private static List<FsNode> Kids(ContainerNode c, object? sync, Func<FsNode, bool> include, Func<FsNode, long> sizeOf)
    {
        List<FsNode> list;
        if (sync != null) lock (sync) list = c.Children.Where(k => !k.IsSynthetic && include(k)).ToList();
        else list = c.Children.Where(k => !k.IsSynthetic && include(k)).ToList();
        list.Sort((a, b) => sizeOf(b).CompareTo(sizeOf(a)));
        return list;
    }

    private static string Iso(DateTime utc) => utc == default ? "" : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ CSV

    /// <summary>
    /// One line per element: path, name, type, extension, size (length), size on disk, files and folders below, modified, created,
    /// attributes. <paramref name="separator"/>: null = the list separator of the current culture (what Excel expects:
    /// ";" in Turkish and most European settings, "," in English ones).
    /// </summary>
    public static int WriteCsv(ContainerNode root, object? sync, Func<FsNode, bool> include, Func<FsNode, long> sizeOf, TextWriter w, CancellationToken token, char? separator = null, Action<int>? progress = null)
    {
        char sep = separator ?? (CultureInfo.CurrentCulture.TextInfo.ListSeparator is { Length: 1 } ls ? ls[0] : ',');
        string Q(string s) => Csv.Cell(s, sep);
        void Row(params string[] cells) => w.Write(string.Join(sep, cells.Select(Q)) + "\r\n");
        Row("Path", "Name", "Type", "Extension", "Size", "SizeOnDisk", "Files", "Folders", "Modified", "Created", "Attributes");
        int rows = 0;
        var stack = new Stack<ContainerNode>();
        stack.Push(root);
        string rootPath = root.RealPath();
        bool first = true;
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var c = stack.Pop();
            if (first)
            {
                Row(rootPath, c.Name, "folder", "", c.LogicalSize.ToString(CultureInfo.InvariantCulture), c.DiskSize.ToString(CultureInfo.InvariantCulture),
                    c.FileCount.ToString(CultureInfo.InvariantCulture), c.FolderCount.ToString(CultureInfo.InvariantCulture), Iso((c as FolderNode)?.Modified ?? default), Iso((c as FolderNode)?.Created ?? default), ((c as FolderNode)?.Attributes ?? 0).ToString());
                rows++; first = false;
            }
            var kids = Kids(c, sync, include, sizeOf);
            foreach (var k in kids)
            {
                string path = k.RealPath();
                if (k is FileNode f)
                    Row(path, f.Name, "file", f.Extension, f.LogicalSize.ToString(CultureInfo.InvariantCulture), f.DiskSize.ToString(CultureInfo.InvariantCulture), "", "", Iso(f.Modified), Iso(f.Created), f.Attributes.ToString());
                else if (k is FolderNode fo)
                    Row(path, fo.Name, "folder", "", fo.LogicalSize.ToString(CultureInfo.InvariantCulture), fo.DiskSize.ToString(CultureInfo.InvariantCulture),
                        fo.FileCount.ToString(CultureInfo.InvariantCulture), fo.FolderCount.ToString(CultureInfo.InvariantCulture), Iso(fo.Modified), Iso(fo.Created), fo.Attributes.ToString());
                if ((++rows & 0x3FF) == 0) progress?.Invoke(rows);
            }
            // folders in the order listed (biggest first): pushed backwards
            for (int i = kids.Count - 1; i >= 0; i--) if (kids[i] is ContainerNode kc) stack.Push(kc);
        }
        progress?.Invoke(rows);
        return rows;
    }

    // ------------------------------------------------------------------ JSON

    /// <summary>Folder levels a JSON report nests at most: deeper folders keep their totals and are marked "truncated" (readers limit nesting too).</summary>
    public const int JsonMaxLevels = 1000;

    /// <summary>
    /// The tree as nested JSON objects (folders with "children"), biggest first, compact (<paramref name="indented"/> for people);
    /// <paramref name="maxDepth"/> limits the nesting (folders below keep their totals).
    /// </summary>
    public static int WriteJson(ContainerNode root, object? sync, Func<FsNode, bool> include, Func<FsNode, long> sizeOf, Stream s, CancellationToken token, int maxDepth = JsonMaxLevels, Action<int>? progress = null, bool indented = false)
    {
        maxDepth = Math.Min(maxDepth, JsonMaxLevels);
        using var w = new Utf8JsonWriter(s, new JsonWriterOptions { Indented = indented, MaxDepth = 2 * JsonMaxLevels + 16 });
        int nodes = 0;
        w.WriteStartObject();
        w.WriteString("tool", Branding.Name + " " + Branding.Version);
        w.WriteString("generated", Iso(DateTime.UtcNow));
        w.WriteString("path", root.RealPath());
        if (root is DriveNode { IsDrive: true } d) { w.WriteNumber("driveTotal", d.TotalBytes); w.WriteNumber("driveFree", d.FreeBytes); }
        w.WritePropertyName("root");
        void Node(FsNode n, int depth)
        {
            token.ThrowIfCancellationRequested();
            w.WriteStartObject();
            w.WriteString("name", n is DriveNode ? n.RealPath() : n.Name);
            w.WriteString("type", n is FileNode ? "file" : "folder");
            w.WriteNumber("size", n.LogicalSize);
            w.WriteNumber("sizeOnDisk", n.DiskSize);
            if (n is FileNode f)
            {
                w.WriteString("modified", Iso(f.Modified));
                if (f.Attributes != FileAttributes.Archive && f.Attributes != 0) w.WriteString("attributes", f.Attributes.ToString());
            }
            else if (n is ContainerNode c)
            {
                w.WriteNumber("files", c.FileCount);
                w.WriteNumber("folders", c.FolderCount);
                if (c is FolderNode fo && fo.Modified != default) w.WriteString("modified", Iso(fo.Modified));
                if (depth >= maxDepth && c.ChildCount > 0) w.WriteBoolean("truncated", true);
                else
                {
                    w.WritePropertyName("children");
                    w.WriteStartArray();
                    foreach (var k in Kids(c, sync, include, sizeOf)) Node(k, depth + 1);
                    w.WriteEndArray();
                }
            }
            w.WriteEndObject();
            if ((++nodes & 0x3FF) == 0) progress?.Invoke(nodes);
        }
        Node(root, 0);
        w.WriteEndObject();
        w.Flush();
        progress?.Invoke(nodes);
        return nodes;
    }

    // ------------------------------------------------------------------ HTML

    /// <summary>What the HTML report is made of (also useful on its own).</summary>
    public sealed class Stats
    {
        public long Total, Files, Folders;
        public List<(FsNode Node, string Path, long Bytes)> TopLevel { get; } = new();
        public List<(FsNode Node, string Path, long Bytes, int Depth)> Folders2 { get; } = new();
        public List<(FileNode Node, string Path, long Bytes)> LargestFiles { get; } = new();
        public Dictionary<string, (long Bytes, int Count)> Types { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Bytes by age of last change: under a month, 6 months, a year, 3 years, older, unknown.</summary>
        public long[] Ages { get; } = new long[6];
    }

    public static readonly string[] AgeLabels = { /*loc*/ "under 1 month", /*loc*/ "1 – 6 months", /*loc*/ "6 – 12 months", /*loc*/ "1 – 3 years", /*loc*/ "over 3 years", /*loc*/ "unknown" };

    public static Stats Collect(ContainerNode root, object? sync, Func<FsNode, bool> include, Func<FsNode, long> sizeOf, CancellationToken token, int topFiles = 50, int topFolders = 30, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var st = new Stats { Total = sizeOf(root) };
        var heap = new PriorityQueue<(FileNode, string), long>();
        var folders = new PriorityQueue<(FsNode, string, int), long>();
        var stack = new Stack<(ContainerNode Node, string Path, int Depth)>();
        string rootPath = root.RealPath().TrimEnd('\\');
        stack.Push((root, rootPath.Length == 2 ? rootPath + "\\" : rootPath, 0));
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (c, p, depth) = stack.Pop();
            foreach (var k in Kids(c, sync, include, sizeOf))
            {
                string kp = p.EndsWith('\\') ? p + k.Name : p + "\\" + k.Name;
                long size = sizeOf(k);
                if (depth == 0) st.TopLevel.Add((k, kp, size));
                if (k is FileNode f)
                {
                    st.Files++;
                    heap.Enqueue((f, kp), size);
                    if (heap.Count > topFiles) heap.Dequeue();
                    var ext = f.Extension.Length > 0 ? "." + f.Extension.ToLowerInvariant() : T("(no extension)");
                    st.Types.TryGetValue(ext, out var t);
                    st.Types[ext] = (t.Bytes + size, t.Count + 1);
                    double days = (now - f.Modified).TotalDays;
                    int bucket = f.Modified == default || days < -1 ? 5 : days < 31 ? 0 : days < 183 ? 1 : days < 366 ? 2 : days < 1096 ? 3 : 4;
                    st.Ages[bucket] += size;
                }
                else if (k is ContainerNode kc)
                {
                    st.Folders++;
                    if (depth < 4) { folders.Enqueue((kc, kp, depth + 1), size); if (folders.Count > topFolders) folders.Dequeue(); }
                    stack.Push((kc, kp, depth + 1));
                }
            }
        }
        while (heap.TryDequeue(out var e, out var size)) st.LargestFiles.Add((e.Item1, e.Item2, size));
        st.LargestFiles.Reverse();
        while (folders.TryDequeue(out var e, out var size)) st.Folders2.Add((e.Item1, e.Item2, size, e.Item3));
        st.Folders2.Reverse();
        return st;
    }

    /// <summary>A self-contained HTML page (no scripts, no external files): opens in any browser, prints, mails.</summary>
    public static void WriteHtml(ContainerNode root, object? sync, Func<FsNode, bool> include, Func<FsNode, long> sizeOf, TextWriter w, CancellationToken token, string filterText = "")
    {
        var st = Collect(root, sync, include, sizeOf, token);
        // markup characters only: the page is UTF-8, so "ü" stays "ü" (HtmlEncode would write &#252;)
        string H(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
        string Pct(long part) => st.Total <= 0 ? "0" : (100.0 * part / st.Total).ToString("0.0", CultureInfo.InvariantCulture);
        string Bar(long part, long max) => "<div class=bar><div style=\"width:" + (max <= 0 ? "0" : (100.0 * part / max).ToString("0.0", CultureInfo.InvariantCulture)) + "%\"></div></div>";
        var path = root.RealPath();
        w.Write("<!DOCTYPE html>\n<html lang=\"" + (Loc.Language == Loc.Pseudo ? "en" : Loc.Language) + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        w.Write("<title>" + H(F($"{Branding.Name} report") + " — " + path) + "</title><style>");
        w.Write("body{font-family:'Segoe UI',system-ui,sans-serif;margin:24px auto;max-width:1100px;padding:0 16px;color:#1b2d4b;background:#f4f7fb}");
        w.Write("h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:28px 0 8px}.sub{color:#5c7091;font-size:13px}");
        w.Write(".cards{display:flex;flex-wrap:wrap;gap:12px;margin-top:16px}.card{background:#fff;border:1px solid #d9e3f0;border-radius:6px;padding:12px 16px;min-width:150px}");
        w.Write(".card b{display:block;font-size:20px;margin-top:2px}table{width:100%;border-collapse:collapse;background:#fff;border:1px solid #d9e3f0;font-size:13px}");
        w.Write("th,td{padding:5px 8px;border-bottom:1px solid #eef3fa;text-align:left;vertical-align:middle}th{background:#eef3fa;font-weight:600}td.n{text-align:right;white-space:nowrap}");
        w.Write("td.p{word-break:break-all}.bar{background:#eef3fa;border-radius:3px;height:8px;min-width:120px}.bar div{background:#1f6fdb;height:8px;border-radius:3px}");
        w.Write("footer{margin-top:28px;color:#5c7091;font-size:12px}</style></head><body>\n");
        w.Write("<h1>" + H(path) + "</h1><div class=sub>" + H(F($"{Branding.Name} report")) + ", " + H(DateTime.Now.ToString("f", CultureInfo.CurrentCulture)) + (filterText.Length > 0 ? " — " + H(T("filter:")) + " " + H(filterText) : "") + "</div>\n");
        w.Write("<div class=cards><div class=card>" + H(T("Size")) + "<b>" + H(Format.Size(st.Total)) + "</b></div><div class=card>" + H(T("Files")) + "<b>" + st.Files.ToString("N0") + "</b></div><div class=card>" + H(T("Folders")) + "<b>" + st.Folders.ToString("N0") + "</b></div>");
        if (root is DriveNode { IsDrive: true, TotalBytes: > 0 } d)
            w.Write("<div class=card>" + H(T("Drive")) + "<b>" + H(F($"{Format.Size(d.FreeBytes)} free")) + "</b><span class=sub>" + H(F($"of {Format.Size(d.TotalBytes)}")) + "</span></div>");
        w.Write("</div>\n");

        w.Write("<h2>" + H(T("Top level")) + "</h2><table><tr><th>" + H(T("Name")) + "</th><th>" + H(T("Size")) + "</th><th>" + H(T("Share")) + "</th><th></th></tr>");
        long maxTop = st.TopLevel.Count > 0 ? st.TopLevel.Max(t => t.Bytes) : 0;
        foreach (var (n, _, bytes) in st.TopLevel.Take(40))
            w.Write("<tr><td class=p>" + (n is ContainerNode ? "📁 " : "") + H(n.Name) + "</td><td class=n>" + H(Format.Size(bytes)) + "</td><td class=n>" + Pct(bytes) + "%</td><td>" + Bar(bytes, maxTop) + "</td></tr>");
        w.Write("</table>\n");

        w.Write("<h2>" + H(T("Largest folders")) + "</h2><table><tr><th>" + H(T("Folder")) + "</th><th>" + H(T("Size")) + "</th><th>" + H(T("Share")) + "</th><th></th></tr>");
        long maxFolder = st.Folders2.Count > 0 ? st.Folders2.Max(t => t.Bytes) : 0;
        foreach (var (_, p, bytes, _) in st.Folders2)
            w.Write("<tr><td class=p>" + H(p) + "</td><td class=n>" + H(Format.Size(bytes)) + "</td><td class=n>" + Pct(bytes) + "%</td><td>" + Bar(bytes, maxFolder) + "</td></tr>");
        w.Write("</table>\n");

        w.Write("<h2>" + H(T("Largest files")) + "</h2><table><tr><th>" + H(T("File")) + "</th><th>" + H(T("Size")) + "</th><th>" + H(T("Modified")) + "</th><th></th></tr>");
        long maxFile = st.LargestFiles.Count > 0 ? st.LargestFiles[0].Bytes : 0;
        foreach (var (f, p, bytes) in st.LargestFiles)
            w.Write("<tr><td class=p>" + H(p) + "</td><td class=n>" + H(Format.Size(bytes)) + "</td><td class=n>" + H(Format.Date(f.Modified)) + "</td><td>" + Bar(bytes, maxFile) + "</td></tr>");
        w.Write("</table>\n");

        w.Write("<h2>" + H(T("File types")) + "</h2><table><tr><th>" + H(T("Type")) + "</th><th>" + H(T("Files")) + "</th><th>" + H(T("Size")) + "</th><th>" + H(T("Share")) + "</th><th></th></tr>");
        var types = st.Types.OrderByDescending(t => t.Value.Bytes).ToList();
        long maxType = types.Count > 0 ? types[0].Value.Bytes : 0;
        foreach (var (ext, (bytes, count)) in types.Take(30))
            w.Write("<tr><td>" + H(ext) + "</td><td class=n>" + count.ToString("N0") + "</td><td class=n>" + H(Format.Size(bytes)) + "</td><td class=n>" + Pct(bytes) + "%</td><td>" + Bar(bytes, maxType) + "</td></tr>");
        if (types.Count > 30)
        {
            long rest = types.Skip(30).Sum(t => t.Value.Bytes);
            w.Write("<tr><td>" + H(P(types.Count - 30, "{0} other type", "{0} other types")) + "</td><td class=n>" + types.Skip(30).Sum(t => t.Value.Count).ToString("N0") + "</td><td class=n>" + H(Format.Size(rest)) + "</td><td class=n>" + Pct(rest) + "%</td><td>" + Bar(rest, maxType) + "</td></tr>");
        }
        w.Write("</table>\n");

        w.Write("<h2>" + H(T("By last change")) + "</h2><table><tr><th>" + H(T("Changed")) + "</th><th>" + H(T("Size")) + "</th><th>" + H(T("Share")) + "</th><th></th></tr>");
        long maxAge = st.Ages.Max();
        for (int i = 0; i < st.Ages.Length; i++)
            if (st.Ages[i] > 0 || i < 5) w.Write("<tr><td>" + H(T(AgeLabels[i])) + "</td><td class=n>" + H(Format.Size(st.Ages[i])) + "</td><td class=n>" + Pct(st.Ages[i]) + "%</td><td>" + Bar(st.Ages[i], maxAge) + "</td></tr>");
        w.Write("</table>\n");
        w.Write("<footer>" + H(F($"Made with {Branding.Name} {Branding.Version}.") + " " + T(st.Total == root.DiskSize ? "Sizes are sizes on disk." : "Sizes are as shown in the view.")) + "</footer></body></html>\n");
    }

    /// <summary>Writes a report file (the encoding each format wants: CSV with a BOM for Excel, JSON and HTML plain UTF-8).</summary>
    public static void WriteFile(ReportFormat format, string file, ContainerNode root, object? sync, Func<FsNode, bool> include, Func<FsNode, long> sizeOf, CancellationToken token, string filterText = "", Action<int>? progress = null)
    {
        switch (format)
        {
            case ReportFormat.Csv:
                using (var w = new StreamWriter(file, false, new UTF8Encoding(true))) WriteCsv(root, sync, include, sizeOf, w, token, progress: progress);
                break;
            case ReportFormat.Json:
                using (var s = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16)) WriteJson(root, sync, include, sizeOf, s, token, progress: progress);
                break;
            default:
                using (var w = new StreamWriter(file, false, new UTF8Encoding(false))) WriteHtml(root, sync, include, sizeOf, w, token, filterText);
                break;
        }
    }

    /// <summary>The first lines of a report, for the export dialog's preview.</summary>
    public static string Preview(ReportFormat format, ContainerNode root, object? sync, Func<FsNode, bool> include, Func<FsNode, long> sizeOf, int lines = 40)
    {
        if (format == ReportFormat.Html) return T("(HTML page: summary, top level, largest folders and files, file types and ages, with bars; open the file in a browser)");
        var sw = new LimitedWriter(lines);
        try
        {
            if (format == ReportFormat.Csv) WriteCsv(root, sync, include, sizeOf, sw, CancellationToken.None);
            else
            {
                using var ms = new MemoryStream();
                WriteJson(root, sync, include, sizeOf, ms, CancellationToken.None, maxDepth: 2, indented: true);
                var text = Encoding.UTF8.GetString(ms.ToArray()).Split('\n');
                return string.Join("\n", text.Take(lines)) + (text.Length > lines ? "\n…" : "");
            }
        }
        catch (LimitedWriter.Full) { }
        return sw.ToString() + "…";
    }

    private sealed class LimitedWriter : StringWriter
    {
        public sealed class Full : Exception { }
        private readonly int _max;
        private int _lines;
        public LimitedWriter(int max) => _max = max;
        public override void Write(string? value)
        {
            base.Write(value);
            if (value != null) _lines += value.Count(ch => ch == '\n');
            if (_lines >= _max) throw new Full();
        }
    }
}
