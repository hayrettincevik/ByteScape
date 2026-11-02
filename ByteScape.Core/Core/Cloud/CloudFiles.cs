using System.Runtime.CompilerServices;
using ByteScape.Core.Model;
using ByteScape.Core.Util;
using Microsoft.Win32;

namespace ByteScape.Core.Cloud;

/// <summary>Where a cloud file's data is (OneDrive / Dropbox / iCloud "Files On-Demand").</summary>
public enum CloudState
{
    /// <summary>Not a file of a cloud folder.</summary>
    None,
    /// <summary>Only a placeholder is here, the data is in the cloud: it occupies no space on this disk.</summary>
    OnlineOnly,
    /// <summary>Downloaded copy of a cloud file: "Free up space" can remove it and keep the file online.</summary>
    LocalCopy,
    /// <summary>"Always keep on this device": the provider keeps the local copy on purpose.</summary>
    AlwaysKept,
}

/// <summary>A cloud sync root (the OneDrive folder...) and the program that syncs it.</summary>
public sealed record SyncRoot(string Path, string Provider);

/// <summary>
/// Cloud file awareness from what a scan already knows: placeholder attributes (RECALL_ON_DATA_ACCESS = online-only,
/// PINNED = always keep) and the sync roots the files lie in (the Windows sync root registry, the Cloud Files API
/// for folders that show up as reparse points - the root itself does - and, in snapshots of other machines, the
/// usual folder names). Nothing here reads file data, so nothing gets downloaded.
/// </summary>
public static class CloudFiles
{
    public const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    public const FileAttributes Pinned = (FileAttributes)0x00080000;
    public const FileAttributes Unpinned = (FileAttributes)0x00100000;
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes CloudMarks = RecallOnOpen | Pinned | Unpinned | RecallOnDataAccess;
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    private sealed class Box { public SyncRoot? Root; }
    private static readonly ConditionalWeakTable<ContainerNode, Box> Cache = new();

    /// <summary>Provider of the sync root a path lies in (Cloud Files API), null when it is not in one. Tests replace it.</summary>
    internal static Func<string, string?> ProviderByPath = Interop.Native.SyncRootProvider;

    /// <summary>Provider whose sync root contains <paramref name="path"/> (Cloud Files API), or null.</summary>
    public static string? ProviderOf(string path) => ProviderByPath(path);

    private static IReadOnlyList<SyncRoot>? _known;
    private static HashSet<string> _knownLeaves = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sync roots registered on this machine (OneDrive accounts, Dropbox, iCloud...), read once.</summary>
    public static IReadOnlyList<SyncRoot> KnownRoots
    {
        get
        {
            if (_known == null) SetKnownRoots(ReadRegisteredRoots());
            return _known!;
        }
    }

    /// <summary>Tests: replace the registered roots.</summary>
    internal static void SetKnownRoots(IReadOnlyList<SyncRoot> roots)
    {
        _knownLeaves = new HashSet<string>(roots.Select(r => System.IO.Path.GetFileName(r.Path.TrimEnd('\\'))), StringComparer.OrdinalIgnoreCase);
        _known = roots;
    }

    /// <summary>Where the data of a file is; folders and synthetic nodes are <see cref="CloudState.None"/>.</summary>
    public static CloudState StateOf(FsNode n)
    {
        if (n is not FileNode f) return CloudState.None;
        var a = f.Attributes;
        if ((a & (RecallOnDataAccess | FileAttributes.Offline)) != 0) return CloudState.OnlineOnly;
        if ((a & Pinned) != 0) return CloudState.AlwaysKept;
        return f.Parent is { } p && RootOf(p) != null ? CloudState.LocalCopy : CloudState.None;
    }

    /// <summary>The sync root a node lies in (a file's folder decides), or null.</summary>
    public static SyncRoot? SyncRootOf(FsNode n)
    {
        if (n.IsSynthetic) return null;
        var c = n as ContainerNode ?? n.Parent;
        return c == null ? null : RootOf(c);
    }

    private static SyncRoot? RootOf(ContainerNode c)
    {
        if (Cache.TryGetValue(c, out var box)) return box.Root;
        SyncRoot? r;
        if (c is DriveNode { IsSnapshotFrame: true }) r = null;
        else if (c.Parent is ContainerNode p && p is not DriveNode { IsSnapshotFrame: true }) r = RootOf(p) ?? Own(c, top: false);
        else r = Own(c, top: true);   // top of the tree: its ancestors are not scanned, the path decides
        Cache.AddOrUpdate(c, new Box { Root = r });
        return r;
    }

