using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;

namespace Strayta.Editor.Controls;

/// <summary>
/// A compact numeric text field for the options bar (Free Transform's X, Y, W, H and angle). Shows the value
/// with a unit, and writes back only what the person typed, on Enter or when focus leaves; Esc restores it. NaN
/// shows blank (mixed values), with the TextBox's placeholder text if one is set.
/// </summary>
public sealed class NumberField : TextBox
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<NumberField, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> UnitProperty =
        AvaloniaProperty.Register<NumberField, string>(nameof(Unit), "");

    // As in PercentField: a field that merely had focus must not write its old value back.
    private bool _typed;

    public NumberField()
    {
        Width = 64;
        MinHeight = 0;
        Height = 22;
        Padding = new Thickness(5, 1);
        FontSize = 11;
        HorizontalContentAlignment = HorizontalAlignment.Right;
        VerticalContentAlignment = VerticalAlignment.Center;
        Show();
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <summary>Shown after the number, e.g. "px", "%" or "°".</summary>
    public string Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == UnitProperty) Show();
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        _typed = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Back or Key.Delete) _typed = true;
        if (e.Key == Key.Enter)
        {
            Commit();
            e.Handled = true; // Enter applies the field; a second Enter (outside it) commits the transform
            return;
        }
        if (e.Key == Key.Escape)
        {
            _typed = false;
            Show(force: true);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        Commit();
    }

    private void Show(bool force = false)
    {
        // NaN shows an empty field: a value that differs across a selection (the Character panel's mixed values).
        if (force || !IsFocused) Text = double.IsNaN(Value) ? "" : $"{Value.ToString("0.#", CultureInfo.CurrentCulture)}{Unit}";
    }

    private void Commit()
    {
        if (_typed)
        {
            _typed = false;
            var t = (Text ?? "").Trim();
            if (Unit.Length > 0 && t.EndsWith(Unit, StringComparison.Ordinal)) t = t[..^Unit.Length];
            if (double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var v) && double.IsFinite(v)) Value = v;
        }
        Show(force: true);
    }
}
