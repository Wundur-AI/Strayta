using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

/// <summary>
/// Select › Select and Mask as a workspace inside the main window, over one <see cref="SelectAndMaskViewModel"/>
/// session. Raises <see cref="Completed"/> with true for OK. Keys: W Quick Selection, R Refine Edge brush, B Brush,
/// L Lasso, H Hand, Z Zoom, [ and ] brush size, F cycles the views, ⌘Z / ⇧⌘Z undo and redo inside the workspace,
/// Enter OK, Esc cancel.
/// </summary>
public partial class SelectAndMaskWorkspace : UserControl
{
    private bool _optionAtPress;

    public SelectAndMaskWorkspace()
    {
        InitializeComponent();
        OkButton.Click += (_, _) => Completed?.Invoke(true);
        CancelButton.Click += (_, _) => Completed?.Invoke(false);

        // Option at the start of a stroke erases refinements or subtracts, as in Photoshop; the canvas only reports positions.
        Canvas.AddHandler(PointerPressedEvent, (_, e) => _optionAtPress = e.KeyModifiers.HasFlag(KeyModifiers.Alt), RoutingStrategies.Tunnel);
        Canvas.StrokeBegin = (x, y) => Session?.BeginStroke(x, y, _optionAtPress) == true;
        Canvas.StrokeMove += (x, y) => Session?.ContinueStroke(x, y);
        Canvas.StrokeEnd += () => _ = Session?.EndStrokeAsync();
        Canvas.QuickSelectBegin = (x, y, mode) => Session?.BeginQuickSelection(x, y, mode) == true;
        Canvas.QuickSelectMove += (x, y) => Session?.ContinueQuickSelection(x, y);
        Canvas.QuickSelectEnd += () => _ = Session?.EndQuickSelectionAsync();
        Canvas.SelectionGestureCompleted += g => _ = Session?.ApplyLassoAsync(g);
        Canvas.ZoomChanged += zoom => Session?.SetViewScale(zoom * Scaling);
        DataContextChanged += (_, _) =>
        {
            if (Session is { } s)
            {
                s.SetViewScale(Canvas.Zoom * Scaling);
                Canvas.Focus();
            }
        };
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    /// <summary>OK (true) or Cancel (false).</summary>
    public event Action<bool>? Completed;

    public SelectAndMaskViewModel? Session => DataContext as SelectAndMaskViewModel;

    /// <summary>The canvas, for tests that read what it shows.</summary>
    internal Controls.ImageCanvas PreviewCanvas => Canvas;

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    private void OnUndo(object? sender, RoutedEventArgs e) => Session?.Undo();
    private void OnRedo(object? sender, RoutedEventArgs e) => Session?.Redo();

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (Session is not { } s || !IsVisible) return;
        bool command = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (command && e.Key == Key.Z)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) s.Redo();
            else s.Undo();
            e.Handled = true;
            return;
        }
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox && e.Key is not (Key.Enter or Key.Escape)) return;
        if (e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift)) return;
        switch (e.Key)
        {
            case Key.OemOpenBrackets: s.ResizeBrush(-1); break;
            case Key.OemCloseBrackets: s.ResizeBrush(1); break;
            case Key.W: s.Tool = RefineTool.QuickSelection; break;
            case Key.R: s.Tool = RefineTool.RefineEdge; break;
            case Key.B: s.Tool = RefineTool.Brush; break;
            case Key.L: s.Tool = RefineTool.Lasso; break;
            case Key.H: s.Tool = RefineTool.Hand; break;
            case Key.Z: s.Tool = RefineTool.Zoom; break;
            case Key.F: s.ViewIndex = (s.ViewIndex + (e.KeyModifiers == KeyModifiers.Shift ? 6 : 1)) % 7; break;
            case Key.Enter: Completed?.Invoke(true); break;
            case Key.Escape: Completed?.Invoke(false); break;
            default: return;
        }
        e.Handled = true;
    }
}
