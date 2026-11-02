using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using ByteScape.Core.Localization;

namespace ByteScape.Wpf;

/// <summary>
/// Translates the fixed texts a XAML file gives a window (titles, labels, buttons, menus, check boxes, tool tips, list
/// columns), right after <c>InitializeComponent</c> — before the code behind fills in data, so file and folder names are
/// never taken for texts to translate. Bound values are left alone (their view models translate); so are strings
/// without a letter (icon glyphs).
/// </summary>
public static class Localizer
{
    public static void Apply(DependencyObject root)
    {
        Controls.A11y.LinkLabels(root);   // screen reader names for inputs (any language: the labels are linked, not copied)
        if (Loc.Language == Loc.English) return;
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        Walk(root, seen);
    }

    private static void Walk(DependencyObject d, HashSet<DependencyObject> seen)
    {
        if (!seen.Add(d)) return;
        Translate(d);
        // a TextBlock given its text in one piece: its (implicit) run needs nothing more
        if (d is TextBlock tb && tb.ReadLocalValue(TextBlock.TextProperty) is string) { WalkExtras(tb, seen); return; }
        WalkExtras(d, seen);
        foreach (var child in LogicalTreeHelper.GetChildren(d))
            if (child is DependencyObject c) Walk(c, seen);
    }

    private static void WalkExtras(DependencyObject d, HashSet<DependencyObject> seen)
    {
        if (d is FrameworkElement fe)
        {
            if (fe.ContextMenu != null) Walk(fe.ContextMenu, seen);
            if (fe.ToolTip is DependencyObject tip) Walk(tip, seen);
        }
        if (d is ListView { View: GridView gv } lv)
            foreach (var col in gv.Columns)
                if (col.Header is string h && !Bound(col, GridViewColumn.HeaderProperty))
                {
                    col.Header = Loc.T(h);
                    // a translated header longer than the column's fixed width: the column grows to show it whole
                    if (!double.IsNaN(col.Width)) col.Width = Math.Max(col.Width, HeaderWidth(lv, (string)col.Header));
                }
        if (d is DataGrid dg)
            foreach (var col in dg.Columns)
                if (col.Header is string h) col.Header = Loc.T(h);
    }

    private static double HeaderWidth(Control c, string text) => TextWidth(c, text) + 24;   // the header's padding and the sort arrow

    /// <summary>
    /// A list column whose cells hold one of a few fixed words ("Desktop", "added"...): wide enough for the longest of
    /// them in the interface language, as the column's header is.
    /// </summary>
    public static void FitColumn(ListView lv, int column, params string[] english)
    {
        if (lv.View is not GridView gv || column >= gv.Columns.Count || double.IsNaN(gv.Columns[column].Width)) return;
        var col = gv.Columns[column];
        foreach (var e in english) col.Width = Math.Max(col.Width, TextWidth(lv, Loc.T(e)) + 16);   // the cell's padding
    }

    private static double TextWidth(Control c, string text)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                   new Typeface(c.FontFamily, c.FontStyle, FontWeights.Normal, c.FontStretch), c.FontSize, Brushes.Black, 1.0);
        return Math.Ceiling(ft.WidthIncludingTrailingWhitespace);
    }

    private static bool Bound(DependencyObject d, DependencyProperty p) => BindingOperations.GetBindingExpressionBase(d, p) != null;

    private static void Set(DependencyObject d, DependencyProperty p)
    {
        if (Bound(d, p)) return;
        if (d.ReadLocalValue(p) is string s && s.Length > 0)
        {
            var t = Loc.T(s);
            if (!ReferenceEquals(t, s)) d.SetValue(p, t);
        }
    }

    private static void Translate(DependencyObject d)
    {
        switch (d)
        {
            case Window: Set(d, Window.TitleProperty); break;
            case TextBlock: Set(d, TextBlock.TextProperty); break;   // one made of runs is translated run by run
            case Run: Set(d, Run.TextProperty); break;
            case HeaderedContentControl: Set(d, HeaderedContentControl.HeaderProperty); Set(d, ContentControl.ContentProperty); break;
            case HeaderedItemsControl: Set(d, HeaderedItemsControl.HeaderProperty); break;   // menu items
            case TextBox:
            case PasswordBox: break;                                                           // what the user typed
            case ContentControl: Set(d, ContentControl.ContentProperty); break;                // buttons, check boxes, combo box items
        }
        if (d is FrameworkElement) Set(d, FrameworkElement.ToolTipProperty);
        // what a screen reader says for an element whose name is not its text
        Set(d, System.Windows.Automation.AutomationProperties.NameProperty);
        Set(d, System.Windows.Automation.AutomationProperties.HelpTextProperty);
    }
}

/// <summary>For texts inside templates, which <see cref="Localizer"/> does not reach: <c>Text="{l:T 'Nothing to show'}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string text) => Text = text;
    [ConstructorArgument("text")] public string Text { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
