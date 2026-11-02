using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace ByteScape.Wpf.Controls;

/// <summary>
/// An icon from the icon font. It is drawn but never read: a screen reader would otherwise say the private-use character
/// ("E721") or nothing sensible. What the icon means belongs in the name of the control that carries it.
/// </summary>
public sealed class Glyph : TextBlock
{
    public Glyph() => SetResourceReference(FontFamilyProperty, "IconFont");

    protected override AutomationPeer OnCreateAutomationPeer() => new DecorativePeer(this);
}

/// <summary>A peer that keeps a purely visual element out of the control and content views of UI Automation.</summary>
internal sealed class DecorativePeer : FrameworkElementAutomationPeer
{
    public DecorativePeer(FrameworkElement owner) : base(owner) { }
    protected override bool IsControlElementCore() => false;
    protected override bool IsContentElementCore() => false;
    protected override string GetNameCore() => "";
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
    protected override List<AutomationPeer>? GetChildrenCore() => null;
}

/// <summary>Names for screen readers where WPF finds none on its own.</summary>
public static class A11y
{
    /// <summary>
    /// For an icon-only button: the name is its tool tip up to the first colon, parenthesis or line break ("Go back
    /// (Backspace)" → "Go back", "Cleanup: large files, …" → "Cleanup"); the whole tool tip stays the help text.
    /// </summary>
    public static readonly IValueConverter TipName = new TipNameConverter();

    public static string ShortName(string tip)
    {
        int cut = tip.Length;
        foreach (var mark in new[] { ": ", " (", "\n", "：", "（", " — " })
        {
            int i = tip.IndexOf(mark, StringComparison.Ordinal);
            if (i >= 3 && i < cut) cut = i;
        }
        return tip[..cut].Trim();
    }

    /// <summary>
    /// (tone colour, the row's text colour) → the tone (a safety level on a list row), or in high contrast the row's text
    /// colour (window text, or highlight text while the row is selected).
    /// </summary>
    public static readonly IMultiValueConverter Tone = new ToneConverter();

    private sealed class ToneConverter : IMultiValueConverter
    {
        public object? Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
            !ThemeManager.HighContrast && values.Length > 0 && values[0] is Brush tone ? tone
            : values.Length > 1 && values[1] is Brush row ? row : DependencyProperty.UnsetValue;
        public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    private sealed class TipNameConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is string s && s.Length > 0 ? ShortName(s) : DependencyProperty.UnsetValue;
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>
    /// The name of a list row: the texts of its columns, in the order shown ("Chrome, 1.2 GB, Google LLC"). Without it a
    /// screen reader says the row object's type name. Bound as (row, the list's GridView) by the ListViewItem style.
    /// </summary>
    public static readonly IMultiValueConverter RowName = new RowNameConverter();

    private sealed class RowNameConverter : IMultiValueConverter
    {
        public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            var item = values.Length > 0 ? values[0] : null;
            if (item is string s) return s;
            if (item == null || item == DependencyProperty.UnsetValue) return DependencyProperty.UnsetValue;
            return values.Length > 1 && values[1] is GridView gv && RowText(item, gv) is { Length: > 0 } text ? text : DependencyProperty.UnsetValue;
        }
        public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>The texts a row shows in its columns (plain bindings and the first bound text of a column template).</summary>
    public static string RowText(object item, GridView view)
    {
        var parts = new List<string>();
        foreach (var col in view.Columns)
        {
            var binding = col.DisplayMemberBinding as Binding;
            string? path = binding?.Path?.Path;
            string? format = binding?.StringFormat;
            if (binding == null && col.CellTemplate != null) (path, format) = TemplateText(col.CellTemplate);
            if (string.IsNullOrEmpty(path)) continue;
            object? v = Eval(item, path);
            string text = v switch
            {
                null => "",
                _ when format != null => string.Format(CultureInfo.CurrentCulture, format.Contains('{') ? format : "{0:" + format + "}", v),
                IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
                _ => v.ToString() ?? "",
            };
            if (!string.IsNullOrWhiteSpace(text) && !text.All(c => c >= '\uE000' && c <= '\uF8FF' || char.IsWhiteSpace(c))) parts.Add(text.Trim());
        }
        return string.Join(", ", parts);
    }

    private static readonly Dictionary<DataTemplate, (string?, string?)> TemplateTexts = new();

    /// <summary>The binding of the first text in a cell template (e.g. the safety verdict next to its coloured dot).</summary>
    private static (string? Path, string? Format) TemplateText(DataTemplate t)
    {
        if (TemplateTexts.TryGetValue(t, out var known)) return known;
        (string?, string?) found = (null, null);
        try
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(t.LoadContent());
            while (stack.Count > 0 && found.Item1 == null)
            {
                var d = stack.Pop();
                if (d is TextBlock tb && BindingOperations.GetBinding(tb, TextBlock.TextProperty) is { Path.Path.Length: > 0 } b) found = (b.Path.Path, b.StringFormat);
                foreach (var c in LogicalTreeHelper.GetChildren(d)) if (c is DependencyObject dc) stack.Push(dc);
            }
        }
        catch { }
        return TemplateTexts[t] = found;
    }

