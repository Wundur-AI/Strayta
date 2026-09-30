using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Strayta.Core;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

// The Layers panel's menus: the right-click menu on rows (with the color labels), the panel menu (thumbnail size,
// Collapse All Groups, Delete Hidden Layers, the merge commands) and the New Layer button's Option-click dialog.
public partial class LayersView
{
    private bool _newLayerOption;

    private void WireLayerMenus()
    {
        NewLayerButton.AddHandler(PointerPressedEvent, (_, e) => _newLayerOption = e.KeyModifiers.HasFlag(KeyModifiers.Alt), RoutingStrategies.Tunnel, handledEventsToo: true);
        NewLayerButton.Click += (_, e) =>
        {
            if (!_newLayerOption || Editor is not { } editor || TopLevel.GetTopLevel(this) is not Window owner) return;
            _newLayerOption = false;
            _ = NewLayerWindow.NewLayerAsync(owner, editor);
            e.Handled = true; // the button's plain New Layer must not run too
        };
    }

    private static MenuItem Item(string header, ICommand command, object? parameter = null, bool enabled = true) =>
        new() { Header = header, Command = command, CommandParameter = parameter, IsEnabled = enabled };

    /// <summary>Right-click on a row: selects it (unless it is already among the selected layers) and lists what applies.</summary>
    private void ShowRowMenu(LayerItemViewModel item)
    {
        if (Editor is not { ActiveDocument: { } doc } editor) return;
        if (!item.IsSelected) doc.ClickLayer(item, command: false, shift: false);
        bool several = doc.HasMultipleSelected;
        var top = doc.SelectedTopLevel();
        var items = new List<Control>
        {
            Item("Blending Options…", editor.LayerStyleCommand, nameof(LayerStylePage.BlendingOptions), !several),
            new Separator(),
            Item(several ? "Duplicate Layers" : "Duplicate Layer", editor.DuplicateLayerCommand),
            Item(several ? "Delete Layers" : "Delete Layer", editor.DeleteLayerCommand),
            Item("Group from Layers", editor.GroupLayersCommand),
        };
        if (top.Any(n => n is LayerGroup)) items.Add(Item("Ungroup Layers", editor.UngroupLayersCommand));
        items.Add(new Separator());
        items.Add(Item("Convert to Smart Object", editor.ConvertToSmartObjectCommand, enabled: !several));
        items.Add(Item("Rasterize Layer", editor.RasterizeLayerCommand, enabled: !several));
        items.Add(new Separator());
        items.Add(top.Count > 1 && top.All(n => n.LinkGroup != 0 && n.LinkGroup == top[0].LinkGroup)
            ? Item("Unlink Layers", editor.UnlinkLayersCommand)
            : Item("Link Layers", editor.LinkLayersCommand, enabled: several));
        items.Add(Item("Select Linked Layers", editor.SelectLinkedLayersCommand, enabled: item.IsLinked));
        items.Add(Item("Select Similar Layers", editor.SelectSimilarLayersCommand));
        items.Add(new Separator());
        items.Add(Item(several ? "Merge Layers" : item.IsGroup ? "Merge Group" : "Merge Down", editor.MergeDownCommand));
        items.Add(Item("Merge Visible", editor.MergeVisibleCommand));
        items.Add(Item("Flatten Image", editor.FlattenImageCommand));
        items.Add(new Separator());
        foreach (var color in Enum.GetValues<LayerColor>())
            items.Add(ColorItem(color, editor, item.ColorLabel == color && !several));
        var menu = new ContextMenu { ItemsSource = items, PlacementTarget = Rows };
        menu.Open(Rows);
    }

    private static MenuItem ColorItem(LayerColor color, EditorViewModel editor, bool current)
    {
        var swatch = new Border
        {
            Width = 12, Height = 12, CornerRadius = new Avalonia.CornerRadius(2),
            Background = LayerItemViewModel.LabelBrush(color) ?? Brushes.Transparent,
            BorderBrush = Brushes.Gray, BorderThickness = new Avalonia.Thickness(color == LayerColor.None ? 1 : 0),
        };
        return new MenuItem
        {
            Header = color == LayerColor.None ? "No Color" : color.ToString(),
            Icon = swatch,
            Command = editor.SetLayerColorCommand,
            CommandParameter = color.ToString(),
            ToggleType = MenuItemToggleType.Radio,
            IsChecked = current,
        };
    }

    /// <summary>The panel menu (the button at the end of the footer).</summary>
    private void OnPanelMenu(object? sender, RoutedEventArgs e)
    {
        if (Editor is not { } editor) return;
        MenuItem Size(string name) => new()
        {
            Header = name,
            Command = editor.SetLayerThumbnailSizeCommand,
            CommandParameter = name,
            ToggleType = MenuItemToggleType.Radio,
            IsChecked = editor.LayerThumbnailSize.ToString() == name,
        };
        var newLayer = new MenuItem { Header = "New Layer…" };
        newLayer.Click += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is Window owner) _ = NewLayerWindow.NewLayerAsync(owner, editor);
        };
        var items = new List<Control>
        {
            newLayer,
            Item("Duplicate Layer", editor.DuplicateLayerCommand),
            Item("Delete Layer", editor.DeleteLayerCommand),
            Item("Delete Hidden Layers", editor.DeleteHiddenLayersCommand),
            new Separator(),
            Item("New Group", editor.NewGroupCommand),
            Item("Group Layers", editor.GroupLayersCommand),
            Item("Ungroup Layers", editor.UngroupLayersCommand),
            Item("Collapse All Groups", editor.CollapseAllGroupsCommand),
            new Separator(),
            Item("Link Layers", editor.LinkLayersCommand),
            Item("Select Linked Layers", editor.SelectLinkedLayersCommand),
            Item("Select Similar Layers", editor.SelectSimilarLayersCommand),
            new Separator(),
            Item("Merge Down", editor.MergeDownCommand),
            Item("Merge Visible", editor.MergeVisibleCommand),
            Item("Stamp Visible", editor.StampVisibleCommand),
            Item("Flatten Image", editor.FlattenImageCommand),
            new Separator(),
            new MenuItem { Header = "Thumbnail Size", ItemsSource = new[] { Size("None"), Size("Small"), Size("Medium"), Size("Large") } },
        };
        var menu = new ContextMenu { ItemsSource = items, PlacementTarget = PanelMenuButton, Placement = PlacementMode.TopEdgeAlignedRight };
        menu.Open(PanelMenuButton);
    }
}
