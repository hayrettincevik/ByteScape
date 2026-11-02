using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ByteScape.Core.Model;
using ByteScape.Core.Util;

namespace ByteScape.Core.Scanning;

/// <summary>A scan kept on disk (unfinished, or complete for a quick rescan): what was scanned of which path, when.</summary>
public sealed record ResumeInfo(string Path, DateTime SavedUtc, int Files, int Folders, long Bytes, int PendingFolders, int Nodes)
{
    /// <summary>The file of the saved tree.</summary>
    public string Key { get; init; } = "";
    /// <summary>The scan was complete: kept so that the next scan of the path lists only what the change journal says changed since <see cref="Mark"/>.</summary>
    public bool Complete { get; init; }
    /// <summary>Where the NTFS change journal stood for this tree (null: not known, the tree cannot be brought up to date from it).</summary>
    public JournalMark? Mark { get; init; }
}

/// <summary>
/// Keeps a scan on disk for the next time its path is opened: a stopped scan (it continues with the folders it had not
/// reached) and a complete scan of a drive with an NTFS change journal (only the folders changed since are listed). The
/// tree is written whole and compressed: every element with its sizes (on disk too: compressed, sparse, cloud and
/// hard-linked files take less than their length), attributes, times, NTFS file id, and for folders whether they were
/// listed yet or denied. Kept next to the configuration, at most a week (a month for complete scans, which the journal
/// checks anyway) and a few paths.
/// </summary>
public static class ScanResume
{
    private const int Magic = 0x544B5342;   // "BSKT"
    private const int Version = 2;
    private const byte KindFile = 1, KindFolder = 2;
    private const byte NotListed = 1, Denied = 2, Elsewhere = 4, Ads = 8;
    public static TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(7);
    public static TimeSpan MaxAgeComplete { get; set; } = TimeSpan.FromDays(30);
    private const int MaxKept = 4;

