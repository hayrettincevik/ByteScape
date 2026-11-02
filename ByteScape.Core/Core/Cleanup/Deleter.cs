using System.Runtime.InteropServices;
using ByteScape.Core.Scanning;
using ByteScape.Core.Util;
using Microsoft.Win32;

namespace ByteScape.Core.Cleanup;

/// <summary>The Recycle Bin of a drive: whether there is one, and how much it holds before Windows deletes for good.</summary>
public sealed record RecycleBinInfo(bool Available, long MaxBytes, bool MaxIsEstimate, long UsedBytes, string Note);

/// <summary>What happened to one element of a delete.</summary>
public sealed record DeleteItemResult(string Path, bool Deleted, string Error, long Bytes);

public sealed class DeleteReport
{
    public bool Recycled { get; init; }
    public List<DeleteItemResult> Items { get; } = new();
    public bool Cancelled { get; internal set; }
    public int DeletedCount => Items.Count(i => i.Deleted);
    public int FailedCount => Items.Count(i => !i.Deleted);
    public long DeletedBytes => Items.Where(i => i.Deleted).Sum(i => i.Bytes);
    public TimeSpan Elapsed { get; internal set; }
}

public static class RecycleBin
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, char[] volumeName, int length);

    // shellapi.h packs its structures to 1 byte on 32-bit Windows only; ByteScape is 64-bit: natural layout (24 bytes)
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string root, ref SHQUERYRBINFO info);

    /// <summary>
    /// Windows keeps no Recycle Bin on removable, network and optical drives, nor where it was turned off for the drive
    /// (NukeOnDelete). Its size limit is per drive (MaxCapacity, MB); without a setting Windows uses about 10% of the first
    /// 40 GB plus 5% of the rest.
    /// </summary>
    public static RecycleBinInfo For(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return new(false, 0, false, 0, T("a network location has no Recycle Bin: deleting there is permanent"));
            var root = Path.GetPathRoot(path) ?? "";
            if (root.Length < 2) return new(false, 0, false, 0, T("unknown drive"));
            var drive = new DriveInfo(root);
            if (drive.DriveType is DriveType.Removable) return new(false, 0, false, 0, T("removable drives (USB sticks, memory cards) have no Recycle Bin: deleting there is permanent"));
            if (drive.DriveType is not DriveType.Fixed) return new(false, 0, false, 0, T("this drive has no Recycle Bin: deleting there is permanent"));
            long total = drive.TotalSize;
            long max = 0; bool estimate = true; bool nuke = false;
            var name = new char[64];
            if (GetVolumeNameForVolumeMountPointW(root, name, name.Length))
            {
                var vol = new string(name).TrimEnd('\0').TrimEnd('\\');
                int b = vol.IndexOf('{');
                if (b >= 0)
                {
                    using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\" + vol.Substring(b));
                    if (k?.GetValue("NukeOnDelete") is int n && n != 0) nuke = true;
                    if (k?.GetValue("MaxCapacity") is int mb && mb > 0) { max = (long)mb << 20; estimate = false; }
                }
            }
            if (nuke) return new(false, 0, false, 0, T("the Recycle Bin is turned off for this drive (Recycle Bin properties): deleting there is permanent"));
            if (max == 0) { const long g40 = 40L << 30; max = Math.Min(total, g40) / 10 + Math.Max(0, total - g40) / 20; }
            var q = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            long used = SHQueryRecycleBinW(root, ref q) == 0 ? q.i64Size : 0;
            return new(true, max, estimate, used, "");
        }
        catch (Exception ex) { return new(false, 0, false, 0, T("the Recycle Bin could not be checked") + " (" + ex.Message + ")"); }
    }

    /// <summary>The current user's Recycle Bin folder on a drive (C:\$Recycle.Bin\S-1-5-21-...).</summary>
    public static string? FolderOf(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            return root == null || sid == null ? null : Path.Combine(root, "$Recycle.Bin", sid);
        }
        catch { return null; }
    }
}

