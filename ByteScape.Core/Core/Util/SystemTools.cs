namespace ByteScape.Core.Util;

/// <summary>
/// Windows' own programs, always by their full path. A bare "explorer.exe" or "MsiExec.exe" is looked for in the
/// program's own folder and the current folder first, where a planted file could sit: a portable exe often runs from
/// Downloads.
/// </summary>
public static class SystemTools
{
    public static string Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>A program named without its folder, looked for in Windows' own folders only; a full path as it is; null otherwise.</summary>
    public static string? Resolve(string exe)
    {
        exe = exe.Trim();
        if (exe.Length == 0) return null;
        if (Path.IsPathFullyQualified(exe)) return exe;
        if (exe.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) return null;   // a relative path: it would depend on the current folder
        var name = Path.HasExtension(exe) ? exe : exe + ".exe";
        foreach (var dir in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }
}

/// <summary>CSV cells for spreadsheets.</summary>
public static class Csv
{
    /// <summary>
    /// A cell: quoted when it holds the separator, a quote or a line break. A text a spreadsheet would run as a formula
    /// (starting with = + - @, a tab or a line break — a file can be named "=WEBSERVICE(…)") gets a ' in front, as OWASP
    /// advises against CSV injection; numbers stay as they are.
    /// </summary>
    public static string Cell(string s, char separator)
    {
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n'
            && !double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            s = "'" + s;
        return s.IndexOfAny(new[] { separator, '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
