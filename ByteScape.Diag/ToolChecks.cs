// Duplicate files, CSV / JSON / HTML reports and folder comparison on real data. Read only (the reports go to the given folder).
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ByteScape.Core.Compare;
using ByteScape.Core.Config;
using ByteScape.Core.Duplicates;
using ByteScape.Core.Export;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;

namespace ByteScape.Diag;

internal static partial class Program
{
    private static ScanTree ScanOnce(string path, out double seconds)
    {
        AppConfig.Replace(new AppConfig { WatchDog = false });
        var t = new ScanTree(DataCache.CreateRoot(path), live: true);
        var sw = Stopwatch.StartNew();
        t.StartMasterScan();
        while (t.IsMasterScanning) Thread.Sleep(10);
        seconds = sw.Elapsed.TotalSeconds;
        return t;
    }

    private static void DuplicatesCheck(string path, long minBytes)
    {
        Out($"== DUPLICATES {path} (files from {Fmt(minBytes)}, read only)");
        var tree = ScanOnce(path, out var scan);
        Out($"   scan           : {scan:0.00} s, {tree.Root.FileCount:N0} files");
        var r = DuplicateFinder.Find(tree.Root, tree.Sync, new DuplicateOptions { MinBytes = minBytes }, null, CancellationToken.None);
        Out($"   found          : {r.Groups.Count:N0} group(s), {Fmt(r.WastedBytes)} extra; {r.Candidates:N0} candidates, {Fmt(r.BytesRead)} read in {r.Elapsed.TotalSeconds:0.00} s; hard link names {r.HardLinkNames:N0}, online-only {r.SkippedOnline}, unreadable {r.SkippedUnreadable}");
        foreach (var g in r.Groups.Take(8)) Out($"      {Fmt(g.Wasted),9}  {g.Copies.Count} × {Fmt(g.Length)}  {g.Name}");
        // check a few groups byte by byte with Windows' own comparison (fc /b)
        var rnd = new Random(1);
        int checkedGroups = 0, same = 0;
        foreach (var g in r.Groups.OrderBy(_ => rnd.Next()).Take(10))
        {
            checkedGroups++;
            var psi = new ProcessStartInfo("fc.exe", $"/b \"{g.Copies[0].Path}\" \"{g.Copies[1].Path}\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode == 0) same++;
        }
        Out($"   fc /b          : {same} of {checkedGroups} random group(s) identical byte by byte");
        DataCache.Release(tree);
        AppConfig.Replace(new AppConfig());
    }

    private static void ReportsCheck(string path, string outDir)
    {
        Out($"== REPORTS {path} → {outDir}");
        var tree = ScanOnce(path, out var scan);
        Directory.CreateDirectory(outDir);
        bool All(FsNode _) => true;
        long Size(FsNode n) => n.DiskSize;
        foreach (var f in new[] { ReportFormat.Csv, ReportFormat.Json, ReportFormat.Html })
        {
            var file = Path.Combine(outDir, "report." + f.ToString().ToLowerInvariant());
            var sw = Stopwatch.StartNew();
            Reports.WriteFile(f, file, tree.Root, tree.Sync, All, Size, CancellationToken.None);
            Out($"   {f,-5}          : {sw.ElapsedMilliseconds,6} ms, {Fmt(new FileInfo(file).Length)}");
        }
        // what the files say against the tree
        int csvRows = File.ReadLines(Path.Combine(outDir, "report.csv")).Count() - 1;
        int expected = tree.Root.FileCount + tree.Root.FolderCount + 1;
        Out($"   CSV rows       : {csvRows:N0} (tree: {expected:N0} elements) {(csvRows == expected ? "MATCH" : "DIFF")}");
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(outDir, "report.json")), new JsonDocumentOptions { MaxDepth = 4 * Reports.JsonMaxLevels }))
        {
            var root = doc.RootElement.GetProperty("root");
            long json = root.GetProperty("sizeOnDisk").GetInt64();
            int files = root.GetProperty("files").GetInt32();
            Out($"   JSON root      : {Fmt(json)}, {files:N0} files (tree: {Fmt(tree.Root.DiskSize)}, {tree.Root.FileCount:N0}) {(json == tree.Root.DiskSize && files == tree.Root.FileCount ? "MATCH" : "DIFF")}");
        }
        var html = File.ReadAllText(Path.Combine(outDir, "report.html"));
        Out($"   HTML           : {(html.Contains("<h2>Largest files</h2>") && html.EndsWith("</html>\n") ? "complete" : "INCOMPLETE")}");
        DataCache.Release(tree);
        AppConfig.Replace(new AppConfig());
    }

    private static void CompareFoldersCheck(string left, string right, bool contents)
    {
        Out($"== COMPARE FOLDERS {left} ↔ {right}{(contents ? " (contents)" : "")}");
        var a = ScanOnce(left, out var s1);
        var b = ScanOnce(right, out var s2);
        var r = FolderCompare.Compare(a.Root, a.Sync, b.Root, b.Sync, new FolderCompareOptions { CompareContents = contents });
        foreach (var k in Enum.GetValues<FolderDiffKind>())
            Out($"   {FolderCompare.KindText(k),-18}: {r.Count(k),8:N0}  {Fmt(r.Bytes(k))}");
        Out($"   time           : scans {s1:0.00} + {s2:0.00} s, compare {r.Elapsed.TotalMilliseconds:0} ms");
        foreach (var e in r.Entries.Where(e => e.Kind != FolderDiffKind.Same).Take(8))
            Out($"      {FolderCompare.KindText(e.Kind),-18} {e.RelativePath}  {e.Reason}");
        DataCache.Release(a); DataCache.Release(b);
        AppConfig.Replace(new AppConfig());
    }
}
