using System.Windows;
using System.Windows.Controls;

namespace ByteScape.Wpf.Controls;

/// <summary>
/// The bottom row of a window: the buttons (the first child, a <see cref="WrapPanel"/>) on the right and a note (the second
/// child) beside them. When the buttons leave the note less than <see cref="MinNoteWidth"/> (a text; a note of another kind,
/// e.g. a button, needs its own width), the note goes above them — long translations, a narrow window — and buttons wider
/// than the window go on to a second line: nothing is cut off.
/// </summary>
public sealed class FooterPanel : Panel
{
    public double MinNoteWidth { get; set; } = 240;
    public double Gap { get; set; } = 6;

    private bool _stacked;

    private UIElement? Buttons => InternalChildren.Count > 0 ? InternalChildren[0] : null;
    private UIElement? Note => InternalChildren.Count > 1 ? InternalChildren[1] : null;
    private bool HasNote => Note is { Visibility: not Visibility.Collapsed } n && (n is not TextBlock t || t.Text.Length > 0);

    protected override Size MeasureOverride(Size available)
    {
        if (Buttons is not { } buttons) return new Size();
        buttons.Measure(new Size(double.PositiveInfinity, available.Height));
        var line = buttons.DesiredSize;
        double needs = MinNoteWidth;
        if (HasNote && Note is not TextBlock)
        {
            Note!.Measure(new Size(double.PositiveInfinity, available.Height));
            needs = Note.DesiredSize.Width;
        }
        _stacked = line.Width > available.Width || (HasNote && available.Width - line.Width < needs);
        if (!_stacked)
        {
            Note?.Measure(new Size(Math.Max(0, available.Width - line.Width), available.Height));
            var note = Note?.DesiredSize ?? new Size();
            return new Size(double.IsInfinity(available.Width) ? line.Width + note.Width : available.Width, Math.Max(line.Height, note.Height));
        }
        Note?.Measure(new Size(available.Width, double.PositiveInfinity));
        buttons.Measure(new Size(available.Width, double.PositiveInfinity));   // a WrapPanel wraps
        return new Size(available.Width, NoteHeight() + buttons.DesiredSize.Height);
    }

    private double NoteHeight() => HasNote ? Note!.DesiredSize.Height + Gap : 0;

    protected override Size ArrangeOverride(Size final)
    {
        if (Buttons is not { } buttons) return final;
        double bw = Math.Min(buttons.DesiredSize.Width, final.Width);
        if (!_stacked)
        {
            buttons.Arrange(new Rect(final.Width - bw, 0, bw, final.Height));
            Note?.Arrange(new Rect(0, 0, Math.Max(0, final.Width - bw), final.Height));
            return final;
        }
        double nh = NoteHeight();
        Note?.Arrange(new Rect(0, 0, final.Width, Math.Max(0, nh - Gap)));
        buttons.Arrange(new Rect(final.Width - bw, nh, bw, Math.Max(0, final.Height - nh)));
        return final;
    }
}
