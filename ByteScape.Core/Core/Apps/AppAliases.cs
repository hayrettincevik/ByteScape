using System.Text.RegularExpressions;

namespace ByteScape.Core.Apps;

/// <summary>
/// Folders of programs whose names do not reveal the program ("AppData\Roaming\Code" is Visual Studio Code,
/// "AppData\Local\npm-cache" is Node.js). An alias applies only when a program whose display name matches
/// <see cref="Name"/> is installed. Placeholders as in the knowledge base (%LOCALAPPDATA%, %APPDATA%, ...).
/// </summary>
internal sealed record AppAlias(Regex Name, string[] Patterns)
{
    public static AppAlias Of(string namePattern, params string[] patterns) =>
        new(new Regex(namePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), patterns);
}

internal static class AppAliases
{
    private static AppAlias JetBrains(string namePattern, params string[] folders) =>
        AppAlias.Of(namePattern, folders.SelectMany(f => new[] { @"%LOCALAPPDATA%\JetBrains\" + f + "*", @"%APPDATA%\JetBrains\" + f + "*" }).ToArray());

    public static readonly AppAlias[] All =
    {
        AppAlias.Of(@"^(Microsoft )?Visual Studio Code", @"%APPDATA%\Code", @"%USERPROFILE%\.vscode", @"%LOCALAPPDATA%\Programs\Microsoft VS Code"),
        AppAlias.Of(@"^Cursor\b", @"%APPDATA%\Cursor", @"%USERPROFILE%\.cursor", @"%LOCALAPPDATA%\Programs\cursor"),
        AppAlias.Of(@"^Node\.?js", @"%APPDATA%\npm", @"%APPDATA%\npm-cache", @"%LOCALAPPDATA%\npm-cache"),
        AppAlias.Of(@"^Python \d", @"%LOCALAPPDATA%\Programs\Python", @"%APPDATA%\Python", @"%LOCALAPPDATA%\pip"),
        AppAlias.Of(@"^Docker Desktop", @"%LOCALAPPDATA%\Docker", @"%APPDATA%\Docker", @"%APPDATA%\Docker Desktop", @"%USERPROFILE%\.docker", @"%PROGRAMDATA%\DockerDesktop", @"%PROGRAMDATA%\Docker"),
        AppAlias.Of(@"^Google Chrome", @"%PROGRAMFILES(X86)%\Google\GoogleUpdater", @"%PROGRAMFILES(X86)%\Google\Update", @"%PROGRAMFILES%\Google\GoogleUpdater", @"%LOCALAPPDATA%\Google\GoogleUpdater"),
        AppAlias.Of(@"^Microsoft Edge$", @"%PROGRAMFILES(X86)%\Microsoft\EdgeCore", @"%PROGRAMFILES(X86)%\Microsoft\EdgeUpdate", @"%LOCALAPPDATA%\Microsoft\Edge"),
        AppAlias.Of(@"^Microsoft (Office|365)",
            @"%PROGRAMFILES%\Microsoft Office", @"%PROGRAMFILES(X86)%\Microsoft Office", @"%LOCALAPPDATA%\Microsoft\Office", @"%APPDATA%\Microsoft\Office",
            @"%LOCALAPPDATA%\Microsoft\Outlook", @"%APPDATA%\Microsoft\Outlook", @"%APPDATA%\Microsoft\Word", @"%APPDATA%\Microsoft\Excel",
            @"%APPDATA%\Microsoft\PowerPoint", @"%LOCALAPPDATA%\Microsoft\OneNote", @"%APPDATA%\Microsoft\OneNote", @"%PROGRAMDATA%\Microsoft\Office"),
        AppAlias.Of(@"^Microsoft OneDrive", @"%LOCALAPPDATA%\Microsoft\OneDrive", @"%PROGRAMFILES%\Microsoft OneDrive"),
        AppAlias.Of(@"^Microsoft Teams", @"%APPDATA%\Microsoft\Teams", @"%LOCALAPPDATA%\Microsoft\Teams"),
        AppAlias.Of(@"^(Microsoft )?Visual Studio (Community|Professional|Enterprise|Build Tools|Preview|Derleme)",
            @"%LOCALAPPDATA%\Microsoft\VisualStudio", @"%APPDATA%\Microsoft\VisualStudio", @"%PROGRAMDATA%\Microsoft\VisualStudio",
            @"%PROGRAMFILES(X86)%\Microsoft Visual Studio\Shared"),
        AppAlias.Of(@"^Android Studio", @"%LOCALAPPDATA%\Google\AndroidStudio*", @"%APPDATA%\Google\AndroidStudio*", @"%USERPROFILE%\.android", @"%LOCALAPPDATA%\Android\Sdk"),
        JetBrains(@"^IntelliJ IDEA", "IntelliJIdea", "IdeaIC"),
        JetBrains(@"^PyCharm", "PyCharm"),
        JetBrains(@"^(JetBrains )?Rider", "Rider"),
        JetBrains(@"^WebStorm", "WebStorm"),
        JetBrains(@"^CLion", "CLion"),
        JetBrains(@"^GoLand", "GoLand"),
        JetBrains(@"^DataGrip", "DataGrip"),
        JetBrains(@"^PhpStorm", "PhpStorm"),
        JetBrains(@"^RubyMine", "RubyMine"),
        AppAlias.Of(@"^Java( \d+ Update|\(TM\)| SE| Runtime)", @"%USERPROFILE%\AppData\LocalLow\Sun\Java", @"%USERPROFILE%\AppData\LocalLow\Oracle\Java", @"%APPDATA%\Sun\Java"),
        AppAlias.Of(@"^Unity Hub", @"%APPDATA%\UnityHub", @"%LOCALAPPDATA%\UnityHub"),
        AppAlias.Of(@"^Epic Games Launcher", @"%LOCALAPPDATA%\EpicGamesLauncher", @"%PROGRAMDATA%\Epic\EpicGamesLauncher"),
        AppAlias.Of(@"^Ubisoft Connect", @"%LOCALAPPDATA%\Ubisoft Game Launcher", @"%PROGRAMFILES(X86)%\Ubisoft\Ubisoft Game Launcher", @"%PROGRAMDATA%\Ubisoft"),
        AppAlias.Of(@"^(EA app|EA|Origin)$", @"%LOCALAPPDATA%\Electronic Arts", @"%PROGRAMDATA%\EA Desktop", @"%PROGRAMFILES%\Electronic Arts\EA Desktop", @"%LOCALAPPDATA%\Origin", @"%PROGRAMDATA%\Origin"),
        AppAlias.Of(@"^Zoom\b", @"%APPDATA%\Zoom"),
        AppAlias.Of(@"^Adobe Acrobat", @"%LOCALAPPDATA%\Adobe\Acrobat", @"%APPDATA%\Adobe\Acrobat"),
        AppAlias.Of(@"^NVIDIA (Graphics Driver|Grafik Sürücüsü|Grafiktreiber|Pilote graphique)", @"%PROGRAMDATA%\NVIDIA Corporation", @"%PROGRAMFILES%\NVIDIA Corporation", @"%LOCALAPPDATA%\NVIDIA"),
    };
}
