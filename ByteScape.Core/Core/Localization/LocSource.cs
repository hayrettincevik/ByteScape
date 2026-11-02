using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ByteScape.Core.Localization;

/// <summary>A text of the interface as the source code writes it: the catalog key and where it was found.</summary>
public sealed record LocKey(string Key, string Where)
{
    public uint Id => Loc.Id(Key);
    public bool IsPlural => Key.Contains('\u0001');
}

/// <summary>
/// Finds every text the interface translates, in the source tree (a developer tool: the tests check the catalogs against it,
/// the Diag harness writes the list to translate). In C#: the English arguments of <c>T(…)</c>, <c>Fmt(…)</c>, <c>P(n, one, other)</c>,
/// the composite format of <c>F($"…")</c>, literals marked <c>/*loc*/</c> and the texts of the knowledge base table; in XAML:
/// the literal titles, texts, contents, headers and tool tips <see cref="Wpf.Localizer"/> translates, and <c>{l:T '…'}</c>.
/// </summary>
internal static class LocSource
{
    public static List<LocKey> Extract(string repoRoot)
    {
        var keys = new List<LocKey>();
        foreach (var dir in new[] { "ByteScape.Core", "ByteScape.Wpf" })
        {
            var root = Path.Combine(repoRoot, dir);
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                if (IsBuildOutput(root, file) || file.EndsWith("LocSource.cs", StringComparison.Ordinal)) continue;
                var rel = Path.GetRelativePath(repoRoot, file);
                FromCSharp(File.ReadAllText(file), rel, keys);
            }
            foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                if (IsBuildOutput(root, file)) continue;
                FromXaml(File.ReadAllText(file), Path.GetRelativePath(repoRoot, file), keys);
            }
        }
        // one entry per key, first place it was seen
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return keys.Where(k => k.Key.Length > 0 && HasLetter(k.Key) && seen.Add(k.Key)).ToList();
    }

    private static bool IsBuildOutput(string root, string file)
    {
        var rel = Path.GetRelativePath(root, file).Replace('/', '\\');
        return rel.StartsWith(@"bin\", StringComparison.OrdinalIgnoreCase) || rel.StartsWith(@"obj\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasLetter(string s) { foreach (char c in s) if (char.IsLetter(c)) return true; return false; }

    // ------------------------------------------------------------------ C#

    private static readonly Regex CallRx = new(@"(?<![\w.])(?:Loc\.)?(?<f>T|Fmt|P)\(|(?<![\w.])(?:Loc\.)?F\(\$""|/\*loc\*/\s*", RegexOptions.Compiled);

    internal static void FromCSharp(string src, string where, List<LocKey> keys)
    {
        var code = BlankComments(src);
        foreach (Match m in CallRx.Matches(code))
        {
            int line = 1 + code.Take(m.Index).Count(c => c == '\n');
            string at = where + ":" + line;
            if (m.Value.StartsWith("/*loc*/", StringComparison.Ordinal))
            {
                if (ReadLiteral(src, m.Index + m.Length, out var text, out _)) keys.Add(new LocKey(text, at));
                continue;
            }
            if (m.Value.EndsWith("$\"", StringComparison.Ordinal))
            {
                if (ReadInterpolated(src, m.Index + m.Length - 2, out var format, out _)) keys.Add(new LocKey(format, at));
                continue;
            }
            int open = m.Index + m.Length - 1;
            var args = Arguments(src, open);
            switch (m.Groups["f"].Value)
            {
                case "T":
                    if (args.Count >= 1) foreach (var lit in Literals(args[0])) keys.Add(new LocKey(lit, at));
                    break;
                case "Fmt":
                    if (args.Count >= 1) foreach (var lit in Literals(args[0])) keys.Add(new LocKey(lit, at));
                    break;
                case "P":
                    if (args.Count >= 3 && Literals(args[1]) is [var one] && Literals(args[2]) is [var other]) keys.Add(new LocKey(Loc.PluralKey(one, other), at));
                    break;
            }
        }
        if (where.EndsWith("KnownLocations.cs", StringComparison.Ordinal)) FromKnowledgeTable(src, where, keys);
    }

    /// <summary>R(id, title, category, safety, description, consequence, cleanup, patterns…): title, description, consequence, cleanup.</summary>
    private static void FromKnowledgeTable(string src, string where, List<LocKey> keys)
    {
        var code = BlankComments(src);
        foreach (Match m in Regex.Matches(code, @"(?<![\w.])R\("))
        {
            var args = Arguments(src, m.Index + m.Length - 1);
            if (args.Count < 7) continue;
            int line = 1 + code.Take(m.Index).Count(c => c == '\n');
            foreach (int i in new[] { 1, 4, 5, 6 })
                if (Literals(args[i]) is [var text]) keys.Add(new LocKey(text, where + ":" + line));
        }
    }

    /// <summary>Comments replaced by spaces (strings kept), so that calls in documentation are not taken for keys.</summary>
    private static string BlankComments(string s)
    {
        var sb = new StringBuilder(s);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"' || (c == '@' && i + 1 < s.Length && s[i + 1] == '"') || (c == '$' && i + 1 < s.Length && (s[i + 1] == '"' || s[i + 1] == '@')))
            {
                if (!SkipString(s, i, out int end)) break;
                i = end - 1;
            }
            else if (c == '\'' && i + 2 < s.Length) { int j = s[i + 1] == '\\' ? s.IndexOf('\'', i + 3) : i + 2; if (j > 0 && j < s.Length && s[j] == '\'') i = j; }
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                if (i + 2 < s.Length && s.Substring(i).StartsWith("/*loc*/", StringComparison.Ordinal)) continue;
                int e = s.IndexOf('\n', i); if (e < 0) e = s.Length;
                for (int k = i; k < e; k++) sb[k] = ' ';
                i = e;
            }
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int e = s.IndexOf("*/", i + 2, StringComparison.Ordinal); e = e < 0 ? s.Length : e + 2;
                if (s.Substring(i, Math.Min(7, s.Length - i)) == "/*loc*/") { i = e - 1; continue; }
                for (int k = i; k < e; k++) if (sb[k] != '\n') sb[k] = ' ';
                i = e - 1;
            }
        }
        return sb.ToString();
    }

    /// <summary>End (exclusive) of the string literal starting at <paramref name="i"/> ("…", @"…", $"…", $@"…", @$"…").</summary>
    private static bool SkipString(string s, int i, out int end)
    {
        bool verbatim = false, interp = false;
        int j = i;
        while (j < s.Length && (s[j] == '@' || s[j] == '$')) { if (s[j] == '@') verbatim = true; else interp = true; j++; }
        if (j >= s.Length || s[j] != '"') { end = i + 1; return true; }
        j++;
        int depth = 0;
        while (j < s.Length)
        {
            char c = s[j];
            if (interp && depth == 0 && c == '{') { if (j + 1 < s.Length && s[j + 1] == '{') { j += 2; continue; } depth = 1; j++; continue; }
            if (interp && depth > 0)
            {
                if (c == '"' || c == '@' && j + 1 < s.Length && s[j + 1] == '"' || c == '$' && j + 1 < s.Length && (s[j + 1] == '"' || s[j + 1] == '@'))
                { if (!SkipString(s, j, out int e)) { end = s.Length; return false; } j = e; continue; }
                if (c == '\'') { int q = s[j + 1] == '\\' ? s.IndexOf('\'', j + 3) : j + 2; j = q + 1; continue; }
                if (c == '{') depth++;
                else if (c == '}') depth--;
                j++; continue;
            }
            if (!verbatim && c == '\\') { j += 2; continue; }
            if (c == '"')
            {
                if (verbatim && j + 1 < s.Length && s[j + 1] == '"') { j += 2; continue; }
                end = j + 1; return true;
            }
            if (interp && c == '}' && j + 1 < s.Length && s[j + 1] == '}') { j += 2; continue; }
            j++;
        }
        end = s.Length; return false;
    }

    /// <summary>The arguments of the call whose '(' is at <paramref name="open"/>, split at top level commas.</summary>
    private static List<string> Arguments(string s, int open)
    {
        var args = new List<string>();
        int depth = 0, start = open + 1;
        for (int i = open; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"' || c == '@' && i + 1 < s.Length && s[i + 1] == '"' || c == '$' && i + 1 < s.Length && (s[i + 1] == '"' || s[i + 1] == '@'))
            { if (!SkipString(s, i, out int e)) break; i = e - 1; continue; }
            if (c == '\'') { int q = i + 1 < s.Length && s[i + 1] == '\\' ? s.IndexOf('\'', i + 3) : i + 2; if (q > 0) i = q; continue; }
            if (c is '(' or '[' or '{') { depth++; continue; }
            if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0) { args.Add(s.Substring(start, i - start)); break; }
                continue;
            }
            if (c == ',' && depth == 1) { args.Add(s.Substring(start, i - start)); start = i + 1; }
        }
        return args;
    }

    /// <summary>The plain string literals of an argument (both sides of a conditional, every arm of a switch), except switch patterns.</summary>
    private static List<string> Literals(string arg)
    {
        var list = new List<string>();
        for (int i = 0; i < arg.Length; i++)
        {
            char c = arg[i];
            if (c == '$' && i + 1 < arg.Length && (arg[i + 1] == '"' || arg[i + 1] == '@'))
            { if (!SkipString(arg, i, out int e)) break; i = e - 1; continue; }   // an interpolated string inside is not a key
            if (c == '"' || c == '@' && i + 1 < arg.Length && arg[i + 1] == '"')
            {
                if (!ReadLiteral(arg, i, out var text, out int end)) break;
                var rest = arg.Substring(end).TrimStart();
                var before = arg.Substring(0, i).TrimEnd();
                // switch patterns ("x" => …) and values compared with (kind == "x" ? …) are not texts
                bool pattern = rest.StartsWith("=>", StringComparison.Ordinal) || rest.StartsWith("or ", StringComparison.Ordinal);
                bool compared = before.EndsWith("==", StringComparison.Ordinal) || before.EndsWith("!=", StringComparison.Ordinal)
                                || rest.StartsWith("==", StringComparison.Ordinal) || rest.StartsWith("!=", StringComparison.Ordinal);
                if (!pattern && !compared) list.Add(text);
                i = end - 1;
                continue;
            }
            if (c == '\'') { int q = i + 1 < arg.Length && arg[i + 1] == '\\' ? arg.IndexOf('\'', i + 3) : i + 2; if (q > 0) i = q; }
        }
        return list;
    }

    /// <summary>A regular or verbatim literal at <paramref name="i"/> (spaces before it allowed), unescaped.</summary>
    internal static bool ReadLiteral(string s, int i, out string text, out int end)
    {
        text = ""; end = i;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        bool verbatim = i < s.Length && s[i] == '@';
        if (verbatim) i++;
        if (i >= s.Length || s[i] != '"') return false;
        var sb = new StringBuilder();
        for (int j = i + 1; j < s.Length; j++)
        {
            char c = s[j];
            if (verbatim)
            {
                if (c == '"') { if (j + 1 < s.Length && s[j + 1] == '"') { sb.Append('"'); j++; continue; } end = j + 1; text = sb.ToString().Replace("\r\n", "\n"); return true; }
                sb.Append(c);
                continue;
            }
            if (c == '\\' && j + 1 < s.Length)
            {
                char n = s[++j];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '0': sb.Append('\0'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(s.Substring(j + 1, 4), 16)); j += 4; break;
                    default: sb.Append(n); break;
                }
                continue;
            }
            if (c == '"') { end = j + 1; text = sb.ToString(); return true; }
            sb.Append(c);
        }
        return false;
    }

    /// <summary>The composite format the compiler makes of $"…" at <paramref name="i"/>: holes become {0}, {1}… with their alignment and format.</summary>
    internal static bool ReadInterpolated(string s, int i, out string format, out int end)
    {
        format = ""; end = i;
        if (s[i] != '$' || i + 1 >= s.Length || s[i + 1] != '"') return false;
        var sb = new StringBuilder();
        int hole = 0;
        for (int j = i + 2; j < s.Length; j++)
        {
            char c = s[j];
            if (c == '\\' && j + 1 < s.Length)
            {
                char n = s[++j];
                sb.Append(n switch { 'n' => "\n", 't' => "\t", 'r' => "\r", _ => n.ToString() });
                continue;
            }
            if (c == '{' && j + 1 < s.Length && s[j + 1] == '{') { sb.Append("{{"); j++; continue; }
            if (c == '}' && j + 1 < s.Length && s[j + 1] == '}') { sb.Append("}}"); j++; continue; }
            if (c == '"') { end = j + 1; format = sb.ToString(); return true; }
            if (c != '{') { sb.Append(c); continue; }
            // a hole: find its end, and a top level ',' (alignment) or ':' (format)
            int depth = 0, k = j + 1, colon = -1, comma = -1;
            for (; k < s.Length; k++)
            {
                char h = s[k];
                if (h == '"' || h == '@' && s[k + 1] == '"' || h == '$' && (s[k + 1] == '"' || s[k + 1] == '@'))
                { if (!SkipString(s, k, out int e)) return false; k = e - 1; continue; }
                if (h == '\'') { int q = s[k + 1] == '\\' ? s.IndexOf('\'', k + 3) : k + 2; k = q; continue; }
                if (h is '(' or '[' or '{') { depth++; continue; }
                if (h is ')' or ']') { depth--; continue; }
                if (h == '}') { if (depth == 0) break; depth--; continue; }
                if (depth == 0 && h == '?' ) { /* a conditional: its ':' is not a format */ int q = SkipConditional(s, k); if (q > k) { k = q - 1; continue; } }
                if (depth == 0 && h == ':' && colon < 0) colon = k;
                if (depth == 0 && h == ',' && comma < 0 && colon < 0) comma = k;
            }
            sb.Append('{').Append(hole++);
            if (comma >= 0) sb.Append(s, comma, (colon >= 0 ? colon : k) - comma);
            if (colon >= 0) sb.Append(s, colon, k - colon);
            sb.Append('}');
            j = k;
        }
        return false;
    }

    /// <summary>After the '?' of a conditional inside a hole: the index past its ':' branch start, so that ':' is not read as a format.</summary>
    private static int SkipConditional(string s, int q)
    {
        // "x ?? y" and "?." are not conditionals
        if (q + 1 < s.Length && (s[q + 1] == '?' || s[q + 1] == '.' || s[q + 1] == '[')) return q + 2;
        int depth = 0;
        for (int k = q + 1; k < s.Length; k++)
        {
            char h = s[k];
            if (h == '"' || h == '$' && s[k + 1] == '"') { if (!SkipString(s, k, out int e)) return q; k = e - 1; continue; }
            if (h is '(' or '[' or '{') depth++;
            else if (h is ')' or ']' or '}') { if (depth == 0) return q; depth--; }
            else if (h == ':' && depth == 0) return k + 1;
        }
        return q;
    }

    // ------------------------------------------------------------------ XAML

    private static readonly string[] TextAttributes = { "Title", "Text", "Content", "Header", "ToolTip", "AutomationProperties.Name", "AutomationProperties.HelpText" };
    private static readonly Regex MarkupT = new(@"\{l:T\s+'([^']*)'\s*\}", RegexOptions.Compiled);

    internal static void FromXaml(string xaml, string where, List<LocKey> keys)
    {
        var doc = XDocument.Parse(xaml, LoadOptions.SetLineInfo);
        foreach (var e in doc.Descendants())
        {
            string name = e.Name.LocalName;
            if (name is "Setter" or "Style" or "ResourceDictionary") continue;
            foreach (var a in e.Attributes())
            {
                string an = a.Name.LocalName;
                int line = ((System.Xml.IXmlLineInfo)a).LineNumber;
                foreach (Match m in MarkupT.Matches(a.Value)) keys.Add(new LocKey(m.Groups[1].Value, where + ":" + line));
                if (!a.Name.NamespaceName.Equals("", StringComparison.Ordinal) || !TextAttributes.Contains(an)) continue;
                if (a.Value.StartsWith('{')) continue;
                if (name is "TextBox" or "PasswordBox" && an is "Text") continue;
                if (an == "Title" && name != "Window" && !name.EndsWith("Window", StringComparison.Ordinal)) continue;
                keys.Add(new LocKey(a.Value.Replace("\r\n", "\n"), where + ":" + line));
            }
        }
    }
}
