// Localization tools: the list of texts to translate (from the source tree), what each catalog lacks, and a review sheet.
using System.IO;
using System.Text;
using ByteScape.Core.Localization;

namespace ByteScape.Diag;

internal static partial class Program
{
    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ByteScape.sln"))) return d.FullName;
        throw new DirectoryNotFoundException("ByteScape.sln not found above " + AppContext.BaseDirectory);
    }

    private static string CatalogPath(string root, string code) => Path.Combine(root, "ByteScape.Core", "Core", "Localization", code + ".txt");

    /// <summary>locstatus: keys found in the source, per language how many are translated, missing or stale.</summary>
    private static void LocStatus()
    {
        var root = RepoRoot();
        var keys = LocSource.Extract(root);
        Out($"== LOC STATUS: {keys.Count} texts ({keys.Count(k => k.IsPlural)} counts with plural forms), {keys.Sum(k => k.Key.Length):N0} characters");
        var dup = keys.GroupBy(k => k.Id).Where(g => g.Count() > 1).ToList();
        foreach (var g in dup) Out("   ID COLLISION " + Loc.IdText(g.Key) + ": " + string.Join(" | ", g.Select(k => k.Key)));
        var ids = keys.Select(k => k.Id).ToHashSet();
        foreach (var lang in Loc.Languages.Where(l => l.Code != Loc.English))
        {
            var path = CatalogPath(root, lang.Code);
            var cat = File.Exists(path) ? Loc.Parse(File.ReadAllText(path)) : new Dictionary<uint, string>();
            int missing = keys.Count(k => !cat.ContainsKey(k.Id));
            int stale = cat.Keys.Count(id => !ids.Contains(id));
            Out($"   {lang.Code,-8} translated {keys.Count - missing,5}  missing {missing,5}  stale {stale,4}");
        }
    }

    /// <summary>
    /// locbatch &lt;lang&gt; &lt;from&gt; &lt;count&gt; &lt;out&gt;: the next texts a catalog lacks, as "id TAB english" lines (\n escaped), in source order —
    /// what a translator fills in. Plural texts show their English forms as "one|other".
    /// </summary>
    private static void LocBatch(string lang, int from, int count, string outFile)
    {
        var root = RepoRoot();
        var path = CatalogPath(root, lang);
        var cat = File.Exists(path) ? Loc.Parse(File.ReadAllText(path)) : new Dictionary<uint, string>();
        var todo = LocSource.Extract(root).Where(k => !cat.ContainsKey(k.Id)).Skip(from).Take(count).ToList();
        var sb = new StringBuilder();
        foreach (var k in todo) sb.Append(Loc.IdText(k.Id)).Append('\t').AppendLine(Loc.Escape(k.Key.Replace('\u0001', '|')));
        File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
        Out($"== LOC BATCH {lang}: {todo.Count} texts written to {outFile}");
    }

    /// <summary>locmerge &lt;lang&gt; &lt;file&gt;: adds "id TAB translation" lines to the catalog (replacing earlier ones), sorted by id.</summary>
    private static void LocMerge(string lang, string file)
    {
        var root = RepoRoot();
        var path = CatalogPath(root, lang);
        var cat = File.Exists(path) ? Loc.Parse(File.ReadAllText(path)) : new Dictionary<uint, string>();
        var add = Loc.Parse(File.ReadAllText(file));
        var ids = LocSource.Extract(root).ToDictionary(k => k.Id, k => k.Key);
        int unknown = 0;
        foreach (var (id, text) in add)
        {
            if (!ids.ContainsKey(id)) { unknown++; Out("   unknown id " + Loc.IdText(id) + ": " + text); continue; }
            cat[id] = text;
        }
        WriteCatalog(path, lang, cat, ids);
        Out($"== LOC MERGE {lang}: {add.Count - unknown} added or replaced, {unknown} unknown ids, catalog now {cat.Count}");
    }

    private static void WriteCatalog(string path, string lang, Dictionary<uint, string> cat, Dictionary<uint, string> english)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(Branding.Name).Append(" translation catalog: ").Append(Loc.Languages.First(l => l.Code == lang).EnglishName)
          .AppendLine(". One text per line: id (hash of the English text) TAB translation; \\n = line break; plural forms separated by |.");
        // texts the source no longer has are dropped
        foreach (var (id, text) in cat.Where(p => english.ContainsKey(p.Key)).OrderBy(p => p.Key)) sb.Append(Loc.IdText(id)).Append('\t').AppendLine(Loc.Escape(text));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>locreview &lt;lang&gt; &lt;out.tsv&gt;: English and translation side by side, for a human reviewer (opens in a spreadsheet).</summary>
    private static void LocReview(string lang, string outFile)
    {
        var root = RepoRoot();
        var cat = Loc.Parse(File.ReadAllText(CatalogPath(root, lang)));
        var sb = new StringBuilder("Id\tWhere\tEnglish\tTranslation\r\n");
        foreach (var k in LocSource.Extract(root))
            sb.Append(Loc.IdText(k.Id)).Append('\t').Append(k.Where).Append('\t').Append(Loc.Escape(k.Key.Replace('\u0001', '|'))).Append('\t')
              .Append(cat.TryGetValue(k.Id, out var t) ? Loc.Escape(t) : "").Append("\r\n");
        File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(true));
        Out($"== LOC REVIEW {lang}: {outFile}");
    }
}
