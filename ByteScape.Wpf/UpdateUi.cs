using System.Windows;
using ByteScape.Core.Config;
using ByteScape.Core.Update;
using ByteScape.Core.Util;
using ByteScape.Wpf.Windows;

namespace ByteScape.Wpf;

/// <summary>The update check as the user sees it: asked for (the answer is always shown), or the weekly one (only news is shown).</summary>
internal static class UpdateUi
{
    /// <summary>Tests: gets the message a check would show in a message box.</summary>
    internal static Action<string>? MessageHook;

    public static async Task CheckNow(Window owner)
    {
        var old = owner.Cursor;
        owner.Cursor = System.Windows.Input.Cursors.AppStarting;
        UpdateResult r;
        try { r = await UpdateCheck.CheckAsync(); }
        finally { owner.Cursor = old; }
        Record(r);
        switch (r.Status)
        {
            case UpdateStatus.NewerAvailable:
                new UpdateWindow(r.Info!) { Owner = owner }.Show();
                break;
            case UpdateStatus.UpToDate:
                Tell(owner, F($"{Branding.Name} {Branding.Version} is the newest version."), MessageBoxImage.Information);
                break;
            default:
                Tell(owner, T("Could not check for updates:") + " " + r.Message, MessageBoxImage.Warning);
                break;
        }
    }

    /// <summary>The weekly check: a newer version the user has not skipped opens the update window; nothing else is shown.</summary>
    public static async void CheckInBackground(Window owner)
    {
        try
        {
            var r = await UpdateCheck.CheckAsync();
            Record(r);
            if (r.Status == UpdateStatus.NewerAvailable && !string.Equals(r.Info!.Version, AppConfig.Current.SkippedVersion, StringComparison.OrdinalIgnoreCase) && owner.IsLoaded)
                new UpdateWindow(r.Info) { Owner = owner }.Show();
        }
        catch (Exception ex) { Log.Debug("Update check: " + ex.Message); }
    }

    /// <summary>An answer was had: the next weekly check is a week from now (a failed check is tried again at the next start).</summary>
    private static void Record(UpdateResult r)
    {
        if (r.Status is UpdateStatus.NewerAvailable or UpdateStatus.UpToDate) AppConfig.Current.LastUpdateCheckUtc = DateTime.UtcNow;
    }

    private static void Tell(Window owner, string text, MessageBoxImage icon)
    {
        if (MessageHook != null) { MessageHook(text); return; }
        Dialogs.Show(owner, text, T("Check for updates"), MessageBoxButton.OK, icon);
    }
}
