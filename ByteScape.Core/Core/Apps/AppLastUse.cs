using System.Runtime.InteropServices;
using ByteScape.Core.Util;
using Microsoft.Win32;

namespace ByteScape.Core.Apps;

/// <summary>
/// When programs were last started, as Windows remembers it for the current user ("UserAssist": what was started from
/// the Start menu, the taskbar, Explorer or a shortcut, with a run count and the last time; value names are ROT13 and
/// start with a known folder id). Nothing is written; empty when Windows was told not to track app launches.
/// </summary>
public sealed class AppLastUse
{
    private readonly List<(string Path, DateTime Last, int Count)> _programs = new();
    private readonly List<(string Key, DateTime Last, int Count)> _shortcuts = new();
    private readonly Dictionary<string, (DateTime Last, int Count)> _families = new(StringComparer.OrdinalIgnoreCase);

    public static readonly AppLastUse Empty = new();

    /// <summary>Remembered starts (programs, shortcuts, Store apps).</summary>
    public int Entries => _programs.Count + _shortcuts.Count + _families.Count;

    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";

    public static AppLastUse FromRegistry()
    {
        var values = new List<(string, byte[])>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Key);
            if (root != null)
                foreach (var guid in root.GetSubKeyNames())
                {
                    using var count = root.OpenSubKey(guid + @"\Count");
                    if (count == null) continue;
                    foreach (var name in count.GetValueNames())
                        if (count.GetValue(name) is byte[] data) values.Add((ExpandKnownFolder(Rot13(name)), data));
                }
        }
        catch (Exception ex) { Log.Debug("Program starts (UserAssist) unreadable: " + ex.Message); }
        return FromValues(values);
    }

    /// <summary>Decoded names (paths, shortcut paths, Store app ids) with their 72 byte records (Windows 7 and later).</summary>
    internal static AppLastUse FromValues(IEnumerable<(string Name, byte[] Data)> values)
    {
        var u = new AppLastUse();
        foreach (var (name, data) in values)
        {
            if (data.Length < 68) continue;
            int count = BitConverter.ToInt32(data, 4);
            long ft = BitConverter.ToInt64(data, 60);
            if (ft <= 0) continue;
            DateTime last;
            try { last = DateTime.FromFileTimeUtc(ft); } catch { continue; }
            if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var k = AppCatalog.Key(AppCatalog.CleanName(Path.GetFileNameWithoutExtension(name)));
                if (k.Length >= 3) u._shortcuts.Add((k, last, count));
            }
            else if (name.Contains('!') && !name.Contains('\\'))
            {
                var fam = name.Substring(0, name.IndexOf('!'));
                if (!u._families.TryGetValue(fam, out var old) || old.Last < last) u._families[fam] = (last, count);
            }
            else if (name.Length > 3 && name[1] == ':' && name[2] == '\\') u._programs.Add((name, last, count));
        }
        return u;
    }

    /// <summary>When <paramref name="app"/> was last started and how Windows saw it, or null when it did not see it.</summary>
    public (DateTime When, string How)? LastRun(InstalledApp app)
    {
        DateTime best = default; string how = "";
        foreach (var (path, last, _) in _programs)
            if (last > best && app.InstallDirs.Any(d => path.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase))) { best = last; how = "started " + Path.GetFileName(path); }
        var keys = new HashSet<string>(app.Keys, StringComparer.Ordinal) { AppCatalog.Key(AppCatalog.CleanName(app.Name)) };
        foreach (var (key, last, _) in _shortcuts)
            if (last > best && keys.Contains(key)) { best = last; how = "started from its shortcut"; }
        foreach (var fam in new[] { app.PackageFamily }.Concat(app.ExtraFamilies))
            if (fam != null && _families.TryGetValue(fam, out var f) && f.Last > best) { best = f.Last; how = "started from the Start menu"; }
        return best == default ? null : (best, how);
    }

    internal static string Rot13(string s)
    {
        var c = s.ToCharArray();
        for (int i = 0; i < c.Length; i++)
        {
            char x = c[i];
            if (x is >= 'a' and <= 'z') c[i] = (char)('a' + (x - 'a' + 13) % 26);
            else if (x is >= 'A' and <= 'Z') c[i] = (char)('A' + (x - 'A' + 13) % 26);
        }
        return new string(c);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    /// <summary>"{6D809377-...}\Mozilla Firefox\firefox.exe" → "C:\Program Files\Mozilla Firefox\firefox.exe".</summary>
    internal static string ExpandKnownFolder(string name)
    {
        if (name.Length < 39 || name[0] != '{' || name[37] != '}' || !Guid.TryParse(name.AsSpan(0, 38), out var id)) return name;
        IntPtr p = IntPtr.Zero;
        try
        {
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out p) != 0) return name;
            var folder = Marshal.PtrToStringUni(p);
            return string.IsNullOrEmpty(folder) ? name : folder.TrimEnd('\\') + name.Substring(38);
        }
        catch { return name; }
        finally { if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p); }
    }
}
