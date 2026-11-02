using System.Diagnostics;
using ByteScape.Core.Config;
using ByteScape.Core.Dev;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using Xunit;
using Xunit.Abstractions;

namespace ByteScape.Tests;

/// <summary>Developer leftovers: projects, their rebuildable folders, grouping, exclusions and the last change of each project.</summary>
[Collection("config")]
public class DevTests
{
    private readonly ITestOutputHelper _out;
    public DevTests(ITestOutputHelper o) => _out = o;

    internal static readonly string Dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "dev");
    private static readonly DateTime Old = DateTime.UtcNow.AddDays(-400);
    private static readonly DateTime Recent = DateTime.UtcNow.AddDays(-2);

    private static void Put(string rel, int bytes, DateTime? modified = null)
    {
        var p = Path.Combine(Dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllBytes(p, new byte[bytes]);
        System.IO.File.SetLastWriteTimeUtc(p, modified ?? Recent);
    }

    /// <summary>
    /// An old Node.js app with a lock file, a new one without, a .NET solution in a git repository (two projects), a Rust
    /// crate, a Python project with a virtual environment (and an "env" folder that is not one), a Unity project, a stray
    /// node_modules without package.json, an Electron program's bundled app and a VS Code extension (never projects).
    /// </summary>
    internal static void BuildLayout()
    {
        if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        Put(@"webapp\package.json", 800, Old); Put(@"webapp\package-lock.json", 50_000, Old); Put(@"webapp\src\index.js", 3000, Old);
        Put(@"webapp\node_modules\react\index.js", 400_000, Recent);          // installed again lately: not the project's activity
        Put(@"webapp\.next\cache\webpack.pack", 120_000, Recent);
        Put(@"webapp2\package.json", 700); Put(@"webapp2\index.js", 1000); Put(@"webapp2\node_modules\left-pad\index.js", 30_000);
        Directory.CreateDirectory(Path.Combine(Dir, @"dotnet\.git\objects"));
        Put(@"dotnet\App.sln", 2000); Put(@"dotnet\App\App.csproj", 900); Put(@"dotnet\App\Program.cs", 1500);
        Put(@"dotnet\App\bin\Debug\net8.0\App.dll", 200_000); Put(@"dotnet\App\obj\project.assets.json", 60_000);
        Put(@"dotnet\Lib\Lib.csproj", 700); Put(@"dotnet\Lib\bin\Debug\Lib.dll", 90_000);
        Put(@"rust\Cargo.toml", 300); Put(@"rust\src\main.rs", 800); Put(@"rust\target\debug\app.exe", 700_000);
        Put(@"py\requirements.txt", 100); Put(@"py\main.py", 500); Put(@"py\.venv\pyvenv.cfg", 100); Put(@"py\.venv\Lib\site-packages\requests\api.py", 150_000);
        Put(@"py\env\settings.ini", 200);                                    // a folder named env, not an environment
        Put(@"unity\Assets\Scene.unity", 5000); Put(@"unity\ProjectSettings\ProjectSettings.asset", 1000); Put(@"unity\Library\ArtifactDB", 300_000);
        Put(@"loose\node_modules\x\index.js", 10_000);                       // no package.json: nothing rebuilds it
        Put(@"Tools\Editor\resources\app\package.json", 500); Put(@"Tools\Editor\resources\app\node_modules\y\index.js", 80_000);
        Put(@"home\.vscode\extensions\ext\package.json", 500); Put(@"home\.vscode\extensions\ext\node_modules\z\index.js", 40_000);
        Put(@"o'brien\package.json", 100); Put(@"o'brien\node_modules\q.js", 5000);
    }

    internal static ScanTree Scan()
    {
        AppConfig.Replace(new AppConfig { WatchDog = false });
        var tree = new ScanTree(DataCache.CreateRoot(Dir), live: true);
        tree.StartMasterScan();
        var sw = Stopwatch.StartNew();
        while (tree.IsMasterScanning && sw.ElapsedMilliseconds < 20000) Thread.Sleep(5);
        Assert.Equal(ScanState.Completed, tree.MasterState);
        return tree;
    }

    [Fact]
    public void Projects_TheirRebuildableFolders_AndLastChange()
    {
        try
        {
            BuildLayout();
            var tree = Scan();
            var r = DevProjects.Compute(tree.Root, n => n.LogicalSize, CancellationToken.None, tree.Sync);
            _out.WriteLine(DevProjects.ToText(r, DateTime.UtcNow));
            Assert.Equal(new[] { "rust", "webapp", "dotnet", "unity", "py", "webapp2", "o'brien" }, r.Projects.Select(p => p.Name));   // most rebuildable first

            var web = r.Projects.Single(p => p.Name == "webapp");
            Assert.Equal(new[] { "node_modules", ".next" }, web.Artifacts.Select(a => a.Name));
            Assert.Equal("npm ci", web.Artifacts[0].Rebuild);                  // a lock file: exact versions come back
            Assert.Equal("", web.Artifacts[0].Note);
            Assert.Equal(520_000, web.ArtifactBytes);
            Assert.InRange((DateTime.UtcNow - web.LastActivity).TotalDays, 399, 401);   // node_modules' recent files do not count
            Assert.Equal(53_800, web.SourceBytes);

            var web2 = r.Projects.Single(p => p.Name == "webapp2");
            Assert.Equal("npm install", web2.Artifacts[0].Rebuild);
            Assert.Contains("No lock file", web2.Artifacts[0].Note);

            // a solution in a git repository is one project with the folders of both of its projects
            var dotnet = r.Projects.Single(p => p.Name == "dotnet");
            Assert.True(dotnet.IsGitRepo);
            Assert.Equal(new[] { ".NET" }, dotnet.Kinds);
            Assert.Equal(new[] { @"App\bin", @"Lib\bin", @"App\obj" }, dotnet.Artifacts.Select(a => Path.GetRelativePath(dotnet.Path, a.Path)));
            Assert.All(dotnet.Artifacts, a => Assert.Equal("dotnet build", a.Rebuild));

            Assert.Equal("cargo build", r.Projects.Single(p => p.Name == "rust").Artifacts.Single().Rebuild);
            var py = r.Projects.Single(p => p.Name == "py");
            Assert.Equal(".venv", py.Artifacts.Single().Name);                 // "env" has no pyvenv.cfg
            Assert.Equal("python -m venv .venv, then pip install -r requirements.txt", py.Artifacts.Single().Rebuild);
            Assert.Equal(new[] { "Library" }, r.Projects.Single(p => p.Name == "unity").Artifacts.Select(a => a.Name));

            // never projects: node_modules without package.json, a program's bundled app, editor extensions
            Assert.DoesNotContain(r.Projects, p => p.Path.Contains("loose") || p.Path.Contains(@"resources\app") || p.Path.Contains(".vscode"));

            Assert.Equal(new[] { "webapp" }, r.Untouched(TimeSpan.FromDays(182), DateTime.UtcNow).Select(p => p.Name));
            Assert.Equal(7, r.Untouched(TimeSpan.Zero, DateTime.UtcNow.AddDays(1)).Count());
            var spot = r.SpotlightFor(dotnet.Artifacts);
            Assert.Equal(3, spot.Count);

            // what a single folder is, for the details panel / tooltip / menu
            var nm = GameTests_Node(tree.Root, @"webapp\node_modules");
            var what = DevProjects.ArtifactOf(nm, tree.Sync)!.Value;
            Assert.Equal(("webapp", "Node.js", "npm ci"), (what.Project, what.Ecosystem, what.Rebuild));
            Assert.Equal(Path.Combine(Dir, "webapp"), what.ProjectPath);
            Assert.Null(DevProjects.ArtifactOf(GameTests_Node(tree.Root, @"loose\node_modules"), tree.Sync));
            Assert.Null(DevProjects.ArtifactOf(GameTests_Node(tree.Root, @"Tools\Editor\resources\app\node_modules"), tree.Sync));
            Assert.Null(DevProjects.ArtifactOf(GameTests_Node(tree.Root, @"py\env"), tree.Sync));
            Assert.Null(DevProjects.ArtifactOf(GameTests_Node(tree.Root, @"webapp\src"), tree.Sync));
            Assert.Equal(".NET", DevProjects.ArtifactOf(GameTests_Node(tree.Root, @"dotnet\Lib\bin"), tree.Sync)!.Value.Ecosystem);

            // copied commands: literal paths, quotes doubled, with the rebuild command
            var cmd = DevProjects.DeleteCommands(r.Projects.Single(p => p.Name == "o'brien").Artifacts);
            Assert.Equal("Remove-Item -LiteralPath '" + Path.Combine(Dir, "o''brien", "node_modules") + "' -Recurse -Force   # 5KB, then: npm install", cmd);
            Assert.Contains("Untouched for 6 months: 1 project,", DevProjects.ToText(r, DateTime.UtcNow));

            // cancellation is honoured
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => DevProjects.Compute(tree.Root, n => n.LogicalSize, cts.Token, tree.Sync));
        }
        finally { AppConfig.Replace(new AppConfig()); try { Directory.Delete(Dir, true); } catch { } }
    }

    [Fact]
    public void Exclusions_ProgramsCachesAndProfileRoots()
    {
        Assert.True(DevProjects.IsExcluded(@"C:\Program Files\App\node_modules"));
        Assert.True(DevProjects.IsExcluded(@"C:\Windows\x"));
        Assert.True(DevProjects.IsExcluded(@"C:\Users\ali\AppData\Local\Programs\x\package.json"));
        Assert.True(DevProjects.IsExcluded(@"C:\Users\ali\.cargo\registry\src\crate"));
        Assert.True(DevProjects.IsExcluded(@"D:\scoop\apps\vscode\current"));
        Assert.True(DevProjects.IsExcluded(@"C:\Apps\Tool\resources\app\node_modules"));
        Assert.False(DevProjects.IsExcluded(@"C:\Users\ali\source\repos\web"));
        Assert.True(DevProjects.IsExcluded(@"D:\Program Files\x"));                  // any drive
        Assert.False(DevProjects.IsExcluded(@"C:\dev\Windows\project"));
    }

    [Fact]
    public void ProfileRoots_NeverGroupTheProjectsInside()
    {
        // an accidental npm install or a dotfiles repository in C:\Users\<name> must not swallow every project of the user
        Assert.True(DevProjects.IsProfileRoot(@"C:\Users\ali"));
        Assert.False(DevProjects.IsProfileRoot(@"C:\Users\ali\code"));
        Assert.False(DevProjects.IsProfileRoot(@"C:\Users"));
        Assert.False(DevProjects.IsProfileRoot(@"C:\Work\ali"));
    }

    private static FsNode GameTests_Node(ContainerNode root, string rel) => GameTests.Node(root, rel);
}
