using ByteScape.Core.Model;
using ByteScape.Core.Util;

namespace ByteScape.Core.Scanning;

public enum ScanState { Idle, Scanning, Stopping, Completed, Aborted }

/// <summary>
/// A scanned (or loaded) tree shared by every view that looks at the same media
/// (a path is scanned only once, whichever view asked for it).
/// </summary>
public sealed class ScanTree : IDisposable
{
    public ScanTree(DriveNode root, bool live)
    {
        Root = root;
        IsLive = live;
        if (live) RootIdentity = Cleanup.SafeFs.Identity(root.FullPath);
    }

    /// <summary>A folder at <see cref="FsNode.MaxDepth"/> was met (reported once in the log).</summary>
    internal bool DepthLimitReported;

    /// <summary>The scanned folder as the file system knows it when the scan started: a delete checks it is still the same folder.</summary>
    public Cleanup.FileIdentity? RootIdentity { get; }

    /// <summary>Guards every structural access to the node tree.</summary>
    public object Sync { get; } = new();

    public DriveNode Root { get; }

    /// <summary>False for loaded snapshots (no live link with the media).</summary>
    public bool IsLive { get; }

    public ScanJob? MasterScan { get; private set; }
    public FileSystemWatchdog? Watchdog { get; private set; }

    /// <summary>Hard-linked files of this tree, grouped after each scan (see <see cref="CountsLinksOnce"/>).</summary>
    public HardLinkRegistry HardLinks { get; } = new();

    /// <summary>Hard-linked files are counted once: the option is on and the tree is a live local NTFS drive or folder.</summary>
    public bool CountsLinksOnce => IsLive && Config.AppConfig.Current.CountHardLinksOnce && LocalNtfs;

    /// <summary>The tree is on a local NTFS drive (file ids can be listed).</summary>
    internal bool LocalNtfs => _localNtfs ??= IsLocalNtfs(Root.FullPath);
    private bool? _localNtfs;

    /// <summary>Folder rescans (zoom scans) running now: a full scan of a spinning disk waits for them.</summary>
    internal int ActiveSubScans;

