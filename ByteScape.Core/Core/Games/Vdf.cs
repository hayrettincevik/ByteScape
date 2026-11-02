using System.Text;

namespace ByteScape.Core.Games;

/// <summary>
/// Valve's text KeyValues format (.vdf / .acf): quoted keys and values, nested blocks in braces, // comments,
/// backslash escapes and optional platform conditions ("[$WIN32]") that are ignored. Keys are case insensitive.
/// </summary>
public sealed class VdfNode
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VdfNode> Blocks { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Block names in file order (library folders "0", "1"...).</summary>
    public List<string> BlockOrder { get; } = new();

    public string? this[string key] => Values.TryGetValue(key, out var v) ? v : null;
    public VdfNode? Block(string key) => Blocks.TryGetValue(key, out var b) ? b : null;

    /// <summary>Follows a path of block names ("UserLocalConfigStore", "Software", "Valve"...), null when one is missing.</summary>
    public VdfNode? Path(params string[] keys)
    {
        VdfNode? n = this;
        foreach (var k in keys) { n = n?.Block(k); if (n == null) return null; }
        return n;
    }

    public long Long(string key) => long.TryParse(this[key], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    public static VdfNode Parse(string text)
    {
        int p = 0;
        var root = new VdfNode();
        ParseBlock(text, ref p, root, topLevel: true);
        return root;
    }

    public static VdfNode? TryLoad(string file)
    {
        try { return File.Exists(file) ? Parse(File.ReadAllText(file)) : null; }
        catch (Exception ex) { Util.Log.Debug("Unreadable " + file + ": " + ex.Message); return null; }
    }

    private static void ParseBlock(string s, ref int p, VdfNode node, bool topLevel)
    {
        while (true)
        {
            var key = Token(s, ref p, out bool keyQuoted);
            if (key == null) return;                                              // end of text
            if (!keyQuoted && key == "}") { if (!topLevel) return; continue; }    // stray brace at the top: ignore
            if (!keyQuoted && key == "{") continue;
            var next = Token(s, ref p, out bool quoted);
            if (next == null) return;
            if (!quoted && next.Length > 1 && next[0] == '[' && next[^1] == ']') next = Token(s, ref p, out quoted);   // a condition right after the key
            if (next == null) return;
            if (!quoted && next == "{")
            {
                // a block seen twice ("Apps" and "apps" in localconfig.vdf) is merged, so no entry is lost
                if (!node.Blocks.TryGetValue(key, out var child))
                {
                    node.Blocks[key] = child = new VdfNode();
                    node.BlockOrder.Add(key);
                }
                ParseBlock(s, ref p, child, topLevel: false);
            }
            else node.Values[key] = next;
            SkipCondition(s, ref p);
        }
    }

    private static void SkipCondition(string s, ref int p)
    {
        int q = p;
        SkipSpace(s, ref q);
        if (q < s.Length && s[q] == '[')
        {
            int e = s.IndexOf(']', q);
            if (e > q) p = e + 1;
        }
    }

    private static void SkipSpace(string s, ref int p)
    {
        while (p < s.Length)
        {
            if (char.IsWhiteSpace(s[p])) { p++; continue; }
            if (s[p] == '/' && p + 1 < s.Length && s[p + 1] == '/') { while (p < s.Length && s[p] != '\n') p++; continue; }
            break;
        }
    }

    /// <summary>Next token: a quoted string (escapes resolved), a brace, or a bare word; null at the end.</summary>
    private static string? Token(string s, ref int p, out bool quoted)
    {
        quoted = false;
        SkipSpace(s, ref p);
        if (p >= s.Length) return null;
        char c = s[p];
        if (c is '{' or '}') { p++; return c.ToString(); }
        var sb = new StringBuilder();
        if (c == '"')
        {
            quoted = true;
            p++;
            while (p < s.Length && s[p] != '"')
            {
                if (s[p] == '\\' && p + 1 < s.Length)
                {
                    char e = s[p + 1];
                    sb.Append(e switch { 'n' => '\n', 't' => '\t', _ => e });
                    p += 2;
                    continue;
                }
                sb.Append(s[p++]);
            }
            p++;   // closing quote
            return sb.ToString();
        }
        while (p < s.Length && !char.IsWhiteSpace(s[p]) && s[p] is not ('{' or '}' or '"')) sb.Append(s[p++]);
        return sb.ToString();
    }
}
