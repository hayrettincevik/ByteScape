using System.Diagnostics;
using ByteScape.Core.Config;
using ByteScape.Core.Games;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using Xunit;
using Xunit.Abstractions;

namespace ByteScape.Tests;

/// <summary>Game library: Valve KeyValues, Steam / Epic / GOG sources, numbered folder names and the audit of a scanned tree.</summary>
[Collection("config")]
public class GameTests
{
    private readonly ITestOutputHelper _out;
    public GameTests(ITestOutputHelper o) => _out = o;

    internal static readonly string Dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "games");
    internal static readonly DateTime Now = DateTime.UtcNow;
    private static long Unix(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds();
    private static string Esc(string path) => path.Replace("\\", "\\\\");

    private static void Put(string rel, int bytes) { var p = Path.Combine(Dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); System.IO.File.WriteAllBytes(p, new byte[bytes]); }
    private static void Text(string rel, string text) { var p = Path.Combine(Dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); System.IO.File.WriteAllText(p, text); }

    private static string Acf(string id, string name, string dir, long size, DateTime? updated = null, DateTime? played = null) => $$"""
        "AppState"
        {
            "appid"		"{{id}}"
            "Universe"		"1"
            "name"		"{{name}}"
            "StateFlags"		"4"
            "installdir"		"{{dir}}"
            "LastUpdated"		"{{(updated is { } u ? Unix(u) : 0)}}"
            "LastPlayed"		"{{(played is { } p ? Unix(p) : 0)}}"
            "SizeOnDisk"		"{{size}}"
            "InstalledDepots"
            {
                "{{id}}1" { "manifest" "123" "size" "{{size}}" }
            }
        }
        """;

    /// <summary>
    /// Fake launchers under %TEMP%: Steam with two libraries (Florence and a tool in the main one, ELDEN RING in the second),
    /// play history, caches and saves of installed and uninstalled games; Epic with one game, a DLC, an unfinished install
    /// and a broken manifest; one GOG game.
    /// </summary>
    internal static GameSources BuildLayout()
    {
        if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        string steam = Path.Combine(Dir, "Steam"), lib2 = Path.Combine(Dir, "Lib2");
        Text(@"Steam\steamapps\libraryfolders.vdf", $$"""
            "libraryfolders"
            {
                "0" { "path" "{{Esc(steam).ToLowerInvariant()}}" "label" "" "apps" { "1102130" "300000" } }
                "1"
                {
                    "path"		"{{Esc(lib2.Replace('\\', '/'))}}"
                    "apps" { "1245620" "1000000" }
                }
            }
            """);
        Text(@"Steam\steamapps\appmanifest_1102130.acf", Acf("1102130", "Florence", "Florence", 300000, updated: Now.AddDays(-900)));
        Text(@"Steam\steamapps\appmanifest_228980.acf", Acf("228980", "Steamworks Common Redistributables", "Steamworks Shared", 50000));
        Text(@"Lib2\steamapps\appmanifest_1245620.acf", Acf("1245620", "ELDEN RING", "ELDEN RING", 1000000, updated: Now.AddDays(-20), played: Now.AddDays(-3)));
        Put(@"Steam\steamapps\common\Florence\Florence.exe", 300000);
        Put(@"Steam\steamapps\common\Steamworks Shared\_CommonRedist\vcredist.exe", 50000);
        Put(@"Steam\steamapps\common\Overcooked\Save\slot1.dat", 20000);         // uninstalled, files stayed
        Put(@"Steam\steamapps\common\Steam Controller Configs\1\cfg.vdf", 5000);    // Steam's own, not a game
        Put(@"Steam\steamapps\shadercache\1102130\fozpipelinesv6\cache.foz", 40000);
        Put(@"Steam\steamapps\shadercache\206440\cache.foz", 30000);              // played, uninstalled
        Directory.CreateDirectory(Path.Combine(Dir, @"Steam\steamapps\shadercache\999999"));   // empty: not worth a line
        Put(@"Steam\steamapps\workshop\content\241100\123456\mod.pak", 60000);
        Put(@"Steam\userdata\12345\1102130\remote\save.sav", 8000);
        Put(@"Steam\userdata\12345\206440\remote\save.sav", 9000);
        Put(@"Steam\userdata\12345\7\remote\sharedconfig.vdf", 7000);           // Steam client settings
        Text(@"Steam\userdata\12345\config\localconfig.vdf", $$"""
            "UserLocalConfigStore"
            {
                "Software"
                {
                    "Valve"
                    {
                        "Steam"
                        {
                            "apps"
                            {
                                "1102130" { "LastPlayed" "{{Unix(Now.AddDays(-800))}}" "Playtime" "480" "Playtime2wks" "0" }
                                "206440" { "LastPlayed" "{{Unix(Now.AddDays(-400))}}" "Playtime" "95" }
                                "1245620" { "LastPlayed" "{{Unix(Now.AddDays(-10))}}" "Playtime" "7200" }
                                "555" { "Playtime" "0" }
                            }
                        }
                    }
                }
            }
            """);
        Put(@"Lib2\steamapps\common\ELDEN RING\Game\eldenring.exe", 1000000);
        Put(@"Lib2\steamapps\shadercache\1245620\cache.foz", 70000);

        string epic = Path.Combine(Dir, "EpicData"), epicLib = Path.Combine(Dir, "Epic Games");
        string manifests = @"EpicData\EpicGamesLauncher\Data\Manifests\";
        Text(manifests + "A1.item", $$"""{ "FormatVersion": 0, "AppName": "Fortnite", "DisplayName": "Fortnite", "InstallLocation": "{{Esc(epicLib)}}\\Fortnite", "InstallSize": 150000, "MainGameAppName": "Fortnite" }""");
        Text(manifests + "B2.item", $$"""{ "AppName": "FortniteDLC", "DisplayName": "Some DLC", "InstallLocation": "{{Esc(epicLib)}}\\Fortnite", "MainGameAppName": "Fortnite" }""");
        Text(manifests + "C3.item", $$"""{ "AppName": "Half", "DisplayName": "Half installed", "InstallLocation": "{{Esc(epicLib)}}\\Half", "bIsIncompleteInstall": true }""");
        Text(manifests + "D4.item", "this is not json");
        Put(@"Epic Games\Fortnite\FortniteGame.exe", 150000);
        Put(@"Epic Games\OldGame\old.pak", 25000);        // the launcher no longer knows it
        Put(@"Epic Games\Launcher\Portal\x.dll", 5000);   // the launcher itself
        Put(@"GOG Games\The Witcher\witcher.exe", 120000);
        Put(@"Other\userdata\u\123\settings.bin", 3000);             // same shape, not Steam's
        Put(@"Other\steamapps\shadercache\123\c.bin", 3000);
        return new GameSources(steam, epic, new[] { ("1207658924", "The Witcher: Enhanced Edition", Path.Combine(Dir, "GOG Games", "The Witcher").ToLowerInvariant() + "\\") });
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

    internal static FsNode Node(ContainerNode root, string rel)
    {
        FsNode cur = root;
        foreach (var part in rel.Split('\\')) cur = ((ContainerNode)cur).FindChild(part) ?? throw new InvalidOperationException("missing " + rel);
        return cur;
    }

    // ------------------------------------------------------------------ Valve KeyValues

    [Fact]
    public void Vdf_ParsesNestedBlocks_Escapes_CommentsAndConditions()
    {
        var v = VdfNode.Parse("""
            // a comment
            "Root"
            {
                "path"      "D:\\SteamLibrary"   // trailing comment
                "quote"     "say \"hi\""
                "tab"       "a\tb"
                "brace"     "{"
                "cond"      "win" [$WIN32]
                "cond2"     [$OSX] "mac"
                bare        word
                "Apps" { "10" "1" "20" { "Size" "123" } }
                "APPS" { "30" "3" }
                "empty"     ""
            }
            }
            "Second" { "x" "y" }
            """);
        var r = v.Block("root")!;                     // keys are case insensitive
        Assert.Equal(@"D:\SteamLibrary", r["path"]);
        Assert.Equal("say \"hi\"", r["quote"]);
        Assert.Equal("a\tb", r["tab"]);
        Assert.Equal("{", r["brace"]);                // a quoted brace is a value, not a block
        Assert.Equal("win", r["cond"]);
        Assert.Equal("mac", r["cond2"]);
        Assert.Equal("word", r["bare"]);
        Assert.Equal("", r["empty"]);
        Assert.Equal(123, r.Path("apps", "20")!.Long("size"));
        Assert.Equal(0, r.Long("path"));
        Assert.Null(r.Path("apps", "nope", "deeper"));
        Assert.Equal("y", v.Block("Second")!["x"]);   // a stray closing brace at the top level is ignored
        Assert.Equal(new[] { "Apps" }, r.BlockOrder);  // a repeated block is merged into the first one
        Assert.Equal("3", r.Block("apps")!["30"]);
        Assert.Equal("1", r.Block("apps")!["10"]);
        // truncated or empty text never throws
        Assert.Empty(VdfNode.Parse("").Values);
        Assert.NotNull(VdfNode.Parse("\"a\" { \"b\" \"c").Block("a"));
        Assert.Null(VdfNode.TryLoad(Path.Combine(Dir, "missing.vdf")));
    }

    // ------------------------------------------------------------------ launchers

    [Fact]
    public void Library_ReadsSteamLibraries_PlayHistory_Epic_AndGog()
    {
        try
        {
            var lib = GameLibrary.Load(BuildLayout());
            foreach (var g in lib.Games) _out.WriteLine($"{g} | {g.InstallDir} | played {g.LastPlayed} | {g.Playtime} | tool {g.IsTool}");
            Assert.Equal(new[] { Path.Combine(Dir, "Steam"), Path.Combine(Dir, "Lib2") }, lib.SteamLibraries);   // real spelling, no duplicate
            Assert.Equal(5, lib.Games.Count);                     // 3 Steam (one is a tool), 1 Epic (DLC, unfinished and broken skipped), 1 GOG
            var florence = lib.Games.Single(g => g.Name == "Florence");
            Assert.Equal(GameStore.Steam, florence.Store);
            Assert.Equal(Path.Combine(Dir, @"Steam\steamapps\common\Florence"), florence.InstallDir);
            Assert.Equal(300000, florence.ReportedBytes);
            Assert.Equal(TimeSpan.FromHours(8), florence.Playtime);                      // from the user's localconfig
            Assert.InRange((Now - florence.LastPlayed!.Value).TotalDays, 799.9, 800.1);   // the manifest says 0: localconfig wins
            Assert.InRange((Now - florence.LastUpdated!.Value).TotalDays, 899.9, 900.1);
            var elden = lib.Games.Single(g => g.Name == "ELDEN RING");
            Assert.StartsWith(Path.Combine(Dir, "Lib2"), elden.InstallDir);
            Assert.InRange((Now - elden.LastPlayed!.Value).TotalDays, 2.9, 3.1);          // the newer of manifest and localconfig
            Assert.Equal(TimeSpan.FromHours(120), elden.Playtime);
            Assert.True(lib.Games.Single(g => g.Id == "228980").IsTool);
            Assert.False(elden.IsTool);
            Assert.Contains(elden.ExtraDirs, d => d.Kind == GamePieceKind.ShaderCache && d.Path == Path.Combine(Dir, @"Lib2\steamapps\shadercache\1245620"));
            Assert.Contains(florence.ExtraDirs, d => d.Kind == GamePieceKind.Saves && d.Path == Path.Combine(Dir, @"Steam\userdata\12345\1102130"));

            var fortnite = Assert.Single(lib.Games, g => g.Store == GameStore.Epic);
            Assert.Equal("Fortnite", fortnite.Name);
            Assert.Equal(150000, fortnite.ReportedBytes);
            Assert.Equal(new[] { Path.Combine(Dir, "Epic Games") }, lib.EpicLibraries);
            var witcher = Assert.Single(lib.Games, g => g.Store == GameStore.Gog);
            Assert.Equal(Path.Combine(Dir, "GOG Games", "The Witcher"), witcher.InstallDir);   // trailing slash gone, real case back
            Assert.Null(witcher.LastPlayed);

            // played but not installed: only ids with a date
            Assert.True(lib.SteamPlayedNotInstalled.ContainsKey("206440"));
            Assert.False(lib.SteamPlayedNotInstalled.ContainsKey("555"));
            Assert.False(lib.SteamPlayedNotInstalled.ContainsKey("1102130"));
            Assert.Equal("Florence", lib.SteamName("1102130"));
            Assert.Null(lib.SteamName("206440"));
            Assert.Empty(lib.OfflineLibraries);
            Assert.Equal("not installed", lib.NotInstalledText);

            // a library on a disconnected drive: unknown ids may be installed there
            var vdf = Path.Combine(Dir, @"Steam\steamapps\libraryfolders.vdf");
            System.IO.File.WriteAllText(vdf, System.IO.File.ReadAllText(vdf).TrimEnd().TrimEnd('}') + "\"2\" { \"path\" \"" + Esc(Path.Combine(Dir, "Unplugged")) + "\" }\n}");
            var offline = GameLibrary.Load(new GameSources(Path.Combine(Dir, "Steam"), null, Array.Empty<(string, string, string)>()));
            Assert.Equal(new[] { Path.Combine(Dir, "Unplugged") }, offline.OfflineLibraries);
            Assert.Equal(3, offline.Games.Count);
            Assert.Contains("disconnected", offline.NotInstalledText);

            // nothing installed anywhere: an empty library, no exception
            var none = GameLibrary.Load(new GameSources(Path.Combine(Dir, "nope"), Path.Combine(Dir, "nope2"), Array.Empty<(string, string, string)>()));
            Assert.Empty(none.Games);
            Assert.Empty(none.SteamLibraries);
        }
        finally { try { Directory.Delete(Dir, true); } catch { } }
    }

    [Fact]
    public void SteamTools_AreRecognised()
    {
        Assert.True(GameLibrary.IsSteamTool("228980", "Steamworks Common Redistributables"));
        Assert.True(GameLibrary.IsSteamTool("1", "Proton 9.0"));
        Assert.True(GameLibrary.IsSteamTool("2", "Steam Linux Runtime 3.0 (sniper)"));
        Assert.True(GameLibrary.IsSteamTool("3", "Counter-Strike 2 Dedicated Server"));
        Assert.False(GameLibrary.IsSteamTool("1245620", "ELDEN RING"));
        Assert.False(GameLibrary.IsSteamTool("4", "Steam Deck Sim"));
    }

    // ------------------------------------------------------------------ folders of a scanned tree

    [Fact]
    public void NumberedFolders_AreNamed_AndPathsHaveOwners()
    {
        try
        {
            var lib = GameLibrary.Load(BuildLayout());
            var tree = Scan();
            var root = tree.Root;
            Assert.Equal("1102130 · Florence", lib.LabelFor(Node(root, @"Steam\steamapps\shadercache\1102130")));
            Assert.Equal("1245620 · ELDEN RING", lib.LabelFor(Node(root, @"Lib2\steamapps\shadercache\1245620")));
            Assert.Equal("1102130 · Florence", lib.LabelFor(Node(root, @"Steam\userdata\12345\1102130")));
            Assert.Equal("206440 · not installed", lib.LabelFor(Node(root, @"Steam\steamapps\shadercache\206440")));   // no name known for uninstalled games
            Assert.Equal("241100 · not installed", lib.LabelFor(Node(root, @"Steam\steamapps\workshop\content\241100")));
            Assert.Equal("7 · Steam client settings", lib.LabelFor(Node(root, @"Steam\userdata\12345\7")));
            Assert.Null(lib.LabelFor(Node(root, @"Other\userdata\u\123")));                        // another program's folders
            Assert.Null(lib.LabelFor(Node(root, @"Other\steamapps\shadercache\123")));
            Assert.Null(GameLibrary.Empty.LabelFor(Node(root, @"Steam\steamapps\shadercache\206440")));
            Assert.Null(lib.LabelFor(Node(root, @"Steam\steamapps\common\Florence")));
            Assert.Null(lib.LabelFor(Node(root, @"Steam\userdata\12345\1102130\remote\save.sav"))); // files never

            var own = lib.OwnerOf(Path.Combine(Dir, @"Steam\steamapps\shadercache\1102130\fozpipelinesv6"))!.Value;
            Assert.Equal("Florence", own.Game.Name);
            Assert.Equal(GamePieceKind.ShaderCache, own.Kind);
            Assert.Equal(GamePieceKind.Game, lib.OwnerOf(Path.Combine(Dir, @"Lib2\steamapps\common\ELDEN RING\Game\eldenring.exe").ToUpperInvariant())!.Value.Kind);
            Assert.Null(lib.OwnerOf(Path.Combine(Dir, @"Steam\steamapps\common\Florence2")));      // a prefix is not a parent
            Assert.Null(lib.OwnerOf(Path.Combine(Dir, @"Steam\steamapps")));
        }
        finally { AppConfig.Replace(new AppConfig()); try { Directory.Delete(Dir, true); } catch { } }
    }

    [Fact]
    public void Audit_FindsEveryGamesFolders_Leftovers_AndIdleGames()
    {
        try
        {
            var lib = GameLibrary.Load(BuildLayout());
            var tree = Scan();
            var r = GameAudit.Compute(tree.Root, lib, n => n.LogicalSize, CancellationToken.None, tree.Sync);
            _out.WriteLine(GameAudit.ToText(r, Now));
            Assert.Equal(Dir, r.RootPath.TrimEnd('\\'));
            Assert.All(r.Games, g => Assert.True(g.InView));
            Assert.Equal(new[] { "ELDEN RING", "Florence", "Fortnite", "The Witcher: Enhanced Edition", "Steamworks Common Redistributables" }, r.Games.Select(g => g.Game.Name));   // largest first

            var florence = r.Games.Single(g => g.Game.Name == "Florence");
            Assert.Equal(300000, florence.GameBytes);
            Assert.Equal(40000 + 8000, florence.ExtraBytes);                       // shader cache + saves
            Assert.Equal(new[] { GamePieceKind.Game, GamePieceKind.ShaderCache, GamePieceKind.Saves }, florence.Pieces.Select(p => p.Kind));
            Assert.All(florence.Pieces, p => Assert.NotNull(p.Node));
            Assert.Equal(1070000, r.Games.Single(g => g.Game.Name == "ELDEN RING").ShownBytes);

            // left behind: the game folder without manifest, cache / workshop / saves of uninstalled ids, the unknown Epic folder
            var left = r.Leftovers.Select(l => Path.GetRelativePath(Dir, l.Path)).ToList();
            Assert.Equal(new[]
            {
                @"Steam\steamapps\workshop\content\241100", @"Steam\steamapps\shadercache\206440", @"Epic Games\OldGame",
                @"Steam\steamapps\common\Overcooked", @"Steam\userdata\12345\206440",
            }, left);
            Assert.Contains("last played", r.Leftovers.Single(l => l.Path.EndsWith(@"shadercache\206440")).Note);
            Assert.Contains("saves", r.Leftovers.Single(l => l.Kind == GamePieceKind.Saves).Note);
            Assert.Equal(60000 + 30000 + 25000 + 20000 + 9000, r.LeftoverBytes);

            // not played for a year: Steam games only, tools excluded
            Assert.Equal(new[] { "Florence" }, r.NotPlayedFor(TimeSpan.FromDays(365), Now).Select(g => g.Game.Name));
            Assert.Equal(new[] { "ELDEN RING", "Florence" }, r.NotPlayedFor(TimeSpan.FromDays(1), Now).Select(g => g.Game.Name));
            var spot = r.SpotlightFor(florence.Pieces);
            Assert.Equal(3, spot.Count);

            var text = GameAudit.ToText(r, Now);
            Assert.Contains("Not played for a year: 1 game,", text);
            Assert.Contains("Florence\tSteam\t", text);
            Assert.Contains("never", GameAudit.ToText(r, Now).Split('\n').Single(l => l.StartsWith("Steamworks")));
            Assert.Contains(@"common\Overcooked", text);
            Assert.Equal("8 h", GameAudit.PlaytimeText(TimeSpan.FromMinutes(480)));
            Assert.Equal("45 min", GameAudit.PlaytimeText(TimeSpan.FromMinutes(45)));

            // a view of only one library: the other games keep the launcher's size
            var lib2 = (ContainerNode)Node(tree.Root, "Lib2");
            var part = GameAudit.Compute(lib2, lib, n => n.LogicalSize, CancellationToken.None, tree.Sync);
            Assert.True(part.Games.Single(g => g.Game.Name == "ELDEN RING").InView);
            var outside = part.Games.Single(g => g.Game.Name == "Florence");
            Assert.False(outside.InView);
            Assert.Equal(300000, outside.ShownBytes);
            Assert.Empty(part.Leftovers);

            // cancellation is honoured
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => GameAudit.Compute(tree.Root, lib, n => n.LogicalSize, cts.Token, tree.Sync));
        }
        finally { AppConfig.Replace(new AppConfig()); try { Directory.Delete(Dir, true); } catch { } }
    }

    [Fact]
    public void Audit_OfASnapshot_UsesThePathsInsideTheFrame()
    {
        try
        {
            var lib = GameLibrary.Load(BuildLayout());
            var tree = Scan();
            var file = Path.Combine(Path.GetTempPath(), "bytescape-tests", "games.sns");
            Core.Snapshot.SnsFormat.Write(file, tree.Root, _ => true);
            var frame = new DriveNode("[SNAPSHOT: games.sns]") { IsDrive = false, IsSnapshotFrame = true, Enumerated = true, ScanComplete = true };
            frame.AddChild(Core.Snapshot.SnsFormat.Read(file));
            var r = GameAudit.Compute(frame, lib, n => n.LogicalSize, CancellationToken.None);
            Assert.Equal(5, r.Games.Count(g => g.InView));
            Assert.Equal(5, r.Leftovers.Count);
            System.IO.File.Delete(file);
        }
        finally { AppConfig.Replace(new AppConfig()); try { Directory.Delete(Dir, true); } catch { } }
    }
}
