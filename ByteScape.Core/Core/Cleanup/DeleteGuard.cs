using System.ComponentModel;
using System.Runtime.InteropServices;
using ByteScape.Core.Scanning;
using ByteScape.Core.Util;
using Microsoft.Win32.SafeHandles;

namespace ByteScape.Core.Cleanup;

/// <summary>A file or folder as the file system knows it: the volume and the object's number on it (the same whatever its name).</summary>
public readonly record struct FileIdentity(uint Volume, ulong Index);

/// <summary>
/// Keeps a delete where it was checked. A program without rights can make a privileged one delete elsewhere by swapping a
/// folder on the way for a junction or symbolic link between the check and the delete ("junction swap"). While an element
/// is deleted, every folder from the drive root down to it is therefore held open without delete sharing — none of them can
/// be renamed, moved or replaced meanwhile — and each must still be what the scan saw: no link below the scanned folder
/// (the scan does not follow links), and the scanned folder itself the same folder (volume and file number) as when the scan
/// started. Folders are then emptied through handles opened on the link itself, never through what it points to.
/// </summary>
public sealed class DeleteGuard
{
    /// <summary>The folder the scan started from (links above and at it were the user's choice).</summary>
    public required string ScanRoot { get; init; }
    /// <summary>The scanned folder's identity when the scan started; null when it could not be read (then not compared).</summary>
    public FileIdentity? RootId { get; init; }
    /// <summary>The scan followed links (Settings): links below the scanned folder are expected then.</summary>
    public bool LinksFollowed { get; init; }
    /// <summary>A folder on the way that cannot be held open refuses the delete (running as administrator: only then is there
    /// a privilege to abuse); otherwise the delete goes on without holding it.</summary>
    public bool Strict { get; init; } = AccessProbe.IsElevated;

    public static DeleteGuard For(ScanTree tree) => new()
    {
        ScanRoot = tree.Root.FullPath, RootId = tree.RootIdentity, LinksFollowed = Config.AppConfig.Current.FollowReparsePoints,
    };

    /// <summary>
    /// Holds the folders from the drive root down to <paramref name="path"/>'s parent and checks them; null (and why in
    /// <paramref name="error"/>) when the delete must not happen. Dispose the result after the delete.
    /// </summary>
    public IDisposable? Hold(string path, out string error)
    {
        error = "";
        var pins = new Pins();
        try
        {
            string full = Norm(Path.GetFullPath(path));
            string root = Norm(Path.GetFullPath(ScanRoot));
            var parent = Path.GetDirectoryName(full);
            if (parent == null) { error = T("a drive root is never deleted"); pins.Dispose(); return null; }
            foreach (var dir in Chain(parent))
            {
                var d = Norm(dir);
                bool belowRoot = d.Length > root.Length && d.StartsWith(root, StringComparison.OrdinalIgnoreCase) && (root.EndsWith('\\') || d[root.Length] == '\\');
                var h = SafeFs.Open(dir, SafeFs.ReadAttributes, SafeFs.ShareReadWrite);
                if (h.IsInvalid)
                {
                    int e = Marshal.GetLastWin32Error();
                    h.Dispose();
                    if (e is SafeFs.ErrorFileNotFound or SafeFs.ErrorPathNotFound) { error = F($"{dir} is not there any more"); pins.Dispose(); return null; }
                    if (Strict) { error = F($"{dir} is in use and cannot be held while deleting; try again"); pins.Dispose(); return null; }
                    Log.Debug($"Delete guard: {dir} not held (error {e})");
                    continue;
                }
                pins.Add(h);
                if (!SafeFs.TryAttributes(h, out var attrs, out uint tag)) continue;
                if ((attrs & FileAttributes.Directory) == 0) { error = F($"{dir} is not a folder any more"); pins.Dispose(); return null; }
                if (belowRoot && !LinksFollowed && (attrs & FileAttributes.ReparsePoint) != 0 && SafeFs.IsNameSurrogate(tag))
                {
                    error = F($"{dir} has become a link (junction or symbolic link) since the scan: nothing is deleted through it");
                    pins.Dispose();
                    return null;
                }
            }
            // the scanned folder is still the one the scan started from (a folder above it may have been swapped before the holds)
            if (RootId is { } expected && SafeFs.Identity(ScanRoot) is { } now && now != expected)
            {
                error = F($"{ScanRoot} is not the folder that was scanned any more: scan again before deleting");
                pins.Dispose();
                return null;
            }
            return pins;
        }
        catch (Exception ex)
        {
            pins.Dispose();
            error = ex.Message;
            return null;
        }
    }

