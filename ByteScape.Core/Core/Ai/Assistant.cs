using System.Text;
using System.Text.RegularExpressions;
using ByteScape.Core.Compare;
using ByteScape.Core.Filtering;
using ByteScape.Core.Model;
using ByteScape.Core.Util;

namespace ByteScape.Core.Ai;

/// <summary>
/// The three things the optional local model is allowed to do, each with a narrow prompt and a validated result:
/// turn a request into filter syntax (checked by the real parser), describe an unknown folder (facts only, no
/// deletion advice, always labelled unverified) and narrate a snapshot comparison. Everything degrades to the
/// rule based features when the model is off or unreachable.
/// </summary>
public static class Assistant
{
    private const string FilterGrammar =
        "You translate a user's request about files into ByteScape filter syntax. Output ONLY the filter, nothing else.\n" +
        "Syntax: conditions separated by ';'. Name masks: *.mp4  report*  (several masks = OR). Folder name mask: \\name*.\n" +
        "Size: >100mb  <2gb  (units b,kb,mb,gb,tb; disk size). Age: modify>6months  create<7days  access>1year (units minutes,hours,days,weeks,months,years;\n" +
        "'access' = not opened, 'create' = created, 'modify' = changed; '>' = older than, '<' = within). Tags: :red :yellow :green :blue.\n" +
        "Attributes: :attr:h (hidden) :attr:s (system) :attr:r (read-only). Known caches/temp: :reclaimable. Prefix '|' negates a condition.\n" +
        "Examples: 'videos over 1 GB not opened for a year' -> *.mp4;*.mkv;*.avi;*.mov;*.wmv;>1gb;access>1year\n" +
        "'1 yıldan eski büyük pdfler' -> *.pdf;>100mb;modify>1year\n'photos except png' -> *.jpg;*.jpeg;*.heic;|*.png\n" +
        "If the request cannot be expressed, output the single word NONE.";

    /// <summary>Filter syntax for a request, or null when the model gives nothing the parser accepts.</summary>
    public static async Task<(string filter, string raw)?> TranslateFilterAsync(LocalModel model, string request, CancellationToken ct)
    {
        var raw = await model.CompleteAsync(FilterGrammar, request.Trim(), ct, temperature: 0, maxTokens: 120).ConfigureAwait(false);
        var candidate = ExtractFilter(raw);
        if (candidate == null) return null;
        try
        {
            var f = new Filter(candidate);
            return f.IsEmpty ? null : (candidate, raw);
        }
        catch (FilterException) { return null; }
    }

    /// <summary>Pulls the filter out of a chatty answer: first line that parses as syntax, without code fences or prose.</summary>
    internal static string? ExtractFilter(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (var line in raw.Replace("```", "\n").Split('\n'))
        {
            var t = line.Trim().Trim('`', '"', '\'', ' ');
            if (t.Length == 0 || t.Equals("NONE", StringComparison.OrdinalIgnoreCase)) continue;
            if (t.Contains(' ') && !Regex.IsMatch(t, @"^[\\|*?.:;<>=\w\- +]+$")) continue;   // prose
            if (t.StartsWith("filter:", StringComparison.OrdinalIgnoreCase)) t = t.Substring(7).Trim();
            if (Regex.IsMatch(t, @"[*?:<>\\|]") || Regex.IsMatch(t, @"^\d")) return t;
        }
        return null;
    }

    private const string FolderSystem =
        "You are a Windows storage expert helping a user understand what a folder on their disk is. You only see the folder's path, size and the\n" +
        "names of its biggest children. Answer in the user's language (Turkish if the question is Turkish, else English), in at most 5 short sentences:\n" +
        "1) what the folder most likely is and which program creates it, 2) whether it is a cache / user data / part of a program / system,\n" +
        "3) what would generally happen if it were removed. Never tell the user to delete anything; if unsure, say so plainly.";

    /// <summary>Description of an unknown folder from its path and biggest children (names and sizes only).</summary>
    public static Task<string> DescribeFolderAsync(LocalModel model, ContainerNode folder, object? sync, string language, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("Language: ").Append(language).Append('\n');
        sb.Append("Path: ").Append(folder.FullPath).Append('\n');
        List<FsNode> kids;
        long size;
        if (sync != null) lock (sync) { kids = folder.Children.Where(k => !k.IsSynthetic).OrderByDescending(k => k.DiskSize).Take(12).ToList(); size = folder.DiskSize; }
        else { kids = folder.Children.Where(k => !k.IsSynthetic).OrderByDescending(k => k.DiskSize).Take(12).ToList(); size = folder.DiskSize; }
        sb.Append("Size: ").Append(Format.Size(size)).Append(", ").Append(folder.FileCount).Append(" files, ").Append(folder.FolderCount).Append(" folders\n");
        sb.Append("Biggest children:\n");
        foreach (var k in kids) sb.Append("  ").Append(k is ContainerNode ? "[dir] " : "").Append(k.Name).Append("  ").Append(Format.Size(k.DiskSize)).Append('\n');
        sb.Append("What is this folder?");
        return model.CompleteAsync(FolderSystem, sb.ToString(), ct, temperature: 0.2, maxTokens: 300);
    }

    private const string DiffSystem =
        "You summarise what changed on a disk between two scans for a non-technical user. Answer in the requested language, 3 to 5 sentences,\n" +
        "concrete: name the folders and sizes that explain most of the change, mention notable deletions, and end with one plain observation.\n" +
        "Do not recommend deleting anything.";

    /// <summary>Narrative for a snapshot comparison from its headline numbers and biggest entries.</summary>
    public static Task<string> SummarizeDiffAsync(LocalModel model, DiffResult diff, string language, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("Language: ").Append(language).Append('\n');
        sb.Append("Area: ").Append(diff.CurrentPath).Append('\n');
        sb.Append("Snapshot from ").Append(diff.BaselineTime.ToLocalTime().ToString("d")).Append(": ").Append(Format.Size(diff.OldTotal)).Append(" -> now ").Append(Format.Size(diff.NewTotal))
          .Append(" (").Append(diff.Delta >= 0 ? "+" : "-").Append(Format.Size(Math.Abs(diff.Delta))).Append(")\n");
        sb.Append("Added: ").Append(diff.AddedCount).Append(" items, ").Append(Format.Size(diff.AddedBytes)).Append("; removed: ").Append(diff.RemovedCount).Append(" items, ").Append(Format.Size(diff.RemovedBytes)).Append('\n');
        sb.Append("Biggest changes (kind, delta, before, after, path):\n");
        foreach (var e in diff.Entries.Take(15))
            sb.Append("  ").Append(e.Kind).Append(' ').Append(e.Delta >= 0 ? "+" : "-").Append(Format.Size(e.AbsDelta)).Append(' ').Append(Format.Size(e.OldBytes)).Append("->").Append(Format.Size(e.NewBytes)).Append(' ').Append(e.IsFolder ? "[dir] " : "").Append(e.Path).Append('\n');
        return model.CompleteAsync(DiffSystem, sb.ToString(), ct, temperature: 0.3, maxTokens: 350);
    }
}
