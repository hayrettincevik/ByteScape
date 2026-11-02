using System.IO.Enumeration;
using ByteScape.Core.Config;
using ByteScape.Core.Model;
using ByteScape.Core.Util;
using ByteScape.Interop;

namespace ByteScape.Core.Scanning;

/// <summary>
/// The scanning thread. A master scan walks the whole tree; a sub scan (started from a
/// zoomed folder) walks only that subtree and has priority because it runs on its own thread.
/// Folders already enumerated by another job are skipped, so master + sub scans cooperate.
/// </summary>
public sealed class ScanJob
{
    private readonly ScanTree _tree;
    private readonly ContainerNode _start;
    private readonly string _startPath;
    private readonly CancellationTokenSource _cts = new();
    /// <summary>Open while the job may work; closed while it is paused (the walkers wait on it before each folder).</summary>
    private readonly ManualResetEventSlim _gate = new(true);
    private Thread? _thread;

    public ScanJob(ScanTree tree, ContainerNode start, string startPath, bool isMaster, bool resume = false, JournalMark? since = null)
    {
        _tree = tree;
        _start = start;
        _startPath = startPath;
        IsMaster = isMaster;
        Resume = resume;
        Since = since;
    }

    public bool IsMaster { get; }
    /// <summary>Continues a stopped scan: folders already listed are not listed again, only the ones not reached yet.</summary>
    public bool Resume { get; }
    /// <summary>Quick rescan: only the folders the NTFS change journal says changed after this mark are listed again.</summary>
    public JournalMark? Since { get; }
    /// <summary>What the change journal said for a quick rescan (null for other scans, or when it could not say and every folder was listed).</summary>
    public JournalSummary? Journal { get; private set; }
    public bool IsPaused => IsRunning && !_gate.IsSet;

    /// <summary>Holds the scan before its next folder (a folder being listed finishes); the time does not run meanwhile.</summary>
    public void Pause()
    {
        if (!IsRunning || !_gate.IsSet) return;
        _gate.Reset();
        _watch.Stop();
        Log.Debug("Scan paused: " + _startPath);
    }

    public void Continue()
    {
        if (_gate.IsSet) return;
        _watch.Start();
        _gate.Set();
        Log.Debug("Scan continued: " + _startPath);
    }
    public ScanState State { get; private set; } = ScanState.Idle;
    public bool IsRunning => State is ScanState.Scanning or ScanState.Stopping;
    public event Action<ScanJob>? Finished;

    public void Start()
    {
        State = ScanState.Scanning;
        _thread = new Thread(Run) { IsBackground = true, Name = IsMaster ? "MasterScan" : "SubScan", Priority = IsMaster ? ThreadPriority.BelowNormal : ThreadPriority.Normal };
        _thread.Start();
    }

    public void Stop()
    {
        if (!IsRunning) return;
        State = ScanState.Stopping;
        _cts.Cancel();
        _gate.Set();        // a paused job wakes up to stop
    }

    /// <summary>How the job lists folders: "NTFS MFT" (fast scan as administrator) or "directory listing".</summary>
    public string Method { get; private set; } = "";
    /// <summary>What the job does right now, for the status line (empty while it walks folders).</summary>
    public string Phase { get; private set; } = "";
    /// <summary>How the drive was scanned (drive kind, folders at once, order), for the status line.</summary>
    public ScanStrategy? Strategy { get; private set; }
    public TimeSpan Elapsed => _watch.Elapsed;
    private readonly System.Diagnostics.Stopwatch _watch = new();