    /// <summary>Tests: keep the files there instead of next to the configuration.</summary>
    internal static string? DirOverride;
    public static string Dir => DirOverride ?? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Config.AppConfig.ConfigPath)!, "ByteScapeResume");

    private static string KeyOf(string path)
    {
        var norm = DataCache.NormalizePath(path).TrimEnd('\\').ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(norm)))[..16];
    }

    private static string File(string key, string ext) => System.IO.Path.Combine(Dir, key + ext);

    /// <summary>A key as <see cref="KeyOf"/> makes it: 16 hex digits. A key read from a file is checked before it names a
    /// file to read or delete (an edited one could name a path elsewhere, "..\\" or a full path).</summary>
    internal static bool IsKey(string key) => key.Length == 16 && key.All(char.IsAsciiHexDigitUpper);

    /// <summary>Files of a kept scan, also those of the first format (a snapshot and a state file), deleted with it.</summary>
    private static readonly string[] Extensions = { ".json", ".tree", ".sns", ".state" };

    /// <summary>
    /// Saves an unfinished scan of a live tree (its master scan stopped or paused), or a complete one whose changes since can be
    /// read from the NTFS change journal (quick rescans on). False when there is nothing to keep.
    /// </summary>
    public static bool Save(ScanTree tree)
    {
        if (!tree.IsLive || !tree.Root.Enumerated) return false;
        bool complete = tree.Root.ScanComplete;
        if (complete && (tree.ChangeMark == null || !Config.AppConfig.Current.QuickRescan)) return false;
        var path = DataCache.NormalizePath(tree.Root.FullPath);
        var key = KeyOf(path);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(Dir);
            string data = File(key, ".tree"), json = File(key, ".json");
            int nodes = 0, pending = 0;
            lock (tree.Sync)
            {
                using var fs = new FileStream(data + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                using var z = new BrotliStream(fs, CompressionLevel.Fastest);
                using var w = new BinaryWriter(new BufferedStream(z, 1 << 16), Encoding.UTF8);
                w.Write(Magic); w.Write(Version);
                void Walk(FsNode n)
                {
                    nodes++;
                    if (n is FileNode f)
                    {
                        byte flags = 0;
                        long disk = f.DiskSize;
                        // counted at another name: its size is 0, the file's own size is kept for when it is left alone
                        if (f.CountedElsewhere) { flags |= Elsewhere; disk = tree.HardLinks.OwnDiskOf(f); }
                        if (f.HasAlternateDataStream) flags |= Ads;
                        w.Write(KindFile); w.Write(f.Name);
                        w.Write(f.LogicalSize); w.Write(disk); w.Write((int)f.Attributes);
                        w.Write(f.Created.Ticks); w.Write(f.Accessed.Ticks); w.Write(f.Modified.Ticks);
                        w.Write(f.FileId); w.Write(flags); w.Write((byte)f.Tag);
                    }
                    else if (n is ContainerNode c)
                    {
                        byte flags = 0;
                        if (!c.Enumerated) { flags |= NotListed; pending++; }
                        if (c.AccessDenied) flags |= Denied;
                        var fo = c as FolderNode;
                        w.Write(KindFolder); w.Write(c.Name);
                        w.Write((int)(fo?.Attributes ?? 0));
                        w.Write(fo?.Created.Ticks ?? 0); w.Write(fo?.Accessed.Ticks ?? 0); w.Write(fo?.Modified.Ticks ?? 0);
                        w.Write(fo?.FileId ?? 0); w.Write(flags); w.Write((byte)c.Tag);
                        var kids = c.Children.Where(x => !x.IsSynthetic).ToList();
                        w.Write(kids.Count);
                        foreach (var k in kids) Walk(k);
                    }
                }
                Walk(tree.Root);
                w.Write(nodes); w.Write(Magic);   // the end: a cut file is never read as a whole one
            }
            var info = new ResumeInfo(path, DateTime.UtcNow, tree.Root.FileCount, tree.Root.FolderCount, tree.Root.DiskSize, pending, nodes)
            { Key = key, Complete = complete, Mark = tree.ChangeMark };
            System.IO.File.WriteAllText(json + ".tmp", JsonSerializer.Serialize(info));
            System.IO.File.Move(data + ".tmp", data, true);
            System.IO.File.Move(json + ".tmp", json, true);   // last: a half written save is never found
            foreach (var ext in new[] { ".sns", ".state" }) try { System.IO.File.Delete(File(key, ext)); } catch { }
            Log.Debug(complete ? $"Complete scan of {path} kept for a quick rescan: {info.Files:N0} files ({sw.ElapsedMilliseconds} ms)"
                               : $"Unfinished scan of {path} kept: {info.Files:N0} files, {pending:N0} folders to go ({sw.ElapsedMilliseconds} ms)");
            foreach (var old in All(includeOld: true).Skip(MaxKept)) Delete(old.Path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("The " + (complete ? "" : "unfinished ") + "scan of " + path + " could not be kept: " + ex.Message);
            return false;
        }
    }

    /// <summary>Scans kept, newest first (unfinished ones a week at most, complete ones a month, unless <paramref name="includeOld"/>).</summary>
    public static List<ResumeInfo> All(bool includeOld = false)
    {
        var list = new List<ResumeInfo>();
        try
        {
            if (!Directory.Exists(Dir)) return list;
            foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
            {
                try
                {
                    var info = JsonSerializer.Deserialize<ResumeInfo>(System.IO.File.ReadAllText(f));
                    if (info == null || !IsKey(info.Key) || !string.Equals(info.Key, System.IO.Path.GetFileNameWithoutExtension(f), StringComparison.Ordinal)) continue;
                    if (!System.IO.File.Exists(File(info.Key, ".tree")))
                    {
                        // the first format (snapshot + state) or a lost tree: nothing to restore, the leftovers go
                        if (!Cleanup.AccessProbe.IsElevated) foreach (var ext in Extensions) try { System.IO.File.Delete(File(info.Key, ext)); } catch { }
                        continue;
                    }
                    if (!includeOld && DateTime.UtcNow - info.SavedUtc > (info.Complete ? MaxAgeComplete : MaxAge)) continue;
                    list.Add(info);
                }
                catch { }
            }
        }
        catch { }
        return list.OrderByDescending(i => i.SavedUtc).ToList();
    }

    /// <summary>The scan kept for exactly this path, if any (and not too old).</summary>
    public static ResumeInfo? Find(string path)
    {
        var key = KeyOf(path);
        return All().FirstOrDefault(i => i.Key == key);
    }

    public static void Delete(string path)
    {
        var key = KeyOf(path);
        foreach (var ext in Extensions)
            try { System.IO.File.Delete(File(key, ext)); } catch { }
    }

    /// <summary>
    /// Puts a kept scan into the (empty) live tree of the same path, ready for <see cref="ScanTree.StartMasterScan(bool, bool)"/>
    /// with resume (the folders not listed then are listed now) or, for a complete one, with changes only (the folders the
    /// change journal names are listed again). False (tree untouched) when the file does not fit.
    /// </summary>
    public static bool Restore(ScanTree tree, ResumeInfo info)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var shadows = new List<(FileNode Node, long Disk)>();
            DriveNode loaded;
            using (var fs = new FileStream(File(info.Key, ".tree"), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var z = new BrotliStream(fs, CompressionMode.Decompress))
            using (var r = new BinaryReader(new BufferedStream(z, 1 << 16), Encoding.UTF8))
            {
                if (r.ReadInt32() != Magic || r.ReadInt32() != Version) throw new InvalidDataException("not a kept scan of this version");
                int nodes = 0;
                static DateTime Time(long ticks) => ticks <= 0 ? default : new DateTime(ticks, DateTimeKind.Utc);
                // bottom up: a folder is filled before it joins its parent, so each size is added once, not up the whole chain
                FsNode Read(bool root, int depth = 0)
                {
                    nodes++;
                    if (depth > FsNode.MaxDepth + 1) throw new InvalidDataException("folders nested too deep");
                    byte kind = r.ReadByte();
                    string name = r.ReadString();
                    if (kind == KindFile)
                    {
                        var f = new FileNode(name) { LogicalSize = r.ReadInt64(), DiskSize = r.ReadInt64(), Attributes = (FileAttributes)r.ReadInt32() };
                        f.Created = Time(r.ReadInt64()); f.Accessed = Time(r.ReadInt64()); f.Modified = Time(r.ReadInt64());
                        f.FileId = r.ReadInt64();
                        byte flags = r.ReadByte();
                        f.Tag = (TagColor)r.ReadByte();
                        f.HasAlternateDataStream = (flags & Ads) != 0;
                        if ((flags & Elsewhere) != 0) { f.CountedElsewhere = true; shadows.Add((f, f.DiskSize)); f.DiskSize = 0; }
                        return f;
                    }
                    if (kind != KindFolder) throw new InvalidDataException("unknown element");
                    var attrs = (FileAttributes)r.ReadInt32();
                    DateTime c = Time(r.ReadInt64()), a = Time(r.ReadInt64()), m = Time(r.ReadInt64());
                    long id = r.ReadInt64();
                    byte fl = r.ReadByte();
                    var tag = (TagColor)r.ReadByte();
                    ContainerNode node = root ? new DriveNode(name) : new FolderNode(name) { Attributes = attrs, Created = c, Accessed = a, Modified = m, FileId = id };
                    node.Tag = tag;
                    node.Enumerated = (fl & NotListed) == 0;
                    node.AccessDenied = (fl & Denied) != 0;
                    int count = r.ReadInt32();
                    if (count < 0) throw new InvalidDataException("bad element count");
                    for (int i = 0; i < count; i++) node.AddChild(Read(false, depth + 1));
                    return node;
                }
                loaded = (DriveNode)Read(true);
                if (r.ReadInt32() != nodes || r.ReadInt32() != Magic || nodes != info.Nodes) throw new InvalidDataException("the tree does not end where it should");
            }
            lock (tree.Sync)
            {
                if (tree.Root.ChildCount > 0 || tree.IsMasterScanning) return false;   // somebody scanned meanwhile
                foreach (var ch in loaded.Children.ToList()) { loaded.RemoveChild(ch); tree.Root.AddChild(ch); }
                tree.Root.Enumerated = loaded.Enumerated;
                tree.Root.AccessDenied = loaded.AccessDenied;
                tree.Root.ScanComplete = false;
                foreach (var (node, disk) in shadows) tree.HardLinks.Seed(node, disk);
                tree.ChangeMark = info.Mark;
            }
            Log.Debug(info.Complete ? $"Complete scan of {info.Path} restored: {info.Files:N0} files ({sw.ElapsedMilliseconds} ms)"
                                    : $"Unfinished scan of {info.Path} restored: {info.Files:N0} files, {info.PendingFolders:N0} folders to go ({sw.ElapsedMilliseconds} ms)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("The kept scan of " + info.Path + " could not be read (" + ex.Message + "): scanning again");
            return false;
        }
    }
}
