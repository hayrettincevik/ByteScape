using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using ByteScape.Core.Config;
using ByteScape.Treemap;
using ByteScape.Wpf.Controls;
using ByteScape.Wpf.ViewModels;
using Microsoft.Win32;

namespace ByteScape.Wpf.Windows;

/// <summary>Settings window with navigation pages and a live preview. OK/Apply save; Cancel discards.</summary>
public partial class ConfigWindow : Window
{
    internal ConfigViewModel Vm { get; private set; }
    private readonly bool _watchDogInitial;
    private readonly ViewModel _previewModel;
    private StackPanel[] _pages = Array.Empty<StackPanel>();

    public bool Applied { get; private set; }

    internal sealed record ClassRow(FileClass Class)
    {
        public string Name => Class.DisplayName;
        public string Extensions => Class.Extensions;
        public Brush Brush => new SolidColorBrush(ConfigViewModel.ToWpf(Class.Color));
    }

    /// <summary>Update checks: the portable exe with an update address only (the Store updates its package).</summary>
    private void ShowUpdates()
    {
        UpdatesPanel.Visibility = Core.Update.UpdateCheck.Available ? Visibility.Visible : Visibility.Collapsed;
        UpdatesHint.Text = F($"Only a request for a small file at {Core.Update.UpdateCheck.Url} is sent, with the program's name and version — nothing about you or your files. A new version's download page opens in your browser; nothing is installed by itself. Off: nothing is checked unless you choose Check for updates in the menu.");
    }

    /// <summary>The Explorer entry of the portable exe (the Store package brings its own).</summary>
    private void UpdateExplorerMenu()
    {
        if (AppConfig.IsPackaged())
        {
            ExplorerMenuText.Text = F($"\"{Core.ShellIntegration.MenuText}\" is in Explorer's right-click menu of folders and drives (the Store version adds it itself).");
            ExplorerMenuBtn.Visibility = Visibility.Collapsed;
            ExplorerMenuHint.Visibility = Visibility.Collapsed;
            return;
        }
        var exe = Environment.ProcessPath ?? "";
        bool mine = exe.Length > 0 && Core.ShellIntegration.IsInstalled(exe);
        bool other = !mine && Core.ShellIntegration.IsAnyInstalled();
        ExplorerMenuText.Text = mine ? F($"\"{Core.ShellIntegration.MenuText}\" is in Explorer's right-click menu of folders and drives.")
            : other ? F($"Explorer's menu opens another copy of {Branding.Name}. Add to point it to this one.")
            : F($"Add \"{Core.ShellIntegration.MenuText}\" to Explorer's right-click menu of folders and drives.");
        ExplorerMenuBtn.Content = T(mine ? "Remove" : "Add");
        ExplorerMenuBtn.IsEnabled = exe.Length > 0;
    }

