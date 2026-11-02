using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ByteScape.Core.Filtering;

/// <summary>
/// Rule based translation of a plain language request (Turkish or English) into the filter language:
/// "6 aydır açılmamış 100 MB üstü videolar" → "*.mp4;*.mkv;...;>100mb;a>6months".
/// No model involved: nouns map to extension groups, numbers + units to size / age conditions, a few
/// adjectives to attributes and tags. Everything understood is reported back so the user can see (and edit)
/// what the request became; anything not understood is ignored.
/// </summary>
public static class NaturalFilter
{
    public sealed record Result(string Filter, string Explanation, IReadOnlyList<string> Ignored)
    {
        public bool IsEmpty => Filter.Length == 0;
    }

    // ------------------------------------------------------------------ vocabulary

    private sealed record Group(string Key, string Label, string[] Extensions, string[] Words);

    private static readonly Group[] Groups =
    {
        new("video", /*loc*/ "videos", new[] { "mp4", "mkv", "avi", "mov", "wmv", "webm", "flv", "m4v", "mpg", "mpeg", "ts", "vob", "3gp" },
            new[] { "video", "videos", "videolar", "videoları", "videoyu", "film", "filmler", "filmleri", "movie", "movies", "clip", "clips" }),
        new("image", /*loc*/ "pictures", new[] { "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "heic", "heif", "webp", "raw", "cr2", "nef", "arw", "dng", "psd", "svg" },
            new[] { "resim", "resimler", "resimleri", "fotoğraf", "fotoğraflar", "fotoğrafları", "foto", "fotolar", "görsel", "görseller", "photo", "photos", "picture", "pictures", "image", "images", "screenshot", "screenshots", "ekran görüntüsü", "ekran görüntüleri" }),
        new("audio", /*loc*/ "music", new[] { "mp3", "flac", "wav", "aac", "m4a", "ogg", "wma", "opus", "aiff", "alac" },
            new[] { "müzik", "müzikler", "müzikleri", "şarkı", "şarkılar", "şarkıları", "ses", "ses dosyaları", "music", "songs", "song", "audio", "podcast", "podcasts" }),
        new("document", /*loc*/ "documents", new[] { "doc", "docx", "pdf", "xls", "xlsx", "ppt", "pptx", "txt", "odt", "ods", "odp", "rtf", "md", "csv" },
            new[] { "belge", "belgeler", "belgeleri", "doküman", "dokümanlar", "döküman", "dökümanlar", "document", "documents", "office dosyaları", "office files" }),
        new("pdf", /*loc*/ "PDFs", new[] { "pdf" }, new[] { "pdf", "pdfler", "pdfleri", "pdfs" }),
        new("archive", /*loc*/ "archives", new[] { "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "cab", "tgz" },
            new[] { "arşiv", "arşivler", "arşivleri", "sıkıştırılmış", "archive", "archives", "zips", "compressed files" }),
        new("diskimage", /*loc*/ "disk images", new[] { "iso", "img", "vhd", "vhdx", "vmdk", "qcow2", "wim", "esd" },
            new[] { "iso", "isolar", "iso dosyaları", "disk imajı", "disk imajları", "imaj", "imajlar", "disk image", "disk images", "iso files", "sanal disk", "sanal diskler", "virtual disk", "virtual disks" }),
        new("installer", /*loc*/ "installers", new[] { "exe", "msi", "msix", "appx", "msu" },
            new[] { "kurulum", "kurulum dosyaları", "kurulumlar", "yükleyici", "yükleyiciler", "installer", "installers", "setup files", "setups", "exe dosyaları", "programlar", "program dosyaları", "executables" }),
        new("log", /*loc*/ "logs", new[] { "log", "etl", "evtx" }, new[] { "log", "loglar", "logları", "günlük dosyaları", "logs", "log files" }),
        new("temp", /*loc*/ "temporary files", new[] { "tmp", "temp", "bak", "old", "dmp", "part", "crdownload" },
            new[] { "geçici dosyalar", "geçici dosyaları", "geçici", "temporary files", "temp files", "temporary" }),
        new("backup", /*loc*/ "backups", new[] { "bak", "bkp", "backup", "old", "orig" }, new[] { "yedek", "yedekler", "yedekleri", "yedek dosyaları", "backup", "backups", "backup files" }),
        new("ebook", /*loc*/ "e-books", new[] { "epub", "mobi", "azw", "azw3", "djvu" }, new[] { "e-kitap", "ekitap", "e-kitaplar", "kitaplar", "ebook", "ebooks", "e-book", "e-books" }),
        new("code", /*loc*/ "source code", new[] { "cs", "js", "ts", "py", "java", "cpp", "c", "h", "go", "rs", "kt", "swift", "php", "rb", "html", "css", "json", "xml", "yaml", "yml" },
            new[] { "kaynak kod", "kod dosyaları", "kodlar", "source code", "source files", "code files" }),
        new("database", /*loc*/ "databases", new[] { "db", "sqlite", "sqlite3", "mdf", "ldf", "accdb", "mdb" }, new[] { "veritabanı", "veritabanları", "database", "databases", "db files" }),
        new("font", /*loc*/ "fonts", new[] { "ttf", "otf", "woff", "woff2" }, new[] { "yazı tipi", "yazı tipleri", "font", "fonts" }),
        new("torrent", /*loc*/ "torrents", new[] { "torrent" }, new[] { "torrent", "torrentler", "torrents" }),
    };

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bir"] = 1, ["iki"] = 2, ["üç"] = 3, ["dört"] = 4, ["beş"] = 5, ["altı"] = 6, ["yedi"] = 7, ["sekiz"] = 8, ["dokuz"] = 9, ["on"] = 10,
        ["on iki"] = 12, ["yirmi"] = 20, ["otuz"] = 30, ["kırk"] = 40, ["elli"] = 50, ["yüz"] = 100, ["yarım"] = 0,
        ["one"] = 1, ["a"] = 1, ["an"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
        ["twelve"] = 12, ["twenty"] = 20, ["thirty"] = 30, ["fifty"] = 50, ["hundred"] = 100, ["half"] = 0,
    };

    private const string Num = @"(?<n>\d+(?:[.,]\d+)?|bir|iki|üç|dört|beş|altı|yedi|sekiz|dokuz|on iki|on|yirmi|otuz|kırk|elli|yüz|yarım|one|an|a|two|three|four|five|six|seven|eight|nine|ten|twelve|twenty|thirty|fifty|hundred|half)";
    private const string SizeUnit = @"(?<u>tb|terabayt|terabyte|gb|gigabayt|gigabyte|gig|mb|megabayt|megabyte|meg|kb|kilobayt|kilobyte|bayt|byte|b)s?";
    private const string AgeUnit = @"(?<u>yıl|yil|sene|year|ay|month|hafta|week|gün|gun|day|saat|hour|dakika|minute|min)['’]?[a-zçğıöşü]*";

    private static readonly Regex SizeRx = new($@"\b{Num}\s*{SizeUnit}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AgeRx = new($@"\b{Num}\s*{AgeUnit}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ExtRx = new(@"(?:^|[\s,(])(?:\*\.|\.)(?<e>[a-z0-9]{1,6})\b|\b(?<e2>[a-z0-9]{2,6})\s+(?:dosyalar[ıi]?|uzantılı|files?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private const string FolderWord = @"(?:klasör[a-zçğıöşü]*|dizin[a-zçğıöşü]*|folders?|directory|directories)";
    private static readonly Regex FolderRx = new(
        @"(?:\b" + FolderWord + @"\s+(?:adı\s+|named\s+|called\s+)[""']?(?<f1>[\w\-.]{2,40})[""']?)" +
        @"|(?:\b(?:in|inside|under|içinde|altında)\s+[""']?(?<f2>[\w\-.]{2,40})[""']?(?:\s+" + FolderWord + @")?)" +
        @"|(?:\b(?<f3>[\w\-.]{2,40})\s+" + FolderWord + @"(?![\p{L}]))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ------------------------------------------------------------------ translation

    /// <summary>True when the text looks like a sentence rather than filter syntax (no filter operators, has letters and a space or a known noun).</summary>
    public static bool LooksNatural(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return false;
        if (t.IndexOfAny(new[] { '*', '?', ':', '|', '\\', '<', '>', ';' }) >= 0) return false;
        if (t.Contains(' ')) return true;
        return Groups.Any(g => g.Words.Contains(t, StringComparer.OrdinalIgnoreCase)) || Reclaim.Any(w => t.Equals(w, StringComparison.OrdinalIgnoreCase))
               || CloudWords.Any(w => t.Equals(w, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] CloudWords = { "bulut", "cloud", "onedrive", "dropbox", "icloud" };

    private static readonly string[] Reclaim = { "önbellek", "önbellekler", "cache", "caches", "gereksiz", "gereksiz dosyalar", "silinebilir", "silinebilir dosyalar", "çöp", "junk", "reclaimable", "cleanup", "temizlenebilir" };

    public static Result Translate(string text)
    {
        var s = " " + Normalize(text) + " ";
        var parts = new List<string>();
        var explain = new List<string>();
        bool negateNext = false;

        // --- reclaimable (knowledge base)
        if (Reclaim.Any(w => ContainsWord(s, w)))
        {
            bool neg = ContainsWord(s, "hariç") && ContainsAny(s, "cache", "önbellek", "gereksiz") && s.IndexOf("hariç", StringComparison.Ordinal) > IndexOfAny(s, "cache", "önbellek", "gereksiz");
            parts.Add((neg ? "|" : "") + ":reclaimable");
            explain.Add(T(neg ? "excluding known caches" : "known caches / temporary areas"));
        }

        // --- cloud files (OneDrive / Dropbox / iCloud "Files On-Demand")
        bool cloudWord = Regex.IsMatch(s, @"\b(?:onedrive|dropbox|icloud|bulut|cloud)");
        if (cloudWord || Regex.IsMatch(s, @"yalnızca çevrimiçi|sadece çevrimiçi|online.only|free up space|always keep on this device"))
        {
            if (Regex.IsMatch(s, @"yer aç|boşalt|free up|freeable|yerel kopya|local cop|indirilmiş|indirilen|downloaded"))
            { parts.Add(":freeable"); explain.Add(T("local copies of cloud files (\"Free up space\" can remove them)")); }
            else if (Regex.IsMatch(s, @"çevrimiçi|online.only|only online|yalnızca bulutta|sadece bulutta|diskte olmayan|not on (?:this )?(?:disk|device)"))
            { parts.Add(":onlineonly"); explain.Add(T("online-only cloud files (no space on this disk)")); }
            else if (Regex.IsMatch(s, @"her zaman|always keep|always kept|sabitlenmiş|pinned"))
            { parts.Add(":alwayskept"); explain.Add(T("cloud files kept on this device")); }
            else { parts.Add(":cloud"); explain.Add(T("files in cloud folders")); }
        }

        // --- file type groups (OR group) with "except / hariç / olmayan / not" negation
        var groupsFound = new List<(Group g, bool neg)>();
        foreach (var g in Groups)
        {
            foreach (var w in g.Words.OrderByDescending(w => w.Length))
            {
                int i = IndexOfWord(s, w);
                if (i < 0) continue;
                string before = s.Substring(Math.Max(0, i - 12), Math.Min(12, i));
                string after = s.Substring(i + w.Length, Math.Min(14, s.Length - i - w.Length));
                bool neg = Regex.IsMatch(before, @"(?:\bexcept\b|\bnot\b|\bno\b|\bwithout\b|\bexcluding\b)\s*$")
                           || Regex.IsMatch(after, @"^\s*(?:hariç|dışında|dışındaki|olmayan|olmayanlar|değil|değilse|haricinde)\b");
                if (!groupsFound.Any(x => x.g == g)) groupsFound.Add((g, neg));
                break;
            }
        }
        // explicit extensions (".psd", "psd files", "psd dosyaları")
        var exts = new List<(string ext, bool neg)>();
        foreach (Match m in ExtRx.Matches(s))
        {
            var e = (m.Groups["e"].Success ? m.Groups["e"] : m.Groups["e2"]).Value.ToLowerInvariant();
            if (e.Length == 0 || IsStopWord(e) || Groups.Any(g => g.Words.Contains(e, StringComparer.OrdinalIgnoreCase))) continue;
            if (SizeRx.IsMatch(m.Value) || AgeRx.IsMatch(m.Value)) continue;
            string before = s.Substring(Math.Max(0, m.Index - 12), Math.Min(12, m.Index));
            string after = s.Substring(m.Index + m.Length, Math.Min(14, s.Length - m.Index - m.Length));
            bool neg = Regex.IsMatch(before, @"(?:\bexcept\b|\bnot\b|\bno\b|\bwithout\b|\bexcluding\b)\s*$") || Regex.IsMatch(after, @"^\s*(?:hariç|dışında|dışındaki|olmayan|değil|haricinde)\b");
            if (!exts.Any(x => x.ext == e)) exts.Add((e, neg));
        }
        foreach (var (g, neg) in groupsFound)
        {
            parts.AddRange(g.Extensions.Select(e => (neg ? "|" : "") + "*." + e));
            explain.Add(neg ? Fmt("not {0}", T(g.Label)) : T(g.Label));
        }
        foreach (var (e, neg) in exts)
        {
            parts.Add((neg ? "|" : "") + "*." + e);
            explain.Add(neg ? Fmt("not {0}", "*." + e) : "*." + e);
        }

        // --- size
        bool sizeDone = false;
        foreach (Match m in SizeRx.Matches(s))
        {
            double n = ParseNumber(m.Groups["n"].Value);
            string unit = UnitLetter(m.Groups["u"].Value);
            string ctx = s.Substring(Math.Max(0, m.Index - 25), Math.Min(25, m.Index)) + "|" + s.Substring(m.Index + m.Length, Math.Min(30, s.Length - m.Index - m.Length));
            bool atMost = Regex.IsMatch(ctx, @"en fazla|en çok|at most|up to|\bmax(?:imum)?\b|no more than", RegexOptions.IgnoreCase);
            bool less = atMost || Regex.IsMatch(ctx, @"küçük|altı|altında|\baz\b|smaller|less|under|below", RegexOptions.IgnoreCase);
            bool greater = !atMost && Regex.IsMatch(ctx, @"büyük|üstü|üstünde|fazla|çok|en az|aşan|over|above|larger|bigger|more|greater|at least|\bmin(?:imum)?\b|exceed", RegexOptions.IgnoreCase);
            string op = less && !greater ? "<" : ">";
            string val = n % 1 == 0 ? ((long)n).ToString(CultureInfo.InvariantCulture) : n.ToString("0.##", CultureInfo.InvariantCulture);
            parts.Add(op + val + unit);
            explain.Add(Fmt(op == ">" ? "larger than {0}" : "smaller than {0}", val + " " + unit.ToUpperInvariant()));
            sizeDone = true;
        }
        if (!sizeDone)
        {
            if (Regex.IsMatch(s, @"\b(?:dev|devasa|kocaman|huge|giant|enormous)\b")) { parts.Add(">1gb"); explain.Add(T("huge (> 1 GB)")); }
            else if (Regex.IsMatch(s, @"\b(?:büyük|large|big|kalın)\b")) { parts.Add(">100mb"); explain.Add(T("large (> 100 MB)")); }
            else if (Regex.IsMatch(s, @"\b(?:küçük|small|tiny|minik|ufak)\b")) { parts.Add("<1mb"); explain.Add(T("small (< 1 MB)")); }
            else if (Regex.IsMatch(s, @"\b(?:boş|sıfır bayt|empty|zero byte|zero-byte)\b")) { parts.Add("<1"); explain.Add(T("empty (0 bytes)")); }
        }

        // --- age
        bool ageDone = false;
        foreach (Match m in AgeRx.Matches(s))
        {
            double n = ParseNumber(m.Groups["n"].Value);
            if (m.Groups["n"].Value.Equals("yarım", StringComparison.OrdinalIgnoreCase) || m.Groups["n"].Value.Equals("half", StringComparison.OrdinalIgnoreCase)) n = 0.5;
            string unit = AgeUnitWord(m.Groups["u"].Value);
            string after = s.Substring(m.Index + m.Length, Math.Min(40, s.Length - m.Index - m.Length));
            string before = s.Substring(Math.Max(0, m.Index - 30), Math.Min(30, m.Index));
            string ctx = before + "|" + m.Value + "|" + after;
            // which timestamp
            string kind = Regex.IsMatch(ctx, @"açıl|kullanıl|erişil|bakıl|access|opened|used|touched", RegexOptions.IgnoreCase) ? "access"
                        : Regex.IsMatch(ctx, @"oluştur|yaratıl|indiril|eklen|creat|download|added", RegexOptions.IgnoreCase) ? "create" : "modify";
            // older than (>) or within (<)
            bool within = Regex.IsMatch(ctx, @"\bson\b|\bgeçen\b|\bbu\b|içinde|içindeki|last|past|within|this|recent|new|yeni", RegexOptions.IgnoreCase)
                          && !Regex.IsMatch(ctx, @"aşkın|fazla|eski|önce|older|more than|ago|beri|dır\b|dir\b|dur\b|dür\b|değişmemiş|açılmamış|kullanılmamış|dokunulmamış|not\b", RegexOptions.IgnoreCase);
            string op = within ? "<" : ">";
            string val = n % 1 == 0 ? ((long)n).ToString(CultureInfo.InvariantCulture) : n.ToString("0.##", CultureInfo.InvariantCulture);
            parts.Add(kind + op + val + unit);
            string amount = Amount(val, unit);
            explain.Add(within
                ? Fmt(kind == "access" ? "opened within the last {0}" : kind == "create" ? "created within the last {0}" : "modified within the last {0}", amount)
                : Fmt(kind == "access" ? "not opened for {0}" : kind == "create" ? "created more than {0} ago" : "modified more than {0} ago", amount));
            ageDone = true;
        }
        if (!ageDone)
        {
            if (Regex.IsMatch(s, @"\b(?:bugün|today)\b")) { parts.Add("modify<1day"); explain.Add(T("modified today")); }
            else if (Regex.IsMatch(s, @"\b(?:dün|yesterday)\b")) { parts.Add("modify<2days"); explain.Add(T("modified since yesterday")); }
            else if (Regex.IsMatch(s, @"\b(?:bu hafta|this week|son günlerde|recently|recent|yeni|new)\b")) { parts.Add("modify<7days"); explain.Add(T("modified in the last 7 days")); }
            else if (Regex.IsMatch(s, @"\b(?:bu ay|this month)\b")) { parts.Add("modify<1month"); explain.Add(T("modified this month")); }
            else if (Regex.IsMatch(s, @"\b(?:eski|old|stale|unused|kullanılmayan)\b")) { parts.Add("access>1year"); explain.Add(T("not opened for a year")); }
        }

        // --- attributes and tags
        if (Regex.IsMatch(s, @"\b(?:gizli|hidden)\b")) { parts.Add(":attr:h"); explain.Add(T("hidden")); }
        if (Regex.IsMatch(s, @"\b(?:sistem dosya|system file)")) { parts.Add(":attr:s"); explain.Add(T("system")); }
        if (Regex.IsMatch(s, @"\b(?:salt okunur|read-only|readonly)\b")) { parts.Add(":attr:r"); explain.Add(T("read-only")); }
        foreach (var (words, tag) in new[] { (new[] { "kırmızı", "red" }, "red"), (new[] { "sarı", "yellow" }, "yellow"), (new[] { "yeşil", "green" }, "green"), (new[] { "mavi", "blue" }, "blue") })
            if (words.Any(w => Regex.IsMatch(s, $@"\b{w}\b\s*(?:etiket|tag|işaret)"))) { parts.Add(":" + tag); explain.Add(T(tag switch { "red" => "red tagged", "yellow" => "yellow tagged", "green" => "green tagged", _ => "blue tagged" })); }
        if (Regex.IsMatch(s, @"\b(?:etiketli|etiketlenmiş|tagged)\b") && !parts.Any(p => p is ":red" or ":yellow" or ":green" or ":blue")) { parts.Add(":tags:r+y+g+b"); explain.Add(T("tagged")); }

        // --- folder
        var fm = FolderRx.Match(s);
        if (fm.Success)
        {
            var name = (fm.Groups["f1"].Success ? fm.Groups["f1"] : fm.Groups["f2"].Success ? fm.Groups["f2"] : fm.Groups["f3"]).Value.Trim();
            int at = text.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) name = text.Substring(at, name.Length);     // keep the user's casing
            name = MapFolderName(name);
            if (name.Length >= 2 && !IsStopWord(name) && !SizeRx.IsMatch(name + " ") && !Groups.Any(g => g.Words.Contains(name, StringComparer.OrdinalIgnoreCase)))
            {
                bool neg = Regex.IsMatch(s.Substring(fm.Index + fm.Length, Math.Min(14, s.Length - fm.Index - fm.Length)), @"^\s*(?:hariç|dışında|dışındaki)\b") || Regex.IsMatch(s.Substring(Math.Max(0, fm.Index - 10), Math.Min(10, fm.Index)), @"(?:except|not|outside)\s*$");
                parts.Add((neg ? "|" : "") + "\\" + name + "*");
                explain.Add(Fmt(neg ? "outside folders named {0}…" : "inside folders named {0}…", name));
            }
        }
        else if (Regex.IsMatch(s, @"\b(?:indirilenler|downloads)\b")) { parts.Add("\\Downloads"); explain.Add(T("inside Downloads")); }
        else if (Regex.IsMatch(s, @"\b(?:masaüstü|desktop)\b")) { parts.Add("\\Desktop"); explain.Add(T("inside Desktop")); }

        _ = negateNext;
        var filter = string.Join(";", parts.Distinct());
        return new Result(filter, string.Join(", ", explain), Array.Empty<string>());
    }

    // ------------------------------------------------------------------ helpers

    private static string Normalize(string t)
    {
        t = t.Trim().Replace('İ', 'i').ToLowerInvariant();   // dotted capital I first, so "İndirilenler" lowers to "indirilenler"
        t = t.Replace("'", " ").Replace("’", " ").Replace("\"", " ");
        t = Regex.Replace(t, @"\s+", " ");
        return t;
    }

    private static bool ContainsWord(string s, string w) => IndexOfWord(s, w) >= 0;
    private static bool ContainsAny(string s, params string[] ws) => ws.Any(w => s.Contains(w, StringComparison.Ordinal));
    private static int IndexOfAny(string s, params string[] ws) => ws.Select(w => s.IndexOf(w, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();

    private static int IndexOfWord(string s, string w)
    {
        var m = Regex.Match(s, @"(?<![\p{L}\p{N}])" + Regex.Escape(w) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? m.Index : -1;
    }

    private static double ParseNumber(string n)
    {
        if (double.TryParse(n.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return NumberWords.TryGetValue(n, out var v) ? v : 1;
    }

    private static string UnitLetter(string u)
    {
        u = u.ToLowerInvariant();
        if (u.StartsWith("t")) return "tb";
        if (u.StartsWith("g")) return "gb";
        if (u.StartsWith("m")) return "mb";
        if (u.StartsWith("k")) return "kb";
        return "";
    }

    /// <summary>"3 months" in the interface language (the filter itself keeps the unit word of the filter language).</summary>
    private static string Amount(string val, string unit)
    {
        if (!long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
            return Fmt(unit switch { "years" => "{0} years", "months" => "{0} months", "weeks" => "{0} weeks", "days" => "{0} days", "hours" => "{0} hours", _ => "{0} minutes" }, val);
        return unit switch
        {
            "years" => P(n, "{0} year", "{0} years"),
            "months" => P(n, "{0} month", "{0} months"),
            "weeks" => P(n, "{0} week", "{0} weeks"),
            "days" => P(n, "{0} day", "{0} days"),
            "hours" => P(n, "{0} hour", "{0} hours"),
            _ => P(n, "{0} minute", "{0} minutes"),
        };
    }

    private static string AgeUnitWord(string u)
    {
        u = u.ToLowerInvariant();
        if (u.StartsWith("yıl") || u.StartsWith("yil") || u.StartsWith("sene") || u.StartsWith("year")) return "years";
        if (u.StartsWith("ay") || u.StartsWith("month")) return "months";
        if (u.StartsWith("hafta") || u.StartsWith("week")) return "weeks";
        if (u.StartsWith("gün") || u.StartsWith("gun") || u.StartsWith("day")) return "days";
        if (u.StartsWith("saat") || u.StartsWith("hour")) return "hours";
        return "minutes";
    }

    private static string MapFolderName(string name) => name.ToLowerInvariant() switch
    {
        "indirilenler" or "indirmeler" => "Downloads",
        "masaüstü" => "Desktop",
        "belgelerim" or "belgeler" => "Documents",
        "resimlerim" => "Pictures",
        "müziğim" or "müzik" => "Music",
        "videolarım" => "Videos",
        _ => name,
    };

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "ve", "ile", "and", "or", "veya", "the", "of", "in", "on", "at", "to", "for", "with", "than", "olan", "tüm", "bütün", "all", "files", "file", "dosya", "dosyalar",
        "bir", "bu", "şu", "en", "çok", "daha", "az", "büyük", "küçük", "eski", "yeni", "old", "new", "large", "small", "big", "son", "last", "past",
    };
    private static bool IsStopWord(string w) => Stop.Contains(w) || NumberWords.ContainsKey(w) || CloudWords.Contains(w, StringComparer.OrdinalIgnoreCase) || Regex.IsMatch(w, @"^(?:mb|gb|kb|tb|gün|ay|yıl|hafta|day|days|month|months|year|years|week|weeks)$", RegexOptions.IgnoreCase);
}
