using Avalonia.Controls;
using Avalonia.Input;

namespace Strayta.Editor.Views;

// Keyboard shortcuts for the Quick Selection / Magic Wand tool group.
public partial class MainWindow
{
    private void AddWandShortcuts() => AddHandler(KeyDownEvent, OnWandKey, Avalonia.Interactivity.RoutingStrategies.Bubble);

    /// <summary>W picks the group's last used tool; Shift+W switches between Quick Selection and Magic Wand, as in Photoshop.</summary>
    private void OnWandKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.W || FocusManager?.GetFocusedElement() is TextBox) return;
        if (e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift)) return;
        Editor.SelectWandToolCommand.Execute(e.KeyModifiers == KeyModifiers.Shift ? "cycle" : null);
        e.Handled = true;
    }
}
