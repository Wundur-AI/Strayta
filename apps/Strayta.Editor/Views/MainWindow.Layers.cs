using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Input;
using Strayta.Editor.Controls;

namespace Strayta.Editor.Views;

// The layer workflow's menus, in Photoshop's places: Layer › New › Layer… (⇧⌘N dialog), Group / Ungroup Layers (⌘G,
// ⇧⌘G), Hide Layers (⌘,), Lock Layers, Link Layers, Align, Distribute, Merge Down (⌘E), Merge Visible (⇧⌘E), Flatten
// Image, Stamp Visible (⌥⇧⌘E), Delete › Hidden Layers; Select › All Layers (⌥⌘A), Deselect Layers, Similar Layers.
// Arrow keys nudge the selected layers with the Move tool.
public partial class MainWindow
{
    private void AddLayersMenus(NativeMenu menu, Func<string, ICommand, KeyGesture?, object?, NativeMenuItem> item)
    {
        var cmd = Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        var e = Editor;
        var layer = menu.Items.OfType<NativeMenuItem>().First(i => i.Header == "Layer").Menu!;
        NativeMenuItem? Find(NativeMenu m, string header) => m.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == header);

        // ⇧⌘N opens the New Layer dialog; ⌘G groups the selected layers (New Group keeps the panel's button).
        if (Find(layer, "New Layer") is { } newLayer)
        {
            newLayer.Header = "New Layer…";
            newLayer.Command = new AsyncRelayCommand(() => NewLayerWindow.NewLayerAsync(this, Editor));
            foreach (var binding in KeyBindings.Where(b => b.Gesture == newLayer.Gesture).ToList()) binding.Command = newLayer.Command;
        }
        if (Find(layer, "New Group") is { Gesture: { } groupGesture } newGroup)
        {
            newGroup.Gesture = null;
            foreach (var binding in KeyBindings.Where(b => b.Gesture == groupGesture).ToList()) KeyBindings.Remove(binding);
        }
        int at = Find(layer, "Duplicate Layer") is { } duplicate ? layer.Items.IndexOf(duplicate) + 1 : layer.Items.Count;
        NativeMenuItemBase[] grouping =
        [
            item("Group Layers", e.GroupLayersCommand, new KeyGesture(Key.G, cmd), null),
            item("Ungroup Layers", e.UngroupLayersCommand, new KeyGesture(Key.G, cmd | KeyModifiers.Shift), null),
            item("Hide Layers", e.HideLayersCommand, new KeyGesture(Key.OemComma, cmd), null),
        ];
        foreach (var i in grouping) layer.Items.Insert(at++, i);

        NativeMenuItem Sub(string header, params NativeMenuItemBase[] items)
        {
            var m = new NativeMenu();
            foreach (var i in items) m.Items.Add(i);
            return new NativeMenuItem(header) { Menu = m };
        }
        // Before Move Up / Move Down: locks, links, align and distribute; after them, the merge commands.
        int arrange = Find(layer, "Move Up") is { } up ? layer.Items.IndexOf(up) : layer.Items.Count;
        NativeMenuItemBase[] arranging =
        [
            Sub("Lock Layers",
                item("Transparent Pixels", e.ToggleLayerLockCommand, null, "Transparency"),
                item("Image Pixels", e.ToggleLayerLockCommand, null, "Pixels"),
                item("Position", e.ToggleLayerLockCommand, null, "Position"),
                item("All", e.ToggleLayerLockCommand, new KeyGesture(Key.Oem2, cmd), "All")),
            item("Link Layers", e.LinkLayersCommand, null, null),
            item("Select Linked Layers", e.SelectLinkedLayersCommand, null, null),
            new NativeMenuItemSeparator(),
            Sub("Align",
                item("Top Edges", e.AlignLayersCommand, null, "Top"),
                item("Vertical Centers", e.AlignLayersCommand, null, "VerticalCenter"),
                item("Bottom Edges", e.AlignLayersCommand, null, "Bottom"),
                new NativeMenuItemSeparator(),
                item("Left Edges", e.AlignLayersCommand, null, "Left"),
                item("Horizontal Centers", e.AlignLayersCommand, null, "HorizontalCenter"),
                item("Right Edges", e.AlignLayersCommand, null, "Right")),
            Sub("Distribute",
                item("Top Edges", e.DistributeLayersCommand, null, "Top"),
                item("Vertical Centers", e.DistributeLayersCommand, null, "VerticalCenter"),
                item("Bottom Edges", e.DistributeLayersCommand, null, "Bottom"),
                new NativeMenuItemSeparator(),
                item("Left Edges", e.DistributeLayersCommand, null, "Left"),
                item("Horizontal Centers", e.DistributeLayersCommand, null, "HorizontalCenter"),
                item("Right Edges", e.DistributeLayersCommand, null, "Right"),
                new NativeMenuItemSeparator(),
                item("Vertical Spacing", e.DistributeLayersCommand, null, "VerticalSpacing"),
                item("Horizontal Spacing", e.DistributeLayersCommand, null, "HorizontalSpacing")),
            new NativeMenuItemSeparator(),
        ];
        foreach (var i in arranging) layer.Items.Insert(arrange++, i);

        int merge = Find(layer, "Delete") is { } delete ? layer.Items.IndexOf(delete) : layer.Items.Count;
        NativeMenuItemBase[] merging =
        [
            item("Merge Down", e.MergeDownCommand, new KeyGesture(Key.E, cmd), null),
            item("Merge Visible", e.MergeVisibleCommand, new KeyGesture(Key.E, cmd | KeyModifiers.Shift), null),
            item("Stamp Visible", e.StampVisibleCommand, new KeyGesture(Key.E, cmd | KeyModifiers.Shift | KeyModifiers.Alt), null),
            item("Flatten Image", e.FlattenImageCommand, null, null),
            new NativeMenuItemSeparator(),
        ];
        foreach (var i in merging) layer.Items.Insert(merge++, i);
        if (Find(layer, "Delete") is { } del)
            layer.Items.Insert(layer.Items.IndexOf(del) + 1, item("Delete Hidden Layers", e.DeleteHiddenLayersCommand, null, null));

        if (Find(menu, "Select") is { Menu: { } select })
        {
            int s = Find(select, "Inverse") is { } inverse ? select.Items.IndexOf(inverse) + 1 : select.Items.Count;
            NativeMenuItemBase[] layers =
            [
                new NativeMenuItemSeparator(),
                item("All Layers", e.SelectAllLayersCommand, new KeyGesture(Key.A, cmd | KeyModifiers.Alt), null),
                item("Deselect Layers", e.DeselectLayersCommand, null, null),
                item("Similar Layers", e.SelectSimilarLayersCommand, null, null),
            ];
            foreach (var i in layers) select.Items.Insert(s++, i);
        }

        AddHandler(KeyDownEvent, OnLayerNudgeKey, RoutingStrategies.Bubble);
    }

    /// <summary>Arrow keys with the Move tool move the selected layers one pixel (Shift: ten), as in Photoshop.</summary>
    private void OnLayerNudgeKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || IsTyping || Editor.Tool != CanvasTool.Move || Editor.ActiveDocument is not { FreeTransform: null } doc) return;
        if (e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift)) return;
        int step = e.KeyModifiers == KeyModifiers.Shift ? 10 : 1;
        (int dx, int dy) = e.Key switch
        {
            Key.Left => (-step, 0),
            Key.Right => (step, 0),
            Key.Up => (0, -step),
            Key.Down => (0, step),
            _ => (0, 0),
        };
        if (dx == 0 && dy == 0) return;
        doc.Nudge(dx, dy);
        e.Handled = true;
    }
}