    private void ExplorerMenu_Click(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        try
        {
            if (Core.ShellIntegration.IsInstalled(exe)) Core.ShellIntegration.Uninstall();
            else if (Core.Cleanup.AccessProbe.IsElevated)
            {
                // it would be added for the administrator account, pointing at an exe the everyday account can replace
                Dialogs.Show(this, F($"{Branding.Name} is running as administrator: the menu would be added for the administrator account. Start {Branding.Name} normally to add it."), T("Explorer menu"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            else Core.ShellIntegration.Install(exe);
        }
        catch (Exception ex) { Dialogs.Show(this, ex.Message, T("Explorer menu"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        UpdateExplorerMenu();
    }

    public ConfigWindow()
    {
        InitializeComponent();
        Localizer.Apply(this);
        Vm = new ConfigViewModel(AppConfig.Current.Clone());
        ShowUpdates();
        _watchDogInitial = Vm.Model.WatchDog;
        DataContext = Vm;
        _pages = new[] { PageGeometry, PageAnimation, PageBehavior, PageScanning, PageLook, PageStyles, PageAdvanced };
        Nav.SelectedIndex = 0;
        ConfigPathBox.Text = AppConfig.ConfigPath;
        UpdateExplorerMenu();
        var tree = SampleTree.Create();
        _previewModel = new ViewModel(tree, tree.Root) { ShowFreeSpace = true, DetailLevel = 3 };
        Preview.Model = _previewModel;
        Preview.ConfigOverride = Vm.Model;
        Vm.Changed += () => { Preview.ConfigOverride = Vm.Model; _previewModel.DetailLevel = Vm.Model.InitialDetailLevel; _previewModel.ShowFreeSpace = Vm.Model.ShowFreeSpace; Preview.ApplyConfig(); };
        FillClasses();
        ShowAiKey();
        _languageInitial = Loc.Resolve(Vm.Model.Language);
        FillLanguages();
    }

    internal sealed record LanguageChoice(string Code, string Name) { public override string ToString() => Name; }
    private readonly string _languageInitial;
    private bool _fillingLanguages;

    /// <summary>"Automatic (Türkçe)" first — Windows' display language, or English when it is not one of ours — then every language in its own name.</summary>
    private void FillLanguages()
    {
        var auto = Loc.Resolve("");
        var list = new List<LanguageChoice> { new("", T("Automatic") + " (" + Loc.Languages.First(l => l.Code == auto).NativeName + ")") };
        list.AddRange(Loc.Languages.Select(l => new LanguageChoice(l.Code, l.NativeName)));
        _fillingLanguages = true;   // showing the setting is not changing it (an unknown code stays as it is: it means automatic)
        LanguageBox.ItemsSource = list;
        LanguageBox.SelectedItem = list.FirstOrDefault(l => l.Code.Equals(Vm.Model.Language, StringComparison.OrdinalIgnoreCase)) ?? list[0];
        _fillingLanguages = false;
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_fillingLanguages && LanguageBox.SelectedItem is LanguageChoice c) Vm.Language = c.Code;
    }

    // a PasswordBox cannot be bound: the key goes in and out by hand
    private void ShowAiKey() { if (AiKeyBox.Password != Vm.AiApiKey) AiKeyBox.Password = Vm.AiApiKey; }
    private void AiKeyBox_PasswordChanged(object sender, RoutedEventArgs e) => Vm.AiApiKey = AiKeyBox.Password;

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        for (int i = 0; i < _pages.Length; i++) _pages[i].Visibility = i == Nav.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void SelectPage(int index) => Nav.SelectedIndex = index;

    private void PreviewClasses_Click(object sender, RoutedEventArgs e)
    {
        _previewModel.Style = PreviewClasses.IsChecked == true ? ViewStyle.FileClasses : ViewStyle.Flat;
        Preview.MarkDirty();
    }

    // ------------------------------------------------------------------ apply / defaults / import / export

    internal static string? Validate(AppConfig c)
    {
        foreach (var m in c.ExcludePaths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (m.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return T("Exclude mask contains invalid characters:") + " " + m;
        foreach (var f in c.FileClasses)
            if (string.IsNullOrWhiteSpace(f.Name)) return T("Every file class needs a description.");
        if (c.FileClasses.Select(f => f.Name.Trim().ToLowerInvariant()).Distinct().Count() != c.FileClasses.Count) return T("File class descriptions must be unique.");
        return null;
    }

    internal bool ApplyChanges()
    {
        var c = Vm.Model.Clone();
        var error = Validate(c);
        if (error != null) { Dialogs.Show(this, error, T("Settings"), MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        AppConfig.Replace(c);
        AppConfig.Save();
        Applied = true;
        if (c.WatchDog != _watchDogInitial)
            Dialogs.Show(this, F($"The new setting for following changes on the disk takes effect when {Branding.Name} starts again."), T("Information"), MessageBoxButton.OK, MessageBoxImage.Information);
        var language = Loc.Resolve(c.Language);
        if (language != _languageInitial && language != Loc.Language)
        {
            // windows opened from now on speak the new language; the ones already open need a restart
            App.UseLanguage(c.Language);
            if (MainWindow.Instance?.Unattended != true && Dialogs.Show(this, F($"The new language is used everywhere after {Branding.Name} restarts. Restart now? (Open scans are kept.)"),
                    Branding.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                MainWindow.Instance?.Dispatcher.BeginInvoke(() => MainWindow.Instance?.Restart(elevated: false));
        }
        return true;
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => ApplyChanges();
    private void Ok_Click(object sender, RoutedEventArgs e) { if (ApplyChanges()) Close(); }

    internal void RestoreDefaults()
    {
        var d = AppConfig.Defaults();
        d.ExportConfigs = Vm.Model.ExportConfigs.Select(x => x.Clone()).ToList();
        d.RecentPaths = new List<string>(Vm.Model.RecentPaths);
        d.AiApiKey = Vm.Model.AiApiKey;
        Vm.Reset(d);
        FillClasses();
        ShowAiKey();
        FillLanguages();
    }

    private void Defaults_Click(object sender, RoutedEventArgs e) => RestoreDefaults();

    internal void ImportFrom(string path)
    {
        var cfg = AppConfig.FromXml(XDocument.Load(path).Root ?? throw new InvalidDataException("empty document"));
        if (cfg.AiApiKey.Length == 0) cfg.AiApiKey = Vm.Model.AiApiKey; // exported profiles carry no key: keep the one entered here
        // a profile passed around could switch the local AI on with an address of its own: folder and file names would go there
        var cur = Vm.Model;
        if (cfg.AiEnabled && (!cur.AiEnabled || !string.Equals(cfg.AiEndpoint.Trim(), cur.AiEndpoint.Trim(), StringComparison.OrdinalIgnoreCase)) && !ConfirmAi(cfg.AiEndpoint))
        {
            cfg.AiEnabled = cur.AiEnabled; cfg.AiEndpoint = cur.AiEndpoint; cfg.AiModel = cur.AiModel;
        }
        Vm.Reset(cfg);
        FillClasses();
        ShowAiKey();
        FillLanguages();
    }

    /// <summary>Tests: answers the question about a profile's local AI address instead of the user.</summary>
    internal static Func<string, bool>? ConfirmAiHook;

    private bool ConfirmAi(string endpoint) => ConfirmAiHook?.Invoke(endpoint)
        ?? Dialogs.Show(this, F($"This profile switches the local AI on: the names and sizes of folders and files would be sent to {endpoint}.") + "\n\n" + T("Take over the local AI settings of the profile?"),
               T("Import settings"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>A profile is meant to be shared or carried to another computer: the API key stays out of it.</summary>
    internal bool ExportTo(string path)
    {
        var c = Vm.Model.Clone();
        c.AiApiKey = "";
        return AppConfig.Save(c, path);
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = T("XML files (*.xml)|*.xml"), Title = T("Import settings") };
        if (Dialogs.Pick(dlg, this) != true) return;
        try { ImportFrom(dlg.FileName); }
        catch (Exception ex) { Dialogs.Show(this, T("The settings could not be imported:") + "\n\n" + ex.Message, T("Error"), MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = T("XML files (*.xml)|*.xml"), FileName = Branding.Name + "Config.xml", Title = T("Export settings") };
        if (Dialogs.Pick(dlg, this) != true) return;
        if (!ExportTo(dlg.FileName)) Dialogs.Show(this, F($"Cannot write {dlg.FileName}"), T("Error"), MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void ClearRecent_Click(object sender, RoutedEventArgs e) => Vm.Model.RecentPaths.Clear();

    /// <summary>Lists the models of the configured endpoint (and fills the model box) — the connection test.</summary>
    private async void AiTest_Click(object sender, RoutedEventArgs e)
    {
        AiTestResult.Text = T("Connecting…");
        try
        {
            var model = new Core.Ai.LocalModel(Vm.AiEndpoint, Vm.AiModel, Vm.AiApiKey, Math.Min(Vm.AiTimeoutSeconds, 15));
            var models = await model.ListModelsAsync();
            AiModelBox.ItemsSource = models;
            if (models.Count > 0 && string.IsNullOrWhiteSpace(Vm.AiModel)) Vm.AiModel = models[0];
            AiTestResult.Text = models.Count == 0 ? T("Connected, but the server lists no models (pull one first, e.g. ollama pull qwen2.5:3b).") : "OK — " + P(models.Count, "{0} model:", "{0} models:") + " " + string.Join(", ", models.Take(6)) + (models.Count > 6 ? ", …" : "");
        }
        catch (Exception ex) { AiTestResult.Text = T("Not reachable:") + " " + ex.Message; }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Dialogs.Start(new ProcessStartInfo(Core.Util.SystemTools.Explorer, "/select,\"" + AppConfig.ConfigPath + "\"") { UseShellExecute = true }); } catch { }
    }

    // ------------------------------------------------------------------ file classes

    internal void FillClasses()
    {
        int sel = Classes.SelectedIndex;
        Classes.ItemsSource = Vm.Model.FileClasses.Select(c => new ClassRow(c)).ToList();
        if (sel >= 0 && sel < Classes.Items.Count) Classes.SelectedIndex = sel;
    }

    private FileClass? SelectedClass => (Classes.SelectedItem as ClassRow)?.Class;

    private void AddClass_Click(object sender, RoutedEventArgs e)
    {
        var c = new FileClass();
        if (EditClassDialog(c)) { Vm.Model.FileClasses.Add(c); FillClasses(); Classes.SelectedIndex = Classes.Items.Count - 1; Vm.NotifyClasses(); }
    }

    private void EditClass_Click(object sender, RoutedEventArgs e)
    {
        var c = SelectedClass;
        if (c != null && EditClassDialog(c)) { FillClasses(); Vm.NotifyClasses(); }
    }

    private void DeleteClass_Click(object sender, RoutedEventArgs e)
    {
        var c = SelectedClass;
        if (c == null) return;
        Vm.Model.FileClasses.Remove(c);
        FillClasses(); Vm.NotifyClasses();
    }

    private void MoveClass(int delta)
    {
        int i = Classes.SelectedIndex, j = i + delta;
        if (i < 0 || j < 0 || j >= Vm.Model.FileClasses.Count) return;
        var l = Vm.Model.FileClasses;
        (l[i], l[j]) = (l[j], l[i]);
        FillClasses();
        Classes.SelectedIndex = j;
        Vm.NotifyClasses();
    }

    private void UpClass_Click(object sender, RoutedEventArgs e) => MoveClass(-1);
    private void DownClass_Click(object sender, RoutedEventArgs e) => MoveClass(+1);
    private void DefaultClasses_Click(object sender, RoutedEventArgs e) { Vm.Model.FileClasses = AppConfig.DefaultFileClasses(); FillClasses(); Vm.NotifyClasses(); }

    private bool EditClassDialog(FileClass c)
    {
        var w = new Window { Title = T("File class"), Owner = this, Width = 460, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var name = new TextBox { Text = c.DisplayName, Margin = new Thickness(0, 4, 0, 10) };
        var ext = new TextBox { Text = c.Extensions, Margin = new Thickness(0, 4, 0, 10) };
        var color = new ColorPicker { Color = ConfigViewModel.ToWpf(c.Color), Margin = new Thickness(0, 4, 0, 14) };
        var ok = new Button { Content = T("OK"), MinWidth = 90, IsDefault = true, Style = (Style)FindResource("AccentButton"), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = T("Cancel"), MinWidth = 90, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = T("Description") }); panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = T("Extensions (a;b;c)") }); panel.Children.Add(ext);
        panel.Children.Add(new TextBlock { Text = T("Colour") }); panel.Children.Add(color);
        panel.Children.Add(buttons);
        w.Content = panel;
        bool result = false;
        ok.Click += (_, _) => { result = true; w.Close(); };
        Dialogs.ShowModal(w);
        if (!result) return false;
        var typed = name.Text.Trim();
        if (typed != c.DisplayName) c.Name = typed;          // a default class keeps its name while it is shown translated
        c.Extensions = ext.Text.Trim(); c.Color = ConfigViewModel.ToDrawing(color.Color); c.Invalidate();
        return true;
    }
}