/// <summary>
/// Deletes elements one by one: to the Recycle Bin through the Windows shell (SHFileOperation with undo, which asks
/// "delete permanently?" itself when something cannot be recycled - too big, or no Recycle Bin), or for good through
/// handles (<see cref="SafeFs.DeleteTree"/>: read-only files too, links removed without following them, long paths). One element at a
/// time so that each one's result is known and the rest goes on when one fails (open in a program, no rights).
/// </summary>
public static class Deleter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);

    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x4, FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofNoErrorUi = 0x400, FofWantNukeWarning = 0x4000;

    /// <summary>Tests of the windows: instead of deleting, this gets each path (recycle or not) and returns an error or null.</summary>
    internal static Func<string, bool, string?>? Fake;

    /// <summary>Deletes on a background STA thread (the shell wants one); <paramref name="owner"/> is the window the shell's own questions belong to.</summary>
    public static Task<DeleteReport> RunAsync(IReadOnlyList<(string Path, long Bytes)> targets, bool recycle, IntPtr owner, IProgress<(int Done, int Total, string Path)>? progress, CancellationToken token, DeleteGuard? guard = null)
    {
        var tcs = new TaskCompletionSource<DeleteReport>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(Run(targets, recycle, owner, progress, token, guard)); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "Delete" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    /// <summary><paramref name="guard"/>: the folders on the way are held and checked while each element goes (see <see cref="DeleteGuard"/>).</summary>
    public static DeleteReport Run(IReadOnlyList<(string Path, long Bytes)> targets, bool recycle, IntPtr owner, IProgress<(int Done, int Total, string Path)>? progress, CancellationToken token, DeleteGuard? guard = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var report = new DeleteReport { Recycled = recycle };
        for (int i = 0; i < targets.Count; i++)
        {
            if (token.IsCancellationRequested) { report.Cancelled = true; break; }
            var (path, bytes) = targets[i];
            progress?.Report((i, targets.Count, path));
            string error;
            IDisposable? hold = null;
            if (Fake == null && guard != null && (hold = guard.Hold(path, out var refused)) == null)
            {
                report.Items.Add(new DeleteItemResult(path, false, refused, bytes));
                Log.Warn("Not deleted (" + refused + "): " + path);
                continue;
            }
            using (hold)
            {
                if (Fake != null) error = Fake(path, recycle) ?? "";
                else error = recycle ? Recycle(path, owner) : SafeFs.DeleteTree(path);
            }
            bool gone = Fake != null ? error.Length == 0 : !Exists(path);
            if (gone) error = "";
            else if (Fake == null && (error.Length == 0 || !Directory.Exists(DirectoryScanner.IoPath(path))))
                error = Explain(path) ?? (error.Length > 0 ? error : T("not deleted"));   // a file: why it is still there; a folder: what inside failed
            report.Items.Add(new DeleteItemResult(path, gone, error, bytes));
            Log.Debug((gone ? (recycle ? "Moved to the Recycle Bin: " : "Deleted: ") : "Not deleted (" + error + "): ") + path);
        }
        progress?.Report((targets.Count, targets.Count, ""));
        report.Elapsed = sw.Elapsed;
        return report;
    }

    private static bool Exists(string path)
    {
        var io = DirectoryScanner.IoPath(path);
        return File.Exists(io) || Directory.Exists(io);
    }

    /// <summary>Why an element is still there: open in a program, no rights, protected by Windows.</summary>
    private static string? Explain(string path) => AccessProbe.Probe(path) switch
    {
        AccessState.InUse => T("open in a program"),
        AccessState.NeedsAdmin => T("needs administrator rights"),
        AccessState.ProtectedByWindows => T("protected by Windows"),
        AccessState.Ok when Directory.Exists(DirectoryScanner.IoPath(path)) => T("something inside could not be deleted (open in a program or no rights)"),
        _ => null,
    };

    private static string Recycle(string path, IntPtr owner)
    {
        // the shell works with plain paths only: longer ones cannot go to the Recycle Bin
        if (path.Length >= 260 || DirectoryScanner.IoPath(path) != path) return T("the path is too long (or unusual) for the Recycle Bin: delete it permanently instead");
        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner, wFunc = FoDelete, pFrom = path + "\0",
            fFlags = (ushort)(FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent | FofWantNukeWarning),
        };
        int rc = SHFileOperationW(ref op);
        if (op.fAnyOperationsAborted != 0) return T("cancelled");
        if (rc != 0) Log.Debug($"SHFileOperation 0x{rc:X} for {path}");
        return "";   // the shell's codes say little: whether the element is gone decides, and the probe explains why not
    }

}