    private void Run()
    {
        var token = _cts.Token;
        _watch.Start();
        if (!IsMaster) Interlocked.Increment(ref _tree.ActiveSubScans);
        try
        {
            Log.Debug((IsMaster ? "scan " : "folder rescan ") + _startPath);
            var scanner = new DirectoryScanner(_tree, token) { Gate = _gate };
            Strategy = scanner.Strategy;
            if (IsMaster && Since != null && TryChanges(token))
            {
                Method = "NTFS change journal";
                scanner.ScanTree(_start, _startPath, yieldToSubScans: Strategy.YieldToSubScans, resume: true);
            }
            else
            {
                if (IsMaster && !Resume)
                {
                    if (Since != null) _tree.ResetScanFlags(_start);   // the journal could not say: every folder is listed again
                    // where the change journal stands before anything is listed: a later rescan lists again what changed after it
                    _tree.ChangeMark = _tree.TracksChanges ? ChangeJournal.Mark(_startPath) : null;
                }
                if (IsMaster) TryMft(scanner, token);
                if (Method.Length == 0)
                {
                    Method = "directory listing";
                    Log.Debug($"Scan strategy for {_startPath}: {Strategy.Short}{(Strategy.Overridden ? " (configuration)" : "")}");
                    if (Resume) Log.Debug("Continuing the scan of " + _startPath + ": folders listed before are kept");
                    scanner.ScanTree(_start, _startPath, yieldToSubScans: IsMaster && Strategy.YieldToSubScans, resume: Resume);
                }
            }
            // nothing changed: the hard link groups of the last pass still hold
            if (!token.IsCancellationRequested && _tree.CountsLinksOnce && !(Journal is { ToList: 0 } && _tree.HardLinks.HasRun))
            {
                Phase = /*loc*/ "counting hard-linked files once";
                _tree.HardLinks.Apply(_tree, token);
                Phase = /*loc*/ "";
            }
            State = token.IsCancellationRequested ? ScanState.Aborted : ScanState.Completed;
            Log.Debug($"{(IsMaster ? "full" : "zoom")} scan of {_startPath} ({Method}) finished in {_watch.ElapsedMilliseconds} ms");
        }
        catch (OperationCanceledException)
        {
            State = ScanState.Aborted;
        }
        catch (Exception ex)
        {
            Log.Error("Scan error on " + _startPath + ": " + ex.Message);
            State = ScanState.Aborted;
        }
        finally
        {
            if (!IsMaster) Interlocked.Decrement(ref _tree.ActiveSubScans);
            _watch.Stop();      // Elapsed is the scan's duration from now on (the status line shows it)
            if (State == ScanState.Completed)
            {
                lock (_tree.Sync) MarkComplete(_start);
            }
            DataCache.RefreshDriveInfo(_tree.Root);
            Finished?.Invoke(this);
        }
    }

    /// <summary>
    /// Quick rescan: reads what changed since <see cref="Since"/> from the NTFS change journal and marks those folders to be
    /// listed again. False when the journal cannot say (it was made again, or overwritten since): then everything is listed.
    /// </summary>
    private bool TryChanges(CancellationToken token)
    {
        Phase = /*loc*/ "reading the NTFS change journal";
        try
        {
            var changes = ChangeJournal.ReadSince(_startPath, Since!, token, out var why);
            if (changes == null)
            {
                Log.Debug("Quick rescan of " + _startPath + " not possible (" + why + "): every folder is listed again");
                return false;
            }
            int toList = _tree.MarkChanged(changes.Folders, Since!.RootId);
            _tree.ChangeMark = changes.Next;
            Journal = new JournalSummary(changes.Records, changes.Folders.Count, toList, changes.Elapsed, Since.TakenUtc);
            Log.Debug($"Quick rescan of {_startPath}: {changes.Records:N0} changes on the drive since {Since.TakenUtc.ToLocalTime():g} " +
                      $"({changes.Folders.Count:N0} folders), {toList:N0} of them here to list again; journal read in {changes.Elapsed.TotalMilliseconds:0} ms");
            return true;
        }
        finally { Phase = /*loc*/ ""; }
    }

