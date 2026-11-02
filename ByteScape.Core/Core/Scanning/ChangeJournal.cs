using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ByteScape.Core.Scanning;

/// <summary>
/// A position in the NTFS change journal (USN journal) of a volume: every change made on the volume after it can be read
/// back. Taken before a scan lists anything, it tells a later scan which folders changed since. <see cref="RootId"/> is the
/// file id of the scanned folder: a folder deleted and made again under the same path is another folder.
/// </summary>
public sealed record JournalMark(uint Volume, long JournalId, long Usn, long RootId, DateTime TakenUtc);

/// <summary>What the journal says changed since a mark: the ids of the folders whose entries changed, and the mark after them.</summary>
public sealed record JournalChanges(HashSet<long> Folders, int Records, JournalMark Next, TimeSpan Elapsed);

/// <summary>
/// Reads the NTFS change journal, which the file system keeps on every NTFS system drive (one record per change of a file or
/// folder: created, deleted, renamed, written, attributes...). Each record names the changed element's id and its folder's
/// id, so the folders to list again after a scan are known without walking the drive. No administrator rights needed:
/// FSCTL_READ_UNPRIVILEGED_USN_JOURNAL (Windows 10 1709 and later) returns the records without their names, which a scan
/// does not need; as administrator the classic read works on older systems too.
/// </summary>
public static class ChangeJournal
{
    private const uint FsctlQueryUsnJournal = 0x000900F4, FsctlReadUsnJournal = 0x000900BB, FsctlReadUnprivilegedUsnJournal = 0x000903AB;
    private const int ErrorInvalidFunction = 1, ErrorNotSupported = 50, ErrorJournalDeleteInProgress = 1178, ErrorJournalNotActive = 1179, ErrorJournalEntryDeleted = 1181;

    /// <summary>
    /// The changes that can alter what a listing shows: data written, extended or cut (also of named streams), created,
    /// deleted, renamed, attributes, security (a folder may become readable), hard links, compression, encryption, reparse
    /// points (cloud placeholders), streams. Not: object ids, indexing flags, extended attributes, integrity, storage class.
    /// </summary>
    internal const uint Reasons = 0x0000_0077 | 0x0000_0100 | 0x0000_0200 | 0x0000_0800 | 0x0000_3000 | 0x0000_8000
                                | 0x0001_0000 | 0x0002_0000 | 0x0004_0000 | 0x0010_0000 | 0x0020_0000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[]? inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, byte[] info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathNameW(string path, char[] volumePath, int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, char[] volumeName, int length);

    private readonly record struct Journal(long Id, long FirstUsn, long NextUsn);

    /// <summary>The folder itself (read attributes only, which almost every folder allows): the journal is asked through it.</summary>
    private static SafeFileHandle OpenFolder(string path) =>
        CreateFile(DirectoryScanner.IoPath(path), 0x80 /*FILE_READ_ATTRIBUTES*/, 7, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0x02000000 /*BACKUP_SEMANTICS*/, IntPtr.Zero);

