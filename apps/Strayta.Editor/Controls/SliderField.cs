using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Strayta.Editor.Controls;

/// <summary>
/// A Properties-panel row: label and number field above a slider. Dragging writes continuously; typing commits
/// on Enter or when focus leaves. Only the person's own input writes back, so a range or value change caused
/// by switching layers never turns into an edit.
/// </summary>
public sealed class SliderField : TemplatedControl
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<SliderField, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<SliderField, double>(nameof(Minimum));
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<SliderField, double>(nameof(Maximum), 100);
    public static readonly StyledProperty<string> LabelProperty = AvaloniaProperty.Register<SliderField, string>(nameof(Label), "");
    public static readonly StyledProperty<string> FormatProperty = AvaloniaProperty.Register<SliderField, string>(nameof(Format), "0");

    /// <summary>The slider moves on a log scale (for gamma, so 1.00 sits in the middle of 0.10..9.99).</summary>
    public static readonly StyledProperty<bool> LogarithmicProperty = AvaloniaProperty.Register<SliderField, bool>(nameof(Logarithmic));

    private readonly Slider _slider;
    private readonly TextBox _text;
    private readonly TextBlock _label;
    private bool _dragging, _typed, _updating;

    public SliderField()
    {
        _label = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        _label.Classes.Add("label");
        _text = new TextBox
        {
            Width = 52, MinHeight = 0, Height = 22, Padding = new Thickness(5, 1), FontSize = 11,
            HorizontalContentAlignment = HorizontalAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center,
        };
        _slider = new Slider { Margin = new Thickness(0, -8, 0, -6), SmallChange = 1, LargeChange = 10 };

        // A drag counts from press to release even when the pointer leaves the slider.
        _slider.AddHandler(PointerPressedEvent, (_, _) => _dragging = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        _slider.AddHandler(PointerReleasedEvent, (_, _) => _dragging = false, RoutingStrategies.Tunnel, handledEventsToo: true);
        _slider.AddHandler(PointerCaptureLostEvent, (_, _) => _dragging = false, RoutingStrategies.Tunnel, handledEventsToo: true);
        _slider.ValueChanged += (_, e) =>
        {
            if (_updating || !(_dragging || _slider.IsKeyboardFocusWithin)) return;
            Value = FromSlider(e.NewValue);
            ShowText();
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

        Template = new Avalonia.Controls.Templates.FuncControlTemplate<SliderField>((_, _) =>
        {
            var header = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_text, Avalonia.Controls.Dock.Right);
            header.Children.Add(_text);
            header.Children.Add(_label);
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(header);
            stack.Children.Add(_slider);
            return stack;
        });
        Sync();
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Format { get => GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    public bool Logarithmic { get => GetValue(LogarithmicProperty); set => SetValue(LogarithmicProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty) _label.Text = Label;
        else if (change.Property == DataContextProperty) _typed = false;
        if (change.Property == ValueProperty || change.Property == MinimumProperty || change.Property == MaximumProperty
            || change.Property == LogarithmicProperty || change.Property == FormatProperty || change.Property == DataContextProperty)
            Sync();
    }

    private double ToSlider(double v) => Logarithmic ? Math.Log10(Math.Max(v, 1e-6)) : v;
    private double FromSlider(double s) => Logarithmic ? Math.Round(Math.Pow(10, s), 2) : Math.Round(s);

    /// <summary>Shows the current value without writing anything back.</summary>
    private void Sync()
    {
        _updating = true;
        try
        {
            _slider.Minimum = ToSlider(Minimum);
            _slider.Maximum = ToSlider(Maximum);
            _slider.SmallChange = Logarithmic ? 0.01 : 1;
            _slider.LargeChange = Logarithmic ? 0.1 : 10;
            if (!_dragging) _slider.Value = ToSlider(Value);
        }
        finally
        {
            _updating = false;
        }
        ShowText();
    }

    private void ShowText()
    {
        if (!_text.IsFocused || !_typed) _text.Text = Value.ToString(Format, CultureInfo.CurrentCulture);
    }

    private void Commit()
    {
        if (!_typed)
        {
            ShowText();
            return;
        }
        _typed = false;
        if (double.TryParse((_text.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var v))
            Value = Math.Clamp(v, Minimum, Maximum);
        _text.Text = Value.ToString(Format, CultureInfo.CurrentCulture);
    }
}
