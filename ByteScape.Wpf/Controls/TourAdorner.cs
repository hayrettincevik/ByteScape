using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ByteScape.Wpf.Controls;

/// <summary>
/// One step of the tour drawn over the window: everything dimmed except the elements being explained (outlined), and a card
/// next to them with the explanation and the buttons. Clicks outside the card are held back while the tour runs.
/// </summary>
internal sealed class TourAdorner : Adorner
{
    private readonly VisualCollection _children;
    private readonly FrameworkElement _card;
    private IReadOnlyList<FrameworkElement> _targets = Array.Empty<FrameworkElement>();
    private readonly Brush _dim = new SolidColorBrush(Color.FromArgb(0x8C, 0x0E, 0x1A, 0x2E));

    /// <summary>The outlined area (in the window's content coordinates); null for a step about nothing in particular.</summary>
    internal Rect? Hole { get; private set; }
    /// <summary>Where the card is (in the window's content coordinates).</summary>
    internal Rect CardBounds { get; private set; }

    public TourAdorner(UIElement adorned, FrameworkElement card) : base(adorned)
    {
        _card = card;
        _children = new VisualCollection(this) { card };
        IsHitTestVisible = true;
        Focusable = false;
    }

    public void Show(IReadOnlyList<FrameworkElement> targets)
    {
        _targets = targets;
        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
    }

    protected override int VisualChildrenCount => _children.Count;
    protected override Visual GetVisualChild(int index) => _children[index];

    private Rect? Measured()
    {
        Rect? r = null;
        foreach (var t in _targets)
        {
            if (!t.IsVisible || t.ActualWidth <= 0 || t.ActualHeight <= 0 || !t.IsDescendantOf(AdornedElement)) continue;
            var b = t.TransformToAncestor(AdornedElement).TransformBounds(new Rect(t.RenderSize));
            r = r == null ? b : Rect.Union(r.Value, b);
        }
        if (r is not { } h) return null;
        h.Inflate(6, 6);
        h.Intersect(new Rect(AdornedElement.RenderSize));
        return h.IsEmpty ? null : h;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var size = AdornedElement.RenderSize;
        _card.Measure(new Size(Math.Max(200, Math.Min(420, size.Width - 32)), double.PositiveInfinity));
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = AdornedElement.RenderSize;
        Hole = Measured();
        var c = _card.DesiredSize;
        const double gap = 14, edge = 12;
        double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(v, Math.Max(min, max)));
        Point p;
        if (Hole is not { } h) p = new((size.Width - c.Width) / 2, (size.Height - c.Height) / 2);
        else
        {
            double x = Clamp(h.Left + h.Width / 2 - c.Width / 2, edge, size.Width - c.Width - edge);
            double y = Clamp(h.Top, edge, size.Height - c.Height - edge);
            if (h.Bottom + gap + c.Height <= size.Height - edge) p = new(x, h.Bottom + gap);                         // below
            else if (h.Top - gap - c.Height >= edge) p = new(x, h.Top - gap - c.Height);                              // above
            else if (h.Right + gap + c.Width <= size.Width - edge) p = new(h.Right + gap, y);                         // right
            else if (h.Left - gap - c.Width >= edge) p = new(h.Left - gap - c.Width, y);                              // left
            else p = new(x, size.Height - c.Height - 3 * edge);                                                       // over a large area: near its bottom
        }
        CardBounds = new Rect(p, c);
        _card.Arrange(CardBounds);
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var full = new Rect(AdornedElement.RenderSize);
        if (Hole is { } h)
        {
            dc.DrawGeometry(_dim, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(h, 6, 6)));
            dc.DrawRoundedRectangle(Brushes.Transparent, null, h, 6, 6);           // the outlined element is not clickable meanwhile either
            var accent = TryFindResource("Accent") as Brush ?? Brushes.DodgerBlue;
            dc.DrawRoundedRectangle(null, new Pen(accent, 2.5), h, 6, 6);
        }
        else dc.DrawRectangle(_dim, null, full);
    }
}
