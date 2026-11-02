using Microsoft.Win32;

namespace ByteScape.Core;

/// <summary>
/// "Open in ByteScape" in Explorer's right-click menu of folders, drives and a folder's background, for the portable exe:
/// three verbs under the current user's HKCU\Software\Classes (no administrator rights; they show in the classic menu,
/// "Show more options" on Windows 11). The Store package brings its own entry (manifest + <c>Shell/ExplorerCommand</c>)
/// and does not use this. Only written or removed when the user asks for it (Settings).
/// </summary>
public static class ShellIntegration
{
    /// <summary>The menu entry, in the interface language when the entry was written (Store package: when Explorer asks).</summary>
    public static string MenuText => F($"Open in {Branding.Name}");
    private const string VerbName = "ByteScape";

    /// <summary>Tests: another key below HKCU instead of Software\Classes (Explorer never sees it).</summary>
    internal static string ClassesRoot = @"Software\Classes";

    private static readonly string[] Targets = { @"Directory\shell", @"Directory\Background\shell", @"Drive\shell" };

    /// <summary>
    /// The command Explorer runs: "%V" is the folder (or drive, or the folder whose background was clicked); "\." after it
    /// keeps "C:\" from ending in a backslash right before the closing quote (which would swallow the quote).
    /// </summary>
    public static string CommandFor(string exe) => "\"" + exe + "\" scan \"%V\\.\"";

    public static bool IsInstalled(string exe)
    {
        try
        {
            foreach (var t in Targets)
            {
                using var k = Registry.CurrentUser.OpenSubKey(ClassesRoot + "\\" + t + "\\" + VerbName + "\\command");
                if (k?.GetValue(null) is not string cmd || !cmd.Contains(exe, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>True when any of the verbs is there (maybe of another copy of the exe).</summary>
    public static bool IsAnyInstalled()
    {
        try
        {
            foreach (var t in Targets)
            {
                using var k = Registry.CurrentUser.OpenSubKey(ClassesRoot + "\\" + t + "\\" + VerbName);
                if (k != null) return true;
            }
        }
        catch { }
        return false;
    }

    public static void Install(string exe)
    {
        foreach (var t in Targets)
        {
            using var verb = Registry.CurrentUser.CreateSubKey(ClassesRoot + "\\" + t + "\\" + VerbName);
            verb.SetValue(null, MenuText);
            verb.SetValue("Icon", "\"" + exe + "\",0");
            using var cmd = verb.CreateSubKey("command");
            cmd.SetValue(null, CommandFor(exe));
        }
    }

    public static void Uninstall()
    {
        foreach (var t in Targets)
            try { Registry.CurrentUser.DeleteSubKeyTree(ClassesRoot + "\\" + t + "\\" + VerbName, throwOnMissingSubKey: false); } catch { }
    }
}
