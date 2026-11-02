using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using ByteScape.Core.Config;
using ByteScape.Core.Export;
using ByteScape.Core.Model;
using ByteScape.Treemap;
using Microsoft.Win32;

namespace ByteScape.Wpf.Windows;

/// <summary>Export module: run time mode (pick a configuration, export) and design time mode (edit configurations).</summary>
public partial class ExportWindow : Window
{
    private readonly ViewModel _model;
    private bool _loading;
    private bool _design;
    private CancellationTokenSource? _cts;

    public ExportWindow(ViewModel model)
    {
        InitializeComponent();
        Localizer.Apply(this);
        _model = model;
        Block.ItemsSource = new[] { T("none"), T("files first"), T("folders first") };
        Fine.ItemsSource = new[] { T("none"), T("size"), T("name") };
        FillConfigs(0);
    }

    private ExportConfig? Current => Configs.SelectedItem as ExportConfig;

    private void FillConfigs(int index)
    {
        _loading = true;
        Configs.ItemsSource = null;
        Configs.ItemsSource = ExportConfig.All();
        _loading = false;
        Configs.SelectedIndex = Math.Clamp(index, 0, Configs.Items.Count - 1);
    }

    private void SelectConfig(ExportConfig c)
    {
        for (int i = 0; i < Configs.Items.Count; i++) if (ReferenceEquals(Configs.Items[i], c)) { Configs.SelectedIndex = i; return; }
    }

