using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ByteScape.Core.Util;

/// <summary>Size / age formatting used by the treemap labels, tooltips and export tags.</summary>
public static class Format
{
    private static readonly string[] Units = { "bytes", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>When true sizes use 1000-based units (configuration: "Decimal units"); default is 1024-based.</summary>
    public static bool DecimalUnits { get; set; }

    /// <summary>"88KB", "195.2MB", "1.3GB", "512 bytes": KB as an integer, MB and above with one decimal.</summary>
    public static string Size(long bytes)
    {
        double k = DecimalUnits ? 1000 : 1024;
        if (bytes < k) return bytes + "b";   // "0b", "512b"
        double v = bytes;
        int u = 0;
        while (v >= k && u < Units.Length - 1) { v /= k; u++; }
        string s = u == 1 ? Math.Round(v).ToString(CultureInfo.InvariantCulture) : v.ToString("0.0", CultureInfo.InvariantCulture);
        return s + Units[u];
    }

    /// <summary>File attributes in the interface language: "Hidden, System, Archive".</summary>
    public static string Attributes(FileAttributes a)
    {
        if (a == 0) return "";
        var names = new List<string>();
        foreach (FileAttributes f in Enum.GetValues(typeof(FileAttributes)))
            if (f != 0 && (a & f) == f && !names.Contains(AttributeName(f))) names.Add(AttributeName(f));
        return string.Join(", ", names);
    }

    private static string AttributeName(FileAttributes f) => f switch
    {
        FileAttributes.ReadOnly => T("Read-only"),
        FileAttributes.Hidden => T("Hidden"),
        FileAttributes.System => T("System"),
        FileAttributes.Directory => T("Folder"),
        FileAttributes.Archive => T("Archive"),
        FileAttributes.Device => T("Device"),
        FileAttributes.Normal => T("Normal"),
        FileAttributes.Temporary => T("Temporary"),
        FileAttributes.SparseFile => T("Sparse file"),
        FileAttributes.ReparsePoint => T("Reparse point"),
        FileAttributes.Compressed => T("Compressed"),
        FileAttributes.Offline => T("Offline"),
        FileAttributes.NotContentIndexed => T("Not indexed"),
        FileAttributes.Encrypted => T("Encrypted"),
        FileAttributes.IntegrityStream => T("Integrity stream"),
        FileAttributes.NoScrubData => T("No scrub data"),
        _ => f.ToString(),
    };

    /// <summary>"1 year 3 months", "2 days", "just now".</summary>
    public static string Age(DateTime utc, DateTime? nowUtc = null)
    {
        if (utc == default) return string.Empty;
        var now = nowUtc ?? DateTime.UtcNow;
        var span = now - utc;
        if (span.TotalSeconds < 0) span = TimeSpan.Zero;
        double days = span.TotalDays;
        if (days >= 365)
        {
            int years = (int)(days / 365.25);
            int months = (int)((days - years * 365.25) / 30.4375);
            return months > 0 ? Years(years) + " " + Months(months) : Years(years);
        }
        if (days >= 30.4375)
        {
            int months = (int)(days / 30.4375);
            int d = (int)(days - months * 30.4375);
            return d > 0 ? Months(months) + " " + Days(d) : Months(months);
        }
        if (days >= 1)
        {
            int d = (int)days;
            int h = span.Hours;
            return h > 0 ? Days(d) + " " + Hours(h) : Days(d);
        }
        if (span.TotalHours >= 1) return Hours(span.Hours) + " " + Minutes(span.Minutes);
        if (span.TotalMinutes >= 1) return Minutes(span.Minutes);
        return P(span.Seconds, "{0} second", "{0} seconds");
    }

    // the units of an age, in the interface language
    private static string Years(int n) => P(n, "{0} year", "{0} years");
    private static string Months(int n) => P(n, "{0} month", "{0} months");
    private static string Days(int n) => P(n, "{0} day", "{0} days");
    private static string Hours(int n) => P(n, "{0} hour", "{0} hours");
    private static string Minutes(int n) => P(n, "{0} minute", "{0} minutes");

    public static string Date(DateTime utc) => utc == default ? string.Empty : utc.ToLocalTime().ToShortDateString();
    public static string Time(DateTime utc) => utc == default ? string.Empty : utc.ToLocalTime().ToLongTimeString();
}

/// <summary>
/// DOS style wildcard matching (? and *), case insensitive. The Turkish dotted / dotless i pairs
/// (İ/i and I/ı) are folded together so "*ist*" finds "İstanbul" the way Explorer does.
/// </summary>
public sealed class Wildcard
{
    private readonly Regex _regex;
    public string Pattern { get; }
    /// <summary>Longest literal run of the pattern (no wildcards); a text that does not contain it cannot match — a cheap pre-check before the regex.</summary>
    private readonly string _literal;