    private static bool IsLocalNtfs(string path)
    {
        try
        {
            if (path.Length < 2 || path[1] != ':') return false;
            var d = new DriveInfo(path.Substring(0, 2));
            return d.DriveType is DriveType.Fixed or DriveType.Removable && string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public int ViewCount;

    public event Action<ScanTree>? MasterScanFinished;

    public ScanState MasterState => MasterScan?.State ?? (Root.ScanComplete ? ScanState.Completed : ScanState.Idle);
    public bool IsMasterScanning => MasterScan is { IsRunning: true };

    /// <summary>0..1 progress when the root is a drive (total size known), else -1.</summary>
    public double Progress
    {
        get
        {
            if (!Root.IsDrive) return -1;
            long used = Root.TotalBytes - Root.FreeBytes;
            if (used <= 0) return -1;
            return Math.Min(1.0, (double)Root.DiskSize / used);
        }
    }

    /// <summary>A master scan was stopped before the end (or restored from disk): it can continue where it stopped.</summary>
    public bool CanResume => IsLive && !IsMasterScanning && !Root.ScanComplete && Root.Enumerated;
    public bool IsMasterPaused => MasterScan is { IsPaused: true };
    public void PauseMasterScan() => MasterScan?.Pause();
    public void ContinueMasterScan() => MasterScan?.Continue();

    /// <summary>
    /// Where the volume's NTFS change journal stood when the last full scan started (or up to where the last quick rescan
    /// read it): every change since is either in the tree or will be read from the journal. Null: not known.
    /// </summary>
    public JournalMark? ChangeMark { get; internal set; }

    /// <summary>Quick rescans are possible here: the option is on and the tree is a live local NTFS drive or folder whose links are not followed.</summary>
    public bool TracksChanges => IsLive && LocalNtfs && Config.AppConfig.Current.QuickRescan && !Config.AppConfig.Current.FollowReparsePoints;

    /// <summary>A complete scan whose changes since can be read from the change journal: a rescan lists only the folders that changed.</summary>
    public bool CanRescanChanges => TracksChanges && ChangeMark != null && !IsMasterScanning && Root.ScanComplete;

    /// <summary>
    /// Starts a full scan: every folder is listed again (<paramref name="resume"/> false), or a stopped scan continues with
    /// the folders it did not reach (true; the ones listed before are kept as they were). <paramref name="changesOnly"/>: only
    /// the folders the NTFS change journal says changed since <see cref="ChangeMark"/> are listed again (a full scan when it cannot say).
    /// </summary>
    public void StartMasterScan(bool resume = false, bool changesOnly = false)
    {
        if (!IsLive) return;
        if (MasterScan is { IsRunning: true }) return;
        var since = changesOnly && !resume && TracksChanges ? ChangeMark : null;
        if (!resume && since == null) ResetScanFlags(Root);
        var job = new ScanJob(this, Root, Root.FullPath, isMaster: true, resume: resume, since: since);
        MasterScan = job;
        job.Finished += j =>
        {
            if (j.State == ScanState.Completed)
            {
                lock (Sync) Root.ScanComplete = true;
            }
            MasterScanFinished?.Invoke(this);
        };
        job.Start();
        Watchdog?.Start();
    }

    public void StopMasterScan() => MasterScan?.Stop();

    /// <summary>
    /// Before a quick rescan: the folders to list again are marked not listed, the folders above them not complete (so the walk
    /// gets there), everything else complete (not walked). Listed again: the folders the journal names (<paramref name="changed"/>
    /// ids; <paramref name="rootId"/> is the root's), folders without an id (their changes cannot be told apart) and folders not
    /// listed yet. Returns how many.
    /// </summary>
    internal int MarkChanged(IReadOnlySet<long> changed, long rootId)
    {
        lock (Sync)
        {
            var dirty = new List<ContainerNode>();
            var stack = new Stack<ContainerNode>();
            stack.Push(Root);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                c.ScanComplete = true;
                long id = c is FolderNode f ? f.FileId : ReferenceEquals(c, Root) ? rootId : 0;
                if (!c.Enumerated || id == 0 || changed.Contains(id)) dirty.Add(c);
                foreach (var k in c.Children) if (k is ContainerNode kc) stack.Push(kc);
            }
            foreach (var d in dirty)
            {
                d.Enumerated = false;
                for (ContainerNode? p = d; p != null && p.ScanComplete; p = p.Parent) p.ScanComplete = false;
            }
            return dirty.Count;
        }
    }

    /// <summary>A new scan of an already scanned subtree must revisit every folder to pick up external changes.</summary>
    public void ResetScanFlags(ContainerNode start)
    {
        lock (Sync)
        {
            start.ScanComplete = false;
            foreach (var n in start.Descendants())
                if (n is ContainerNode c) c.ScanComplete = false;
        }
    }

    public void EnableWatchdog()
    {
        if (!IsLive || Watchdog != null) return;
        try
        {
            Watchdog = new FileSystemWatchdog(this);
            Watchdog.Start();
        }
        catch (Exception ex)
        {
            Log.Warn("File system changes detection unavailable for " + Root.FullPath + ": " + ex.Message);
        }
    }

    /// <summary>Resolves a node by path inside this tree (null when not (yet) present).</summary>
    public FsNode? Find(string fullPath)
    {
        lock (Sync)
        {
            var rootPath = Root.FullPath.TrimEnd('\\');
            if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) return null;
            var rest = fullPath.Substring(rootPath.Length).Trim('\\');
            if (rest.Length == 0) return Root;
            ContainerNode cur = Root;
            var parts = rest.Split('\\');
            for (int i = 0; i < parts.Length; i++)
            {
                var child = cur.FindChild(parts[i]);
                if (child == null) return null;
                if (i == parts.Length - 1) return child;
                if (child is not ContainerNode c) return null;
                cur = c;
            }
            return cur;
        }
    }

    public void Dispose()
    {
        MasterScan?.Stop();
        Watchdog?.Dispose();
    }
}

