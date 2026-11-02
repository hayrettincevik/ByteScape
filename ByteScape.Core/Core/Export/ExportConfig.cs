using System.Xml.Linq;

namespace ByteScape.Core.Export;

public enum BlockSorting { None, Files, Folders }
public enum FineSorting { None, Size, Name }

/// <summary>An export configuration (Frmexport design-time data). Persisted in the config XML.</summary>
public sealed class ExportConfig
{
    public const int CurrentVersion = 1;
    public const string BinarySnapshotName = "ByteScape binary snapshot";

    public int Version { get; set; } = CurrentVersion;
    public string Description { get; set; } = "New export";
    public string PreferredFileName { get; set; } = "Export";
    /// <summary>Dialog filter syntax, e.g. "Text files (*.txt)|*.txt".</summary>
    public string PreferredFileExtension { get; set; } = "Text files (*.txt)|*.txt";
    public string HeaderFormat { get; set; } = "";
    public string DetailFormat { get; set; } = "";
    public string FooterFormat { get; set; } = "";
    public BlockSorting BlockSorting { get; set; } = BlockSorting.Files;
    public FineSorting FineSorting { get; set; } = FineSorting.Size;
    public bool FineSortingDescending { get; set; } = true;

    /// <summary>Hard coded: cannot be edited or deleted (binary snapshot, CSV / JSON / HTML reports).</summary>
    public bool IsHardCoded { get; init; }
    /// <summary>A report written by <see cref="Reports"/> instead of the script templates (null for templates and the snapshot).</summary>
    public ReportFormat? Report { get; init; }
    /// <summary>Shipped with the program: cannot be deleted unless first stored in the local config.</summary>
    public bool IsBuiltin { get; init; }

    public bool IsBinarySnapshot => IsHardCoded && Report == null;

    public string DefaultExtension
    {
        get
        {
            var parts = PreferredFileExtension.Split('|');
            var pat = parts.Length > 1 ? parts[1] : parts[0];
            int dot = pat.LastIndexOf('.');
            return dot >= 0 ? pat.Substring(dot) : ".txt";
        }
    }

    public ExportConfig Clone() => new()
    {
        Version = Version, Description = Description, PreferredFileName = PreferredFileName, PreferredFileExtension = PreferredFileExtension,
        HeaderFormat = HeaderFormat, DetailFormat = DetailFormat, FooterFormat = FooterFormat,
        BlockSorting = BlockSorting, FineSorting = FineSorting, FineSortingDescending = FineSortingDescending,
        IsHardCoded = IsHardCoded, IsBuiltin = IsBuiltin, Report = Report,
    };

    public override string ToString() => Description;

    // ------------------------------------------------------------------ builtins

    public static readonly ExportConfig BinarySnapshot = new()
    {
        Description = BinarySnapshotName, PreferredFileName = "ByteScape snapshot", PreferredFileExtension = "ByteScape snapshot (*.sns)|*.sns",
        IsHardCoded = true, BlockSorting = BlockSorting.None, FineSorting = FineSorting.None,
    };

    /// <summary>The reports for other programs: a CSV list, a JSON tree, an HTML page.</summary>
    public static IReadOnlyList<ExportConfig> ReportConfigs { get; } = new List<ExportConfig>
    {
        new() { Description = Reports.CsvName, PreferredFileName = "ByteScape list", PreferredFileExtension = "CSV file (*.csv)|*.csv", IsHardCoded = true, Report = ReportFormat.Csv, BlockSorting = BlockSorting.None, FineSorting = FineSorting.None },
        new() { Description = Reports.JsonName, PreferredFileName = "ByteScape tree", PreferredFileExtension = "JSON file (*.json)|*.json", IsHardCoded = true, Report = ReportFormat.Json, BlockSorting = BlockSorting.None, FineSorting = FineSorting.None },
        new() { Description = Reports.HtmlName, PreferredFileName = "ByteScape report", PreferredFileExtension = "Web page (*.html)|*.html", IsHardCoded = true, Report = ReportFormat.Html, BlockSorting = BlockSorting.None, FineSorting = FineSorting.None },
    };