    private void Configs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var c = Current;
        if (c == null) return;
        _loading = true;
        Desc.Text = c.Description; FileName.Text = c.PreferredFileName; FileExt.Text = c.PreferredFileExtension;
        Header.Text = c.HeaderFormat; Detail.Text = c.DetailFormat; Footer.Text = c.FooterFormat;
        Block.SelectedIndex = (int)c.BlockSorting; Fine.SelectedIndex = (int)c.FineSorting; Descending.IsChecked = c.FineSortingDescending;
        _loading = false;
        UpdateDesignButtons();
        UpdatePreview();
    }

    private ExportConfig BuildFromDesign(ExportConfig basis)
    {
        var c = basis.Clone();
        c.Description = Desc.Text; c.PreferredFileName = FileName.Text; c.PreferredFileExtension = FileExt.Text;
        c.HeaderFormat = Header.Text; c.DetailFormat = Detail.Text; c.FooterFormat = Footer.Text;
        c.BlockSorting = (BlockSorting)Math.Max(0, Block.SelectedIndex); c.FineSorting = (FineSorting)Math.Max(0, Fine.SelectedIndex); c.FineSortingDescending = Descending.IsChecked == true;
        c.Version = ExportConfig.CurrentVersion;
        return c;
    }

    private ExportConfig Effective()
    {
        var c = Current ?? ExportConfig.BinarySnapshot;
        return _design && !c.IsHardCoded ? BuildFromDesign(new ExportConfig()) : c;
    }

    private void Design_Changed(object sender, RoutedEventArgs e) { if (!_loading) UpdatePreview(); }

    private void UpdatePreview()
    {
        var c = Effective();
        Preview.Text = c.IsBinarySnapshot ? T("(binary format, no preview available)")
            : c.Report is { } report ? Reports.Preview(report, _model.ExportRoot, _model.Tree.Sync, Include, _model.SizeOf)
            : new Exporter(c, _model.ExportRoot, Include, _model.Tree.Sync, _model.SizeOf).Preview(40);
    }

    private bool Include(FsNode n) => _model.IsIncluded(n);

    private void Design_Click(object sender, RoutedEventArgs e)
    {
        _design = DesignToggle.IsChecked == true;
        if (_design && Current is { IsHardCoded: true })
        {
            int idx = -1;
            for (int i = 0; i < Configs.Items.Count; i++) if (Configs.Items[i] is ExportConfig { IsHardCoded: false }) { idx = i; break; }
            if (idx < 0) { New_Click(sender, e); return; }
            Configs.SelectedIndex = idx;
        }
        Design.Visibility = DesignTools.Visibility = _design ? Visibility.Visible : Visibility.Collapsed;
        UpdateDesignButtons();
        UpdatePreview();
    }

    private void UpdateDesignButtons()
    {
        bool editable = Current is { IsHardCoded: false };
        SaveBtn.IsEnabled = DeleteBtn.IsEnabled = ExportCfgBtn.IsEnabled = editable;
        Design.IsEnabled = editable;
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var c = new ExportConfig { Description = F($"New export {AppConfig.Current.ExportConfigs.Count + 1}"), DetailFormat = "<%pathfile%>{&br}" };
        AppConfig.Current.ExportConfigs.Add(c);
        AppConfig.Save();
        _design = true; DesignToggle.IsChecked = true; Design.Visibility = DesignTools.Visibility = Visibility.Visible;
        FillConfigs(0);
        SelectConfig(c);
        UpdateDesignButtons();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var cur = Current;
        if (cur == null || cur.IsHardCoded) return;
        var edited = BuildFromDesign(cur);
        if (string.IsNullOrWhiteSpace(edited.Description)) { Dialogs.Show(this, T("Give the export setting a name first."), T("Save export setting")); return; }
        var user = AppConfig.Current.ExportConfigs;
        var existing = user.FirstOrDefault(u => u.Description == edited.Description);
        if (existing != null && !ReferenceEquals(existing, cur))
        {
            if (Dialogs.Show(this, T("An export setting with this name already exists. Replace it?"), T("Name already used"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { Status.Text = T("Not saved"); return; }
            user.Remove(existing);
        }
        int idx = user.IndexOf(cur);
        if (idx >= 0) user[idx] = edited; else user.Add(edited);
        AppConfig.Save();
        Status.Text = T("Export setting saved");
        FillConfigs(0);
        SelectConfig(edited);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var cur = Current;
        if (cur == null) return;
        if (cur.IsHardCoded) { Dialogs.Show(this, T("This export setting is part of the program and cannot be deleted."), T("Delete export setting")); return; }
        if (cur.IsBuiltin) { Dialogs.Show(this, T("This export setting comes with the program and cannot be deleted.\n\nTo change it, edit it and save it under a name of your own."), T("Delete export setting")); return; }
        if (Dialogs.Show(this, T("Delete this export setting?"), T("Delete export setting"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { Status.Text = T("Not deleted"); return; }
        AppConfig.Current.ExportConfigs.Remove(cur);
        AppConfig.Save();
        Status.Text = T("Export setting deleted");
        FillConfigs(0);
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = T("XML files (*.xml)|*.xml"), Title = T("Import an export setting") };
        if (Dialogs.Pick(dlg, this) != true) { Status.Text = T("Nothing imported"); return; }
        try
        {
            var doc = XDocument.Load(dlg.FileName);
            var el = doc.Root?.Name == "exportConfig" ? doc.Root : doc.Root?.Element("exportConfig");
            if (el == null) { Dialogs.Show(this, T("The file holds no export setting."), T("Import an export setting")); return; }
            var c = ExportConfig.FromXml(el);
            var warn = c.VersionWarning();
            if (warn != null && Dialogs.Show(this, warn, T("Import an export setting"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) { Status.Text = T("Nothing imported"); return; }
            AppConfig.Current.ExportConfigs.RemoveAll(u => u.Description == c.Description);
            AppConfig.Current.ExportConfigs.Add(c);
            AppConfig.Save();
            Status.Text = T("Export setting imported");
            FillConfigs(0);
            SelectConfig(c);
        }
        catch (Exception ex) { Dialogs.Show(this, T("The export setting could not be imported:") + "\n\n" + ex.Message, T("Error"), MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ExportCfg_Click(object sender, RoutedEventArgs e)
    {
        var cur = Current;
        if (cur == null || cur.IsHardCoded) return;
        var dlg = new SaveFileDialog { Filter = T("XML files (*.xml)|*.xml"), FileName = cur.Description + ".xml" };
        if (Dialogs.Pick(dlg, this) != true) { Status.Text = T("Not saved"); return; }
        try { new XDocument(new XDeclaration("1.0", "UTF-8", null), BuildFromDesign(cur).ToXml()).Save(dlg.FileName); Status.Text = T("Export setting saved to the file"); }
        catch (Exception ex) { Dialogs.Show(this, T("The export setting could not be saved:") + "\n\n" + ex.Message, T("Error"), MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) { _cts.Cancel(); return; }
        var cfg = Effective();
        var exporter = new Exporter(cfg, _model.ExportRoot, Include, _model.Tree.Sync, _model.SizeOf);
        if (exporter.WouldBeEmpty() && Dialogs.Show(this, T("With this setting and filter the file would be empty. Export anyway?"), T("Export"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var dlg = new SaveFileDialog { Filter = cfg.PreferredFileExtension + "|" + T("All files (*.*)|*.*"), FileName = cfg.PreferredFileName + cfg.DefaultExtension, OverwritePrompt = true, Title = T("Export the view") };
        if (Dialogs.Pick(dlg, this) != true) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var path = dlg.FileName;
        var model = _model;
        ExportBtn.Content = T("Cancel");
        Status.Text = T("Exporting…");
        Task.Run(() =>
        {
            try
            {
                RunExport(cfg, model, path, token, n => Dispatcher.BeginInvoke(() => Status.Text = T("Exporting…") + " " + n.ToString("N0")));
                Dispatcher.BeginInvoke(() => Status.Text = T("Export finished"));
            }
            catch (OperationCanceledException) { try { File.Delete(path); } catch { } Dispatcher.BeginInvoke(() => Status.Text = T("Export cancelled; the unfinished file was deleted")); }
            catch (Exception ex) { Dispatcher.BeginInvoke(() => { Status.Text = T("Export failed"); Dialogs.Show(this, T("The file could not be written:") + "\n\n" + ex.Message, T("Export"), MessageBoxButton.OK, MessageBoxImage.Error); }); }
            finally { Dispatcher.BeginInvoke(() => { _cts = null; ExportBtn.Content = T("Export…"); }); }
        });
    }

    /// <summary>Runs an export synchronously (dialog worker and command line auto exports).</summary>
    public static void RunExport(ExportConfig cfg, ViewModel model, string path, CancellationToken token, Action<int>? progress)
    {
        bool Include(FsNode n) => model.IsIncluded(n);
        if (cfg.Report is { } report)
        {
            Reports.WriteFile(report, path, model.ExportRoot, model.Tree.Sync, Include, model.SizeOf, token, model.Filter.IsEmpty ? "" : model.Filter.Text, progress);
            return;
        }
        var exporter = new Exporter(cfg, model.ExportRoot, Include, model.Tree.Sync, model.SizeOf);
        if (cfg.IsBinarySnapshot) { exporter.ExportBinary(path, token, progress); return; }
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        exporter.ExportText(w, token, progress);
    }
}
