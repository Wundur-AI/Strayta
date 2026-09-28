using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

/// <summary>
/// A Photoshop-style filter dialog over one <see cref="FilterSessionViewModel"/>. Closes with true for OK. Enter is OK,
/// Esc cancels, and holding Option turns Cancel into Reset (back to the settings the dialog opened with).
/// </summary>
public partial class FilterWindow : Window
{
    private readonly FilterSessionViewModel _session;
    private Point? _panFrom;
    private bool _reset;

    // Avalonia's XAML loader needs a parameterless constructor; the window is always opened with a session.
    public FilterWindow() : this(null!)
    {
    }

    public FilterWindow(FilterSessionViewModel session)
    {
        InitializeComponent();
        _session = session;
        DataContext = session;
        OkButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) =>
        {
            if (_reset) _session.Reset();
            else Close(false);
        };

        PreviewBox.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(PreviewBox).Properties.IsLeftButtonPressed) return;
            _panFrom = e.GetPosition(PreviewBox);
            e.Pointer.Capture(PreviewBox);
            _session.BeginPan();
            e.Handled = true;
        };
        PreviewBox.PointerMoved += (_, e) =>
        {
            if (_panFrom is not { } from) return;
            var at = e.GetPosition(PreviewBox);
            _session.PanBy(at.X - from.X, at.Y - from.Y);
            _panFrom = at;
        };
        PreviewBox.PointerReleased += (_, _) => EndPan();
        PreviewBox.PointerCaptureLost += (_, _) => EndPan();

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) => SetReset(e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key is not (Key.LeftAlt or Key.RightAlt)), RoutingStrategies.Tunnel);
        Deactivated += (_, _) => SetReset(false);
        // Option already held when the pointer comes back over the dialog.
        PointerMoved += (_, e) => SetReset(e.KeyModifiers.HasFlag(KeyModifiers.Alt));
    }

    private void EndPan()
    {
        if (_panFrom is null) return;
        _panFrom = null;
        _session.EndPan();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftAlt or Key.RightAlt)
        {
            SetReset(true);
            return;
        }
        if (e.Key == Key.Escape)
        {
            Close(false);
            e.Handled = true;
        }
    }

    private void SetReset(bool reset)
    {
        if (reset == _reset) return;
        _reset = reset;
        CancelButton.Content = reset ? "Reset" : "Cancel";
    }
}
