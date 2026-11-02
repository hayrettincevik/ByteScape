using System.Runtime.InteropServices;
using System.Text;

namespace ByteScape.Interop;

/// <summary>Win32 declarations used by the scanner, the privilege request and the shell menu.</summary>
internal static class Native
{
    public const int MAX_PATH = 260;

    /// <summary>Package identity probe: returns APPMODEL_ERROR_NO_PACKAGE (15700) for a plain desktop process.</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

    /// <summary>Resolves an indirect resource string ("@{Package?ms-resource://...}") such as a Store app's display name; null when it cannot.</summary>
    public static string? LoadIndirectString(string source)
    {
        try
        {
            var sb = new StringBuilder(1024);
            return SHLoadIndirectString(source, sb, sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0 ? sb.ToString() : null;
        }
        catch { return null; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileRaw(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "DeviceIoControl")]
    private static extern bool DeviceIoControlRaw(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

    /// <summary>
    /// True when the drive of <paramref name="path"/> is a spinning disk (seeks cost), false for SSDs, null when unknown.
    /// Queries the storage property with a zero-access volume handle, which needs no administrator rights.
    /// </summary>
    public static bool? IncursSeekPenalty(string path)
    {
        try
        {
            if (path.Length < 2 || path[1] != ':') return null;
            using var h = CreateFileRaw(@"\\.\" + path.Substring(0, 2), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) return null;
            var query = new byte[12];
            BitConverter.GetBytes(7).CopyTo(query, 0);          // StorageDeviceSeekPenaltyProperty, PropertyStandardQuery
            var desc = new byte[12];                             // DEVICE_SEEK_PENALTY_DESCRIPTOR: version, size, IncursSeekPenalty
            if (!DeviceIoControlRaw(h, 0x002D1400 /*IOCTL_STORAGE_QUERY_PROPERTY*/, query, query.Length, desc, desc.Length, out int n, IntPtr.Zero) || n < 9) return null;
            return desc[8] != 0;
        }
        catch { return null; }
    }

    /// <summary>
    /// The STORAGE_DEVICE_DESCRIPTOR of the disk under a drive letter (bus type, removable, product), through a zero-access
    /// volume handle: no administrator rights needed. Null when the drive cannot be asked (network, some virtual drives).
    /// </summary>
    public static byte[]? QueryStorageDevice(string path)
    {
        try
        {
            if (path.Length < 2 || path[1] != ':') return null;
            using var h = CreateFileRaw(@"\\.\" + path.Substring(0, 2), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) return null;
            var query = new byte[12];                            // StorageDeviceProperty (0), PropertyStandardQuery (0)
            var desc = new byte[1024];
            if (!DeviceIoControlRaw(h, 0x002D1400 /*IOCTL_STORAGE_QUERY_PROPERTY*/, query, query.Length, desc, desc.Length, out int n, IntPtr.Zero) || n < 36) return null;
            return desc.AsSpan(0, n).ToArray();
        }
        catch { return null; }
    }

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CfGetSyncRootInfoByPath(string path, int infoClass, byte[] info, uint infoLength, out uint returned);

    /// <summary>
    /// Name of the cloud provider (OneDrive, Dropbox…) whose sync root contains <paramref name="path"/>, or null when the
    /// path is not in one (Cloud Files API, CF_SYNC_ROOT_INFO_PROVIDER). Reads metadata only: nothing is downloaded.
    /// </summary>
    public static string? SyncRootProvider(string path)
    {
        try
        {
            var buf = new byte[2048];   // CF_SYNC_ROOT_PROVIDER_INFO: status, name[256], version[256]
            if (CfGetSyncRootInfoByPath(path.Length == 2 ? path + "\\" : path, 2, buf, (uint)buf.Length, out _) != 0) return null;
            var name = Encoding.Unicode.GetString(buf, 4, 512);
            int z = name.IndexOf('\0');
            name = (z >= 0 ? name[..z] : name).Trim();
            return name.Length > 0 ? name : "Cloud";
        }
        catch (Exception) { return null; }   // cldapi.dll missing (older Windows)
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetDiskFreeSpaceW(string lpRootPathName,
        out uint lpSectorsPerCluster, out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters, out uint lpTotalNumberOfClusters);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetDiskFreeSpaceExW(string lpDirectoryName,
        out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

    // ---- alternate data streams -------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WIN32_FIND_STREAM_DATA
    {
        public long StreamSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string cStreamName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindFirstStreamW(string lpFileName, int infoLevel, out WIN32_FIND_STREAM_DATA data, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool FindNextStreamW(IntPtr hFindStream, out WIN32_FIND_STREAM_DATA data);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FindClose(IntPtr hFindFile);

    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    // ---- privileges ---------------------------------------------------------------------

    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool LookupPrivilegeValueW(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Tries to enable SeBackupPrivilege so protected folders can be enumerated
    /// (done at start-up; silently ignored when not permitted).
    /// </summary>
    public static bool TryEnableBackupPrivilege()
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return false;
            try
            {
                if (!LookupPrivilegeValueW(null, "SeBackupPrivilege", out var luid)) return false;
                var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
                return Marshal.GetLastWin32Error() == 0;
            }
            finally { CloseHandle(token); }
        }
        catch { return false; }
    }

    // ---- window flashing -------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

    public const uint FLASHW_ALL = 3;
    public const uint FLASHW_TIMERNOFG = 12;

    [DllImport("user32.dll")]
    public static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    public static void FlashWindow(IntPtr hwnd)
    {
        var fi = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = hwnd, dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG, uCount = uint.MaxValue, dwTimeout = 0 };
        FlashWindowEx(ref fi);
    }

    // ---- shell context menu ------------------------------------------------------------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    public static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("shell32.dll")]
    public static extern void ILFree(IntPtr pidl);

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_LEFTBUTTON = 0x0000;
    public const uint CMF_NORMAL = 0x00000000;
    public const uint CMF_EXPLORE = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    public struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public int fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public int dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public int ptInvokeX;
        public int ptInvokeY;
    }

    public const int CMIC_MASK_UNICODE = 0x00004000;
    public const int CMIC_MASK_PTINVOKE = 0x20000000;
    public const int SW_SHOWNORMAL = 1;

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idcmd, uint uflags, IntPtr reserved, IntPtr commandstring, uint cch);
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    public static Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    public static Guid IID_IContextMenu = new("000214e4-0000-0000-c000-000000000046");
}
