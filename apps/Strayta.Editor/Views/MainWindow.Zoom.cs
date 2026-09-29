using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Strayta.Editor.Views;

// Spring-loaded tool keys: holding a tool's key (Z, say) uses that tool only while the key is down
// (EditorViewModel.Zoom.cs). The key is seen here before the tool shortcut handler picks the tool.
public partial class MainWindow
{
    private void RegisterSpringLoadedTools()
    {
        AddHandler(KeyDownEvent, OnSpringKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, OnSpringKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnSpringKeyDown(object? sender, KeyEventArgs e)
    {
        if (Editor.IsSelectAndMaskOpen || e.KeyModifiers != KeyModifiers.None || e.Key is < Key.A or > Key.Z || IsTyping) return;
        if (Editor.ToolGroups.Any(g => g.Key == e.Key.ToString())) Editor.SpringKeyDown(e.Key.ToString());
    }

    private void OnSpringKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is >= Key.A and <= Key.Z) Editor.SpringKeyUp(e.Key.ToString());
    }
}
