using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.Views;

/// <summary>
/// Filter › Liquify: Photoshop's workspace, the tools down the left, the preview in the middle and the brush and mask
/// settings on the right, OK / Cancel at the bottom right. Tool keys as in Photoshop (W, R, E, C, S, B, O, F, D),
/// [ and ] change the brush size, ⌘Z undoes the last stroke, Enter is OK and Esc Cancel.
/// </summary>
public sealed class LiquifyWindow : Window
{
    private static readonly (LiquifyTool Tool, string Glyph, string Name, Key Key)[] Tools =
    [
        (LiquifyTool.ForwardWarp, "➚", "Forward Warp Tool (W)", Key.W),
        (LiquifyTool.Reconstruct, "↺", "Reconstruct Tool (R)", Key.R),
        (LiquifyTool.Smooth, "≈", "Smooth Tool (E)", Key.E),
        (LiquifyTool.TwirlClockwise, "↻", "Twirl Clockwise Tool (C; Option twirls counterclockwise)", Key.C),
        (LiquifyTool.Pucker, "⊙", "Pucker Tool (S)", Key.S),
        (LiquifyTool.Bloat, "⊕", "Bloat Tool (B)", Key.B),
        (LiquifyTool.PushLeft, "⇤", "Push Left Tool (O; Option pushes right)", Key.O),
        (LiquifyTool.FreezeMask, "❄", "Freeze Mask Tool (F)", Key.F),
        (LiquifyTool.ThawMask, "☀", "Thaw Mask Tool (D)", Key.D),
    ];

    private readonly LiquifySession _session;
    private readonly List<ToggleButton> _toolButtons = [];

    public LiquifyWindow(LiquifySession session)
    {
        _session = session;
        Title = "Liquify";
        Width = 1180;
        Height = 780;
        MinWidth = 800;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(BackgroundProperty, this.GetResourceObservable("St.Panel"));

        var tools = new StackPanel { Spacing = 4, Margin = new Thickness(8) };
        foreach (var (tool, glyph, name, _) in Tools)
        {
            var button = new ToggleButton
            {
                Content = glyph, FontSize = 17, Width = 34, Height = 34, Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                IsChecked = tool == session.Tool,
            };
            ToolTip.SetTip(button, name);
            button.Click += (_, _) => session.Tool = tool;
            _toolButtons.Add(button);
            tools.Children.Add(button);
        }
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LiquifySession.Tool))
                for (int i = 0; i < Tools.Length; i++) _toolButtons[i].IsChecked = Tools[i].Tool == session.Tool;
        };

        var canvas = new LiquifyCanvas(session);

        SliderField Slider(string label, string property, double min, double max)
        {
            var field = new SliderField { Label = label, Minimum = min, Maximum = max };
            field[!SliderField.ValueProperty] = new Binding(property) { Source = session, Mode = BindingMode.TwoWay };
            return field;
        }
        var showMask = new CheckBox { Content = "Show Mask", FontSize = 11 };
        showMask[!ToggleButton.IsCheckedProperty] = new Binding(nameof(LiquifySession.ShowMask)) { Source = session, Mode = BindingMode.TwoWay };
        var restore = new Button { Content = "Restore All", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        restore.Click += (_, _) => session.RestoreAll();
        var thaw = new Button { Content = "Thaw All", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        thaw.Click += (_, _) => session.ThawAll();
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);

        TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
        var properties = new StackPanel
        {
            Spacing = 10, Margin = new Thickness(14, 10), Width = 240,
            Children =
            {
                Heading("Brush Tool Options"),
                Slider("Size", nameof(LiquifySession.BrushSize), 1, 1500),
                Slider("Density", nameof(LiquifySession.BrushDensity), 0, 100),
                Slider("Pressure", nameof(LiquifySession.BrushPressure), 1, 100),
                Slider("Rate", nameof(LiquifySession.BrushRate), 0, 100),
                Heading("Mask Options"),
                showMask,
                thaw,
                Heading("Brush Reconstruct Options"),
                restore,
            },
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14),
            Children = { cancel, ok },
        };
        var right = new DockPanel { Children = { buttons, properties } };
        DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Bottom);

        var root = new DockPanel();
        DockPanel.SetDock(tools, Avalonia.Controls.Dock.Left);
        DockPanel.SetDock(right, Avalonia.Controls.Dock.Right);
        root.Children.Add(tools);
        root.Children.Add(right);
        root.Children.Add(new Border { Child = canvas, Margin = new Thickness(0, 8), BorderBrush = Brushes.Black, BorderThickness = new Thickness(1) });
        Content = root;

        KeyDown += OnKey;
        Opened += (_, _) => canvas.Focus();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;
        bool command = OperatingSystem.IsMacOS() ? e.KeyModifiers.HasFlag(KeyModifiers.Meta) : e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (command && e.Key == Key.Z)
        {
            _session.Undo();
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.OemOpenBrackets:
                _session.BrushSize = Math.Max(1, _session.BrushSize * 0.9 - 1);
                break;
            case Key.OemCloseBrackets:
                _session.BrushSize = _session.BrushSize * 1.1 + 1;
                break;
            case Key.Enter or Key.Return:
                Close(true);
                break;
            case Key.Escape:
                Close(false);
                break;
            default:
                var hit = Tools.FirstOrDefault(t => t.Key == e.Key);
                if (hit.Glyph is null) return;
                _session.Tool = hit.Tool;
                break;
        }
        e.Handled = true;
    }
}
