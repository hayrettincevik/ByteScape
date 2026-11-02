namespace ByteScape;

/// <summary>Command line: help | scan "<path>" | load "<file>" | filter "<filter>" | export "<name>" "<path>" | compare "<file>" | apps | appsreport "<file>" | games | gamesreport "<file>" | dev | devreport "<file>" | forecast | cleanup | cleanupreport "<file>" | duplicates | duplicatesreport "<file>" | comparefolder "<path>" | comparefolderreport "<path>" "<file>" | measure | autoclose.</summary>
public static class CommandLine
{
    public sealed class ViewRequest
    {
        public string Path = "";
        public bool IsLoad;
        public string Filter = "";
        public List<(string name, string path)> Exports = new();
        /// <summary>Snapshot to compare the view with once it is scanned ("compare &lt;file.sns&gt;").</summary>
        public string CompareWith = "";
        /// <summary>Open the "Programs on disk" window once the view is scanned ("apps").</summary>
        public bool ShowApps;
        /// <summary>Write the program footprint report there once the view is scanned ("appsreport &lt;file&gt;").</summary>
        public string AppsReport = "";
        /// <summary>Open the "Games" window once the view is scanned ("games").</summary>
        public bool ShowGames;
        /// <summary>Write the game library report there once the view is scanned ("gamesreport &lt;file&gt;").</summary>
        public string GamesReport = "";
        /// <summary>Open the "Developer leftovers" window once the view is scanned ("dev").</summary>
        public bool ShowDev;
        /// <summary>Write the developer leftovers report there once the view is scanned ("devreport &lt;file&gt;").</summary>
        public string DevReport = "";
        /// <summary>Open the disk forecast once the view is scanned ("forecast").</summary>
        public bool ShowForecast;
        /// <summary>Open the "Duplicate files" window once the view is scanned ("duplicates").</summary>
        public bool ShowDuplicates;
        /// <summary>Write the duplicate groups there once the view is scanned ("duplicatesreport &lt;file&gt;": .csv, .json or text).</summary>
        public string DuplicatesReport = "";
        /// <summary>Compare the view with that folder once scanned ("comparefolder &lt;path&gt;").</summary>
        public string CompareFolderWith = "";
        /// <summary>Write that comparison there ("comparefolderreport &lt;path&gt; &lt;file&gt;": .csv or text).</summary>
        public string CompareFolderReport = "";
        /// <summary>Open the "Cleanup" window once the view is scanned ("cleanup").</summary>
        public bool ShowCleanup;
        /// <summary>Write the cleanup categories there once the view is scanned ("cleanupreport &lt;file&gt;"); nothing is deleted.</summary>
        public string CleanupReport = "";
    }

    /// <summary>"measure": record the drives' free space for the forecast and exit, without a window (for Task Scheduler).</summary>
    public static bool IsMeasure(string[] args) => args.Length > 0 && args.All(a => a.Equals("measure", StringComparison.OrdinalIgnoreCase));

    /// <summary>"C:\"" (a quoted drive root whose backslash escaped the closing quote) → "C:\"; other paths lose a stray trailing quote.</summary>
    internal static string RootFix(string p)
    {
        p = p.TrimEnd('"');
        return p.Length == 2 && p[1] == ':' ? p + "\\" : p;
    }

    public static (List<ViewRequest> requests, bool autoClose, bool help) Parse(string[] args)
    {
        var requests = new List<ViewRequest>();
        bool autoClose = false, help = false;
        ViewRequest? last = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (a.ToLowerInvariant())
            {
                case "help": case "/help": case "-help": case "--help": case "/?":
                    help = true; break;
                case "scan":
                    // a drive root quoted as "C:\" reaches us as C:" (the backslash escaped the quote): the stray quote goes
                    foreach (var p in Next().Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        requests.Add(last = new ViewRequest { Path = RootFix(p) });
                    break;
                case "load":
                    foreach (var p in Next().Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        requests.Add(last = new ViewRequest { Path = p, IsLoad = true });
                    break;
                case "filter":
                    if (last != null) last.Filter = Next(); else Next();
                    break;
                case "export":
                {
                    string name = Next(), path = Next();
                    last?.Exports.Add((name, path));
                    break;
                }
                case "compare":
                    if (last != null) last.CompareWith = Next(); else Next();
                    break;
                case "apps":
                    if (last != null) last.ShowApps = true;
                    break;
                case "appsreport":
                    if (last != null) last.AppsReport = Next(); else Next();
                    break;
                case "games":
                    if (last != null) last.ShowGames = true;
                    break;
                case "gamesreport":
                    if (last != null) last.GamesReport = Next(); else Next();
                    break;
                case "dev":
                    if (last != null) last.ShowDev = true;
                    break;
                case "devreport":
                    if (last != null) last.DevReport = Next(); else Next();
                    break;
                case "forecast":
                    if (last != null) last.ShowForecast = true;
                    break;
                case "cleanup":
                    if (last != null) last.ShowCleanup = true;
                    break;
                case "duplicates":
                    if (last != null) last.ShowDuplicates = true;
                    break;
                case "duplicatesreport":
                    if (last != null) last.DuplicatesReport = Next(); else Next();
                    break;
                case "comparefolder":
                    if (last != null) last.CompareFolderWith = Next(); else Next();
                    break;
                case "comparefolderreport":
                {
                    var other = Next(); var file = Next();
                    if (last != null) { last.CompareFolderWith = other; last.CompareFolderReport = file; }
                    break;
                }
                case "cleanupreport":
                    if (last != null) last.CleanupReport = Next(); else Next();
                    break;
                case "autoclose":
                    autoClose = true; break;
                default:
                    // a bare path is treated like "scan <path>" for drag&drop onto the exe
                    if (Directory.Exists(a)) requests.Add(last = new ViewRequest { Path = a });
                    else if (File.Exists(a)) requests.Add(last = new ViewRequest { Path = a, IsLoad = true });
                    break;
            }
        }
        return (requests, autoClose, help);
    }
}