    /// <summary>A property path like "Entry.Name" read by reflection (public properties only).</summary>
    private static object? Eval(object item, string path)
    {
        object? cur = item;
        foreach (var part in path.Split('.'))
        {
            if (cur == null) return null;
            var p = cur.GetType().GetProperty(part, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (p == null || p.GetIndexParameters().Length > 0) return null;
            cur = p.GetValue(cur);
        }
        return cur;
    }

    /// <summary>
    /// Gives the inputs of a XAML tree that have no name of their own the label next to them, the way a sighted user
    /// pairs them: the text before the box in its panel ("API key" before the password box), the text in the same grid row
    /// or just above it, or the heading inside a radio button's content. The label is linked, not copied, so a translated
    /// or changed label is read as it is shown.
    /// </summary>
    public static void LinkLabels(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is FrameworkElement fe && !HasName(fe))
            {
                FrameworkElement? label = fe switch
                {
                    TextBoxBase or PasswordBox or ComboBox or Slider or ListBox => LabelFor(fe),
                    ButtonBase { Content: DependencyObject content } => FirstText(content),
                    _ => null,
                };
                if (label != null) Link(fe, label);
            }
            foreach (var child in LogicalTreeHelper.GetChildren(d))
                if (child is DependencyObject c) stack.Push(c);
        }
    }

    /// <summary>
    /// The label becomes the element's "labeled by" and its name. WPF's peers of text boxes, sliders and buttons do not
    /// take their name from the label on their own, so the name is bound to the label's text (it follows a translation).
    /// </summary>
    public static void Link(FrameworkElement element, FrameworkElement label)
    {
        AutomationProperties.SetLabeledBy(element, label);
        var path = label is Label ? ContentControl.ContentProperty : TextBlock.TextProperty;
        BindingOperations.SetBinding(element, AutomationProperties.NameProperty, new Binding { Path = new PropertyPath(path), Source = label, Mode = BindingMode.OneWay });
    }

    private static bool HasName(FrameworkElement fe) =>
        AutomationProperties.GetLabeledBy(fe) != null || !string.IsNullOrEmpty(AutomationProperties.GetName(fe))
        || BindingOperations.GetBindingExpressionBase(fe, AutomationProperties.NameProperty) != null;

    /// <summary>A short text with letters; a long one is a hint under a heading, not the label.</summary>
    private static bool IsLabel(object o) => o is TextBlock tb and not Glyph && IsShortText(tb.Text) || o is Label { Content: string s } && IsShortText(s);

    private static bool IsShortText(string s) => s.Length <= 80 && s.Any(char.IsLetter);

    private static bool IsInput(object o) => o is TextBoxBase or PasswordBox or ComboBox or Slider or ListBox or ButtonBase;

    /// <summary>The label of an input: looked for beside it, then beside the panel or border that holds it (three levels at most).</summary>
    public static FrameworkElement? LabelFor(FrameworkElement input)
    {
        FrameworkElement e = input;
        for (int level = 0; level < 3; level++)
        {
            var parent = LogicalTreeHelper.GetParent(e) as FrameworkElement;
            if (parent is Grid g)
            {
                int row = Grid.GetRow(e), col = Grid.GetColumn(e);
                // the text to the left in the same row, else the text right above
                var left = g.Children.OfType<FrameworkElement>().Where(c => IsLabel(c) && Grid.GetRow(c) == row && Grid.GetColumn(c) < col).OrderByDescending(Grid.GetColumn).FirstOrDefault();
                if (left != null) return left;
                var above = g.Children.OfType<FrameworkElement>().Where(c => IsLabel(c) && Grid.GetColumn(c) == col && Grid.GetRow(c) == row - 1).FirstOrDefault();
                if (above != null) return above;
            }
            else if (parent is Panel p)
            {
                int i = p.Children.IndexOf(e);
                for (int k = i - 1; k >= 0; k--)
                {
                    var before = p.Children[k];
                    if (IsLabel(before)) return (FrameworkElement)before;
                    if (IsInput(before)) break;   // a label before it belongs to that input; look beside the panel instead
                }
            }
            if (parent is not (Panel or Decorator or ScrollViewer)) return null;
            e = parent;
        }
        return null;
    }

    /// <summary>The first text with letters inside a control's content (a radio button's heading), depth first.</summary>
    private static FrameworkElement? FirstText(DependencyObject content)
    {
        if (IsLabel(content)) return (FrameworkElement)content;
        foreach (var child in LogicalTreeHelper.GetChildren(content))
            if (child is DependencyObject c && FirstText(c) is { } t) return t;
        return null;
    }
}