    /// <summary>The folder itself starts a sync root (or, at the top of a tree, lies in one).</summary>
    internal static SyncRoot? Own(ContainerNode c, bool top)
    {
        var attrs = (c as FolderNode)?.Attributes ?? default;
        bool hint = (attrs & (FileAttributes.ReparsePoint | CloudMarks)) != 0;
        var known = KnownRoots;
        if (!top && !hint && !_knownLeaves.Contains(c.Name)) return null;   // the common case: no system call, no path
        string path;
        try { path = c.RealPath(); } catch { return null; }
        if (path.Length < 3) return null;   // a drive root is never a sync root
        foreach (var k in known)
            if (string.Equals(k.Path, path, OIC) || (top && path.StartsWith(k.Path + "\\", OIC))) return k;
        // sync roots are local folders only; a network path (maybe from a snapshot someone sent) is never looked up:
        // Windows would connect to that server and offer the user's sign-in there
        if ((hint || top) && !path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var provider = ProviderByPath(path);
            if (provider != null) return new SyncRoot(path, provider);
        }
        // a snapshot from another machine: the reparse folder named like a provider's folder, or files marked by one
        if ((attrs & FileAttributes.ReparsePoint) != 0 && ProviderFromName(c.Name) is { } byName) return new SyncRoot(path, byName);
        if ((attrs & CloudMarks) != 0) return new SyncRoot(path, T("Cloud"));
        return null;
    }

    /// <summary>"OneDrive - Contoso" → OneDrive, "Dropbox", "iCloudDrive"...</summary>
    internal static string? ProviderFromName(string folder)
    {
        if (folder.Equals("OneDrive", OIC) || folder.StartsWith("OneDrive - ", OIC)) return "OneDrive";
        if (folder.Equals("Dropbox", OIC) || folder.StartsWith("Dropbox (", OIC)) return "Dropbox";
        if (folder.Equals("iCloudDrive", OIC) || folder.Equals("iCloud Drive", OIC)) return "iCloud Drive";
        if (folder.Equals("Box", OIC)) return "Box";
        if (folder.Equals("Google Drive", OIC) || folder.Equals("My Drive", OIC)) return "Google Drive";
        return null;
    }

    /// <summary>Tests: forget cached answers (after replacing the providers).</summary>
    internal static void ResetCache(ContainerNode root)
    {
        Cache.Remove(root);
        foreach (var n in root.Descendants()) if (n is ContainerNode c) Cache.Remove(c);
    }

    // ------------------------------------------------------------------ registered sync roots

    private static List<SyncRoot> ReadRegisteredRoots()
    {
        var list = new List<SyncRoot>();
        try
        {
            using var mgr = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
            if (mgr != null)
                foreach (var id in mgr.GetSubKeyNames())
                {
                    using var k = mgr.OpenSubKey(id);
                    using var users = k?.OpenSubKey("UserSyncRoots");
                    if (k == null || users == null) continue;
                    string provider = k.GetValue("DisplayNameResource") is string dn && dn.Length > 0
                        ? (dn.StartsWith('@') ? Interop.Native.LoadIndirectString(dn) : dn) ?? ""
                        : "";
                    if (provider.Length == 0) provider = id.Split('!')[0];   // "OneDrive!S-1-5-21-…!Personal|…"
                    foreach (var v in users.GetValueNames())
                        if (users.GetValue(v) is string p && p.Length > 3) list.Add(new SyncRoot(p.TrimEnd('\\'), provider));
                }
        }
        catch (Exception ex) { Log.Debug("Sync root registry: " + ex.Message); }
        try
        {
            using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            if (accounts != null)
                foreach (var name in accounts.GetSubKeyNames())
                {
                    using var a = accounts.OpenSubKey(name);
                    if (a?.GetValue("UserFolder") is string folder && folder.Length > 3 && !list.Any(r => string.Equals(r.Path, folder.TrimEnd('\\'), OIC)))
                        list.Add(new SyncRoot(folder.TrimEnd('\\'), "OneDrive"));
                }
        }
        catch (Exception ex) { Log.Debug("OneDrive accounts: " + ex.Message); }
        if (list.Count > 0) Log.Debug("Cloud sync roots: " + string.Join(", ", list.Select(r => r.Provider + " = " + r.Path)));
        return list;
    }

