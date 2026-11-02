using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using ByteScape.Core.Config;
using D = System.Drawing;

namespace ByteScape.Wpf.ViewModels;

/// <summary>
/// Bindable wrapper around a working copy of <see cref="AppConfig"/>. Every setter writes the
/// model and raises <see cref="Changed"/> so the live preview can repaint.
/// </summary>
public sealed class ConfigViewModel : INotifyPropertyChanged
{
    public AppConfig Model { get; private set; }

    public ConfigViewModel(AppConfig model) => Model = model;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;

    /// <summary>Swaps the model (defaults / import) and refreshes every binding.</summary>
    public void Reset(AppConfig model)
    {
        Model = model;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        Changed?.Invoke();
    }

    private void Set<T>(T value, Action<T> write, [CallerMemberName] string? name = null)
    {
        write(value);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        Changed?.Invoke();
    }

    public static Color ToWpf(D.Color c) => Color.FromRgb(c.R, c.G, c.B);
    public static D.Color ToDrawing(Color c) => D.Color.FromArgb(c.R, c.G, c.B);

    // geometry
    public int FontSize { get => Model.FontSize; set => Set(value, v => Model.FontSize = v); }
    public int MinElementSizePx { get => Model.MinElementSizePx; set => Set(value, v => Model.MinElementSizePx = v); }
    public int Proportions { get => Model.Proportions; set => Set(value, v => Model.Proportions = v); }
    public int InitialDetailLevel { get => Model.InitialDetailLevel; set => Set(value, v => Model.InitialDetailLevel = v); }
    public bool UseSorting { get => Model.UseSorting; set => Set(value, v => Model.UseSorting = v); }
    public bool AggregateSmallElements { get => Model.AggregateSmallElements; set => Set(value, v => Model.AggregateSmallElements = v); }
    public bool ShowKnowledgeBadges { get => Model.ShowKnowledgeBadges; set => Set(value, v => Model.ShowKnowledgeBadges = v); }
    public bool AiEnabled { get => Model.AiEnabled; set => Set(value, v => Model.AiEnabled = v); }
    public bool CheckForUpdates { get => Model.CheckForUpdates; set => Set(value, v => Model.CheckForUpdates = v); }
    public string AiEndpoint { get => Model.AiEndpoint; set => Set(value, v => Model.AiEndpoint = v); }
    public string AiModel { get => Model.AiModel; set => Set(value, v => Model.AiModel = v); }
    public string AiApiKey { get => Model.AiApiKey; set => Set(value, v => Model.AiApiKey = v); }
    public int AiTimeoutSeconds { get => Model.AiTimeoutSeconds; set => Set(value, v => Model.AiTimeoutSeconds = v); }
    public bool ShowFreeSpace { get => Model.ShowFreeSpace; set => Set(value, v => Model.ShowFreeSpace = v); }
    public bool ShowUnknownSpace { get => Model.ShowUnknownSpace; set => Set(value, v => Model.ShowUnknownSpace = v); }
    // animation
    public int ZoomTimeMs { get => Model.ZoomTimeMs; set => Set(value, v => Model.ZoomTimeMs = v); }
    public bool UseRealZoom { get => Model.UseRealZoom; set => Set(value, v => Model.UseRealZoom = v); }
    public int RefreshDelayMs { get => Model.RefreshDelayMs; set => Set(value, v => Model.RefreshDelayMs = v); }
    public bool UseDynaSmooth { get => Model.UseDynaSmooth; set => Set(value, v => Model.UseDynaSmooth = v); }
    // behavior
    public bool FlashWindow { get => Model.FlashWindow; set => Set(value, v => Model.FlashWindow = v); }
    public bool KeepTooltip { get => Model.KeepTooltip; set => Set(value, v => Model.KeepTooltip = v); }
    public bool ShowFileTooltipDateTime { get => Model.ShowFileTooltipDateTime; set => Set(value, v => Model.ShowFileTooltipDateTime = v); }
    public bool ShowFileTooltipAge { get => Model.ShowFileTooltipAge; set => Set(value, v => Model.ShowFileTooltipAge = v); }
    public int TooltipDelayMs { get => Model.TooltipDelayMs; set => Set(value, v => Model.TooltipDelayMs = v); }
    public bool LogPopup { get => Model.LogPopup; set => Set(value, v => Model.LogPopup = v); }
    public bool LogWarning { get => Model.LogWarning; set => Set(value, v => Model.LogWarning = v); }
    public bool LogIssue { get => Model.LogIssue; set => Set(value, v => Model.LogIssue = v); }
    public bool LogDebug { get => Model.LogDebug; set => Set(value, v => Model.LogDebug = v); }
    public bool LogDebugFine { get => Model.LogDebugFine; set => Set(value, v => Model.LogDebugFine = v); }
    public int LogMaxEntries { get => Model.LogMaxEntries; set => Set(value, v => Model.LogMaxEntries = v); }
    // scanning
    public bool ScanADS { get => Model.ScanADS; set => Set(value, v => Model.ScanADS = v); }
    public bool WatchDog { get => Model.WatchDog; set => Set(value, v => Model.WatchDog = v); }
    public bool FollowReparsePoints { get => Model.FollowReparsePoints; set => Set(value, v => Model.FollowReparsePoints = v); }
    public bool UseMftScan { get => Model.UseMftScan; set => Set(value, v => Model.UseMftScan = v); }
    public bool KeepUnfinishedScans { get => Model.KeepUnfinishedScans; set => Set(value, v => Model.KeepUnfinishedScans = v); }
    public bool QuickRescan { get => Model.QuickRescan; set => Set(value, v => Model.QuickRescan = v); }
    public int ScanDriveKind { get => Model.ScanDriveKind; set => Set(value, v => Model.ScanDriveKind = v); }
    public bool CountHardLinksOnce { get => Model.CountHardLinksOnce; set => Set(value, v => Model.CountHardLinksOnce = v); }
    public bool RecordDiskHistory { get => Model.RecordDiskHistory; set => Set(value, v => Model.RecordDiskHistory = v); }
    public bool DeleteToRecycleBin { get => Model.DeleteToRecycleBin; set => Set(value, v => Model.DeleteToRecycleBin = v); }
    public bool ProtectSystemFolders { get => Model.ProtectSystemFolders; set => Set(value, v => Model.ProtectSystemFolders = v); }
    public int LargeFileMB { get => Model.LargeFileMB; set => Set(value, v => Model.LargeFileMB = v); }
    public int OldInstallerDays { get => Model.OldInstallerDays; set => Set(value, v => Model.OldInstallerDays = v); }
    public int UnusedProgramDays { get => Model.UnusedProgramDays; set => Set(value, v => Model.UnusedProgramDays = v); }
    public int ScanThreads { get => Model.ScanThreads; set => Set(value, v => Model.ScanThreads = v); }
    public string ExcludePathsText
    {
        get => string.Join(Environment.NewLine, Model.ExcludePaths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        set => Set(value, v => Model.ExcludePaths = string.Join(";", v.Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
    }
    public bool RememberRecentPaths { get => Model.RememberRecentPaths; set => Set(value, v => Model.RememberRecentPaths = v); }
    public int MaxRecentPaths { get => Model.MaxRecentPaths; set => Set(value, v => Model.MaxRecentPaths = v); }
    // look
    public int LevelContrast { get => Model.LevelContrast; set => Set(value, v => Model.LevelContrast = v); }
    public int BorderContrast { get => Model.BorderContrast; set => Set(value, v => Model.BorderContrast = v); }
    public int HilightHaloLevels { get => Model.HilightHaloLevels; set => Set(value, v => Model.HilightHaloLevels = v); }
    public bool MouseHover { get => Model.MouseHover; set => Set(value, v => Model.MouseHover = v); }
    public bool HilightShadow { get => Model.HilightShadow; set => Set(value, v => Model.HilightShadow = v); }
    public bool MouseTrail { get => Model.MouseTrail; set => Set(value, v => Model.MouseTrail = v); }
    public int BlinkDurationMs { get => Model.BlinkDurationMs; set => Set(value, v => Model.BlinkDurationMs = v); }
    public Color TagRedColor { get => ToWpf(Model.TagRedColor); set => Set(value, v => Model.TagRedColor = ToDrawing(v)); }
    public Color TagYellowColor { get => ToWpf(Model.TagYellowColor); set => Set(value, v => Model.TagYellowColor = ToDrawing(v)); }
    public Color TagGreenColor { get => ToWpf(Model.TagGreenColor); set => Set(value, v => Model.TagGreenColor = ToDrawing(v)); }
    public Color TagBlueColor { get => ToWpf(Model.TagBlueColor); set => Set(value, v => Model.TagBlueColor = ToDrawing(v)); }
    // styles
    public int InitialStyleIndex { get => Model.InitialStyle == ViewStyle.Flat ? 0 : 1; set => Set(value, v => Model.InitialStyle = v == 0 ? ViewStyle.Flat : ViewStyle.FileClasses); }
    public Color DriveColor { get => ToWpf(Model.DriveColor); set => Set(value, v => Model.DriveColor = ToDrawing(v)); }
    public Color FolderColor { get => ToWpf(Model.FolderColor); set => Set(value, v => Model.FolderColor = ToDrawing(v)); }
    public Color FileColor { get => ToWpf(Model.FileColor); set => Set(value, v => Model.FileColor = ToDrawing(v)); }
    public Color FreeColor { get => ToWpf(Model.FreeColor); set => Set(value, v => Model.FreeColor = ToDrawing(v)); }
    public Color UnknownColor { get => ToWpf(Model.UnknownColor); set => Set(value, v => Model.UnknownColor = ToDrawing(v)); }
    // advanced
    public bool DecimalUnits { get => Model.DecimalUnits; set => Set(value, v => Model.DecimalUnits = v); }
    public string Language { get => Model.Language; set => Set(value, v => Model.Language = v); }

    public void NotifyClasses() { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Model))); Changed?.Invoke(); }
}