    private static string Norm(string p) => p.Length > 3 ? p.TrimEnd('\\') : p;

    /// <summary>"C:\a\b" → "C:\", "C:\a", "C:\a\b"; "\\srv\share\a" → "\\srv\share\", "\\srv\share\a".</summary>
    internal static List<string> Chain(string dir)
    {
        var list = new List<string>();
        var root = Path.GetPathRoot(dir);
        if (string.IsNullOrEmpty(root)) return list;
        if (!root.EndsWith('\\')) root += "\\";
        list.Add(root);
        var rest = dir.Length > root.Length ? dir.Substring(root.Length).TrimEnd('\\') : "";
        var current = root;
        foreach (var part in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.EndsWith('\\') ? current + part : current + "\\" + part;
            list.Add(current);
        }
        return list;
    }

    private sealed class Pins : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = new();
        public void Add(SafeFileHandle h) => _handles.Add(h);
        public void Dispose() { foreach (var h in _handles) h.Dispose(); _handles.Clear(); }
    }
}

/// <summary>File system calls on handles: open a link itself (never what it points to), read attributes and identity, delete through the handle.</summary>
internal static class SafeFs
{
    internal const uint ReadAttributes = 0x80, WriteAttributes = 0x100, Delete = 0x10000, Synchronize = 0x100000;
    internal const uint ShareReadWrite = 0x1 | 0x2, ShareAll = 0x1 | 0x2 | 0x4;
    internal const int ErrorFileNotFound = 2, ErrorPathNotFound = 3, ErrorAccessDenied = 5, ErrorSharingViolation = 32, ErrorDirNotEmpty = 145;
    private const uint OpenExisting = 3, BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, out AttributeTagInfo info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, out BasicInfo info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, ref BasicInfo info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, ref uint info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, ref byte info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out ByHandleInfo info);

    private const int FileBasicInfoClass = 0, FileDispositionInfoClass = 4, FileAttributeTagInfoClass = 9, FileDispositionInfoExClass = 21;
    private const uint DispositionDelete = 0x1, DispositionPosix = 0x2, DispositionIgnoreReadOnly = 0x10;

    [StructLayout(LayoutKind.Sequential)] private struct AttributeTagInfo { public uint Attributes; public uint ReparseTag; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo { public long Creation, LastAccess, LastWrite, Change; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleInfo
    {
        public uint Attributes;
        public long Creation, LastAccess, LastWrite;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    /// <summary>The long form every call gets: no length limit, names ending in a dot or a space as they are.</summary>
    internal static string Long(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path;
    }

    /// <summary>Opens a file or folder itself: a link is opened as the link.</summary>
    internal static SafeFileHandle Open(string path, uint access, uint share)
        => CreateFileW(Long(path), access | Synchronize, share, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);

    internal static bool TryAttributes(SafeFileHandle h, out FileAttributes attributes, out uint reparseTag)
    {
        bool ok = GetFileInformationByHandleEx(h, FileAttributeTagInfoClass, out AttributeTagInfo info, Marshal.SizeOf<AttributeTagInfo>());
        attributes = (FileAttributes)info.Attributes;
        reparseTag = info.ReparseTag;
        return ok;
    }

    /// <summary>Junctions, symbolic links and mount points name another place; other reparse points (cloud files, dedup, WOF) do not.</summary>
    internal static bool IsNameSurrogate(uint tag) => (tag & 0x20000000) != 0;

    /// <summary>The identity of the folder or file a path leads to (links followed, as a scan starting there does).</summary>
    internal static FileIdentity? Identity(string path)
    {
        try
        {
            using var h = CreateFileW(Long(path), ReadAttributes | Synchronize, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
            if (h.IsInvalid || !GetFileInformationByHandle(h, out var info)) return null;
            ulong index = ((ulong)info.IndexHigh << 32) | info.IndexLow;
            return index == 0 && info.VolumeSerial == 0 ? null : new FileIdentity(info.VolumeSerial, index);
        }
        catch { return null; }
    }

    /// <summary>Deletes what the handle is open on (it must have delete access): read-only too, and at once where the file system allows.</summary>
    internal static int DeleteByHandle(SafeFileHandle h)
    {
        uint flags = DispositionDelete | DispositionPosix | DispositionIgnoreReadOnly;
        if (SetFileInformationByHandle(h, FileDispositionInfoExClass, ref flags, sizeof(uint))) return 0;
        int e = Marshal.GetLastWin32Error();
        if (e is ErrorAccessDenied or ErrorDirNotEmpty or ErrorSharingViolation) return e;
        // older file systems (FAT, network shares) know only the classic disposition, which refuses read-only files
        if (GetFileInformationByHandleEx(h, FileBasicInfoClass, out BasicInfo basic, Marshal.SizeOf<BasicInfo>()) && (basic.Attributes & (uint)FileAttributes.ReadOnly) != 0)
        {
            var clear = new BasicInfo { Attributes = basic.Attributes & ~(uint)FileAttributes.ReadOnly };
            if (clear.Attributes == 0) clear.Attributes = (uint)FileAttributes.Normal;
            SetFileInformationByHandle(h, FileBasicInfoClass, ref clear, Marshal.SizeOf<BasicInfo>());
        }
        byte delete = 1;
        return SetFileInformationByHandle(h, FileDispositionInfoClass, ref delete, 1) ? 0 : Marshal.GetLastWin32Error();
    }

    /// <summary>
    /// Deletes a file or a folder with everything in it, for good. Each folder is held open (no delete sharing) while it is
    /// emptied, each element is opened as itself — a link is removed as a link, never followed — and deleted through its
    /// handle. The first problem is returned; the rest is still deleted where possible.
    /// </summary>
    internal static string DeleteTree(string path)
    {
        string? first = null;
        static string Shown(string p) => p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + p.Substring(8) : p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p.Substring(4) : p;
        void Note(string p, int error)
        {
            p = Shown(p);
            first ??= error switch
            {
                ErrorAccessDenied => F($"no rights for {p}"),
                ErrorSharingViolation => F($"{p} is open in a program"),
                ErrorDirNotEmpty => F($"something inside {p} could not be deleted"),
                _ => new Win32Exception(error).Message,
            };
        }
        // a folder being emptied: held open (it cannot be renamed or replaced meanwhile), deleted when its entries are done
        var open = new Stack<(string Path, SafeFileHandle Handle, bool CanDelete, List<string> Entries)>();
        // opens an element as itself; a file or a link goes at once, a real folder is returned to be emptied first
        void Take(string p)
        {
            // write attributes too when allowed (read-only files on older file systems); delete access is what matters
            var h = Open(p, Delete | ReadAttributes | WriteAttributes, ShareReadWrite);
            if (h.IsInvalid) { h.Dispose(); h = Open(p, Delete | ReadAttributes, ShareReadWrite); }
            bool canDelete = !h.IsInvalid;
            if (!canDelete)
            {
                int e = Marshal.GetLastWin32Error();
                h.Dispose();
                if (e is ErrorFileNotFound or ErrorPathNotFound) return;
                // no right to delete this one: a folder is still held (read only) and emptied, its own error is reported
                h = Open(p, ReadAttributes, ShareReadWrite);
                if (h.IsInvalid) { h.Dispose(); Note(p, e); return; }
                Note(p, e);
            }
            if (!TryAttributes(h, out var attrs, out uint tag)) { Note(p, Marshal.GetLastWin32Error()); h.Dispose(); return; }
            // a junction, symbolic link or mount point is removed itself; other reparse folders (cloud files) are real folders
            bool folder = (attrs & FileAttributes.Directory) != 0, link = (attrs & FileAttributes.ReparsePoint) != 0 && IsNameSurrogate(tag);
            if (folder && !link)
            {
                List<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(Long(p)).ToList(); }
                catch (UnauthorizedAccessException) { Note(p, ErrorAccessDenied); h.Dispose(); return; }
                catch (IOException ex) { first ??= ex.Message; h.Dispose(); return; }
                open.Push((p, h, canDelete, entries));
                return;
            }
            using (h)
            {
                if (!canDelete) return;
                int rc = DeleteByHandle(h);
                if (rc != 0) Note(p, rc);
            }
        }
        // a stack of its own instead of recursion: a folder chain of any depth goes without exhausting the thread's stack
        Take(Path.GetFullPath(path));
        var next = new Dictionary<SafeFileHandle, int>(ReferenceEqualityComparer.Instance);
        while (open.Count > 0)
        {
            var (p, h, canDelete, entries) = open.Peek();
            next.TryGetValue(h, out int i);
            if (i < entries.Count) { next[h] = i + 1; Take(entries[i]); continue; }
            open.Pop();
            next.Remove(h);
            using (h)
            {
                if (!canDelete) continue;
                int rc = DeleteByHandle(h);
                if (rc != 0) Note(p, rc);
            }
        }
        return first ?? "";
    }
}
