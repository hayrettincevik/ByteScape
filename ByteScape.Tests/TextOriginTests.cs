using System.Text;
using System.Text.RegularExpressions;
using ByteScape.Core.Localization;
using Xunit;
using Xunit.Abstractions;

namespace ByteScape.Tests;

/// <summary>
/// ByteScape speaks in its own words: no interface text, help text, message or other string of the program repeats the
/// wording of another program. That program's texts are not in the repository: Data/OtherTextFingerprints.txt holds only
/// hashes of them — each whole text of three or more words, and every run of seven consecutive words of its interface and
/// its manuals. Words are compared in lower case, letters and digits only, so punctuation and line breaks do not matter.
/// </summary>
public class TextOriginTests
{
    private const int Run = 7;
    private readonly ITestOutputHelper _out;
    public TextOriginTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void NoText_RepeatsTheWordingOfAnotherProgram()
    {
        var root = LocalizationTests.RepoRoot();
        var (whole, runs) = Load(Path.Combine(root, "ByteScape.Tests", "Data", "OtherTextFingerprints.txt"));
        Assert.True(whole.Count > 500 && runs.Count > 5000, "fingerprints missing");

        var texts = new List<(string Text, string Where)>();
        foreach (var k in LocSource.Extract(root))
            foreach (var t in k.IsPlural ? k.Key.Split('\u0001') : new[] { k.Key }) texts.Add((t, k.Where));
        // the strings that are not translated too: log lines, report headers, export templates
        foreach (var project in new[] { "ByteScape.Core", "ByteScape.Wpf" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                var code = File.ReadAllText(file);
                foreach (Match m in Regex.Matches(code, @"@""((?:[^""]|"""")*)""|""((?:[^""\\\r\n]|\\.)*)"""))
                {
                    var s = m.Groups[1].Success ? m.Groups[1].Value.Replace("\"\"", "\"") : m.Groups[2].Value;
                    if (s.Length >= 12) texts.Add((s, Path.GetRelativePath(root, file)));
                }
            }

        var problems = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (text, where) in texts)
        {
            var w = Words(text);
            if (w.Length >= 3 && whole.Contains(Hash(string.Join(' ', w))))
                problems.Add($"{where}: the whole text \"{Short(text)}\"");
            for (int i = 0; i + Run <= w.Length; i++)
                if (runs.Contains(Hash(string.Join(' ', w, i, Run))))
                {
                    problems.Add($"{where}: \"{string.Join(' ', w, i, Run)}\" in \"{Short(text)}\"");
                    break;
                }
        }
        foreach (var p in problems) _out.WriteLine(p);
        Assert.True(texts.Count > 1500, "only " + texts.Count + " texts looked at");
        Assert.True(problems.Count == 0, problems.Count + " texts repeat another program's wording:\n" + string.Join("\n", problems));
    }

    /// <summary>The same hash and word splitting the fingerprints were made with.</summary>
    [Fact]
    public void Fingerprints_UseTheWordsAndHashOfThisTest()
    {
        Assert.Equal(new[] { "go", "back", "2", "levels" }, Words("Go back — 2 levels!"));
        Assert.Equal(0xdc4c8601ec8cUL, Hash("a"));   // FNV-1a 64 of "a" = 0xaf63dc4c8601ec8c, lower 48 bits
    }

    internal static string[] Words(string s) => Regex.Matches(s.ToLowerInvariant(), "[a-z0-9]+").Select(m => m.Value).ToArray();

    internal static ulong Hash(string s)
    {
        ulong h = 0xcbf29ce484222325;
        foreach (var b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 0x100000001b3; }
        return h & 0xFFFFFFFFFFFF;
    }

    private static (HashSet<ulong> Whole, HashSet<ulong> Runs) Load(string path)
    {
        var whole = new HashSet<ulong>();
        var runs = new HashSet<ulong>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length < 3 || line[0] == '#') continue;
            var h = Convert.ToUInt64(line[2..], 16);
            (line[0] == 'E' ? whole : runs).Add(h);
        }
        return (whole, runs);
    }

    private static string Short(string s) => (s.Length > 70 ? s[..70] + "…" : s).Replace("\n", " ");
}
