using System.Runtime.InteropServices;

namespace ByteScape.Interop;

/// <summary>
/// Shows the Windows Explorer popup menu for a file or folder (right click on an element).
/// ByteScape itself never modifies the file system; every action is Explorer's.
/// </summary>
public static class ShellContextMenu
{
    public static void Show(IntPtr ownerHwnd, string path, int screenX, int screenY)
    {
        IntPtr pidl = IntPtr.Zero, parentFolderPtr = IntPtr.Zero, contextMenuPtr = IntPtr.Zero, hMenu = IntPtr.Zero;
        try
        {
            if (Native.SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return;
            var iidFolder = Native.IID_IShellFolder;
            if (Native.SHBindToParent(pidl, ref iidFolder, out parentFolderPtr, out var pidlLast) != 0) return;
            var folder = (Native.IShellFolder)Marshal.GetObjectForIUnknown(parentFolderPtr);
            var iidMenu = Native.IID_IContextMenu;
            if (folder.GetUIObjectOf(ownerHwnd, 1, new[] { pidlLast }, ref iidMenu, IntPtr.Zero, out contextMenuPtr) != 0) return;
            var menu = (Native.IContextMenu)Marshal.GetObjectForIUnknown(contextMenuPtr);

            hMenu = Native.CreatePopupMenu();
            const uint idFirst = 1, idLast = 0x7FFF;
            menu.QueryContextMenu(hMenu, 0, idFirst, idLast, Native.CMF_NORMAL | Native.CMF_EXPLORE);
            int cmd = Native.TrackPopupMenuEx(hMenu, Native.TPM_RETURNCMD | Native.TPM_LEFTBUTTON, screenX, screenY, ownerHwnd, IntPtr.Zero);
            if (cmd <= 0) return;

            var info = new Native.CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<Native.CMINVOKECOMMANDINFOEX>(),
                fMask = Native.CMIC_MASK_UNICODE | Native.CMIC_MASK_PTINVOKE,
                hwnd = ownerHwnd,
                lpVerb = (IntPtr)(cmd - idFirst),
                lpVerbW = (IntPtr)(cmd - idFirst),
                nShow = Native.SW_SHOWNORMAL,
                ptInvokeX = screenX,
                ptInvokeY = screenY,
            };
            menu.InvokeCommand(ref info);
        }
        catch (Exception ex)
        {
            Core.Util.Log.Warn("Shell menu error: " + ex.Message);
        }
        finally
        {
            if (hMenu != IntPtr.Zero) Native.DestroyMenu(hMenu);
            if (contextMenuPtr != IntPtr.Zero) Marshal.Release(contextMenuPtr);
            if (parentFolderPtr != IntPtr.Zero) Marshal.Release(parentFolderPtr);
            if (pidl != IntPtr.Zero) Native.ILFree(pidl);
        }
    }
}
