using System.Globalization;
using System.Reflection;
using System.Text;

namespace ByteScape.Core.Localization;

/// <summary>A language the user interface can be shown in.</summary>
public sealed record UiLanguage(string Code, string NativeName, string EnglishName);

/// <summary>
/// Translation of the user interface. The English text in the code is the key: <c>Loc.T("Delete")</c>,
/// <c>Loc.F($"{n:N0} files in {name}")</c> (the key is the composite format "{0:N0} files in {1}") and
/// <c>Loc.P(n, "{0:N0} file", "{0:N0} files")</c> for counts. A catalog per language (an embedded
/// <c>Localization\&lt;code&gt;.txt</c>) maps the key's id — a hash of the English text — to the translation; whatever a
/// catalog lacks stays English. Logs, crash reports and file formats are not translated: they are read by developers and
/// programs. XAML is translated when a window is built (the WPF side walks the new window's elements).
/// </summary>
public static class Loc
{
    public const string English = "en";
    /// <summary>Tests: every text is marked (⟦…⟧) so text that escaped translation shows up.</summary>
    public const string Pseudo = "qps";

    public static IReadOnlyList<UiLanguage> Languages { get; } = new[]
    {
        new UiLanguage("en", "English", "English"),
        new UiLanguage("tr", "Türkçe", "Turkish"),
        new UiLanguage("ru", "Русский", "Russian"),
        new UiLanguage("zh-Hans", "简体中文", "Chinese (Simplified)"),
        new UiLanguage("fr", "Français", "French"),
        new UiLanguage("es", "Español", "Spanish"),
        new UiLanguage("pt-BR", "Português (Brasil)", "Portuguese (Brazil)"),
    };

    /// <summary>The language in use (a code of <see cref="Languages"/>, or <see cref="Pseudo"/>).</summary>
    public static string Language { get; private set; } = English;

    private static Dictionary<uint, string> _catalog = new();

    /// <summary>The language a setting stands for: a supported code, or "" (automatic) = Windows' display language when
    /// it is one of ours, otherwise English. Chinese variants all get Simplified Chinese; any Portuguese gets Brazilian.</summary>
    public static string Resolve(string? setting, CultureInfo? windowsUi = null)
    {
        if (!string.IsNullOrWhiteSpace(setting))
        {
            if (setting == Pseudo) return Pseudo;
            var known = Languages.FirstOrDefault(l => l.Code.Equals(setting.Trim(), StringComparison.OrdinalIgnoreCase));
            if (known != null) return known.Code;
        }
        var ui = windowsUi ?? CultureInfo.InstalledUICulture;
        for (var c = ui; c != null && c.Name.Length > 0; c = c.Parent)
        {
            switch (c.TwoLetterISOLanguageName)
            {
                case "tr": return "tr";
                case "ru": return "ru";
                case "zh": return "zh-Hans";
                case "fr": return "fr";
                case "es": return "es";
                case "pt": return "pt-BR";
                case "en": return English;
            }
            if (ReferenceEquals(c, c.Parent)) break;
        }
        return English;
    }

    /// <summary>Switches the language (before windows are built: texts already shown stay as they are).</summary>
    public static void SetLanguage(string code)
    {
        var lang = Resolve(code);
        _catalog = lang is English or Pseudo ? new Dictionary<uint, string>() : Load(lang);
        Language = lang;
    }

    /// <summary>The catalog of a language: id → translation. Empty when the language has none.</summary>
    public static Dictionary<uint, string> Load(string code)
    {
        using var s = typeof(Loc).Assembly.GetManifestResourceStream(ResourceName(code));
        return s == null ? new Dictionary<uint, string>() : Parse(new StreamReader(s, Encoding.UTF8).ReadToEnd());
    }

    internal static string ResourceName(string code) => "ByteScape.Core.Localization." + code + ".txt";