/// <summary>
/// Keeps every live scan tree alive as long as a view uses it. Opening the same path twice
/// (or a path under an already scanned root) reuses the existing tree.
/// </summary>
public static class DataCache
{
    private static readonly List<ScanTree> _trees = new();
    private static readonly object _sync = new();

    public static string NormalizePath(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 2 && path[1] == ':') path += "\\";
        try { path = Path.GetFullPath(path); } catch { }
        if (path.Length > 3 && path.EndsWith('\\')) path = path.TrimEnd('\\');
        return path;
    }

    public static bool IsDriveRoot(string path)
    {
        path = NormalizePath(path);
        return path.Length == 3 && path[1] == ':' && path[2] == '\\';
    }

    /// <summary>Returns the tree covering <paramref name="path"/> and the node for the path inside it.</summary>
    public static (ScanTree tree, ContainerNode node) Acquire(string path)
    {
        path = NormalizePath(path);
        lock (_sync)
        {
            foreach (var t in _trees)
            {
                var rootPath = t.Root.FullPath.TrimEnd('\\');
                if (string.Equals(rootPath, path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref t.ViewCount);
                    return (t, t.Root);
                }
                if (path.StartsWith(rootPath + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    // A parent is already being scanned: hook a view on the sub folder, forcing its
                    // enumeration first so the view fills quickly.
                    var node = EnsurePath(t, path);
                    if (node != null)
                    {
                        Interlocked.Increment(ref t.ViewCount);
                        return (t, node);
                    }
                }
            }
            var root = CreateRoot(path);
            var tree = new ScanTree(root, live: true);
            tree.ViewCount = 1;
            if (Config.AppConfig.Current.WatchDog) tree.EnableWatchdog();
            _trees.Add(tree);
            return (tree, root);
        }
    }

    private static ContainerNode? EnsurePath(ScanTree tree, string path)
    {
        var rootPath = tree.Root.FullPath.TrimEnd('\\');
        var rest = path.Substring(rootPath.Length).Trim('\\');
        lock (tree.Sync)
        {
            ContainerNode cur = tree.Root;
            var curPath = tree.Root.FullPath;
            foreach (var part in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                curPath = Path.Combine(curPath, part);
                var child = cur.FindChild(part);
                if (child == null)
                {
                    if (!Directory.Exists(curPath)) return null;
                    var f = new FolderNode(part);
                    cur.AddChild(f);
                    child = f;
                }
                if (child is not ContainerNode c) return null;
                cur = c;
            }
            return cur;
        }
    }

    public static ScanTree Register(DriveNode root, bool live)
    {
        var tree = new ScanTree(root, live) { ViewCount = 1 };
        lock (_sync) _trees.Add(tree);
        return tree;
    }

    public static void Release(ScanTree tree)
    {
        lock (_sync)
        {
            if (Interlocked.Decrement(ref tree.ViewCount) > 0) return;
            _trees.Remove(tree);
        }
        tree.Dispose();
    }

    public static DriveNode CreateRoot(string path)
    {
        var isDrive = IsDriveRoot(path);
        var root = new DriveNode(path.EndsWith('\\') ? path : path + "\\") { IsDrive = isDrive };
        var drivePath = Path.GetPathRoot(path) ?? path;
        try
        {
            if (Interop.Native.GetDiskFreeSpaceW(drivePath, out var spc, out var bps, out _, out _))
                root.ClusterSize = (int)Math.Max(512, spc * bps);
            if (isDrive && Interop.Native.GetDiskFreeSpaceExW(drivePath, out _, out var total, out var free))
            {
                root.TotalBytes = (long)total;
                root.FreeBytes = (long)free;
            }
        }
        catch { }
        return root;
    }

    public static void RefreshDriveInfo(DriveNode root)
    {
        if (!root.IsDrive) return;
        try
        {
            if (Interop.Native.GetDiskFreeSpaceExW(root.FullPath, out _, out var total, out var free))
            {
                root.TotalBytes = (long)total;
                root.FreeBytes = (long)free;
            }
        }
        catch { }
    }
}