    /// <summary>
    /// Fast path of a master scan: the folder listings come from the NTFS master file table (administrator, local NTFS).
    /// Anything unexpected falls back to the directory listing; folders already done are not listed again.
    /// </summary>
    private void TryMft(DirectoryScanner scanner, CancellationToken token)
    {
        if (!Mft.MftScan.CanUse(_startPath, out var why)) { Log.Debug("Fast NTFS scan not used for " + _startPath + ": " + why); return; }
        try
        {
            Phase = /*loc*/ "reading the NTFS master file table";
            var ix = Mft.MftScan.ReadVolume(_startPath, token, AppConfig.Current.ScanADS, pause: _gate);
            Phase = /*loc*/ "";
            if (scanner.ScanFromMft(ix, _start, _startPath)) Method = "NTFS MFT";
            else Log.Warn("Fast NTFS scan: " + _startPath + " was not found in the master file table, using the normal scan");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn("Fast NTFS scan unavailable for " + _startPath + " (" + ex.Message + "), using the normal scan"); }
        finally { Phase = /*loc*/ ""; }
    }

    private static void MarkComplete(ContainerNode n)
    {
        n.ScanComplete = true;
        foreach (var c in n.Children)
            if (c is ContainerNode cc && !cc.ScanComplete) MarkComplete(cc);
    }
}

/// <summary>
/// A quick rescan: changes read from the NTFS change journal since <paramref name="SinceUtc"/> (on the whole drive), the
/// folders they touched, how many of them were in the tree and listed again, the time the journal took.
/// </summary>
public sealed record JournalSummary(int Records, int Folders, int ToList, TimeSpan Read, DateTime SinceUtc);

/// <summary>
/// Directory enumeration + reconciliation with the in-memory tree. Used by the scan jobs
/// and by the watchdog (which re-reads single directories after a file system event).
/// </summary>
public sealed class DirectoryScanner
{
    private readonly ScanTree _tree;
    private readonly CancellationToken _token;
    private readonly bool _scanAds;
    private readonly int _cluster;
    private readonly AppConfig _cfg;
    /// <summary>Read file ids (hard-linked files are counted once afterwards).</summary>
    private readonly bool _ids;
    /// <summary>List with file ids even when hard links are not counted: for the folders' record order on a spinning disk, and for quick rescans (the change journal names folders by id).</summary>
    private readonly bool _folderIds;
    /// <summary>One folder at a time in the order the folders sit in the master file table (spinning disk).</summary>
    private readonly bool _diskOrder;

    /// <summary>How this drive is scanned (<see cref="DriveProfile"/>).</summary>
    public ScanStrategy Strategy { get; }

    /// <summary>Closed while the scan is paused: waited on before each folder.</summary>
    internal ManualResetEventSlim? Gate { get; init; }

    private void WaitIfPaused() => Gate?.Wait(_token);

    /// <summary>
    /// The folders to walk next under a folder: a resumed scan does not list again a folder listed before (it keeps what
    /// it found then) and goes on with its sub folders not completed; otherwise the folder is listed.
    /// </summary>
    private List<(ContainerNode node, string path)> Next(ContainerNode node, string path, bool resume)
    {
        if (resume)
        {
            lock (_tree.Sync)
                if (node.Enumerated)
                    return node.Children.OfType<ContainerNode>().Where(k => !k.IsSynthetic && !k.ScanComplete)
                               .Select(k => (k, Path.Combine(path, k.Name))).ToList();
        }
        var listed = RefreshDirectory(node, path, recurseInto: true);
        Listed?.Invoke(node);
        return listed;
    }

    public DirectoryScanner(ScanTree tree, CancellationToken token)
    {
        _tree = tree;
        _token = token;
        _scanAds = AppConfig.Current.ScanADS;
        _cluster = Math.Max(512, tree.Root.ClusterSize);
        _cfg = AppConfig.Current;
        _ids = tree.CountsLinksOnce;
        Strategy = DriveProfile.For(tree.Root.FullPath);
        _diskOrder = Strategy.DiskOrder && tree.IsLive && tree.LocalNtfs;
        _folderIds = _diskOrder || tree.TracksChanges;
    }

