namespace ByteScape.Core.Util;

public enum LogLevel { Debug, Fine, Warn, Error, Issue }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string LevelText => Level switch
    {
        LogLevel.Warn => "WARN  ",
        LogLevel.Error => "ERROR ",
        LogLevel.Debug => "DEBUG ",
        LogLevel.Fine => "FINE  ",
        _ => "ISSUE ",
    };
    public override string ToString() => $"{Time:HH:mm:ss} {LevelText} {Message}";
}

/// <summary>
/// Application log feeding the log console (Frmconsole). Thread safe; the console form
/// subscribes to <see cref="Added"/> and marshals to the UI thread itself.
/// </summary>
public static class Log
{
    private static readonly List<LogEntry> _entries = new();
    private static readonly object _sync = new();
    public static int MaxEntries => Math.Max(100, Config.AppConfig.Current.LogMaxEntries);

    public static event Action<LogEntry>? Added;

    public static IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_sync) return _entries.ToArray();
    }

    public static void Clear()
    {
        lock (_sync) _entries.Clear();
    }

    public static void Write(LogLevel level, string message)
    {
        var cfg = Config.AppConfig.Current;
        switch (level)
        {
            case LogLevel.Warn when !cfg.LogWarning: return;
            case LogLevel.Debug when !cfg.LogDebug: return;
            case LogLevel.Fine when !cfg.LogDebugFine: return;
            case LogLevel.Issue when !cfg.LogIssue: return;
        }
        var e = new LogEntry(DateTime.Now, level, message);
        lock (_sync)
        {
            _entries.Add(e);
            if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }
        Added?.Invoke(e);
    }

    public static void Warn(string m) => Write(LogLevel.Warn, m);
    public static void Error(string m) => Write(LogLevel.Error, m);
    public static void Debug(string m) => Write(LogLevel.Debug, m);
    public static void Fine(string m) => Write(LogLevel.Fine, m);
    public static void Issue(string m) => Write(LogLevel.Issue, m);
}
