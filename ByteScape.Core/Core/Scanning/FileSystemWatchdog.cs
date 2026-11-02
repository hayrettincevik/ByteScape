using System.Collections.Concurrent;
using ByteScape.Core.Model;
using ByteScape.Core.Util;

namespace ByteScape.Core.Scanning;

/// <summary>
/// Listens to file system events under the tree root and keeps the tree in sync
/// (created / modified / deleted / renamed elements pop into the views and blink).
/// Events are queued and coalesced per directory, then the directory is re-read.
/// </summary>
public sealed class FileSystemWatchdog : IDisposable
{
    private readonly ScanTree _tree;
    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentDictionary<string, byte> _dirtyDirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _touched = new();
    private readonly CancellationTokenSource _cts = new();
    private Thread? _worker;
    private readonly AutoResetEvent _signal = new(false);

    public FileSystemWatchdog(ScanTree tree)
    {
        _tree = tree;
        _watcher = new FileSystemWatcher(tree.Root.FullPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Created += (_, e) => Enqueue(e.FullPath);
        _watcher.Deleted += (_, e) => Enqueue(e.FullPath);
        _watcher.Changed += (_, e) => Enqueue(e.FullPath);
        _watcher.Renamed += (_, e) => { Enqueue(e.OldFullPath); Enqueue(e.FullPath); };
        _watcher.Error += (_, e) => Log.Warn("File system watcher overflow on " + tree.Root.FullPath + ": " + e.GetException().Message);
    }

    public void Start()
    {
        if (_worker != null) return;
        _worker = new Thread(Run) { IsBackground = true, Name = "Watchdog", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
        _watcher.EnableRaisingEvents = true;
    }

    private void Enqueue(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (dir == null) return;
        _dirtyDirs[dir] = 1;
        _touched.Enqueue(fullPath);
        _signal.Set();
    }

    private void Run()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            _signal.WaitOne(500);
            if (_dirtyDirs.IsEmpty) continue;
            Thread.Sleep(150); // coalesce bursts
            var dirs = _dirtyDirs.Keys.ToArray();
            foreach (var d in dirs) _dirtyDirs.TryRemove(d, out _);
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (_touched.TryDequeue(out var t)) touched.Add(t);

            var scanner = new DirectoryScanner(_tree, token);
            foreach (var dir in dirs)
            {
                try
                {
                    if (_tree.Find(dir) is not ContainerNode node) continue;
                    bool wasEnumerated;
                    HashSet<ContainerNode> queued;
                    lock (_tree.Sync)
                    {
                        wasEnumerated = node.Enumerated;
                        // sub folders a running scan has not listed yet: it gets there itself
                        queued = new HashSet<ContainerNode>(node.Children.OfType<ContainerNode>().Where(c => !c.Enumerated), ReferenceEqualityComparer.Instance);
                    }
                    if (!wasEnumerated) continue;              // scanner will get there anyway
                    Log.Fine("Refreshing " + dir);
                    scanner.RefreshDirectory(node, dir, recurseInto: false);
                    // folders new here (made, or moved in with their content) or replaced by another of the same name:
                    // scanned fully so their content shows up
                    List<(ContainerNode Node, string Path)> fresh;
                    lock (_tree.Sync)
                        fresh = node.Children.OfType<ContainerNode>().Where(c => !c.Enumerated && !queued.Contains(c)).Select(c => (c, Path.Combine(dir, c.Name))).ToList();
                    foreach (var (sub, path) in fresh)
                    {
                        try { scanner.ScanTree(sub, path); } catch (OperationCanceledException) { return; }
                    }
                    long now = Environment.TickCount64;
                    lock (_tree.Sync)
                    {
                        foreach (var c in node.Children)
                            if (touched.Contains(Path.Combine(dir, c.Name))) c.ChangedAtTicks = now;
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Log.Debug("Watchdog: " + ex.Message); }
            }
            DataCache.RefreshDriveInfo(_tree.Root);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _signal.Set();
        try { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } catch { }
    }
}
