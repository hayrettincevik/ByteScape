using System.Diagnostics;
using System.Runtime.InteropServices;
using ByteScape.Core.Util;

namespace ByteScape.Wpf.Shell;

// The Store package puts "Open in ByteScape" into Explorer's right-click menu (also the new Windows 11 menu) through the
// manifest: Explorer asks the COM class below (hosted by ByteScape.exe started with -ShellMenu) for the entry's title and
// icon, and calls Invoke with the chosen folders.

[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetParent(out IShellItem ppsi);
    [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
    [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
}

[ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemArray
{
    [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
    [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int GetCount(out uint pdwNumItems);
    [PreserveSig] int GetItemAt(uint dwIndex, out IShellItem ppsi);
    [PreserveSig] int EnumItems(out IntPtr ppenumShellItems);
}

[ComImport, Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExplorerCommand
{
    [PreserveSig] int GetTitle(IShellItemArray? items, out IntPtr name);
    [PreserveSig] int GetIcon(IShellItemArray? items, out IntPtr icon);
    [PreserveSig] int GetToolTip(IShellItemArray? items, out IntPtr infoTip);
    [PreserveSig] int GetCanonicalName(out Guid name);
    [PreserveSig] int GetState(IShellItemArray? items, [MarshalAs(UnmanagedType.Bool)] bool okToBeSlow, out uint state);
    [PreserveSig] int Invoke(IShellItemArray? items, IntPtr bindCtx);
    [PreserveSig] int GetFlags(out uint flags);
    [PreserveSig] int EnumSubCommands(out IntPtr enumCommands);
}

[ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IClassFactory
{
    [PreserveSig] int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr obj);
    [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool doLock);
}

/// <summary>The "Open in ByteScape" entry of Explorer's menu (Store package).</summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None), Guid(ShellMenu.ClsidText)]
public sealed class OpenInByteScapeCommand : IExplorerCommand
{
    private const int S_OK = 0, E_NOTIMPL = unchecked((int)0x80004001), E_FAIL = unchecked((int)0x80004005);
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    public int GetTitle(IShellItemArray? items, out IntPtr name) { name = Marshal.StringToCoTaskMemUni(Core.ShellIntegration.MenuText); return S_OK; }

    public int GetIcon(IShellItemArray? items, out IntPtr icon)
    {
        icon = IntPtr.Zero;
        var exe = Environment.ProcessPath;
        if (exe == null) return E_NOTIMPL;
        icon = Marshal.StringToCoTaskMemUni(exe + ",0");
        return S_OK;
    }

    public int GetToolTip(IShellItemArray? items, out IntPtr infoTip) { infoTip = IntPtr.Zero; return E_NOTIMPL; }
    public int GetCanonicalName(out Guid name) { name = ShellMenu.Clsid; return S_OK; }

    /// <summary>Shown for folders and drives that have a file system path (not for "This PC", libraries...).</summary>
    public int GetState(IShellItemArray? items, bool okToBeSlow, out uint state)
    {
        state = PathsOf(items).Count > 0 || items == null ? 0u /*ECS_ENABLED*/ : 2u /*ECS_HIDDEN*/;
        return S_OK;
    }

    public int Invoke(IShellItemArray? items, IntPtr bindCtx)
    {
        try
        {
            var paths = PathsOf(items);
            if (paths.Count == 0) return E_FAIL;
            ShellMenu.Touch();
            ShellMenu.Launch(paths);
            return S_OK;
        }
        catch (Exception ex) { Log.Warn("Explorer menu: " + ex.Message); return E_FAIL; }
    }

    public int GetFlags(out uint flags) { flags = 0; /*ECF_DEFAULT*/ return S_OK; }
    public int EnumSubCommands(out IntPtr enumCommands) { enumCommands = IntPtr.Zero; return E_NOTIMPL; }

    /// <summary>File system paths of the chosen items.</summary>
    internal static List<string> PathsOf(IShellItemArray? items)
    {
        var list = new List<string>();
        if (items == null || items.GetCount(out uint n) != 0) return list;
        for (uint i = 0; i < n; i++)
        {
            if (items.GetItemAt(i, out var item) != 0 || item == null) continue;
            if (item.GetDisplayName(SIGDN_FILESYSPATH, out var p) == 0 && p != IntPtr.Zero)
            {
                var s = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                if (!string.IsNullOrEmpty(s) && (Directory.Exists(s))) list.Add(s);
            }
        }
        return list;
    }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class OpenInByteScapeFactory : IClassFactory
{
    private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);

    public int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr obj)
    {
        obj = IntPtr.Zero;
        if (outer != IntPtr.Zero) return CLASS_E_NOAGGREGATION;
        var unk = Marshal.GetIUnknownForObject(new OpenInByteScapeCommand());
        try { return Marshal.QueryInterface(unk, ref riid, out obj); }
        finally { Marshal.Release(unk); }
    }

    public int LockServer(bool doLock) => 0;
}

/// <summary>ByteScape.exe started by COM for Explorer's menu (-ShellMenu): serves the class, opens ByteScape windows, exits when idle.</summary>
public static class ShellMenu
{
    public const string ClsidText = "7B2C3F0E-5A1D-4E8B-9C6F-2D4A8B1E3C57";
    public static readonly Guid Clsid = new(ClsidText);

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(ref Guid clsid, [MarshalAs(UnmanagedType.IUnknown)] object unk, uint context, uint flags, out uint cookie);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint cookie);

    private static long _lastUse = Environment.TickCount64;
    internal static void Touch() => Interlocked.Exchange(ref _lastUse, Environment.TickCount64);
    internal static TimeSpan Idle => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastUse));

    /// <summary>Tests: gets the paths instead of starting ByteScape.</summary>
    internal static Action<IReadOnlyList<string>>? LaunchHook;

    /// <summary>Started by COM: the arguments name this mode (-ShellMenu, and COM's -Embedding).</summary>
    public static bool IsServerStart(string[] args) => args.Any(a => a.Equals("-ShellMenu", StringComparison.OrdinalIgnoreCase)
                                                                   || a.Equals("-Embedding", StringComparison.OrdinalIgnoreCase) || a.Equals("/Embedding", StringComparison.OrdinalIgnoreCase));

    /// <summary>Opens the paths in a new ByteScape window (a new process, like starting it with "scan").</summary>
    internal static void Launch(IReadOnlyList<string> paths)
    {
        if (LaunchHook != null) { LaunchHook(paths); return; }
        var psi = new ProcessStartInfo(Environment.ProcessPath ?? "ByteScape.exe") { UseShellExecute = false };
        foreach (var p in paths) { psi.ArgumentList.Add("scan"); psi.ArgumentList.Add(p); }
        Process.Start(psi);
    }

    /// <summary>Registers the class for Explorer; returns the cookie to revoke it (0 when it failed).</summary>
    public static uint Register()
    {
        var clsid = Clsid;
        int hr = CoRegisterClassObject(ref clsid, new OpenInByteScapeFactory(), 4 /*CLSCTX_LOCAL_SERVER*/, 1 /*REGCLS_MULTIPLEUSE*/, out uint cookie);
        if (hr != 0) { Log.Warn($"Explorer menu class not registered: 0x{hr:X8}"); return 0; }
        Touch();
        return cookie;
    }

    public static void Revoke(uint cookie) { if (cookie != 0) CoRevokeClassObject(cookie); }
}
