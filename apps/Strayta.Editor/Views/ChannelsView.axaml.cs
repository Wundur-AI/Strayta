using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

/// <summary>Window › Channels (ChannelsToolViewModel).</summary>
public partial class ChannelsView : UserControl
{
    public ChannelsView() => InitializeComponent();

    private ChannelsToolViewModel? Panel => DataContext as ChannelsToolViewModel;

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || sender is not Control { DataContext: ChannelRow row }) return;
        var cmd = Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        var mods = e.KeyModifiers;
        Panel?.Click(row, mods.HasFlag(KeyModifiers.Shift), mods.HasFlag(cmd), mods.HasFlag(KeyModifiers.Alt));
    }

    private void OnEyePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || sender is not Control { DataContext: ChannelRow row }) return;
        Panel?.ToggleEye(row);
        e.Handled = true;
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ChannelRow row }) Panel?.Open(row);
    }
}
