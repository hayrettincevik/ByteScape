using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ByteScape.Core.Cleanup;

namespace ByteScape.Wpf.Windows;

/// <summary>How the "is it safe to delete?" verdicts look everywhere: a coloured pill, the headline, why, the warnings.</summary>
internal static class SafetyUi
{
    /// <summary>
    /// The colour of a safety level, as text on white, on the light panels and on a selected row, and behind white pill
    /// text: all at least 4.5:1 (WCAG AA). In high contrast the texts and pills use the theme's colours instead.
    /// </summary>
    public static Color LevelColor(SafetyLevel l) => l switch
    {
        SafetyLevel.Safe => Color.FromRgb(0x1A, 0x70, 0x35),
        SafetyLevel.SafeWhenClosed => Color.FromRgb(0x22, 0x66, 0xA8),
        SafetyLevel.Unknown => Color.FromRgb(0x56, 0x62, 0x7A),
        SafetyLevel.Caution => Color.FromRgb(0x94, 0x55, 0x00),
        SafetyLevel.Personal => Color.FromRgb(0x7A, 0x3F, 0x96),
        _ => Color.FromRgb(0xB0, 0x2A, 0x2A),
    };

    public static Brush LevelBrush(SafetyLevel l) => new SolidColorBrush(LevelColor(l));

    public static Brush WarningBrush(WarningLevel l) => Res(l switch
    {
        WarningLevel.Serious => "Error",
        WarningLevel.Caution => "Warning",
        _ => "TextSecondary",
    });

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    public static Border Pill(SafetyLevel l, string? text = null) => Pill(LevelBrush(l), text ?? DeleteSafety.LevelText(l));

    /// <summary>A small label: white text on a colour, or in high contrast the theme's text in a frame of the same colour.</summary>
    public static Border Pill(Brush back, string text)
    {
        bool hc = ThemeManager.HighContrast;
        return new Border
        {
            Background = hc ? SystemColors.WindowBrush : back, BorderBrush = SystemColors.WindowTextBrush, BorderThickness = new Thickness(hc ? 1 : 0),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, Foreground = hc ? SystemColors.WindowTextBrush : Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold },
        };
    }

    /// <summary>The verdict as a block: pill + headline, why, the proper way, the warnings.</summary>
    public static StackPanel Verdict(Assessment a, bool why = true, bool cleanup = true)
    {
        var sp = new StackPanel();
        var head = new DockPanel();
        var pill = Pill(a.Level);
        DockPanel.SetDock(pill, Dock.Left);
        head.Children.Add(pill);
        head.Children.Add(new TextBlock { Text = a.Headline, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(head);
        if (why && a.Why.Length > 0) sp.Children.Add(new TextBlock { Text = a.Why, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        if (cleanup && a.Cleanup.Length > 0) sp.Children.Add(new TextBlock { Text = T("The proper way:") + " " + a.Cleanup, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Foreground = (Brush)Application.Current.FindResource("TextSecondary") });
        foreach (var w in a.Warnings)
            sp.Children.Add(new TextBlock { Text = (w.Level == WarningLevel.Info ? "•  " : "⚠  ") + w.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), Foreground = WarningBrush(w.Level) });
        return sp;
    }

    /// <summary>One line for what trying the rights found (null: nothing to say yet).</summary>
    public static (string Text, Brush Brush) ProbeLine(ProbeSummary? p, bool elevated)
    {
        if (p == null) return (T("Checking whether it can be deleted…"), (Brush)Application.Current.FindResource("TextSecondary"));
        var parts = new List<string>();
        if (p.NeedsAdminCount > 0) parts.Add("🛡 " + P(p.NeedsAdminCount, "{0:N0} element needs administrator rights (e.g. {1})", "{0:N0} elements need administrator rights (e.g. {1})", Short(p.NeedsAdmin[0])));
        if (p.ProtectedCount > 0) parts.Add("🔒 " + P(p.ProtectedCount, "{0:N0} element protected by Windows, even from administrators (e.g. {1})", "{0:N0} elements protected by Windows, even from administrators (e.g. {1})", Short(p.ProtectedByWindows[0])));
        if (p.InUseCount > 0) parts.Add("⏳ " + P(p.InUseCount, "{0:N0} element open in a program (e.g. {1})", "{0:N0} elements open in a program (e.g. {1})", Short(p.InUse[0])));
        if (parts.Count == 0)
            return ("✓ " + T("Can be deleted") + (elevated ? " " + F($"({Branding.Name} runs as administrator)") : "") + (p.Complete ? "" : " — " + F($"checked the first {p.Checked:N0} elements")),
                    Res("Success"));
        return (string.Join("\n", parts) + (p.Complete ? "" : "\n(" + F($"checked the first {p.Checked:N0} elements") + ")"), Res("Warning"));
    }

    private static string Short(string path) => path.Length > 70 ? "…" + path[^68..] : path;
}