    /// <summary>The volume serial number and the NTFS file id of an open file or folder.</summary>
    private static bool Identify(SafeFileHandle h, out uint volume, out long id)
    {
        var info = new byte[52];   // BY_HANDLE_FILE_INFORMATION
        volume = 0; id = 0;
        if (!GetFileInformationByHandle(h, info)) return false;
        volume = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(28));
        id = (long)(((ulong)BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(44)) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(48)));
        return true;
    }

    private static Journal? Query(SafeFileHandle h, out int error)
    {
        var data = new byte[80];   // USN_JOURNAL_DATA_V0..V2
        if (!DeviceIoControl(h, FsctlQueryUsnJournal, null, 0, data, data.Length, out int n, IntPtr.Zero) || n < 24)
        {
            error = Marshal.GetLastWin32Error();
            return null;
        }
        error = 0;
        return new Journal(BinaryPrimitives.ReadInt64LittleEndian(data), BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8)), BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(16)));
    }

    /// <summary>Where the change journal of <paramref name="path"/>'s volume stands now; null when the volume keeps none (FAT, most USB drives, network).</summary>
    public static JournalMark? Mark(string path)
    {
        try
        {
            using var h = OpenFolder(path);
            if (h.IsInvalid || !Identify(h, out var volume, out var id)) return null;
            return Query(h, out _) is { } j ? new JournalMark(volume, j.Id, j.NextUsn, id, DateTime.UtcNow) : null;
        }
        catch { return null; }
    }

    /// <summary>True when every change since <paramref name="mark"/> can still be read for <paramref name="path"/>; otherwise why not.</summary>
    public static bool IsCurrent(JournalMark mark, string path, out string why)
    {
        try
        {
            using var h = OpenFolder(path);
            if (h.IsInvalid) { why = "the folder cannot be opened"; return false; }
            return Check(h, mark, out why) is not null;
        }
        catch (Exception ex) { why = ex.Message; return false; }
    }

    private static Journal? Check(SafeFileHandle h, JournalMark mark, out string why)
    {
        if (!Identify(h, out var volume, out var id)) { why = "the folder cannot be identified"; return null; }
        if (volume != mark.Volume) { why = "another drive is at this path now"; return null; }
        if (id != mark.RootId) { why = "the folder was replaced by another one"; return null; }
        if (Query(h, out int error) is not { } j) { why = error == ErrorJournalNotActive ? "the drive keeps no change journal any more" : "the change journal cannot be read (" + new Win32Exception(error).Message + ")"; return null; }
        if (j.Id != mark.JournalId) { why = "the change journal was made again since"; return null; }
        if (mark.Usn < j.FirstUsn) { why = "the change journal no longer holds every change since " + mark.TakenUtc.ToLocalTime().ToString("g"); return null; }
        if (mark.Usn > j.NextUsn) { why = "the change journal is behind the mark"; return null; }
        why = "";
        return j;
    }

    /// <summary>Tests: the records are read in pieces of this size.</summary>
    internal static int BufferBytes = 1 << 20;

    /// <summary>
    /// The folders whose entries changed on the volume since <paramref name="mark"/> (the folder of every changed element, and
    /// every changed folder itself), with the mark to use next time. Null, with the reason, when the journal cannot say
    /// (made again, overwritten since: it keeps a few tens of MB, days to weeks of changes; another drive or folder).
    /// </summary>
    public static JournalChanges? ReadSince(string path, JournalMark mark, CancellationToken token, out string why)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var h = OpenFolder(path);
            if (h.IsInvalid) { why = "the folder cannot be opened"; return null; }
            if (Check(h, mark, out why) is not { } journal) return null;
            var folders = new HashSet<long>();
            int records = 0;
            long usn = mark.Usn;
            var input = new byte[44];   // READ_USN_JOURNAL_DATA_V1
            var buf = new byte[BufferBytes];
            SafeFileHandle? volume = null;   // the classic read (administrator), when the unprivileged one is not there
            try
            {
                // read up to where the journal stood when asked: a busy volume keeps writing, what comes later is for next time
                while (usn < journal.NextUsn)
                {
                    token.ThrowIfCancellationRequested();
                    BinaryPrimitives.WriteInt64LittleEndian(input, usn);
                    BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), Reasons);
                    BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(32), journal.Id);
                    BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(40), 2);
                    BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(42), 3);
                    bool ok = DeviceIoControl(volume ?? h, volume == null ? FsctlReadUnprivilegedUsnJournal : FsctlReadUsnJournal, input, input.Length, buf, buf.Length, out int n, IntPtr.Zero);
                    if (!ok)
                    {
                        int e = Marshal.GetLastWin32Error();
                        if (volume == null && e is ErrorInvalidFunction or ErrorNotSupported && OpenVolume(path) is { } v) { volume = v; continue; }
                        why = e switch
                        {
                            ErrorJournalEntryDeleted => "the change journal no longer holds every change since " + mark.TakenUtc.ToLocalTime().ToString("g"),
                            ErrorJournalNotActive or ErrorJournalDeleteInProgress => "the drive keeps no change journal any more",
                            _ => "the change journal cannot be read (" + new Win32Exception(e).Message + ")",
                        };
                        return null;
                    }
                    if (n <= 8) break;
                    long next = BinaryPrimitives.ReadInt64LittleEndian(buf);
                    for (int off = 8; off + 60 <= n;)
                    {
                        var r = buf.AsSpan(off, n - off);
                        int len = BinaryPrimitives.ReadInt32LittleEndian(r);
                        if (len < 60 || len > r.Length) break;
                        long file, parent;
                        uint attrs;
                        switch (BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(4)))
                        {
                            case 2: file = BinaryPrimitives.ReadInt64LittleEndian(r.Slice(8)); parent = BinaryPrimitives.ReadInt64LittleEndian(r.Slice(16)); attrs = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(52)); break;
                            case 3 when len >= 76:   // 128 bit ids: NTFS ids fit in the low half
                                file = BinaryPrimitives.ReadInt64LittleEndian(r.Slice(8)); parent = BinaryPrimitives.ReadInt64LittleEndian(r.Slice(24)); attrs = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(68)); break;
                            default: off += len; continue;
                        }
                        records++;
                        folders.Add(parent);
                        if ((attrs & (uint)FileAttributes.Directory) != 0) folders.Add(file);
                        off += len;
                    }
                    if (next <= usn) break;
                    usn = next;
                }
            }
            finally { volume?.Dispose(); }
            why = "";
            return new JournalChanges(folders, records, mark with { Usn = usn, TakenUtc = DateTime.UtcNow }, sw.Elapsed);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { why = ex.Message; return null; }
    }

    /// <summary>Diagnostics: the oldest mark the journal still covers (its first record) and when that change was made.</summary>
    internal static (JournalMark Mark, DateTime FirstChangeUtc)? Oldest(string path)
    {
        try
        {
            using var h = OpenFolder(path);
            if (h.IsInvalid || !Identify(h, out var volume, out var id) || Query(h, out _) is not { } j) return null;
            var mark = new JournalMark(volume, j.Id, j.FirstUsn, id, DateTime.UtcNow);
            var input = new byte[44];
            BinaryPrimitives.WriteInt64LittleEndian(input, j.FirstUsn);
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), 0xFFFFFFFF);
            BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(32), j.Id);
            BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(40), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(42), 2);
            var buf = new byte[4096];
            DateTime first = default;
            if (DeviceIoControl(h, FsctlReadUnprivilegedUsnJournal, input, input.Length, buf, buf.Length, out int n, IntPtr.Zero) && n >= 8 + 60)
                first = DateTime.FromFileTimeUtc(BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8 + 32)));
            return (mark, first);
        }
        catch { return null; }
    }

    /// <summary>The volume of <paramref name="path"/>, opened for reading (administrator only); null otherwise.</summary>
    private static SafeFileHandle? OpenVolume(string path)
    {
        try
        {
            var mount = new char[1024];
            var name = new char[1024];
            if (!GetVolumePathNameW(path, mount, mount.Length)) return null;
            if (!GetVolumeNameForVolumeMountPointW(new string(mount).TrimEnd('\0'), name, name.Length)) return null;
            var h = CreateFile(new string(name).TrimEnd('\0').TrimEnd('\\'), 0x80000000 /*GENERIC_READ*/, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (!h.IsInvalid) return h;
            h.Dispose();
            return null;
        }
        catch { return null; }
    }
}
