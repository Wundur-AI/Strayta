using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Strayta.Editor.Editing;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

// Edit › Transform (Scale, Rotate, Skew, Distort, Perspective, Warp, Rotate 180° / 90°, Flip), Edit › Puppet Warp and
// Content-Aware Scale, Filter › Liquify, in Photoshop's places; Enter, Esc and Delete in Puppet Warp.
public partial class MainWindow : ILiquifyDialogs
{
    private void AddTransformMenus(NativeMenu menu, Func<string, ICommand, KeyGesture?, object?, NativeMenuItem> item)
    {
        var cmd = Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        var transform = new NativeMenu
        {
            item("Scale", Editor.TransformModeCommand, null, "Scale"),
            item("Rotate", Editor.TransformModeCommand, null, "Rotate"),
            item("Skew", Editor.TransformModeCommand, null, "Skew"),
            item("Distort", Editor.TransformModeCommand, null, "Distort"),
            item("Perspective", Editor.TransformModeCommand, null, "Perspective"),
            item("Warp", Editor.TransformModeCommand, null, "Warp"),
            new NativeMenuItemSeparator(),
            item("Rotate 180°", Editor.TransformActionCommand, null, "rotate180"),
            item("Rotate 90° Clockwise", Editor.TransformActionCommand, null, "rotate90cw"),
            item("Rotate 90° Counter Clockwise", Editor.TransformActionCommand, null, "rotate90ccw"),
            new NativeMenuItemSeparator(),
            item("Flip Horizontal", Editor.TransformActionCommand, null, "flipH"),
            item("Flip Vertical", Editor.TransformActionCommand, null, "flipV"),
        };
        var edit = menu.Items.OfType<NativeMenuItem>().First(i => i.Header == "Edit").Menu!;
        var free = edit.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "Free Transform");
        int at = free is null ? edit.Items.Count : edit.Items.IndexOf(free);
        // Photoshop's order: Content-Aware Scale, Puppet Warp, (Perspective Warp), Free Transform, Transform ›.
        edit.Items.Insert(at, item("Content-Aware Scale", Editor.ContentAwareScaleCommand, new KeyGesture(Key.C, cmd | KeyModifiers.Alt | KeyModifiers.Shift), null));
        edit.Items.Insert(at + 1, item("Puppet Warp", Editor.PuppetWarpCommand, null, null));
        edit.Items.Insert(at + 3, new NativeMenuItem("Transform") { Menu = transform });

        // Filter › Liquify…, among the special filters after Last Filter.
        if (menu.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "Filter")?.Menu is { } filter)
        {
            int sep = filter.Items.IndexOf(filter.Items.OfType<NativeMenuItemSeparator>().FirstOrDefault()!);
            filter.Items.Insert(sep < 0 ? filter.Items.Count : sep + 1, item("Liquify…", Editor.LiquifyCommand, new KeyGesture(Key.X, cmd | KeyModifiers.Shift), null));
            filter.Items.Insert(sep < 0 ? filter.Items.Count : sep + 2, new NativeMenuItemSeparator());
        }

        AddHandler(KeyDownEvent, OnPuppetKey, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    /// <summary>Enter applies Puppet Warp, Esc cancels it, Delete removes the selected pin.</summary>
    private void OnPuppetKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || Editor.ActiveDocument?.PuppetWarp is not { } puppet || IsTyping) return;
        switch (e.Key)
        {
            case Key.Enter or Key.Return when e.KeyModifiers == KeyModifiers.None:
                Editor.CommitPuppetWarpCommand.Execute(null);
                break;
            case Key.Escape when e.KeyModifiers == KeyModifiers.None:
                Editor.CancelPuppetWarpCommand.Execute(null);
                break;
            case Key.Delete or Key.Back:
                puppet.RemoveSelectedPin();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    public async Task<bool> RunLiquifyAsync(LiquifySession session) => await new LiquifyWindow(session).ShowDialog<bool>(this);
}
