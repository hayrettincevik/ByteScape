using System.Diagnostics;
using System.Text;
using ByteScape.Core.Model;
using ByteScape.Core.Util;

namespace ByteScape.Core.Dev;

/// <summary>
/// What makes a folder a project of some ecosystem (marker files) and which of its sub folders the tools rebuild.
/// Markers are file names or "*.ext"; artifact names may end in '*' (prefix). <see cref="NeedsFile"/>: the artifact
/// must contain that file (a Python environment has pyvenv.cfg).
/// </summary>
internal sealed record DevRule(string Ecosystem, string[] Markers, string[] Artifacts, string Rebuild, string? NeedsFile = null)
{
    public bool IsArtifact(string folder)
    {
        foreach (var a in Artifacts)
            if (a[^1] == '*' ? folder.StartsWith(a[..^1], StringComparison.OrdinalIgnoreCase) : folder.Equals(a, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>A folder of a project that the build tools recreate (node_modules, bin, obj, target, .venv...).</summary>
public sealed class DevArtifact
{
    public required string Path { get; init; }
    public required FsNode Node { get; init; }
    /// <summary>Folder name as the ecosystem calls it ("node_modules", "target").</summary>
    public required string Name { get; init; }
    public required string Ecosystem { get; init; }
    /// <summary>How it comes back ("npm install", "dotnet build").</summary>
    public required string Rebuild { get; init; }
    /// <summary>A caution when rebuilding may not give the same result ("no lock file").</summary>
    public string Note { get; init; } = "";
    public long Bytes { get; init; }
}

/// <summary>A project with rebuildable folders: where it is, what it is, when it was last worked on.</summary>
public sealed class DevProject
{
    public required string Path { get; init; }
    public required ContainerNode Node { get; init; }
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path;
    /// <summary>Ecosystems found ("Node.js", ".NET"), in the order found.</summary>
    public List<string> Kinds { get; } = new();
    public List<DevArtifact> Artifacts { get; } = new();
    public long ArtifactBytes => Artifacts.Sum(a => a.Bytes);
    /// <summary>Everything else in the project folder (sources, assets, .git).</summary>
    public long SourceBytes { get; internal set; }
    /// <summary>Newest file outside the rebuildable folders and .git (UTC); default when there is none.</summary>
    public DateTime LastActivity { get; internal set; }
    public bool IsGitRepo { get; internal set; }
    /// <summary>A repository or solution / workspace root: the projects inside it are listed as part of it.</summary>
    internal bool IsGroup { get; init; }
    public string KindsText => string.Join(", ", Kinds);
}

public sealed class DevScanResult
{
    public required string RootPath { get; init; }
    /// <summary>Projects with something to rebuild, most rebuildable bytes first.</summary>
    public required IReadOnlyList<DevProject> Projects { get; init; }
    public long ArtifactBytes => Projects.Sum(p => p.ArtifactBytes);
    public TimeSpan Elapsed { get; init; }

    /// <summary>Projects nobody touched for longer than <paramref name="span"/>.</summary>
    public IEnumerable<DevProject> Untouched(TimeSpan span, DateTime nowUtc) =>
        Projects.Where(p => p.LastActivity == default || nowUtc - p.LastActivity > span);

    public Dictionary<FsNode, bool> SpotlightFor(IEnumerable<DevArtifact> artifacts)
    {
        var d = new Dictionary<FsNode, bool>(ReferenceEqualityComparer.Instance);
        foreach (var a in artifacts) d[a.Node] = true;
        return d;
    }
}

/// <summary>
/// "Developer leftovers": finds projects in a scanned tree (package.json, *.csproj, Cargo.toml, pom.xml, build.gradle,
/// pyproject.toml, CMakeLists.txt, pubspec.yaml, Unity and Unreal projects...) and the folders their tools rebuild
/// (node_modules, bin/obj, target, build, .venv, Library...), with the last time the project itself changed. Folders of
/// installed programs are never looked at (an Electron app's node_modules is part of the app). ByteScape deletes nothing.
/// Meant for a background thread: the tree lock is taken per folder.
/// </summary>
public static class DevProjects
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    internal static readonly DevRule[] Rules =
    {
        new("Node.js", new[] { "package.json" }, new[] { "node_modules", ".next", ".nuxt", ".svelte-kit", ".parcel-cache", ".turbo", ".angular", ".expo" }, "npm install"),
        new(".NET", new[] { "*.csproj", "*.fsproj", "*.vbproj" }, new[] { "bin", "obj" }, "dotnet build"),
        new("Visual Studio", new[] { "*.sln", "*.slnx" }, new[] { ".vs" }, /*loc*/ "Visual Studio recreates it (close it first)"),
        new("Rust", new[] { "Cargo.toml" }, new[] { "target" }, "cargo build"),
        new("Maven", new[] { "pom.xml" }, new[] { "target" }, "mvn package"),
        new("Gradle", new[] { "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts" }, new[] { "build", ".gradle" }, "gradle build"),
        new("Python", new[] { "pyproject.toml", "requirements.txt", "setup.py", "setup.cfg", "Pipfile", "poetry.lock", "uv.lock" }, new[] { ".venv", "venv", "env" },
            /*loc*/ "python -m venv, then pip install -r requirements.txt", NeedsFile: "pyvenv.cfg"),
        new("Python", new[] { "pyproject.toml", "requirements.txt", "setup.py", "setup.cfg", "Pipfile", "tox.ini" }, new[] { ".tox", ".pytest_cache", ".mypy_cache", ".ruff_cache", "__pycache__" }, /*loc*/ "recreated by the tool"),
        new("CMake", new[] { "CMakeLists.txt" }, new[] { "build", "out", "cmake-build-*" }, "cmake --build"),
        new("Flutter / Dart", new[] { "pubspec.yaml" }, new[] { "build", ".dart_tool" }, "flutter pub get / flutter build"),
        new("Unreal", new[] { "*.uproject" }, new[] { "Intermediate", "DerivedDataCache" }, /*loc*/ "Unreal rebuilds it (shaders compile again)"),
        new("Elixir", new[] { "mix.exs" }, new[] { "_build", "deps" }, "mix deps.get, mix compile"),
        new("Haskell", new[] { "stack.yaml" }, new[] { ".stack-work" }, "stack build"),
        new("Haskell", new[] { "*.cabal", "cabal.project" }, new[] { "dist-newstyle" }, "cabal build"),
        new("Swift", new[] { "Package.swift" }, new[] { ".build" }, "swift build"),
        new("Zig", new[] { "build.zig" }, new[] { "zig-cache", ".zig-cache", "zig-out" }, "zig build"),
        new("Terraform", new[] { "*.tf" }, new[] { ".terraform" }, "terraform init"),
        new("PHP", new[] { "composer.json" }, new[] { "vendor" }, "composer install"),
    };

    // marker lookups: exact file names and "*.ext" patterns, each to the rules they start
    private static readonly Dictionary<string, List<DevRule>> MarkerNames = Index(m => m[0] != '*', m => m);
    private static readonly Dictionary<string, List<DevRule>> MarkerExtensions = Index(m => m[0] == '*', m => m.Substring(1));

    private static Dictionary<string, List<DevRule>> Index(Func<string, bool> which, Func<string, string> key)
    {
        var d = new Dictionary<string, List<DevRule>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Rules)
            foreach (var m in r.Markers.Where(which))
                (d.TryGetValue(key(m), out var l) ? l : d[key(m)] = new()).Add(r);
        return d;
    }

    /// <summary>Unity projects are recognised by their folders, not a file.</summary>
    internal static readonly DevRule Unity = new("Unity", new[] { "Assets", "ProjectSettings" }, new[] { "Library", "Temp", "obj", "Logs" }, /*loc*/ "Unity rebuilds it when the project opens (takes a while)");

    /// <summary>Files that make a folder the root of several projects (its nested projects are listed as part of it).</summary>
    private static readonly HashSet<string> WorkspaceFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "pnpm-workspace.yaml", "lerna.json", "nx.json", "turbo.json", "rush.json", "go.work", "settings.gradle", "settings.gradle.kts", "Cargo.toml",
    };

    /// <summary>Never looked into: the system, installed programs, their data, package caches (Cache Radar knows those).</summary>
    private static readonly HashSet<string> SkipAnywhere = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "AppData", "site-packages", "dist-packages", "pkgs", "scoop", "$Recycle.Bin", "System Volume Information", "WindowsApps",
    };
    private static readonly HashSet<string> SkipAtDriveRoot = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Recovery", "PerfLogs", "$WinREAgent", "Config.Msi",
    };

    private static readonly string[] NodeLockFiles = { "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "bun.lockb", "bun.lock", "npm-shrinkwrap.json" };
    private static readonly string[] PythonLockFiles = { "requirements.txt", "poetry.lock", "Pipfile.lock", "uv.lock", "pdm.lock" };

    public static DevScanResult Compute(ContainerNode viewRoot, Func<FsNode, long> sizeOf, CancellationToken token, object? sync = null)
    {
        var sw = Stopwatch.StartNew();
        var root = viewRoot is DriveNode { IsSnapshotFrame: true } f && f.ChildCount == 1 && f.Children[0] is ContainerNode inner ? inner : viewRoot;
        string rootPath;
        try { rootPath = root.RealPath(); } catch { rootPath = ""; }
        var projects = new List<DevProject>();

        List<FsNode> Kids(ContainerNode c)
        {
            if (sync == null) return c.ChildrenSnapshot();
            lock (sync) return c.ChildrenSnapshot();
        }

        // one folder of the walk: newest file in its subtree (rebuildable folders and .git left out), default for none
        Frame Enter(ContainerNode c, string path, DevProject? owner, bool driveRoot)
        {
            token.ThrowIfCancellationRequested();
            var kids = Kids(c);
            var found = Classify(kids, path, c, sync);
            // a repository or a solution / workspace groups the projects inside it (not a whole drive or user profile)
            bool git = kids.Any(k => k is ContainerNode && k.Name.Equals(".git", OIC));
            bool group = !driveRoot && !IsProfileRoot(path) && (git || kids.Any(k => k is FileNode && IsWorkspaceFile(k.Name)));
            DevProject? mine = null;
            if (found.Count > 0 || group)
            {
                if (owner is { IsGroup: true }) mine = owner;
                else
                {
                    mine = new DevProject { Path = path, Node = c, IsGitRepo = git, IsGroup = group };
                    projects.Add(mine);
                }
                foreach (var (rule, art, note, rebuild) in found)
                {
                    if (!mine.Kinds.Contains(rule.Ecosystem)) mine.Kinds.Add(rule.Ecosystem);
                    mine.Artifacts.Add(new DevArtifact { Path = System.IO.Path.Combine(path, art.Name), Node = art, Name = art.Name, Ecosystem = rule.Ecosystem, Rebuild = rebuild, Note = note, Bytes = sizeOf(art) });
                }
            }
            var current = mine ?? owner;
            var artifacts = found.Count == 0 ? null : new HashSet<FsNode>(found.Select(x => (FsNode)x.Art), ReferenceEqualityComparer.Instance);
            return new Frame { Node = c, Path = path, DriveRoot = driveRoot, Kids = kids, Mine = mine, Current = current, Artifacts = artifacts };
        }

        // depth first with a stack of its own (no recursion: deep folder chains do not exhaust the thread's stack)
        if (rootPath.Length > 0 && !IsExcluded(rootPath))
        {
            var stack = new Stack<Frame>();
            stack.Push(Enter(root, rootPath, null, root is DriveNode { IsDrive: true } || DataCacheIsDriveRoot(rootPath)));
            while (stack.Count > 0)
            {
                var fr = stack.Peek();
                if (fr.Next < fr.Kids.Count)
                {
                    var k = fr.Kids[fr.Next++];
                    if (k is FileNode fn) { if (fr.Current != null && fn.Modified > fr.Newest) fr.Newest = fn.Modified; continue; }
                    if (k is not ContainerNode kc || kc.IsSynthetic || (fr.Artifacts != null && fr.Artifacts.Contains(kc))) continue;
                    if (kc.Name.Length > 0 && kc.Name[0] == '.' || SkipAnywhere.Contains(kc.Name) || (fr.DriveRoot && SkipAtDriveRoot.Contains(kc.Name))) continue;
                    var c = fr.Node;
                    if (kc.Name.Equals("mod", OIC) && c.Name.Equals("pkg", OIC)) continue;       // the Go module cache
                    if (kc.Name.Equals("app", OIC) && c.Name.Equals("resources", OIC)) continue; // an Electron program's own code
                    if (kc.Name.Equals("Programs", OIC) && c.Name.Equals("Local", OIC)) continue; // AppData\Local\Programs
                    stack.Push(Enter(kc, System.IO.Path.Combine(fr.Path, kc.Name), fr.Current, false));
                    continue;
                }
                stack.Pop();
                if (fr.Mine != null && ReferenceEquals(fr.Mine.Node, fr.Node))
                {
                    fr.Mine.LastActivity = fr.Newest;
                    fr.Mine.SourceBytes = Math.Max(0, sizeOf(fr.Node) - fr.Mine.ArtifactBytes);
                }
                if (stack.Count > 0 && fr.Newest > stack.Peek().Newest) stack.Peek().Newest = fr.Newest;
            }
        }
        projects.RemoveAll(p => p.ArtifactBytes <= 0);
        foreach (var p in projects) p.Artifacts.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        projects.Sort((a, b) => b.ArtifactBytes.CompareTo(a.ArtifactBytes));
        return new DevScanResult { RootPath = rootPath, Projects = projects, Elapsed = sw.Elapsed };
    }

    /// <summary>A folder being walked by <see cref="Compute"/>.</summary>
    private sealed class Frame
    {
        public required ContainerNode Node;
        public required string Path;
        public bool DriveRoot;
        public required List<FsNode> Kids;
        public int Next;
        public DevProject? Mine, Current;
        public HashSet<FsNode>? Artifacts;
        public DateTime Newest;
    }

    private static bool DataCacheIsDriveRoot(string path) => path.Length <= 3 && path.Length >= 2 && path[1] == ':';

    private static bool IsWorkspaceFile(string name) => WorkspaceFiles.Contains(name) || name.EndsWith(".sln", OIC) || name.EndsWith(".slnx", OIC);

    /// <summary>C:\Users\&lt;name&gt;: a stray package.json or .git there must not swallow every project of the user.</summary>
    internal static bool IsProfileRoot(string path)
    {
        var segs = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return segs.Length == 3 && segs[1].Equals("Users", OIC);
    }

    /// <summary>The rebuildable folders directly inside a folder, with the rule that makes them so.</summary>
    private static List<(DevRule Rule, ContainerNode Art, string Note, string Rebuild)> Classify(List<FsNode> kids, string path, ContainerNode folder, object? sync)
    {
        var result = new List<(DevRule, ContainerNode, string, string)>();
        List<DevRule>? rules = null;
        foreach (var k in kids)
        {
            if (k is not FileNode) continue;
            if (!MarkerNames.TryGetValue(k.Name, out var byName))
            {
                int dot = k.Name.LastIndexOf('.');
                if (dot < 0 || !MarkerExtensions.TryGetValue(k.Name.Substring(dot), out byName)) continue;
            }
            foreach (var r in byName) if (!(rules ??= new()).Contains(r)) rules.Add(r);
        }
        bool unity = kids.Any(k => k is ContainerNode && k.Name.Equals("Assets", OIC)) && kids.Any(k => k is ContainerNode && k.Name.Equals("ProjectSettings", OIC));
        if (unity) (rules ??= new()).Add(Unity);
        if (rules == null) return result;
        foreach (var k in kids)
        {
            if (k is not ContainerNode kc || kc.IsSynthetic) continue;
            foreach (var r in rules)
            {
                if (!r.IsArtifact(kc.Name)) continue;
                if (r.NeedsFile != null)
                {
                    FsNode? needed;
                    if (sync == null) needed = kc.FindChild(r.NeedsFile); else lock (sync) needed = kc.FindChild(r.NeedsFile);
                    if (needed is not FileNode) continue;
                }
                var (rebuild, note) = RebuildOf(r, kc.Name, kids);
                result.Add((r, kc, note, rebuild));
                break;   // one rule per folder ("target" of Rust or Maven, whichever matched first)
            }
        }
        return result;
    }

    private static bool Has(List<FsNode> kids, string file) => kids.Any(k => k is FileNode && k.Name.Equals(file, OIC));

    private static (string Rebuild, string Note) RebuildOf(DevRule r, string artifact, List<FsNode> kids)
    {
        if (r.Ecosystem == "Node.js")
        {
            if (!artifact.Equals("node_modules", OIC))
                return (artifact.ToLowerInvariant() switch
                {
                    ".next" => "next build / next dev", ".nuxt" => "nuxt build / nuxt dev", ".svelte-kit" => "svelte-kit sync / vite build",
                    ".angular" => "ng build (cache)", ".expo" => "expo start", _ => T("recreated by the build tool"),
                }, "");
            string install = Has(kids, "pnpm-lock.yaml") ? "pnpm install" : Has(kids, "yarn.lock") ? "yarn install"
                           : Has(kids, "bun.lockb") || Has(kids, "bun.lock") ? "bun install" : Has(kids, "package-lock.json") || Has(kids, "npm-shrinkwrap.json") ? "npm ci" : "npm install";
            return (install, NodeLockFiles.Any(l => Has(kids, l)) ? "" : T("No lock file: a new install may bring newer package versions"));
        }
        if (r.NeedsFile == "pyvenv.cfg")
            return (Has(kids, "requirements.txt") ? F($"python -m venv {artifact}, then pip install -r requirements.txt")
                    : Has(kids, "poetry.lock") ? "poetry install" : Has(kids, "uv.lock") ? "uv sync" : Has(kids, "Pipfile.lock") ? "pipenv install" : T(r.Rebuild),
                    PythonLockFiles.Any(l => Has(kids, l)) ? "" : T("No requirements file: write down the installed packages first (pip freeze)"));
        return (T(r.Rebuild), "");
    }

    /// <summary>Paths that belong to installed programs, their data or package caches: never a project.</summary>
    public static bool IsExcluded(string realPath)
    {
        var segs = realPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < segs.Length; i++)
        {
            var s = segs[i];
            if (s.Length > 0 && s[0] == '.' && i < segs.Length - 1) return true;   // inside .vscode, .cargo, .nuget...
            // AppData is only skipped when a walk comes from above it: a view opened inside it (Temp) is searched
            if (SkipAnywhere.Contains(s) && i < segs.Length - 1 && !s.Equals("AppData", OIC)) return true;
            if (i == 1 && SkipAtDriveRoot.Contains(s)) return true;
            if (s.Equals("app", OIC) && segs[i - 1].Equals("resources", OIC)) return true;
            if (s.Equals("Programs", OIC) && segs[i - 1].Equals("Local", OIC)) return true;   // AppData\Local\Programs: installed programs
        }
        return false;
    }

    /// <summary>
    /// What a folder is when it is a rebuildable folder of a project (for the details panel, tooltip and menu): cheap,
    /// only the folder's parent is looked at. Null for anything else.
    /// </summary>
    public static (string Project, string ProjectPath, string Ecosystem, string Rebuild)? ArtifactOf(FsNode n, object? sync = null)
    {
        if (n is not ContainerNode c || c.IsSynthetic || c.Parent is not ContainerNode parent) return null;
        bool maybe = Rules.Any(r => r.IsArtifact(c.Name)) || Unity.IsArtifact(c.Name);
        if (!maybe) return null;
        string path;
        try { path = parent.RealPath(); } catch { return null; }
        if (IsExcluded(path + "\\x")) return null;
        List<FsNode> kids;
        if (sync == null) kids = parent.ChildrenSnapshot(); else lock (sync) kids = parent.ChildrenSnapshot();
        foreach (var (rule, art, _, rebuild) in Classify(kids, path, parent, sync))
            if (ReferenceEquals(art, c)) return (System.IO.Path.GetFileName(path), path, rule.Ecosystem, rebuild);
        return null;
    }

    /// <summary>Plain text / tab separated report (window "Copy list" / "Save", command line "devreport").</summary>
    public static string ToText(DevScanResult r, DateTime nowUtc)
    {
        var sb = new StringBuilder();
        var stale = r.Untouched(TimeSpan.FromDays(182), nowUtc).ToList();
        sb.AppendLine(Branding.Name + ": " + T("developer leftovers"));
        sb.AppendLine(T("View:") + " " + (r.RootPath.Length == 2 ? r.RootPath + "\\" : r.RootPath));
        sb.AppendLine(P(r.Projects.Count, "Rebuildable: {1} in {0} project", "Rebuildable: {1} in {0} projects", Format.Size(r.ArtifactBytes)));
        sb.AppendLine(P(stale.Count, "Untouched for 6 months: {0} project, {1} rebuildable", "Untouched for 6 months: {0} projects, {1} rebuildable", Format.Size(stale.Sum(p => p.ArtifactBytes))));
        sb.AppendLine();
        sb.AppendLine("Project\tKind\tRebuildable\tSources\tLast change\tFolder");
        foreach (var p in r.Projects)
        {
            sb.Append(p.Name).Append('\t').Append(p.KindsText).Append('\t').Append(Format.Size(p.ArtifactBytes)).Append('\t').Append(Format.Size(p.SourceBytes)).Append('\t')
              .Append(p.LastActivity == default ? "—" : Format.Date(p.LastActivity)).Append('\t').AppendLine(p.Path);
            foreach (var a in p.Artifacts)
                sb.Append("\t- ").Append(System.IO.Path.GetRelativePath(p.Path, a.Path)).Append('\t').Append(Format.Size(a.Bytes)).Append('\t').Append(a.Rebuild)
                  .Append(a.Note.Length > 0 ? " (" + a.Note + ")" : "").AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>PowerShell lines that delete the given folders (copied for the user to run; ByteScape itself deletes nothing).</summary>
    public static string DeleteCommands(IEnumerable<DevArtifact> artifacts) =>
        string.Join(Environment.NewLine, artifacts.Select(a => "Remove-Item -LiteralPath '" + a.Path.Replace("'", "''") + "' -Recurse -Force   # " + Format.Size(a.Bytes) + ", then: " + a.Rebuild));
}
