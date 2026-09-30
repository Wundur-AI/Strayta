using Avalonia;
using Avalonia.Controls;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

// Transforms on the canvas: the right-click Transform menu, and the open Puppet Warp handed to the canvas.
public partial class DocumentView
{
    private DocumentViewModel? _puppetSource;

    private void WireTransform()
    {
        Canvas.TransformMenuRequested += ShowTransformMenu;
        DataContextChanged += (_, _) =>
        {
            if (_puppetSource is not null) _puppetSource.PropertyChanged -= OnPuppetChanged;
            _puppetSource = DataContext as DocumentViewModel;
            if (_puppetSource is not null) _puppetSource.PropertyChanged += OnPuppetChanged;
            Canvas.PuppetWarp = _puppetSource?.PuppetWarp;
        };
    }

    private void OnPuppetChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.PuppetWarp)) Canvas.PuppetWarp = _puppetSource?.PuppetWarp;
    }

    /// <summary>Photoshop's right-click menu in Free Transform: the modes, the turns and the flips.</summary>
    private void ShowTransformMenu(Point at)
    {
        if (_vm?.Editor is not { } editor || _vm.FreeTransform is not { } t) return;
        MenuItem Mode(string header, string mode) => new()
        {
            Header = header, Command = editor.TransformModeCommand, CommandParameter = mode,
            Icon = t.Mode.ToString() == mode ? new TextBlock { Text = "✓" } : null,
        };
        MenuItem Action(string header, string action) => new() { Header = header, Command = editor.TransformActionCommand, CommandParameter = action };
        var menu = new ContextMenu
        {
            ItemsSource = new Control[]
            {
                Mode("Free Transform", "Free"), Mode("Scale", "Scale"), Mode("Rotate", "Rotate"), Mode("Skew", "Skew"),
                Mode("Distort", "Distort"), Mode("Perspective", "Perspective"), Mode("Warp", "Warp"),
                new Separator(),
                Action("Rotate 180°", "rotate180"), Action("Rotate 90° Clockwise", "rotate90cw"), Action("Rotate 90° Counter Clockwise", "rotate90ccw"),
                new Separator(),
                Action("Flip Horizontal", "flipH"), Action("Flip Vertical", "flipV"),
            },
            Placement = PlacementMode.Pointer,
        };
        menu.Open(Canvas);
        _ = at;
    }
}
