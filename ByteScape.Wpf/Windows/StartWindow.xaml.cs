using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ByteScape.Core.Config;
using ByteScape.Core.Util;
using ByteScape.Wpf.Controls;
using Microsoft.Win32;

namespace ByteScape.Wpf.Windows;

/// <summary>Start dialog: drive cards, a path box with recent paths, and a snapshot file drop area.</summary>
public partial class StartWindow : Window
{
    public StartWindow()
    {
        InitializeComponent();
        Localizer.Apply(this);
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] p && p.Length > 0) { MainWindow.Instance?.OpenDropped(p); Close(); } };
        Loaded += (_, _) => { FillDrives(); FillRecent(); PathBox.Focus(); };
    }

    private void FillDrives()
    {
        Drives.Items.Clear();
        foreach (var d in DriveInfo.GetDrives())
        {
            string label = "", free = "", total = "";
            double used = 0;
            try
            {
                if (d.IsReady)
                {
                    label = d.VolumeLabel;
                    free = F($"{Format.Size(d.TotalFreeSpace)} free");
                    total = F($"of {Format.Size(d.TotalSize)}");
                    used = d.TotalSize > 0 ? 1.0 - (double)d.TotalFreeSpace / d.TotalSize : 0;
                }
            }
            catch { }
            // what kind of drive: SSD, HDD, USB, network, CD (no administrator rights needed); a network drive may be disconnected
            string kind = "", product = "";
            try
            {
                if (d.IsReady)
                {
                    var k = ByteScape.Core.Scanning.DriveProfile.Detect(d.Name);
                    kind = k.Kind switch
                    {
                        ByteScape.Core.Scanning.DriveKind.UsbFlash => "USB", ByteScape.Core.Scanning.DriveKind.Nvme => "NVMe", ByteScape.Core.Scanning.DriveKind.Network => T("Network"),
                        ByteScape.Core.Scanning.DriveKind.Optical => "CD / DVD", _ => ByteScape.Core.Scanning.ScanStrategy.KindName(k.Kind),
                    };
                    product = k.Product;
                }
                else kind = T(d.DriveType == DriveType.Network ? "Network, not connected" : d.DriveType == DriveType.CDRom ? "CD / DVD, no disc" : "not ready");
            }
            catch { }
            // a button, so Tab reaches it and Enter or Space picks it (a double click starts the scan)
            var card = new Button { Style = (Style)FindResource("CardButton"), Width = 180, Height = 96, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(12) };
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new Glyph { Text = d.DriveType == DriveType.Network ? "" : d.DriveType == DriveType.Removable ? "" : "", FontSize = 16, Margin = new Thickness(0, 0, 8, 0) });
            head.Children.Add(new TextBlock { Text = d.Name.TrimEnd('\\') + (label.Length > 0 ? "  " + label : ""), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            g.Children.Add(head);
            string name = d.Name.TrimEnd('\\') + (label.Length > 0 ? "  " + label : "");
            string facts = (kind.Length > 0 ? kind + (d.IsReady ? " · " : "") : "") + (d.IsReady ? free + " " + total : kind.Length > 0 ? "" : d.DriveType.ToString());
            AutomationProperties.SetName(card, name + ", " + facts);
            var sub = new TextBlock { Text = facts, Style = (Style)FindResource("Secondary"), FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            if (product.Length > 0) card.ToolTip = product;
            Grid.SetRow(sub, 1); g.Children.Add(sub);
            var bar = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Bottom };
            bar.Children.Add(new Border { Background = (Brush)FindResource("Border"), CornerRadius = new CornerRadius(3) });
            var fill = new Border { Background = used > 0.9 ? (Brush)FindResource("Error") : (Brush)FindResource("Accent"), CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
            bar.SizeChanged += (_, _) => fill.Width = bar.ActualWidth * used;
            bar.Children.Add(fill);
            Grid.SetRow(bar, 3); g.Children.Add(bar);
            card.Content = g;
            var path = d.Name;
            card.Click += (_, _) => PathBox.Text = path;
            card.MouseDoubleClick += (_, _) => { PathBox.Text = path; Start(); };
            Drives.Items.Add(card);
        }
    }

    private void FillRecent()
    {
        var cfg = AppConfig.Current;
        Recent.Items.Clear();
        Recent.Items.Add(T(cfg.RememberRecentPaths && cfg.RecentPaths.Count > 0 ? "Recent paths…" : "(no recent paths)"));
        if (cfg.RememberRecentPaths) foreach (var p in cfg.RecentPaths) Recent.Items.Add(p);
        Recent.SelectedIndex = 0;
        Recent.IsEnabled = Recent.Items.Count > 1;
    }

    private void Recent_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Recent.SelectedIndex > 0) PathBox.Text = (string)Recent.SelectedItem;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = T("Choose a folder to scan") };
        if (Dialogs.Pick(dlg, this) == true) PathBox.Text = dlg.FolderName;
    }

    private void File_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = F($"{Branding.Name} snapshot (*.sns)|*.sns|Disk Usage output (*.txt)|*.txt|All files (*.*)|*.*"), Title = T("Open snapshot") };
        if (Dialogs.Pick(dlg, this) == true) FileBox.Text = dlg.FileName;
    }

    private void Start_Click(object sender, RoutedEventArgs e) => Start();

    private void Start()
    {
        if (Tabs.SelectedIndex == 0)
        {
            var paths = PathBox.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (paths.Length == 0) return;
            foreach (var p in paths)
                // network paths are checked by OpenScan on a background thread (an unreachable host blocks for seconds)
                if (!p.StartsWith(@"\\", StringComparison.Ordinal) && !Directory.Exists(p)) { Dialogs.Show(this, F($"{p} is not a valid path"), T("Bad Path"), MessageBoxButton.OK, MessageBoxImage.Error); return; }
            foreach (var p in paths) MainWindow.Instance?.OpenScan(p);
        }
        else
        {
            var files = FileBox.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (files.Length == 0) return;
            foreach (var f in files)
                if (!File.Exists(f)) { Dialogs.Show(this, F($"Unable to open file '{f}'"), T("Bad Path"), MessageBoxButton.OK, MessageBoxImage.Error); return; }
            foreach (var f in files) MainWindow.Instance?.OpenSnapshot(f);
        }
        Close();
    }
}
