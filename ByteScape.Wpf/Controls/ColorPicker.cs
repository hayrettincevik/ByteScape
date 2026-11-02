using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ByteScape.Wpf.Controls;

/// <summary>Compact colour picker: a swatch button opening a palette popup with a hex entry.</summary>
public sealed class ColorPicker : Control
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(nameof(Color), typeof(Color), typeof(ColorPicker),
        new FrameworkPropertyMetadata(Colors.Gray, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ColorPicker)d).Refresh()));

    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }

    private readonly Border _swatch;
    private readonly TextBlock _label;
    private readonly Button _button;
    private string _caption = "";   // the label beside the picker ("Folder"), for the screen reader name
    private readonly Popup _popup;
    private readonly TextBox _hex;

    private static readonly string[] Palette =
    {
        "#FFCC99", "#F0C8A0", "#E0B080", "#C8A078", "#99CCFF", "#80B0E0", "#6AA0D8", "#4C8BD0",
        "#99FF99", "#80E080", "#60C060", "#FF9999", "#E08080", "#C06060", "#C080E0", "#A060C0",
        "#E0C060", "#FFD700", "#FF8C00", "#FF4500", "#FF0000", "#32CD32", "#1E90FF", "#8A2BE2",
        "#FFFFFF", "#DDDDDD", "#AAAAAA", "#777777", "#444444", "#222222", "#000000", "#00CED1",
    };

    public ColorPicker()
    {
        _label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 12 };
        _swatch = new Border { Width = 26, Height = 18, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(_swatch);
        panel.Children.Add(_label);
        var button = _button = new Button { Content = panel, Padding = new Thickness(6, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 28 };
        button.Click += (_, _) => _popup.IsOpen = !_popup.IsOpen;

        // the palette: buttons, so the arrow keys, Tab and Enter pick a colour as well as the mouse
        var grid = new UniformGrid { Columns = 8, Margin = new Thickness(0, 0, 0, 6) };
        KeyboardNavigation.SetDirectionalNavigation(grid, KeyboardNavigationMode.Cycle);
        foreach (var hex in Palette)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var b = new Button { Width = 22, Height = 22, Margin = new Thickness(2), Background = new SolidColorBrush(c), BorderBrush = Brushes.Gray, Cursor = Cursors.Hand, ToolTip = hex, Template = SwatchTemplate, Tag = c };
            AutomationProperties.SetName(b, hex);
            b.Click += (_, _) => { Color = c; _popup.IsOpen = false; _button.Focus(); };
            grid.Children.Add(b);
        }
        _hex = new TextBox { Width = 90 };
        _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyHex(); };
        _hex.LostFocus += (_, _) => ApplyHex();
        var hexRow = new StackPanel { Orientation = Orientation.Horizontal };
        hexRow.Children.Add(new TextBlock { Text = T("Hex"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) });
        hexRow.Children.Add(_hex);
        var content = new StackPanel { Margin = new Thickness(8) };
        content.Children.Add(grid);
        content.Children.Add(hexRow);
        _popup = new Popup { PlacementTarget = button, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade };
        _popup.Child = new Border { Child = content, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Margin = new Thickness(0, 2, 0, 0) };
        _popup.Opened += (_, _) =>
        {
            _hex.Text = Hex(Color);
            // keyboard: start on the current colour (or the hex box), Escape goes back to the button
            var current = grid.Children.OfType<Button>().FirstOrDefault(s => s.Tag is Color sc && sc == Color);
            Dispatcher.BeginInvoke(() => { if (current != null) current.Focus(); else _hex.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);
        };
        content.KeyDown += (_, e) => { if (e.Key == Key.Escape) { _popup.IsOpen = false; _button.Focus(); e.Handled = true; } };
        var root = new Grid();
        root.Children.Add(button);
        root.Children.Add(_popup);
        Loaded += (_, _) =>
        {
            if (A11y.LabelFor(this) is TextBlock caption) _caption = caption.Text;
            if (_popup.Child is Border pb) { pb.Background = TryFindResource("Layer") as Brush; pb.BorderBrush = TryFindResource("BorderStrong") as Brush; }
            Refresh();
        };
        AddVisualChild(root);
        AddLogicalChild(root);
        _root = root;
    }

    private readonly Grid _root;
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _root;
    protected override Size MeasureOverride(Size c) { _root.Measure(c); return _root.DesiredSize; }
    protected override Size ArrangeOverride(Size s) { _root.Arrange(new Rect(s)); return s; }

    private void ApplyHex()
    {
        try
        {
            var t = _hex.Text.Trim();
            if (!t.StartsWith('#')) t = "#" + t;
            if (t.Length == 7) Color = (Color)ColorConverter.ConvertFromString(t);
        }
        catch { }
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>A palette swatch: the colour in a rounded frame, a thicker frame while it has the keyboard focus.</summary>
    private static readonly ControlTemplate SwatchTemplate = MakeSwatchTemplate();

    private static ControlTemplate MakeSwatchTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "B");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        var t = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(3), "B"));
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, SystemColors.HighlightBrush, "B"));
        t.Triggers.Add(focus);
        return t;
    }

    private void Refresh()
    {
        _swatch.Background = new SolidColorBrush(Color);
        _label.Text = Hex(Color);
        AutomationProperties.SetName(_button, _caption.Length > 0 ? _caption + ", " + Hex(Color) : F($"Colour {Hex(Color)}"));
    }
}
