using System.Globalization;
using ByteScape.Core.Config;
using ByteScape.Core.Model;
using ByteScape.Core.Util;

namespace ByteScape.Core.Filtering;

public sealed class FilterException : Exception
{
    public FilterException(string message) : base(message) { }
}

/// <summary>
/// The view filter. Conditions are separated by ';'. Rules (manual 6.8):
///  - file masks, tag and class conditions are OR-ed together,
///  - excluding masks (leading '|') are AND-ed together,
///  - every other condition (size, age, attributes, folder masks) is AND-ed.
/// </summary>
public sealed class Filter
{
    public static readonly Filter Empty = new(string.Empty);

    private readonly List<Wildcard> _includeMasks = new();
    private readonly List<Wildcard> _excludeMasks = new();
    private readonly List<Wildcard> _includeFolderMasks = new();
    private readonly List<Wildcard> _excludeFolderMasks = new();
    private readonly List<(TagColor include, TagColor exclude, bool negate)> _tagConds = new();
    private readonly List<(FileAttributes include, FileAttributes exclude, bool includeAds, bool excludeAds, bool negate)> _attrConds = new();
    private readonly List<(FileClass cls, bool negate)> _classConds = new();
    private readonly List<(SizeKind kind, bool greater, long bytes, bool negate)> _sizeConds = new();
    private readonly List<(AgeKind kind, bool greater, TimeSpan span, bool negate)> _ageConds = new();
    /// <summary>":reclaimable" — files inside known caches / temp areas (knowledge base); "|:reclaimable" excludes them.</summary>
    private readonly List<bool> _reclaimConds = new();
    /// <summary>":cloud" (any file of a cloud folder), ":freeable", ":onlineonly", ":alwayskept" — OneDrive / Dropbox Files On-Demand states.</summary>
    private readonly List<(Core.Cloud.CloudState? state, bool negate)> _cloudConds = new();

    public string Text { get; }
    public bool IsEmpty { get; }

    private enum SizeKind { Disk, Logical }
    private enum AgeKind { Create, Modify, Access }

    public Filter(string text)
    {
        Text = text ?? string.Empty;
        foreach (var raw in Text.Split(';'))
        {
            var cond = raw.Trim();
            if (cond.Length == 0) continue;
            bool negate = false;
            if (cond.StartsWith('|')) { negate = true; cond = cond.Substring(1).Trim(); if (cond.Length == 0) continue; }
            Parse(cond, negate);
        }
        IsEmpty = _includeMasks.Count == 0 && _excludeMasks.Count == 0 && _includeFolderMasks.Count == 0 && _excludeFolderMasks.Count == 0
                  && _tagConds.Count == 0 && _attrConds.Count == 0 && _classConds.Count == 0 && _sizeConds.Count == 0 && _ageConds.Count == 0 && _reclaimConds.Count == 0 && _cloudConds.Count == 0;
    }

    /// <summary>True when the filter has at least one condition that only matters for folders (folder masks).</summary>
    public bool HasFolderMasks => _includeFolderMasks.Count > 0;

    // ------------------------------------------------------------------ parsing

    private void Parse(string cond, bool negate)
    {
        if (cond.StartsWith('\\'))
        {
            var mask = cond.Substring(1);
            if (mask.Length == 0 || mask.Contains('\\')) throw new FilterException(T("Folder name not valid:") + " " + cond);
            (negate ? _excludeFolderMasks : _includeFolderMasks).Add(new Wildcard(mask));
            return;
        }
        if (cond.StartsWith(':'))
        {
            ParseColonCondition(cond, negate);
            return;
        }
        if (TryParseSizeOrAge(cond, negate)) return;
        // plain file name mask
        (negate ? _excludeMasks : _includeMasks).Add(new Wildcard(cond));
    }