    // ------------------------------------------------------------------ totals

    /// <summary>
    /// Cloud folders below <paramref name="root"/> with what their files occupy: local copies that "Free up space"
    /// could remove, copies kept on purpose, and online-only files (no space here). Background thread, tree lock per folder.
    /// </summary>
    public static CloudSummary Summarize(ContainerNode root, CancellationToken token, object? sync = null)
    {
        var roots = new List<CloudRootSummary>();
        List<FsNode> Kids(ContainerNode c)
        {
            if (sync == null) return c.ChildrenSnapshot();
            lock (sync) return c.ChildrenSnapshot();
        }
        // looks for the top cloud folders without caching every folder of the drive
        void Find(ContainerNode c, bool top)
        {
            token.ThrowIfCancellationRequested();
            bool frame = c is DriveNode { IsSnapshotFrame: true };
            if (!c.IsSynthetic && !frame && Own(c, top) is { } r)
            {
                roots.Add(CountIn(c, r, token, sync));
                return;
            }
            foreach (var k in Kids(c)) if (k is ContainerNode kc) Find(kc, frame);
        }
        Find(root, top: root.Parent == null || root.Parent is DriveNode { IsSnapshotFrame: true });
        roots.Sort((a, b) => b.LocalBytes.CompareTo(a.LocalBytes));
        return new CloudSummary(roots);
    }

    /// <summary>How the files below a folder of <paramref name="root"/> are held (a sync root, or any folder inside one).</summary>
    public static CloudRootSummary CountIn(ContainerNode folder, SyncRoot root, CancellationToken token, object? sync = null)
    {
        var s = new CloudRootSummary(root, folder);
        List<FsNode> Kids(ContainerNode c)
        {
            if (sync == null) return c.ChildrenSnapshot();
            lock (sync) return c.ChildrenSnapshot();
        }
        void Count(ContainerNode c)
        {
            token.ThrowIfCancellationRequested();
            foreach (var k in Kids(c))
            {
                if (k is ContainerNode kc) { Count(kc); continue; }
                if (k is not FileNode f) continue;
                // every file here is the cloud's: attributes tell online-only and pinned apart, the rest are local copies
                var a = f.Attributes;
                if ((a & (RecallOnDataAccess | FileAttributes.Offline)) != 0) { s.OnlineBytes += f.LogicalSize; s.OnlineFiles++; }
                else if ((a & Pinned) != 0) { s.KeptBytes += f.DiskSize; s.KeptFiles++; }
                else { s.LocalBytes += f.DiskSize; s.LocalFiles++; }
            }
        }
        Count(folder);
        return s;
    }
}

/// <summary>One cloud folder of a view and how its files are held.</summary>
public sealed class CloudRootSummary
{
    public CloudRootSummary(SyncRoot root, ContainerNode node) { Root = root; Node = node; }
    public SyncRoot Root { get; }
    /// <summary>The top cloud folder in the tree (the sync root, or the view root when the view starts inside one).</summary>
    public ContainerNode Node { get; }
    /// <summary>Downloaded copies "Free up space" could remove (on-disk size).</summary>
    public long LocalBytes { get; internal set; }
    public int LocalFiles { get; internal set; }
    /// <summary>"Always keep on this device" copies (on-disk size).</summary>
    public long KeptBytes { get; internal set; }
    public int KeptFiles { get; internal set; }
    /// <summary>Online-only files: their full size, none of it on this disk.</summary>
    public long OnlineBytes { get; internal set; }
    public int OnlineFiles { get; internal set; }
}

public sealed record CloudSummary(IReadOnlyList<CloudRootSummary> Roots)
{
    public static readonly CloudSummary Empty = new(Array.Empty<CloudRootSummary>());
    public long LocalBytes => Roots.Sum(r => r.LocalBytes);
    public long KeptBytes => Roots.Sum(r => r.KeptBytes);
    public long OnlineBytes => Roots.Sum(r => r.OnlineBytes);
    /// <summary>"OneDrive", "OneDrive, Dropbox".</summary>
    public string Providers => string.Join(", ", Roots.Select(r => r.Root.Provider).Distinct(StringComparer.OrdinalIgnoreCase));
}