    /// <summary>
    /// Same breadth first walk as <see cref="ScanTree"/>, but every listing comes from the master file table: no
    /// directory enumeration and no size calls, and folders a normal listing is denied are readable. Junctions and
    /// symbolic links are skipped (never followed in this mode); other reparse folders (cloud sync roots) are walked.
    /// False when <paramref name="startPath"/> is not in the table.
    /// </summary>
    internal bool ScanFromMft(Mft.MftIndex mft, ContainerNode start, string startPath)
    {
        string rel = startPath.Length > 3 ? startPath.Substring(3) : "";
        if (mft.Resolve(rel) is not uint startRecord) return false;
        var queue = new Queue<(ContainerNode node, string path, uint rec)>();
        queue.Enqueue((start, startPath, startRecord));
        while (queue.Count > 0)
        {
            _token.ThrowIfCancellationRequested();
            WaitIfPaused();
            var (node, path, rec) = queue.Dequeue();
            if (node.ScanComplete) continue;
            var kids = mft.ChildrenOf(rec);
            var entries = new List<Entry>(kids.Count);
            var disks = new long[kids.Count];
            var ads = new bool[kids.Count];
            var walk = new string?[kids.Count];
            var records = new Dictionary<string, uint>(kids.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, r) in kids)
            {
                bool dir = mft.IsDirectory(r);
                if (dir && mft.IsLink(r)) continue;
                int i = entries.Count;
                entries.Add(new Entry(name, dir, dir ? 0 : mft.LengthOf(r), mft.AttributesOf(r), mft.CreatedUtc(r), mft.AccessedUtc(r), mft.ModifiedUtc(r), mft.FileIdOf(r)));
                if (dir) walk[i] = Path.Combine(path, name);
                else
                {
                    disks[i] = mft.DiskSizeOf(r);
                    if (_scanAds && mft.HasAds(r)) { ads[i] = true; disks[i] += RoundToCluster(mft.AdsBytesOf(r)); }
                }
                records.TryAdd(name, r);
            }
            foreach (var (n, p) in Reconcile(node, path, entries, disks, ads, walk, denied: false, recurseInto: true))
                if (records.TryGetValue(n.Name, out var r)) queue.Enqueue((n, p, r));
            lock (_tree.Sync) node.Enumerated = true;
        }
        return true;
    }


    /// <summary>
    /// Folders listed at the same time for a scan of <paramref name="path"/>: the configured number, or automatically
    /// one per core (up to 8) on SSDs, 8 on network paths (latency bound) and 1 on spinning disks (parallel listing
    /// makes the head jump between folders, which is slower than walking them one by one).
    /// </summary>
    public static int WorkerCount(string path) => DriveProfile.For(path).Workers;

    /// <summary>Tests: every folder a scan walk lists, in order (one at a time; from the listing thread with several).</summary>
    internal static Action<ContainerNode>? Listed;

    /// <summary>
    /// Breadth first walk so the top level structure appears immediately in the views. With several workers the folders
    /// are listed in parallel (listing and size calls outside the tree lock, reconciliation inside it as before), in
    /// roughly the same breadth first order; the order of the children inside each folder is unchanged.
    /// </summary>
    public void ScanTree(ContainerNode start, string startPath, bool yieldToSubScans = false, bool resume = false)
    {
        int workers = Strategy.Workers;
        if (workers > 1) { ScanTreeParallel(start, startPath, workers, resume); return; }
        // one folder at a time: breadth first, or on a spinning disk in the order the folders sit in the master file table
        var queue = new DiskOrderQueue<(ContainerNode node, string path)>();
        long order = 0;
        queue.Enqueue((start, startPath), 0);
        while (queue.TryDequeue(out var item))
        {
            _token.ThrowIfCancellationRequested();
            // a spinning disk serves one reader well: a full scan waits while the user rescans a folder
            while (yieldToSubScans && Volatile.Read(ref _tree.ActiveSubScans) > 0) { _token.ThrowIfCancellationRequested(); Thread.Sleep(50); }
            WaitIfPaused();
            var (node, path) = item;
            if (node.ScanComplete) continue;               // done by another job
            var subFolders = Next(node, path, resume);
            foreach (var sf in subFolders)
            {
                // without folder ids the numbers only grow: the queue is plain breadth first
                long record = _diskOrder && sf.node is FolderNode { FileId: not 0 } fn ? fn.FileId & 0x0000_FFFF_FFFF_FFFF : ++order + (1L << 50);
                queue.Enqueue(sf, record);
            }
            lock (_tree.Sync) node.Enumerated = true;
        }
    }

    internal readonly struct Entry
    {
        public Entry(string name, bool isDir, long length, FileAttributes attrs, DateTime c, DateTime a, DateTime w, long fileId = 0)
        { Name = name; IsDir = isDir; Length = length; Attributes = attrs; Created = c; Accessed = a; Modified = w; FileId = fileId; }
        public readonly string Name; public readonly bool IsDir; public readonly long Length; public readonly FileAttributes Attributes;
        public readonly DateTime Created, Accessed, Modified;
        /// <summary>NTFS file id when the listing reads it (hard links share it), else 0.</summary>
        public readonly long FileId;
    }

    /// <summary>
    /// Lists <paramref name="path"/> and reconciles the children of <paramref name="node"/>:
    /// new entries are added, vanished ones removed, changed files resized. Returns the sub
    /// folders found (with their nodes) so the caller can continue the walk.
    /// </summary>
    public List<(ContainerNode node, string path)> RefreshDirectory(ContainerNode node, string path, bool recurseInto)
    {
        var entries = new List<Entry>();
        bool denied = false;
        string io = IoPath(path);   // what the system calls get: the path, or its literal \\?\ form
        try
        {
            var opts = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false, BufferSize = Strategy.ListBufferBytes };
            // every entry is listed: directory reparse points are sorted out below (links skipped unless followed,
            // other kinds such as a cloud sync root - the OneDrive folder itself - walked like normal folders)
            if (_ids || _folderIds) entries.AddRange(IdLister.List(io, _token));   // the same listing, with each entry's id
            else
            {
                var e = new FileSystemEnumerable<Entry>(io,
                    (ref FileSystemEntry fse) => new Entry(fse.FileName.ToString(), fse.IsDirectory, fse.IsDirectory ? 0 : fse.Length,
                        fse.Attributes, fse.CreationTimeUtc.UtcDateTime, fse.LastAccessTimeUtc.UtcDateTime, fse.LastWriteTimeUtc.UtcDateTime), opts);
                foreach (var en in e)
                {
                    _token.ThrowIfCancellationRequested();
                    entries.Add(en);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException)
        {
            denied = true;
            Log.Warn("Cannot read " + path + ": access denied (running as administrator may help)");
        }
        catch (Exception ex)
        {
            denied = true;
            Log.Warn("Cannot read " + path + ": " + ex.Message);
        }

        // disk sizes (and optional ADS probing) need system calls: do them before taking the tree lock
        var disks = new long[entries.Count];
        var adsFlags = new bool[entries.Count];
        var linkTargets = new string?[entries.Count];   // real path for followed junctions / symlinks, null = skip
        for (int i = 0; i < entries.Count; i++)
        {
            var en = entries[i];
            if (en.IsDir)
            {
                if ((en.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var p = Path.Combine(path, en.Name);
                    linkTargets[i] = _cfg.FollowReparsePoints ? ReparseTarget(p, node) : IsLink(Path.Combine(io, en.Name)) ? null : p;
                }
                continue;
            }
            disks[i] = DiskSizeOf(io, en);
            if (_scanAds && NtfsAds.HasAlternateStreams(Path.Combine(io, en.Name), out var adsBytes))
            {
                adsFlags[i] = true;
                disks[i] += RoundToCluster(adsBytes);
            }
        }

        return Reconcile(node, path, entries, disks, adsFlags, linkTargets, denied, recurseInto);
    }

    /// <summary>
    /// Brings the children of <paramref name="node"/> in line with a directory listing (from the file system or from
    /// the NTFS master file table): new entries are added, vanished ones removed, changed files resized. For directory
    /// reparse points <paramref name="linkTargets"/> holds the path to walk (null = skip). Returns the sub folders to walk.
    /// </summary>
    internal List<(ContainerNode node, string path)> Reconcile(ContainerNode node, string path, List<Entry> entries, long[] disks, bool[] adsFlags, string?[] linkTargets, bool denied, bool recurseInto)
    {
        var result = new List<(ContainerNode, string)>();
        lock (_tree.Sync)
        {
            node.AccessDenied = denied;
            // folders at the deepest level are not listed (see FsNode.MaxDepth): they stay as not accessible
            bool atLimit = node.Level + 1 >= FsNode.MaxDepth;
            if (atLimit && !_tree.DepthLimitReported)
            {
                _tree.DepthLimitReported = true;
                Log.Warn($"Folders more than {FsNode.MaxDepth} levels deep are not scanned (under {path}).");
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // index the existing children once: FindChild is linear and directories can hold 100k+ entries
            var byName = new Dictionary<string, FsNode>(node.ChildCount, StringComparer.OrdinalIgnoreCase);
            foreach (var c in node.Children) if (!c.IsSynthetic) byName.TryAdd(c.Name, c);
            for (int i = 0; i < entries.Count; i++)
            {
                var en = entries[i];
                seen.Add(en.Name);
                byName.TryGetValue(en.Name, out var existing);
                if (en.IsDir)
                {
                    if (_cfg.IsExcluded(Path.Combine(path, en.Name)))
                    {
                        if (existing != null) node.RemoveChild(existing);
                        continue; // excluded by configuration
                    }
                    // real path to enumerate: the link target for followed junctions / symlinks (loop safe)
                    string childPath = Path.Combine(path, en.Name);
                    if ((en.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        if (linkTargets[i] == null) { if (existing != null) node.RemoveChild(existing); continue; }
                        childPath = linkTargets[i]!;
                    }
                    if (existing is FolderNode f)
                    {
                        // another folder under the same name (deleted and made again, or moved here in place of it): what is
                        // known of the old one is not its content, it is listed again
                        if (f.FileId != 0 && en.FileId != 0 && f.FileId != en.FileId) { f.Enumerated = false; f.ScanComplete = false; }
                        f.Attributes = en.Attributes; f.Created = en.Created; f.Accessed = en.Accessed; f.Modified = en.Modified; f.FileId = en.FileId;
                        if (atLimit) { f.Enumerated = true; f.ScanComplete = true; f.AccessDenied = true; }
                        else if (recurseInto && !f.ScanComplete) result.Add((f, childPath));
                    }
                    else
                    {
                        if (existing != null) node.RemoveChild(existing);
                        var nf = new FolderNode(en.Name) { Attributes = en.Attributes, Created = en.Created, Accessed = en.Accessed, Modified = en.Modified, FileId = en.FileId };
                        node.AddChild(nf);
                        if (atLimit) { nf.Enumerated = true; nf.ScanComplete = true; nf.AccessDenied = true; }
                        else if (recurseInto) result.Add((nf, childPath));
                    }
                }
                else
                {
                    long disk = disks[i];
                    bool ads = adsFlags[i];
                    if (existing is FileNode fn)
                    {
                        fn.Attributes = en.Attributes; fn.Created = en.Created; fn.Accessed = en.Accessed; fn.Modified = en.Modified; fn.HasAlternateDataStream = ads;
                        // a hard link counted at another name stays at 0 until the next grouping pass
                        long fileId = _ids ? en.FileId : 0;    // folder order only: files keep no id
                        if (fn.FileId != fileId) { fn.FileId = fileId; fn.CountedElsewhere = false; }
                        else if (fn.CountedElsewhere) { if (_ids) disk = 0; else fn.CountedElsewhere = false; }
                        if (fn.LogicalSize != en.Length || fn.DiskSize != disk) node.ResizeChild(fn, en.Length, disk);
                    }
                    else
                    {
                        if (existing != null) node.RemoveChild(existing);
                        var nfile = new FileNode(en.Name)
                        {
                            LogicalSize = en.Length, DiskSize = disk, Attributes = en.Attributes,
                            Created = en.Created, Accessed = en.Accessed, Modified = en.Modified, HasAlternateDataStream = ads, FileId = _ids ? en.FileId : 0,
                        };
                        node.AddChild(nfile);
                    }
                }
            }
            if (!denied)
            {
                foreach (var c in node.ChildrenSnapshot())
                    if (!c.IsSynthetic && !seen.Contains(c.Name)) node.RemoveChild(c);
            }
            node.Enumerated = true;
            if (recurseInto && !denied && result.Count == 0) node.ScanComplete = true; // leaf folder
            node.Touch();
        }
        return result;
    }

    private void ScanTreeParallel(ContainerNode start, string startPath, int workers, bool resume = false)
    {
        var queue = new System.Collections.Concurrent.ConcurrentQueue<(ContainerNode node, string path)>();
        queue.Enqueue((start, startPath));
        int pending = 1;                                  // folders queued or being listed
        using var signal = new SemaphoreSlim(0);
        Exception? failure = null;

        void Worker()
        {
            try
            {
                while (Volatile.Read(ref pending) > 0 && failure == null)
                {
                    _token.ThrowIfCancellationRequested();
                    if (!queue.TryDequeue(out var item)) { signal.Wait(20, _token); continue; }
                    try
                    {
                        WaitIfPaused();
                        var (node, path) = item;
                        if (node.ScanComplete) continue;       // done by another job
                        var subFolders = Next(node, path, resume);
                        foreach (var sf in subFolders)
                        {
                            Interlocked.Increment(ref pending);
                            queue.Enqueue(sf);
                        }
                        if (subFolders.Count > 0) signal.Release(Math.Min(subFolders.Count, workers));
                        lock (_tree.Sync) node.Enumerated = true;
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref pending) == 0) signal.Release(workers);   // wake everybody up to finish
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); signal.Release(workers); }
        }

        var threads = new List<Thread>();
        for (int i = 1; i < workers; i++)
        {
            var t = new Thread(Worker) { IsBackground = true, Name = "ScanWorker" + i, Priority = Thread.CurrentThread.Priority };
            threads.Add(t);
            t.Start();
        }
        Worker();
        foreach (var t in threads) t.Join();
        _token.ThrowIfCancellationRequested();
        if (failure != null) throw failure;
    }

    // targets of junctions / symbolic links followed so far (real paths): each physical folder is walked once
    private readonly HashSet<string> _followedTargets = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxLinkNesting = 64;

    /// <summary>
    /// Decides whether a directory reparse point is entered and where. Loops (the target is the link's
    /// own ancestor), targets inside the scanned root (already counted as themselves) and targets followed
    /// before are skipped. Reparse points that are not links (OneDrive placeholders, dedup) are walked as
    /// normal folders. Returns the real path to enumerate, or null to skip.
    /// </summary>
    private string? ReparseTarget(string linkPath, ContainerNode parent)
    {
        string? target;
        try { target = new DirectoryInfo(linkPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName; }
        catch (Exception ex) { Log.Warn("Unable to resolve link " + linkPath + " - " + ex.Message); return null; }
        if (target == null) return linkPath;                      // not a link
        target = target.TrimEnd('\\');
        string link = linkPath.TrimEnd('\\');
        if (string.Equals(link, target, StringComparison.OrdinalIgnoreCase) || link.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn("Skipping " + linkPath + ": it links to its own ancestor " + target + " (loop)");
            return null;
        }
        string root = _tree.Root.FullPath.TrimEnd('\\');
        if (string.Equals(target, root, StringComparison.OrdinalIgnoreCase) || target.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
        {
            Log.Fine("Skipping " + linkPath + ": target " + target + " is inside the scanned tree");
            return null;
        }
        if (parent.Level >= MaxLinkNesting)
        {
            Log.Warn("Skipping " + linkPath + ": link nesting deeper than " + MaxLinkNesting);
            return null;
        }
        lock (_followedTargets)
        {
            if (!_followedTargets.Add(target)) { Log.Fine("Skipping " + linkPath + ": " + target + " already followed"); return null; }
        }
        return target;
    }

    /// <summary>
    /// True for junctions, symbolic links and mount points (name surrogates); false for reparse points that are the
    /// folder itself, such as a cloud sync root (the OneDrive folder) or a deduplicated folder. Unreadable = link (skipped).
    /// </summary>
    /// <summary>
    /// The form of a folder path the system calls need: Win32 drops trailing dots and spaces from every path segment
    /// ("name." would open "name", a folder that Linux tools or archives can create) and the plain calls stop at
    /// MAX_PATH, so such paths are passed in their literal \\?\ form.
    /// </summary>
    internal static string IoPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return path;
        bool literal = path.Length >= 240;
        for (int i = 3; i < path.Length && !literal; i++)
            if (path[i] is '.' or ' ' && (i + 1 == path.Length || path[i + 1] == '\\') && path[i - 1] != '\\') literal = true;
        if (!literal) return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path;
    }

    private static bool IsLink(string path)
    {
        try { return new DirectoryInfo(path).LinkTarget != null; }
        catch (Exception ex) { Log.Fine("Unable to read reparse point " + path + " - " + ex.Message); return true; }
    }

    private long RoundToCluster(long size)
    {
        if (size <= 0) return 0;
        return (size + _cluster - 1) / _cluster * _cluster;
    }

    /// <summary>Attributes of files whose allocation differs from their length: compressed, sparse, and cloud / HSM placeholders whose data is not on this disk.</summary>
    private const FileAttributes AskAllocation = FileAttributes.Compressed | FileAttributes.SparseFile | FileAttributes.Offline | Cloud.CloudFiles.RecallOnDataAccess;

    private long DiskSizeOf(string dir, in Entry e)
    {
        // an "online-only" cloud file (OneDrive Files On-Demand) has its full length but occupies nothing here;
        // GetCompressedFileSize reads the allocation without downloading it
        if ((e.Attributes & AskAllocation) != 0)
        {
            try
            {
                uint low = Native.GetCompressedFileSizeW(Path.Combine(dir, e.Name), out uint high);
                if (low != 0xFFFFFFFF || System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 0)
                    return RoundToCluster(((long)high << 32) | low);
            }
            catch { }
            // unreadable: a file whose data lives elsewhere still occupies nothing here
            if ((e.Attributes & (FileAttributes.Offline | Cloud.CloudFiles.RecallOnDataAccess)) != 0) return 0;
        }
        return RoundToCluster(e.Length);
    }
}

/// <summary>NTFS alternate data stream probing.</summary>
public static class NtfsAds
{
    public static bool HasAlternateStreams(string file, out long totalBytes)
    {
        totalBytes = 0;
        IntPtr h = IntPtr.Zero;
        try
        {
            h = Native.FindFirstStreamW(file, 0, out var data, 0);
            if (h == Native.INVALID_HANDLE_VALUE) return false;
            bool any = false;
            do
            {
                if (!string.Equals(data.cStreamName, "::$DATA", StringComparison.Ordinal))
                {
                    any = true;
                    totalBytes += data.StreamSize;
                }
            } while (Native.FindNextStreamW(h, out data));
            return any;
        }
        catch { return false; }
        finally { if (h != IntPtr.Zero && h != Native.INVALID_HANDLE_VALUE) Native.FindClose(h); }
    }
}
