using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ByteScape.Core.Config;
using ByteScape.Wpf.Controls;

namespace ByteScape.Wpf;

/// <summary>
/// The first-start tour: a dozen steps over the real window, each outlining what it explains — the map, right click, the
/// details panel, the filter, and the tools hidden behind the toolbar's icons. Shown once after the first scan starts
/// (Settings keeps that it was seen); the … menu runs it again. Keys: → / Enter next, ← back, Esc ends.
/// </summary>
internal sealed class Tour
{
    internal sealed record Step(string Title, string Text, Func<IEnumerable<FrameworkElement?>> Targets, bool NeedsTarget = true);

    /// <summary>The tour on screen, if any.</summary>
    internal static Tour? Running { get; private set; }

    private readonly Window _window;
    private readonly List<Step> _steps;
    private readonly UIElement _root;
    private AdornerLayer? _layer;
    internal TourAdorner? Adorner { get; private set; }
    internal readonly TextBlock Counter, TitleText, BodyText;
    internal readonly Button BackBtn, NextBtn, SkipBtn;
    internal int Index { get; private set; }
    internal int Count => _steps.Count;
    internal Step Current => _steps[Index];
    internal IReadOnlyList<Step> Steps => _steps;

    public event Action? Finished;

    private Tour(Window window, IEnumerable<Step> steps)
    {
        _window = window;
        _root = (UIElement)window.Content;
        // steps about something that is not on screen (no view open yet) are left out
        _steps = steps.Where(s => !s.NeedsTarget || Visible(s).Count > 0).ToList();
        Counter = new TextBlock { FontSize = 11 };
        Counter.SetResourceReference(FrameworkElement.StyleProperty, "Secondary");
        TitleText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
        TitleText.SetResourceReference(FrameworkElement.StyleProperty, "Subtitle");
        BodyText = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        BackBtn = new Button { Content = T("Back"), MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        NextBtn = new Button { Content = T("Next"), MinWidth = 90 };
        NextBtn.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        SkipBtn = new Button { Content = T("Skip the tour"), HorizontalAlignment = HorizontalAlignment.Left };
        BackBtn.Click += (_, _) => Back();
        NextBtn.Click += (_, _) => Next();
        SkipBtn.Click += (_, _) => Close();
        var buttons = new WrapPanel { Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(BackBtn);
        buttons.Children.Add(NextBtn);
        var footer = new FooterPanel { Margin = new Thickness(0, 14, 0, 0), MinNoteWidth = 120 };
        footer.Children.Add(buttons);
        footer.Children.Add(SkipBtn);
        var stack = new StackPanel();
        stack.Children.Add(Counter);
        stack.Children.Add(TitleText);
        stack.Children.Add(BodyText);
        stack.Children.Add(footer);
        Card = new Border
        {
            Child = stack, Padding = new Thickness(18, 14, 18, 16), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), MaxWidth = 420,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.3 },
        };
        Card.SetResourceReference(Border.BackgroundProperty, "Layer");
        Card.SetResourceReference(Border.BorderBrushProperty, "Accent");
    }

    internal Border Card { get; }

    private static List<FrameworkElement> Visible(Step s) => s.Targets().OfType<FrameworkElement>().Where(t => t.IsVisible && t.ActualWidth > 0).ToList();

