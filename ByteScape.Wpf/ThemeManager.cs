using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace ByteScape.Wpf;

/// <summary>
/// Applies the (single) ByteScape look to every window: background, text colour, font and
/// text rendering. Implicit Window styles do not reach derived windows, so this is done per
/// window when it loads. With a Windows contrast theme on (high contrast), the palette is the
/// theme's own system colours instead, and follows a change of theme while the program runs.
/// </summary>
public static class ThemeManager
{
    public static void Initialize()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => { if (s is Window w) ApplyToWindow(w); }));
        ApplyPalette();
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast)) Application.Current?.Dispatcher.BeginInvoke(ApplyPalette);
        };
        // switching from one contrast theme to another changes the colours, not the high contrast flag
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility) Application.Current?.Dispatcher.BeginInvoke(ApplyPalette);
        };
    }

    /// <summary>A Windows contrast theme is on (or a test forces the high contrast palette).</summary>
    public static bool HighContrast => _forced ?? SystemParameters.HighContrast;

    private static bool? _forced;

    /// <summary>Tests: the high contrast palette on or off whatever Windows uses (null = follow Windows again).</summary>
    internal static void ForceHighContrast(bool? on) { _forced = on; ApplyPalette(); }

    /// <summary>The palette changed (high contrast turned on or off, or another contrast theme): the map repaints.</summary>
    public static event Action? PaletteChanged;

    private static ResourceDictionary? _contrast;

    /// <summary>Puts the high contrast palette over the ByteScape one, or takes it away.</summary>
    public static void ApplyPalette()
    {
        if (Application.Current is not { } app) return;
        var dicts = app.Resources.MergedDictionaries;
        if (_contrast != null) { dicts.Remove(_contrast); _contrast = null; }
        if (HighContrast) dicts.Add(_contrast = ContrastPalette());
        PaletteChanged?.Invoke();
    }

    /// <summary>
    /// The system colours of the contrast theme under the palette's names: surfaces are Window, text and borders
    /// WindowText, the accent and every selection Highlight with HighlightText on it, and the colours that only carry a
    /// tone (success, warning, error) plain WindowText, as contrast themes ask.
    /// </summary>
    private static ResourceDictionary ContrastPalette()
    {
        static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        var window = B(SystemColors.WindowColor);
        var text = B(SystemColors.WindowTextColor);
        var highlight = B(SystemColors.HighlightColor);
        var highlightText = B(SystemColors.HighlightTextColor);
        var d = new ResourceDictionary();
        foreach (var k in new[] { "WindowBackground", "Layer", "LayerAlt", "ToolbarBackground", "MapBackground" }) d[k] = window;
        foreach (var k in new[] { "Border", "BorderStrong", "Text", "TextSecondary", "Success", "Warning", "Error" }) d[k] = text;
        d["TextDisabled"] = B(SystemColors.GrayTextColor);
        foreach (var k in new[] { "Accent", "AccentHover", "Hover", "Pressed", "Selected" }) d[k] = highlight;
        foreach (var k in new[] { "AccentText", "HoverText", "SelectedText" }) d[k] = highlightText;
        return d;
    }

    /// <summary>A colour that only carries a tone (a safety level, a trend): in high contrast the theme's text colour instead.</summary>
    public static Brush Tone(Brush brush) => HighContrast ? SystemColors.WindowTextBrush : brush;

    public static void ApplyToWindow(Window w)
    {
        w.SetResourceReference(Control.BackgroundProperty, "WindowBackground");
        w.SetResourceReference(Control.ForegroundProperty, "Text");
        w.FontFamily = new FontFamily(Loc.Language == "zh-Hans" ? "Segoe UI, Microsoft YaHei UI, Microsoft YaHei" : "Segoe UI"); // Chinese glyphs from the Chinese UI font
        w.FontSize = 13;
        w.UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
    }
}
