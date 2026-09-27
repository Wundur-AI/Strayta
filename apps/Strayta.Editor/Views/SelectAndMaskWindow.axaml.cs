using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

/// <summary>
/// Select › Select and Mask: a modal workspace over one <see cref="SelectAndMaskViewModel"/> session. Closes with
/// true for OK. Keys: R brush, H hand, [ and ] brush size, Enter OK, Esc cancel.
/// </summary>
public partial class SelectAndMaskWindow : Window
{
    private readonly SelectAndMaskViewModel _session;
    private bool _optionAtPress;

    // Avalonia's XAML loader needs a parameterless constructor; the window is always opened with a session.
    public SelectAndMaskWindow() : this(null!)
    {
    }

    public SelectAndMaskWindow(SelectAndMaskViewModel session)
    {
        InitializeComponent();
        _session = session;
        DataContext = session;
        OkButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);

        // Option at the start of a stroke erases refinements, as in Photoshop; the canvas only reports positions.
        Canvas.AddHandler(PointerPressedEvent, (_, e) => _optionAtPress = e.KeyModifiers.HasFlag(KeyModifiers.Alt), RoutingStrategies.Tunnel);
        Canvas.StrokeBegin = (x, y) => _session.BeginBrush(x, y, _optionAtPress);
        Canvas.StrokeMove += (x, y) => _session.ContinueBrush(x, y);
        Canvas.StrokeEnd += () => _session.EndBrush();
        Canvas.ZoomChanged += zoom => _session.SetViewScale(zoom * RenderScaling);
        Opened += (_, _) => _session.SetViewScale(Canvas.Zoom * RenderScaling);
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox) return;
        switch (e.Key)
        {
            case Key.OemOpenBrackets when e.KeyModifiers == KeyModifiers.None: _session.ResizeBrush(-1); break;
            case Key.OemCloseBrackets when e.KeyModifiers == KeyModifiers.None: _session.ResizeBrush(1); break;
            case Key.R when e.KeyModifiers == KeyModifiers.None: _session.IsBrushTool = true; break;
            case Key.H when e.KeyModifiers == KeyModifiers.None: _session.IsHandTool = true; break;
            default: return;
        }
        e.Handled = true;
    }
}
