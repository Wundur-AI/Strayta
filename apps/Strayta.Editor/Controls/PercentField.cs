using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Strayta.Editor.Controls;

/// <summary>
/// Photoshop's "59% ▾" field: type a percentage, or click the arrow for a slider. Commits on Enter or
/// when focus leaves; dragging the slider updates continuously.
/// </summary>
public sealed class PercentField : TemplatedControl
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<PercentField, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    private readonly TextBox _text;
    private readonly Slider _slider;

    // True only after the person types; a field that merely had focus must not write its old value into a
    // different layer when the selection changes.
    private bool _typed;

    public PercentField()
    {
        _text = new TextBox
        {
            Width = 44, MinHeight = 0, Height = 22, Padding = new Thickness(5, 1), FontSize = 11,
            HorizontalContentAlignment = HorizontalAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(5, 0, 0, 5),
        };
        _slider = new Slider { Minimum = 0, Maximum = 100, Width = 160, Margin = new Thickness(12, 4) };
        var arrow = new Button
        {
            Content = new TextBlock { Text = "▾", FontSize = 10 },
            Padding = new Thickness(5, 0), Height = 22, MinHeight = 0, MinWidth = 0,
            CornerRadius = new CornerRadius(0, 5, 5, 0),
            Flyout = new Flyout { Content = _slider, Placement = PlacementMode.BottomEdgeAlignedRight },
        };

        _text.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Commit();
            e.Handled = true;
        };
        _text.TextInput += (_, _) => _typed = true;
        _text.KeyUp += (_, e) => { if (e.Key is Key.Back or Key.Delete) _typed = true; };
        _text.LostFocus += (_, _) => Commit();
        // Only a slider the person is dragging writes back (its flyout is open); programmatic updates do not.
        _slider.ValueChanged += (_, e) =>
        {
            if (_slider.IsPointerOver && Math.Abs(e.NewValue - Value) > 0.001) Value = e.NewValue;
        };

        Template = new Avalonia.Controls.Templates.FuncControlTemplate<PercentField>((_, _) => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _text, arrow },
        });
        Show();
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty) Show();
        if (change.Property == DataContextProperty)
        {
            _typed = false;
            _text.Text = $"{Value:0}%";
        }
    }

    private void Show()
    {
        if (!_text.IsFocused) _text.Text = $"{Value:0}%";
        _slider.Value = Value;
    }

    private void Commit()
    {
        if (!_typed)
        {
            _text.Text = $"{Value:0}%";
            return;
        }
        _typed = false;
        var t = (_text.Text ?? "").Trim().TrimEnd('%').Trim();
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out var v)) Value = Math.Clamp(v, 0, 100);
        _text.Text = $"{Value:0}%";
    }
}