    private void ParseColonCondition(string cond, bool negate)
    {
        string body = cond.Substring(1);
        if (body.Equals("reclaimable", StringComparison.OrdinalIgnoreCase) || body.Equals("cache", StringComparison.OrdinalIgnoreCase) || body.Equals("junk", StringComparison.OrdinalIgnoreCase))
        { _reclaimConds.Add(negate); return; }
        switch (body.ToLowerInvariant())
        {
            case "cloud": _cloudConds.Add((null, negate)); return;
            case "freeable": case "cloudlocal": _cloudConds.Add((Core.Cloud.CloudState.LocalCopy, negate)); return;
            case "onlineonly": case "online": _cloudConds.Add((Core.Cloud.CloudState.OnlineOnly, negate)); return;
            case "alwayskept": case "pinned": _cloudConds.Add((Core.Cloud.CloudState.AlwaysKept, negate)); return;
        }
        if (body.StartsWith("tags:", StringComparison.OrdinalIgnoreCase)) { AddTagCond(body.Substring(5), negate); return; }
        if (body.StartsWith("tag:", StringComparison.OrdinalIgnoreCase)) { AddTagCond(body.Substring(4), negate); return; }
        if (body.StartsWith("attrs:", StringComparison.OrdinalIgnoreCase)) { AddAttrCond(body.Substring(6), negate); return; }
        if (body.StartsWith("attr:", StringComparison.OrdinalIgnoreCase)) { AddAttrCond(body.Substring(5), negate); return; }
        if (body.StartsWith("class:", StringComparison.OrdinalIgnoreCase))
        {
            var name = body.Substring(6).Trim();
            var cls = AppConfig.Current.ClassByName(name) ?? throw new FilterException(T("No file class has this name:") + " " + name);
            _classConds.Add((cls, negate));
            return;
        }
        // old syntax ":red", ":1", ":r+y", ":all"
        AddTagCond(body, negate);
    }

    private void AddTagCond(string spec, bool negate)
    {
        TagColor include = TagColor.None, exclude = TagColor.None;
        foreach (var (token, minus) in SplitCombo(spec))
        {
            var t = ParseTag(token) ?? throw new FilterException(T("Unknown tag colour (red, yellow, green, blue, all):") + " " + spec);
            if (minus) exclude |= t; else include |= t;
        }
        if (include == TagColor.None && exclude == TagColor.None) throw new FilterException(T("Unknown tag colour (red, yellow, green, blue, all):") + " " + spec);
        if (include == TagColor.None) include = TagColor.All; // ":tag:-red" = tagged but not red
        _tagConds.Add((include, exclude, negate));
    }

    private static TagColor? ParseTag(string t)
    {
        t = t.Trim().ToLowerInvariant();
        return t switch
        {
            "1" or "r" or "red" => TagColor.Red,
            "2" or "y" or "yellow" => TagColor.Yellow,
            "3" or "g" or "green" => TagColor.Green,
            "4" or "b" or "blu" or "blue" => TagColor.Blue,
            "a" or "all" => TagColor.All,
            _ => null,
        };
    }

    private void AddAttrCond(string spec, bool negate)
    {
        FileAttributes include = 0, exclude = 0;
        bool incAds = false, excAds = false;
        foreach (var (token, minus) in SplitCombo(spec))
        {
            var (attr, ads) = ParseAttr(token);
            if (attr == 0 && !ads) throw new FilterException(T("Unknown attribute (archive, hidden, readonly, system...):") + " " + spec);
            if (minus) { exclude |= attr; excAds |= ads; } else { include |= attr; incAds |= ads; }
        }
        _attrConds.Add((include, exclude, incAds, excAds, negate));
    }

    private static (FileAttributes, bool ads) ParseAttr(string t)
    {
        t = t.Trim().ToLowerInvariant();
        switch (t)
        {
            case "a": case "ar": case "arc": case "arch": case "archi": case "archive": return (FileAttributes.Archive, false);
            case "s": case "sys": case "system": return (FileAttributes.System, false);
            case "r": case "ro": case "rdo": case "ronly": case "rdonly": case "readonly": return (FileAttributes.ReadOnly, false);
            case "h": case "hid": case "hidn": case "hidden": return (FileAttributes.Hidden, false);
            case "c": case "cpr": case "compr": case "comprsd": case "compressed": return (FileAttributes.Compressed, false);
            case "e": case "enc": case "encptd": case "encrptd": case "encrypted": case "crypt": case "cptd": case "crypted": return (FileAttributes.Encrypted, false);
            case "o": case "ol": case "off": case "offline": return (FileAttributes.Offline, false);
            case "t": case "tmp": case "temp": case "temporary": return (FileAttributes.Temporary, false);
            case "ni": case "nidx": case "noidx": case "notidx": case "notindx": case "notindexed": return (FileAttributes.NotContentIndexed, false);
            case "sp": case "sparse": return (FileAttributes.SparseFile, false);
            case "ads": case "alt": case "altern": case "alternate": case "alternatedatastream": return (0, true);
        }
        return (0, false);
    }

