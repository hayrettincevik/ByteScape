using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using ByteScape.Core.Util;

namespace ByteScape.Wpf.Windows;

/// <summary>Log console; entries arrive from scanner threads and are drained in batches.</summary>
public partial class LogWindow : Window
{
    private readonly ObservableCollection<LogEntry> _items = new();
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly DispatcherTimer _drain;

    public event Action<bool>? PopupDisabledChanged;

    public LogWindow()
    {
        InitializeComponent();
        Localizer.Apply(this);
        List.ItemsSource = _items;
        foreach (var e in Log.Snapshot()) _pending.Enqueue(e);
        _drain = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _drain.Tick += (_, _) => Drain();
        _drain.Start();
        Log.Added += OnAdded;
        Closed += (_, _) => { Log.Added -= OnAdded; _drain.Stop(); };
    }

    private void OnAdded(LogEntry e) => _pending.Enqueue(e);

    private void Drain()
    {
        if (_pending.IsEmpty) return;
        int n = 0;
        while (n++ < 500 && _pending.TryDequeue(out var e)) _items.Add(e);
        while (_items.Count > Log.MaxEntries) _items.RemoveAt(0);
        if (_items.Count > 0) List.ScrollIntoView(_items[^1]);
    }

    private void AutoPopup_Click(object sender, RoutedEventArgs e)
    {
        PopupDisabledChanged?.Invoke(AutoPopup.IsChecked != true);
        if (AutoPopup.IsChecked != true) Hide();
    }

    private void Clear_Click(object sender, RoutedEventArgs e) { Log.Clear(); _items.Clear(); }
    private void Copy_Click(object sender, RoutedEventArgs e) { try { Dialogs.Copy(string.Join("\r\n", Log.Snapshot())); } catch { } }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true; // keep the window (and its contents) around for the next time it is opened
        Hide();
    }
}