    /// <summary>The tour of the main window, its current view included.</summary>
    internal static Tour For(MainWindow w)
    {
        var v = w.Active;
        var steps = new List<Step>
        {
            new(F($"Welcome to {Branding.Name}"),
                T("Every rectangle on the map is a file or a folder, and its area is its size: the biggest blocks are what fills your disk. Here is, in a minute, what else it can do."),
                () => Array.Empty<FrameworkElement>(), NeedsTarget: false),
            new(T("The map"),
                T("Double click a folder to go in, Backspace to come back out; with the keyboard, the arrow keys move from block to block and Ctrl+Down goes in. The map fills in while the scan runs, and changes on the disk show up as they happen."),
                () => new FrameworkElement?[] { v?.Map }),
            new(T("Right click anything"),
                T("Open it in Explorer, give it a colour tag, or see whether it is safe to delete before it goes to the Recycle Bin. The Delete key does the same."),
                () => new FrameworkElement?[] { v?.Map }),
            new(T("What is this folder?"),
                T("The details panel tells known folders apart — caches, temporary files, parts of Windows — and says what deleting them would do."),
                () => new FrameworkElement?[] { w.BtnDetails }),
            new(T("Find what matters"),
                T("Type *.mp4, >1gb or <6months — or just say it: videos over 1 GB not opened for a year. The funnel lists ready-made filters."),
                () => new FrameworkElement?[] { v?.FilterBox, v?.FilterPresets }),
            new(T("Cleanup"),
                T("Large files, temporary files, old installers and programs you no longer use, each with a clear answer to “is it safe to delete?”."),
                () => new FrameworkElement?[] { w.BtnCleanup }),
            new(T("Who uses the space"),
                T("Programs with all their data and caches, games you no longer play, and the folders your development tools rebuild (node_modules, bin, .venv) in old projects."),
                () => new FrameworkElement?[] { w.BtnApps, w.BtnGames, w.BtnDev }),
            new(T("Copies and comparisons"),
                T("Find duplicate files, compare a folder with its backup, or compare the drive with a snapshot you saved last month."),
                () => new FrameworkElement?[] { w.BtnDuplicates, w.BtnCompare }),
            new(T("Over time"),
                T("See how the drive and its folders grew, and when it will be full at the current pace."),
                () => new FrameworkElement?[] { w.BtnHistory, w.BtnForecast }),
            new(T("Everything else"),
                T("The … menu lists every tool with its keyboard shortcut. The gear opens Settings: the language, Explorer's right-click menu, how deleting works."),
                () => new FrameworkElement?[] { w.BtnMenu, w.BtnConfig }),
            new(T("You are ready"),
                F($"Nothing leaves your computer, and nothing is deleted without asking you first. You can take this tour again from the … menu. Enjoy {Branding.Name}!"),
                () => Array.Empty<FrameworkElement>(), NeedsTarget: false),
        };
        return new Tour(w, steps);
    }

    public void Start()
    {
        if (Running != null || _steps.Count == 0) return;
        _layer = AdornerLayer.GetAdornerLayer(_root);
        if (_layer == null) return;
        Adorner = new TourAdorner(_root, Card);
        _layer.Add(Adorner);
        Running = this;
        _window.SizeChanged += Relayout;
        Show(0);
    }

    private void Relayout(object sender, SizeChangedEventArgs e) => Adorner?.Show(Visible(Current));

    private void Show(int i)
    {
        Index = i;
        var s = _steps[i];
        Counter.Text = F($"{i + 1} of {_steps.Count}");
        TitleText.Text = s.Title;
        BodyText.Text = s.Text;
        BackBtn.Visibility = i > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextBtn.Content = T(i == _steps.Count - 1 ? "Done" : "Next");
        SkipBtn.Visibility = i == _steps.Count - 1 ? Visibility.Collapsed : Visibility.Visible;
        Adorner!.Show(Visible(s));
        NextBtn.Focus();
    }

    public void Next() { if (Index < _steps.Count - 1) Show(Index + 1); else Close(); }
    public void Back() { if (Index > 0) Show(Index - 1); }

    /// <summary>Ends the tour (finished or skipped): it is not shown by itself again.</summary>
    public void Close()
    {
        if (Running != this) return;
        _window.SizeChanged -= Relayout;
        if (Adorner != null) _layer?.Remove(Adorner);
        Adorner = null;
        Running = null;
        if (!AppConfig.Current.TourShown)
        {
            AppConfig.Current.TourShown = true;
            AppConfig.Save();
        }
        Finished?.Invoke();
    }

    /// <summary>The tour's keys; every other key waits until the tour ends.</summary>
    internal bool HandleKey(System.Windows.Input.Key key)
    {
        switch (key)
        {
            case System.Windows.Input.Key.Right:
            case System.Windows.Input.Key.Enter:
            case System.Windows.Input.Key.Space: Next(); return true;
            case System.Windows.Input.Key.Left: Back(); return true;
            case System.Windows.Input.Key.Escape: Close(); return true;
            case System.Windows.Input.Key.Tab: return false;
            default: return true;
        }
    }
}
