using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ByteScape.Wpf.Controls;

/// <summary>Integer entry with min/max clamping; the value is committed on Enter, focus loss and the mouse wheel.</summary>
public sealed class NumberBox : TextBox
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(int), typeof(NumberBox),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((NumberBox)d).Show()));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(int), typeof(NumberBox), new PropertyMetadata(int.MinValue));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(int), typeof(NumberBox), new PropertyMetadata(int.MaxValue));
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step), typeof(int), typeof(NumberBox), new PropertyMetadata(1));

    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Minimum { get => (int)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public int Maximum { get => (int)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Step { get => (int)GetValue(StepProperty); set => SetValue(StepProperty, value); }

    public NumberBox()
    {
        Width = 90;
        HorizontalContentAlignment = HorizontalAlignment.Right;
        Loaded += (_, _) => Show();
        LostFocus += (_, _) => Commit();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Up) { Value = Clamp(Value + Step); e.Handled = true; }
            else if (e.Key == Key.Down) { Value = Clamp(Value - Step); e.Handled = true; }
        };
        PreviewTextInput += (_, e) => { e.Handled = !e.Text.All(ch => char.IsDigit(ch) || ch == '-'); };
        MouseWheel += (_, e) => { if (IsKeyboardFocused) { Value = Clamp(Value + (e.Delta > 0 ? Step : -Step)); e.Handled = true; } };
    }

    private int Clamp(int v) => Math.Clamp(v, Minimum, Maximum);

    private void Show() => Text = Value.ToString(CultureInfo.InvariantCulture);

    private void Commit()
    {
        if (int.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) Value = Clamp(v);
        else Show();
    }
}
