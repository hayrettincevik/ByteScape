using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace ByteScape.Wpf;

/// <summary>
/// Every message box, modal window, file picker, started program, clipboard write and Windows context menu of the
/// interface goes through here. The program shows them as usual; while <see cref="Test"/> is set (the command test that
/// presses every button of every window), they are answered by the test instead, recorded in its log, and nothing
/// reaches the screen, the clipboard or another program.
/// </summary>
internal static class Dialogs
{
    /// <summary>How a test answers the interface. Defaults: questions get "No" / "Cancel", pickers are cancelled.</summary>
    internal sealed class Script
    {
        public Func<string, string, MessageBoxButton, MessageBoxResult> Answer = (_, _, b) =>
            b is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel ? MessageBoxResult.No : b == MessageBoxButton.OKCancel ? MessageBoxResult.Cancel : MessageBoxResult.OK;
        public Func<CommonDialog, bool?> Pick = _ => false;
        /// <summary>A modal window: the test shows it off screen (not modal) and returns what it should have returned.</summary>
        public Func<Window, bool?> Modal = _ => false;
        public readonly List<string> Log = new();
        public readonly List<ProcessStartInfo> Started = new();
        public readonly List<string> Copied = new();
        /// <summary>Menus the interface wanted to open (the test presses their items instead).</summary>
        public readonly List<System.Windows.Controls.ContextMenu> Menus = new();
    }

    internal static Script? Test;

    // ------------------------------------------------------------------ message boxes

    public static MessageBoxResult Show(string text) => Ask(null, text, "", MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(string text, string caption) => Ask(null, text, caption, MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons) => Ask(null, text, caption, buttons, MessageBoxImage.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon) => Ask(null, text, caption, buttons, icon);
    public static MessageBoxResult Show(Window? owner, string text) => Ask(owner, text, "", MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(Window? owner, string text, string caption) => Ask(owner, text, caption, MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton buttons) => Ask(owner, text, caption, buttons, MessageBoxImage.None);
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage icon) => Ask(owner, text, caption, buttons, icon);
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult) => Ask(owner, text, caption, buttons, icon, defaultResult);

    private static MessageBoxResult Ask(Window? owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        if (Test is { } t)
        {
            var answer = t.Answer(text, caption, buttons);
            t.Log.Add($"message [{caption}] {text.Replace('\n', ' ')} -> {answer}");
            return answer;
        }
        return owner != null ? MessageBox.Show(owner, text, caption, buttons, icon, defaultResult) : MessageBox.Show(text, caption, buttons, icon, defaultResult);
    }

    // ------------------------------------------------------------------ modal windows and pickers

    public static bool? ShowModal(Window window)
    {
        if (Test is { } t) { t.Log.Add("modal " + window.GetType().Name); return t.Modal(window); }
        return window.ShowDialog();
    }

    public static bool? Pick(CommonDialog dialog, Window? owner)
    {
        if (Test is { } t)
        {
            var result = t.Pick(dialog);
            t.Log.Add($"picker {dialog.GetType().Name} -> {(result == true ? Picked(dialog) : "cancelled")}");
            return result;
        }
        return owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
    }

    private static string Picked(CommonDialog d) => d switch
    {
        FileDialog f => f.FileName,
        OpenFolderDialog o => o.FolderName,
        _ => "?",
    };

    // ------------------------------------------------------------------ other programs, clipboard, Windows menu

    /// <summary>Starts a program, a document or an address (Explorer, the browser, an uninstaller…).</summary>
    public static Process? Start(ProcessStartInfo psi)
    {
        if (Test is { } t) { t.Started.Add(psi); t.Log.Add($"start {psi.FileName} {psi.Arguments} {psi.Verb}".TrimEnd()); return null; }
        return Process.Start(psi);
    }

    public static void Copy(string text)
    {
        if (Test is { } t) { t.Copied.Add(text); t.Log.Add("clipboard " + text.Length + " characters"); return; }
        Clipboard.SetText(text);
    }

    /// <summary>Opens a menu under the element it belongs to (the map's right-click menu, "More", the ready-made filters).</summary>
    public static void OpenMenu(System.Windows.Controls.ContextMenu menu)
    {
        if (Test is { } t) { t.Menus.Add(menu); t.Log.Add("menu " + menu.Items.Count + " items"); return; }
        menu.IsOpen = true;
    }

    /// <summary>The Windows context menu of a file or folder (the last item of the map's right-click menu).</summary>
    public static void ShellMenu(IntPtr owner, string path, int x, int y)
    {
        if (Test is { } t) { t.Log.Add("windows menu " + path); return; }
        ByteScape.Interop.ShellContextMenu.Show(owner, path, x, y);
    }
}
