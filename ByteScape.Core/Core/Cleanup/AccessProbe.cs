using System.Runtime.InteropServices;
using ByteScape.Core.Model;
using ByteScape.Core.Scanning;
using Microsoft.Win32.SafeHandles;

namespace ByteScape.Core.Cleanup;

/// <summary>What stands in the way of deleting an element, found without deleting it.</summary>
public enum AccessState
{
    /// <summary>This process may delete it.</summary>
    Ok,
    /// <summary>Access denied: an administrator can delete it (restart ByteScape as administrator).</summary>
    NeedsAdmin,
    /// <summary>Owned by TrustedInstaller: Windows protects it even from administrators.</summary>
    ProtectedByWindows,
    /// <summary>A program has it open (or it is a program's current folder): it cannot be deleted until that program lets go.</summary>
    InUse,
    /// <summary>Gone already.</summary>
    Missing,
    Unknown,
}

/// <summary>What a look at a folder's content found: how many elements were tried and which ones would fail.</summary>
public sealed class ProbeSummary
{
    public int Checked { get; internal set; }
    /// <summary>Every element of the folder was tried (false: the limit was reached first).</summary>
    public bool Complete { get; internal set; } = true;
    public List<string> NeedsAdmin { get; } = new();
    public List<string> ProtectedByWindows { get; } = new();
    public List<string> InUse { get; } = new();
    public int NeedsAdminCount { get; internal set; }
    public int ProtectedCount { get; internal set; }
    public int InUseCount { get; internal set; }
    public bool AllOk => NeedsAdminCount == 0 && ProtectedCount == 0 && InUseCount == 0;

    internal void Add(string path, AccessState s)
    {
        const int keep = 20;
        switch (s)
        {
            case AccessState.NeedsAdmin: if (NeedsAdminCount++ < keep) NeedsAdmin.Add(path); break;
            case AccessState.ProtectedByWindows: if (ProtectedCount++ < keep) ProtectedByWindows.Add(path); break;
            case AccessState.InUse: if (InUseCount++ < keep) InUse.Add(path); break;
        }
    }
}

/// <summary>
/// Tries whether elements could be deleted, without deleting anything: each is opened with the DELETE right (and every
/// sharing mode, so a program holding it open without allowing deletion shows as "in use"). Access denied is split into
/// "an administrator can" and "Windows protects it" (owner TrustedInstaller). Runs as the current process: as
/// administrator, what needs an administrator passes.
/// </summary>
public static class AccessProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetNamedSecurityInfoW(string name, int type, int info, out IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr descriptor);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr text);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private const uint Delete = 0x00010000, ReadAttributes = 0x80;
    private const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
    internal const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>Is this process running as administrator.</summary>
    public static bool IsElevated => Environment.IsPrivilegedProcess;

    public static AccessState Probe(string path)
    {
        try
        {
            string io = DirectoryScanner.IoPath(path);
            using var h = CreateFile(io, Delete | ReadAttributes, 7, IntPtr.Zero, 3, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
            if (!h.IsInvalid) return AccessState.Ok;
            int e = Marshal.GetLastWin32Error();
            return e switch
            {
                2 or 3 => AccessState.Missing,
                5 => OwnerIsTrustedInstaller(io) ? AccessState.ProtectedByWindows : AccessState.NeedsAdmin,
                32 or 33 => AccessState.InUse,
                _ => AccessState.Unknown,
            };
        }
        catch { return AccessState.Unknown; }
    }

    /// <summary>The owner of the element is TrustedInstaller (Windows' servicing account).</summary>
    internal static bool OwnerIsTrustedInstaller(string path)
    {
        IntPtr descriptor = IntPtr.Zero, text = IntPtr.Zero;
        try
        {
            if (GetNamedSecurityInfoW(path, 1 /*SE_FILE_OBJECT*/, 1 /*OWNER_SECURITY_INFORMATION*/, out var owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out descriptor) != 0) return false;
            if (!ConvertSidToStringSidW(owner, out text)) return false;
            return string.Equals(Marshal.PtrToStringUni(text), TrustedInstallerSid, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
        finally
        {
            if (text != IntPtr.Zero) LocalFree(text);
            if (descriptor != IntPtr.Zero) LocalFree(descriptor);
        }
    }

    /// <summary>
    /// Tries the element and, for a folder, what is inside it (from the scanned tree, the lock taken per folder) up to
    /// <paramref name="limit"/> elements: deleting a folder needs every file in it.
    /// </summary>
    public static ProbeSummary ProbeTree(FsNode node, string path, object? sync, int limit, CancellationToken token, bool includeRoot = true)
    {
        var s = new ProbeSummary();
        var stack = new Stack<(FsNode Node, string Path)>();
        stack.Push((node, path));
        var kids = new List<FsNode>();
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (n, p) = stack.Pop();
            if (s.Checked >= limit) { s.Complete = false; break; }
            // a folder that is emptied (not deleted) is only looked into
            bool root = ReferenceEquals(n, node);
            var state = root && !includeRoot ? AccessState.Ok : Probe(p);
            if (!root || includeRoot) { s.Checked++; s.Add(p, state); }
            // a folder Windows protects or that cannot be opened: what is inside cannot be deleted either, no need to try it all
            if (n is not ContainerNode c || state is AccessState.ProtectedByWindows or AccessState.Missing) continue;
            kids.Clear();
            if (sync != null) lock (sync) kids.AddRange(c.Children);
            else kids.AddRange(c.Children);
            for (int i = kids.Count - 1; i >= 0; i--)
                if (!kids[i].IsSynthetic) stack.Push((kids[i], p.EndsWith('\\') ? p + kids[i].Name : p + "\\" + kids[i].Name));
        }
        if (stack.Count > 0) s.Complete = false;
        return s;
    }

    public static string Text(AccessState s) => T(s switch
    {
        AccessState.NeedsAdmin => "needs administrator rights",
        AccessState.ProtectedByWindows => "protected by Windows (TrustedInstaller)",
        AccessState.InUse => "open in a program",
        AccessState.Missing => "no longer exists",
        AccessState.Unknown => "could not be checked",
        _ => "",
    });
}