    /// <summary>Maps İ→I and ı→i; together with the case-insensitive regex all four forms match each other.</summary>
    private static string FoldTurkish(string s)
    {
        if (s.IndexOfAny(TurkishI) < 0) return s;
        return s.Replace('İ', 'I').Replace('ı', 'i');
    }

    private static readonly char[] TurkishI = { 'İ', 'ı' };

    public Wildcard(string pattern) : this(pattern, false) { }

    /// <summary>
    /// <paramref name="pathMode"/>: the pattern is a full path where * and ? never cross a backslash and a leading
    /// "**\" matches any number of leading folders (knowledge base rules); otherwise plain DOS semantics.
    /// </summary>
    public Wildcard(string pattern, bool pathMode)
    {
        Pattern = pattern;
        var sb = new StringBuilder("^");
        var p = FoldTurkish(pattern);
        if (pathMode && p.StartsWith(@"**\", StringComparison.Ordinal)) { sb.Append(@"(?:.*\\)?"); p = p.Substring(3); }
        foreach (char c in p)
        {
            switch (c)
            {
                case '*': sb.Append(pathMode ? @"[^\\]*" : ".*"); break;
                case '?': sb.Append(pathMode ? @"[^\\]" : "."); break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        sb.Append('$');
        // three stars or more can make a backtracking match take exponential time on a long name: those get the linear engine
        _regex = p.Count(ch => ch == '*') >= 3
            ? new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)
            : new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        var runs = p.Replace("**", "*").Split(new[] { '*', '?' }, StringSplitOptions.RemoveEmptyEntries);
        _literal = runs.Length == 0 ? "" : runs.OrderByDescending(r => r.Length).First();
        if (_literal.Length < 4) _literal = "";
    }

    public bool IsMatch(string text)
    {
        var t = FoldTurkish(text);
        if (_literal.Length > 0 && t.IndexOf(_literal, StringComparison.OrdinalIgnoreCase) < 0) return false;
        return _regex.IsMatch(t);
    }

    public bool IsPlainMask => Pattern.IndexOf('*') < 0 && Pattern.IndexOf('?') < 0;
}

/// <summary>
/// "Fuzzy" keyword recognition used by the filter parser: it accepts abbreviations
/// such as disk/dsk/dsksz for disksize. A keyword matches when the typed word is a prefix
/// of it, or when the typed letters appear in order inside it (subsequence).
/// </summary>
public static class Fuzzy
{
    public static bool Matches(string typed, params string[] keywords)
    {
        if (string.IsNullOrEmpty(typed)) return false;
        foreach (var k in keywords)
        {
            if (k.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return true;
            if (typed[0] == char.ToLowerInvariant(k[0]) && IsSubsequence(typed, k)) return true;
        }
        return false;
    }

    private static bool IsSubsequence(string needle, string hay)
    {
        int j = 0;
        for (int i = 0; i < hay.Length && j < needle.Length; i++)
            if (char.ToLowerInvariant(hay[i]) == char.ToLowerInvariant(needle[j])) j++;
        return j == needle.Length;
    }
}