    public static IReadOnlyList<ExportConfig> Builtins { get; } = new List<ExportConfig>
    {
        // every folder that holds files, then its files with their sizes, biggest first
        new()
        {
            Description = "Folders and their files", PreferredFileName = "Files by folder", IsBuiltin = true,
            DetailFormat = "{script}\r\n" +
                           "{ if {<%iscontainer%>} == {true}\r\n" +
                           "    ? { if {<%containsfiles%>} == {true} ? {{&br}<%pathfile%>  (<%disksize%>){&br}} }\r\n" +
                           "    : { if {<%isfile%>} == {true} ? {{leftpad{<%disksize%>}{ }{10}}  <%file%>{&br}} }\r\n" +
                           "}",
            BlockSorting = BlockSorting.Files, FineSorting = FineSorting.Size, FineSortingDescending = true,
        },
        // the folders right below the exported one, biggest first
        new()
        {
            Description = "Top-level folders", PreferredFileName = "Top-level folders", IsBuiltin = true,
            DetailFormat = "{script}\r\n" +
                           "{ if {<%nestinglevel%>} == {1}\r\n" +
                           "    ? { if {<%iscontainer%>} == {true} ? {{leftpad{<%disksize%>}{ }{10}}  <%pathfile%>{&br}} }\r\n" +
                           "}",
            BlockSorting = BlockSorting.Folders, FineSorting = FineSorting.Size, FineSortingDescending = true,
        },
        // one full path per line, biggest files first
        new()
        {
            Description = "File paths", PreferredFileName = "File paths", IsBuiltin = true,
            DetailFormat = "{script}\r\n{ if {<%isfile%>} == {true} ? {<%pathfile%>{&br}} }",
            BlockSorting = BlockSorting.Files, FineSorting = FineSorting.Size, FineSortingDescending = true,
        },
        new()
        {
            Description = "Totals", PreferredFileName = "Totals", IsBuiltin = true,
            FooterFormat = "Space on disk: <%disksize%>\r\nFolders: <%foldercount%>\r\nFiles: <%filecount%>\r\n",
            BlockSorting = BlockSorting.Files, FineSorting = FineSorting.Size, FineSortingDescending = true,
        },
    };

    /// <summary>All configurations offered by the export dialog: hard coded, user (config file), builtins not overridden by a user one.</summary>
    public static List<ExportConfig> All()
    {
        var list = new List<ExportConfig> { BinarySnapshot };
        list.AddRange(ReportConfigs);
        var user = Config.AppConfig.Current.ExportConfigs;
        list.AddRange(user);
        foreach (var b in Builtins)
            if (!user.Any(u => string.Equals(u.Description, b.Description, StringComparison.Ordinal))) list.Add(b);
        return list;
    }

    public static ExportConfig? FindByName(string name)
        => All().FirstOrDefault(c => string.Equals(c.Description, name, StringComparison.Ordinal));

    // ------------------------------------------------------------------ xml

    public XElement ToXml() => new("exportConfig",
        new XElement("version", Version),
        new XElement("description", Description),
        new XElement("preferredFileName", PreferredFileName),
        new XElement("preferredFileExtension", PreferredFileExtension),
        new XElement("headerFormat", HeaderFormat),
        new XElement("detailFormat", DetailFormat),
        new XElement("footerFormat", FooterFormat),
        new XElement("blockSorting", BlockSorting.ToString().ToLowerInvariant()),
        new XElement("fineSorting", FineSorting.ToString().ToLowerInvariant()),
        new XElement("fineSortingDirection", FineSortingDescending ? "desc" : "asc"));

    public static ExportConfig FromXml(XElement e)
    {
        var c = new ExportConfig
        {
            Version = int.TryParse((string?)e.Element("version"), out var v) ? v : 0,
            Description = (string?)e.Element("description") ?? "Unnamed",
            PreferredFileName = (string?)e.Element("preferredFileName") ?? "Export",
            PreferredFileExtension = (string?)e.Element("preferredFileExtension") ?? "Text files (*.txt)|*.txt",
            HeaderFormat = (string?)e.Element("headerFormat") ?? "",
            DetailFormat = (string?)e.Element("detailFormat") ?? "",
            FooterFormat = (string?)e.Element("footerFormat") ?? "",
        };
        c.BlockSorting = ((string?)e.Element("blockSorting"))?.ToLowerInvariant() switch { "files" => BlockSorting.Files, "folders" => BlockSorting.Folders, _ => BlockSorting.None };
        c.FineSorting = ((string?)e.Element("fineSorting"))?.ToLowerInvariant() switch { "size" => FineSorting.Size, "name" => FineSorting.Name, _ => FineSorting.None };
        c.FineSortingDescending = string.Equals((string?)e.Element("fineSortingDirection"), "desc", StringComparison.OrdinalIgnoreCase);
        return c;
    }

    /// <summary>Returns a warning when the imported configuration comes from an unknown/newer version.</summary>
    public string? VersionWarning()
    {
        if (Version == 0) return T("This export setting has no version number, so it may not work as expected.\n\nImport it anyway?");
        if (Version > CurrentVersion) return F($"This export setting was made by a newer version of {Branding.Name} and may not work as expected.\n\nImport it anyway?");
        return null;
    }
}