    /// <summary>Splits "red+green-b" / "a,h,-s" / "+a-ro,h" into tokens with a minus flag.</summary>
    private static IEnumerable<(string token, bool minus)> SplitCombo(string spec)
    {
        var cur = new System.Text.StringBuilder();
        bool minus = false;
        foreach (char ch in spec)
        {
            if (ch == '+' || ch == '-' || ch == ',' || ch == ' ')
            {
                if (cur.Length > 0) { yield return (cur.ToString(), minus); cur.Clear(); }
                if (ch == '-') minus = true; else if (ch == '+' || ch == ',') minus = false;
                continue;
            }
            cur.Append(ch);
        }
        if (cur.Length > 0) yield return (cur.ToString(), minus);
    }

    private static readonly string[] SizePrefixes = { "disksize", "clustersize", "filesize", "logicalsize", "size" };
    private static readonly string[] AgePrefixes = { "create", "creation", "created", "modify", "modified", "modification", "access", "accessed" };

    /// <summary>[prefix][&lt;|&gt;] N [unit] with fuzzy prefix recognition.</summary>
    private bool TryParseSizeOrAge(string cond, bool negate)
    {
        int op = cond.IndexOfAny(new[] { '<', '>' });
        if (op < 0) return false;
        string prefix = cond.Substring(0, op).Trim();
        bool greater = cond[op] == '>';
        string rest = cond.Substring(op + 1).Trim();
        if (rest.StartsWith('=')) rest = rest.Substring(1).Trim();

        int i = 0;
        while (i < rest.Length && (char.IsDigit(rest[i]) || rest[i] == '.' || rest[i] == ',')) i++;
        string numText = rest.Substring(0, i).Replace(',', '.');
        string unit = rest.Substring(i).Trim().ToLowerInvariant();
        if (numText.Length == 0 || !double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
            throw new FilterException(T("Size or age not understood:") + " " + cond);

        // Decide by unit first: size units vs age units.
        long? sizeMul = unit switch
        {
            "" or "b" or "byte" or "bytes" => 1L,
            "k" or "kb" => 1L << 10,
            "m" or "mb" => 1L << 20,
            "g" or "gb" => 1L << 30,
            "t" or "tb" => 1L << 40,
            _ => null,
        };
        TimeSpan? ageUnit = unit switch
        {
            "s" or "sec" or "secs" or "second" or "seconds" => TimeSpan.FromSeconds(1),
            "min" or "mins" or "minute" or "minutes" => TimeSpan.FromMinutes(1),
            "h" or "hour" or "hours" => TimeSpan.FromHours(1),
            "d" or "day" or "days" => TimeSpan.FromDays(1),
            "w" or "week" or "weeks" => TimeSpan.FromDays(7),
            "month" or "months" => TimeSpan.FromDays(30.4375),
            "y" or "year" or "years" => TimeSpan.FromDays(365.25),
            _ => null,
        };
        // "m" is minutes for an age prefix but megabytes otherwise ("m<10s" vs "s>1m")
        if (unit == "m" && prefix.Length > 0 && Fuzzy.Matches(prefix, AgePrefixes) && !Fuzzy.Matches(prefix, SizePrefixes))
        { sizeMul = null; ageUnit = TimeSpan.FromMinutes(1); }

        if (sizeMul != null && (prefix.Length == 0 || Fuzzy.Matches(prefix, SizePrefixes)))
        {
            var kind = SizeKind.Disk;
            if (prefix.Length > 0 && (Fuzzy.Matches(prefix, "filesize", "logicalsize") || (Fuzzy.Matches(prefix, "size") && !Fuzzy.Matches(prefix, "disksize", "clustersize"))))
                kind = SizeKind.Logical;
            _sizeConds.Add((kind, greater, (long)(num * sizeMul.Value), negate));
            return true;
        }
        if (ageUnit != null && (prefix.Length == 0 || Fuzzy.Matches(prefix, AgePrefixes)))
        {
            var kind = AgeKind.Modify;
            if (prefix.Length > 0)
            {
                if (Fuzzy.Matches(prefix, "create", "creation", "created")) kind = AgeKind.Create;
                else if (Fuzzy.Matches(prefix, "access", "accessed")) kind = AgeKind.Access;
            }
            _ageConds.Add((kind, greater, TimeSpan.FromTicks((long)(num * ageUnit.Value.Ticks)), negate));
            return true;
        }
        throw new FilterException(T("Size or age not understood:") + " " + cond);
    }

    // ------------------------------------------------------------------ evaluation

    /// <summary>True when the folder name matches an inclusion folder mask (rendered bold).</summary>
    public bool FolderMatches(string folderName)
    {
        foreach (var m in _includeFolderMasks) if (m.IsMatch(folderName)) return true;
        return false;
    }

    /// <summary>
    /// Evaluates a file. <paramref name="folderMaskState"/> is the state computed for the parent
    /// chain by <see cref="FolderState"/> (so folder masks are evaluated once per folder).
    /// </summary>
    public bool Matches(FileNode file, FolderMaskState folderMaskState, DateTime nowUtc)
    {
        if (IsEmpty) return true;
        if (folderMaskState.Excluded) return false;
        if (_includeFolderMasks.Count > 0 && !folderMaskState.Included) return false;

        // OR group: file masks, tags, classes
        bool hasOrGroup = _includeMasks.Count > 0 || _classConds.Any(c => !c.negate) || _tagConds.Any(t => !t.negate);
        if (hasOrGroup)
        {
            bool any = false;
            foreach (var m in _includeMasks) if (m.IsMatch(file.Name)) { any = true; break; }
            if (!any)
            {
                var ext = file.Extension;
                foreach (var (cls, neg) in _classConds) if (!neg && cls.Contains(ext)) { any = true; break; }
            }
            if (!any)
            {
                var tag = file.EffectiveTag;
                foreach (var (inc, exc, neg) in _tagConds)
                    if (!neg && TagMatch(tag, inc, exc)) { any = true; break; }
            }
            if (!any) return false;
        }

        // AND group: exclusions
        foreach (var m in _excludeMasks) if (m.IsMatch(file.Name)) return false;
        if (_classConds.Count > 0)
        {
            var ext = file.Extension;
            foreach (var (cls, neg) in _classConds) if (neg && cls.Contains(ext)) return false;
        }
        if (_tagConds.Count > 0)
        {
            var tag = file.EffectiveTag;
            foreach (var (inc, exc, neg) in _tagConds) if (neg && TagMatch(tag, inc, exc)) return false;
        }

        // AND group: attributes, size, age
        foreach (var (inc, exc, incAds, excAds, neg) in _attrConds)
        {
            bool match = ((inc != 0 && (file.Attributes & inc) != 0) || (incAds && file.HasAlternateDataStream) || (inc == 0 && !incAds))
                         && (exc == 0 || (file.Attributes & exc) == 0) && (!excAds || !file.HasAlternateDataStream);
            if (match == neg) return false;
        }
        foreach (var (kind, greater, bytes, neg) in _sizeConds)
        {
            long v = kind == SizeKind.Disk ? file.DiskSize : file.LogicalSize;
            bool match = greater ? v > bytes : v < bytes;
            if (match == neg) return false;
        }
        foreach (var (kind, greater, span, neg) in _ageConds)
        {
            var t = kind switch { AgeKind.Create => file.Created, AgeKind.Access => file.Accessed, _ => file.Modified };
            var age = nowUtc - t;
            bool match = greater ? age > span : age < span;
            if (match == neg) return false;
        }
        foreach (var neg in _reclaimConds)
        {
            bool match = Core.Knowledge.KnowledgeBase.IsReclaimable(file);
            if (match == neg) return false;
        }
        if (_cloudConds.Count > 0)
        {
            var cs = Core.Cloud.CloudFiles.StateOf(file);
            foreach (var (state, neg) in _cloudConds)
            {
                bool match = state == null ? cs != Core.Cloud.CloudState.None : cs == state;
                if (match == neg) return false;
            }
        }
        return true;
    }

    private static bool TagMatch(TagColor tag, TagColor include, TagColor exclude)
        => (tag & include) != 0 && (tag & exclude) == 0;

    /// <summary>Folder mask state inherited along the path.</summary>
    public readonly record struct FolderMaskState(bool Included, bool Excluded)
    {
        public static readonly FolderMaskState Root = new(false, false);
    }

    public FolderMaskState FolderState(FolderMaskState parent, string folderName)
    {
        if (_includeFolderMasks.Count == 0 && _excludeFolderMasks.Count == 0) return parent;
        bool inc = parent.Included, exc = parent.Excluded;
        if (!inc) foreach (var m in _includeFolderMasks) if (m.IsMatch(folderName)) { inc = true; break; }
        if (!exc) foreach (var m in _excludeFolderMasks) if (m.IsMatch(folderName)) { exc = true; break; }
        return new FolderMaskState(inc, exc);
    }
}