    /// <summary>Catalog text: one entry per line, "id&lt;TAB&gt;translation" (id = 8 hex digits); \n \t \\ escaped; # comments.</summary>
    public static Dictionary<uint, string> Parse(string text)
    {
        var d = new Dictionary<uint, string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 10 || line[0] == '#' || line[8] != '\t') continue;
            if (!uint.TryParse(line.AsSpan(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)) continue;
            d[id] = Unescape(line.Substring(9));
        }
        return d;
    }

    public static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\r\n", "\n").Replace("\n", "\\n").Replace("\t", "\\t");

    public static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char n = s[++i];
                sb.Append(n switch { 'n' => '\n', 't' => '\t', _ => n });
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>The id of an English text: FNV-1a over its UTF-16 code units (line breaks as \n).</summary>
    public static uint Id(string english)
    {
        uint h = 2166136261;
        foreach (char c in english)
        {
            if (c == '\r') continue;
            h = (h ^ c) * 16777619;
        }
        return h;
    }

    public static string IdText(uint id) => id.ToString("x8", CultureInfo.InvariantCulture);

    /// <summary>The key of a count text: both English forms.</summary>
    public static string PluralKey(string one, string other) => one + "\u0001" + other;

    private static bool HasLetter(string s)
    {
        foreach (char c in s) if (char.IsLetter(c)) return true;
        return false;
    }

    // ------------------------------------------------------------------ lookups

    /// <summary>The translation of a fixed English text.</summary>
    public static string T(string english)
    {
        if (Language == English || english.Length == 0) return english;
        if (Language == Pseudo) return HasLetter(english) ? "⟦" + english + "⟧" : english;
        return _catalog.TryGetValue(Id(english), out var t) ? t : english;
    }

    /// <summary>The translation of a text with values: the key is the interpolated string's composite format.</summary>
    public static string F(FormattableString s) => Format(T(s.Format), s.GetArguments());

    /// <summary>A fixed English composite format with its values (when the format is not written inline).</summary>
    public static string Fmt(string englishFormat, params object?[] args) => Format(T(englishFormat), args);

    /// <summary>A count: {0} is <paramref name="n"/>, {1}… the further values. A translation holds its language's plural forms separated by '|'.</summary>
    public static string P(long n, string one, string other, params object?[] more)
    {
        var args = new object?[more.Length + 1];
        args[0] = n;
        Array.Copy(more, 0, args, 1, more.Length);
        string form;
        if (Language == English) form = n == 1 ? one : other;
        else if (Language == Pseudo) form = "⟦" + (n == 1 ? one : other) + "⟧";
        else if (_catalog.TryGetValue(Id(PluralKey(one, other)), out var t))
        {
            var forms = t.Split('|');
            form = forms[Math.Min(PluralIndex(Language, n), forms.Length - 1)];
        }
        else form = n == 1 ? one : other;
        return Format(form, args);
    }

    /// <summary>How many plural forms a language's count texts have, and which one a number takes (CLDR cardinal rules for integers).</summary>
    public static int PluralForms(string lang) => lang switch { "ru" => 3, "zh-Hans" => 1, "tr" => 1, _ => 2 };

    public static int PluralIndex(string lang, long n)
    {
        n = Math.Abs(n);
        switch (lang)
        {
            case "zh-Hans":
            case "tr": return 0;                                        // "5 dosya", "5 个文件": no plural after a number
            case "fr":
            case "pt-BR": return n <= 1 ? 0 : 1;                        // 0 and 1 are singular
            case "ru":
                long d = n % 10, h = n % 100;
                if (d == 1 && h != 11) return 0;                        // 1, 21, 101 файл
                if (d is >= 2 and <= 4 && h is < 12 or > 14) return 1;  // 2–4, 22–24 файла
                return 2;                                               // 0, 5–20, 25 файлов
            default: return n == 1 ? 0 : 1;
        }
    }

    private static string Format(string format, object?[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, format, args); }
        catch (FormatException) { return format; } // a broken translation must not take the window down
    }
}
