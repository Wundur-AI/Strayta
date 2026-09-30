using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Strayta.Core;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

public partial class LayersView : UserControl
{
    private enum DropPosition { Above, Below, Into }

    private LayerItemViewModel? _dragItem;
    private Point _dragStart;
    private bool _dragging;
    private (LayerItemViewModel Item, DropPosition Position)? _dropTarget;
    private LayerItemViewModel? _pendingSingle; // a plain click on one of several selected rows selects it alone on release

    public LayersView()
    {
        InitializeComponent();
        WireMaskButton(); // Option-click hides (LayersView.MaskButton.cs)
        WireLayerMenus(); // New Layer dialog on Option-click (LayersView.Menus.cs)
        // Tunnel so the list's own selection handling still runs.
        Rows.AddHandler(PointerPressedEvent, OnRowsPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Rows.AddHandler(PointerMovedEvent, OnRowsPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        Rows.AddHandler(PointerReleasedEvent, OnRowsPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    // ---- Drag and drop ------------------------------------------------------------------------------

    private void OnRowsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragItem = null;
        _dragging = false;
        _pendingSingle = null;
        var props = e.GetCurrentPoint(Rows).Properties;
        var row = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(true)?.DataContext as LayerItemViewModel;
        if (props.IsRightButtonPressed && row is not null)
        {
            ShowRowMenu(row); // LayersView.Menus.cs
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) return;
        if (e.Source is Visual v && (v.FindAncestorOfType<ToggleButton>(true) is not null || v.FindAncestorOfType<TextBox>(true) is not null
            || v.FindAncestorOfType<Border>(true) is { } b && b.Classes.Contains("hit"))) return;
        if (row is not { } item || Document is not { } doc) return;

        // ⌘-click toggles and ⇧-click selects a range of rows; the list's own single selection must not also react.
        bool command = IsCommand(e.KeyModifiers), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (command || shift)
        {
            doc.ClickLayer(item, command, shift);
            e.Handled = true;
            return;
        }
        // Pressing one of several selected layers keeps them all, so they can be dragged together.
        if (item.IsSelected && doc.HasMultipleSelected)
        {
            _pendingSingle = item;
            e.Handled = true;
        }
        _dragItem = item;
        _dragStart = e.GetPosition(Rows);
    }

    private DocumentViewModel? Document => (DataContext as LayersToolViewModel)?.Editor.ActiveDocument;

    private static bool IsCommand(KeyModifiers m) =>
        OperatingSystem.IsMacOS() ? m.HasFlag(KeyModifiers.Meta) : m.HasFlag(KeyModifiers.Control);

    private void OnRowsPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem is null) return;
        var pos = e.GetPosition(Rows);
        if (!_dragging)
        {
            if (Math.Abs(pos.Y - _dragStart.Y) < 5 && Math.Abs(pos.X - _dragStart.X) < 5) return;
            _dragging = true;
            e.Pointer.Capture(Rows);
        }
        _dropTarget = FindDropTarget(pos);
        ShowIndicator();
    }

    private void OnRowsPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging && _dragItem is { } item && _dropTarget is { } target && DataContext is LayersToolViewModel { Editor.ActiveDocument: { } doc })
            Drop(doc, item, target.Item.Node, target.Position);
        else if (!_dragging && _pendingSingle is { } single && Document is { } d)
            d.ClickLayer(single, command: false, shift: false);
        _pendingSingle = null;
        _dragItem = null;
        _dragging = false;
        _dropTarget = null;
        e.Pointer.Capture(null);
        ShowIndicator();
    }

    /// <summary>The row under the pointer and whether the drop goes above it, below it, or into it (groups).</summary>
    private (LayerItemViewModel, DropPosition)? FindDropTarget(Point pos)
    {
        foreach (var row in Rows.GetVisualDescendants().OfType<ListBoxItem>())
        {
            if (row.DataContext is not LayerItemViewModel item || !row.IsVisible) continue;
            if (RowBounds(row) is not { } r || pos.Y < r.Top || pos.Y >= r.Bottom) continue;
            if (ReferenceEquals(item, _dragItem) || item.IsSelected && _dragItem?.IsSelected == true) return null;
            double t = (pos.Y - r.Top) / r.Height;
            if (item.IsGroup && t is > 0.3 and < 0.7) return (item, DropPosition.Into);
            return (item, t < 0.5 ? DropPosition.Above : DropPosition.Below);
        }
        return null;
    }

    private Rect? RowBounds(Control row) =>
        row.TranslatePoint(new Point(0, 0), Rows) is { } o ? new Rect(o, row.Bounds.Size) : null;

    private void ShowIndicator()
    {
        DropLine.IsVisible = DropBox.IsVisible = false;
        if (!_dragging || _dropTarget is not { } target) return;
        var row = Rows.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(t => ReferenceEquals(t.DataContext, target.Item));
        if (row is null || RowBounds(row) is not { } r) return;
        double indent = 31 + target.Item.Depth * 18; // past the eye column, at the row's nesting level

        if (target.Position == DropPosition.Into)
        {
            DropBox.Width = r.Width - indent - 2;
            DropBox.Height = r.Height;
            Canvas.SetLeft(DropBox, r.Left + indent);
            Canvas.SetTop(DropBox, r.Top);
            DropBox.IsVisible = true;
        }
        else
        {
            DropLine.Width = r.Width - indent - 2;
            Canvas.SetLeft(DropLine, r.Left + indent);
            Canvas.SetTop(DropLine, (target.Position == DropPosition.Above ? r.Top : r.Bottom) - 1);
            DropLine.IsVisible = true;
        }
    }

    /// <summary>
    /// The panel lists the top layer first, while groups store children bottom first. Dragging one of several selected
    /// layers moves them all.
    /// </summary>
    private static void Drop(DocumentViewModel doc, LayerItemViewModel dragged, LayerNode target, DropPosition position)
    {
        IReadOnlyList<LayerNode> nodes = dragged.IsSelected && doc.HasMultipleSelected ? doc.SelectedTopLevel() : [dragged.Node];
        if (position == DropPosition.Into && target is LayerGroup group)
        {
            doc.MoveLayers(nodes, group, group.Children.Count);
            return;
        }
        if (target.Parent is not { } parent) return;
        int index = parent.IndexOf(target) + (position == DropPosition.Above ? 1 : 0);
        doc.MoveLayers(nodes, parent, index);
    }

    // ---- Blend mode ------------------------------------------------------------------------------------

    /// <summary>
    /// The menu shows the selected layer's mode (one-way) and only writes back when the person picks an entry
    /// with the menu open. Selection changes caused by switching layers never become edits.
    /// </summary>
    private void OnBlendChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { IsDropDownOpen: true } && sender is ComboBox { IsKeyboardFocusWithin: false }) return;
        if (sender is ComboBox { DataContext: LayerItemViewModel item, SelectedItem: Strayta.Core.BlendMode mode } && mode != item.BlendMode)
            item.BlendMode = mode;
    }

    // ---- Eye and disclosure --------------------------------------------------------------------------

    // These act on press and mark the event handled, so the row does not also select or start a drag.
    // (Buttons nested in a selectable row act on release, and the row can take the pointer first.)

    private void OnEyePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is Control { DataContext: LayerItemViewModel item }) item.IsVisible = !item.IsVisible;
        e.Handled = true;
    }

    private void OnChevronPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is Control { DataContext: LayerItemViewModel item }) item.IsExpanded = !item.IsExpanded;
        e.Handled = true;
    }

    // ---- Masks and adjustments ------------------------------------------------------------------------

    // Thumbnail clicks choose what painting edits (Photoshop's "target"); the row still selects and drags as usual.

    private void OnLayerThumbPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && sender is Control { DataContext: LayerItemViewModel item }
            && DataContext is LayersToolViewModel { Editor.ActiveDocument: { } doc })
            doc.Target(item, mask: false);
    }

    private void OnMaskThumbPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || sender is not Control { DataContext: LayerItemViewModel item }
            || DataContext is not LayersToolViewModel { Editor.ActiveDocument: { } doc }) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Shift-click disables or enables the mask without changing the target, as in Photoshop.
            doc.SelectedLayer = item;
            doc.ToggleMaskEnabled();
            e.Handled = true;
            return;
        }
        doc.Target(item, mask: true);
    }

    private void OnNewAdjustment(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string kind } && DataContext is LayersToolViewModel { Editor: var editor })
            editor.NewAdjustmentCommand.Execute(kind);
    }

    // ---- Renaming -----------------------------------------------------------------------------------

    private void OnNameDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: LayerItemViewModel item }) item.IsRenaming = true;
        e.Handled = true;
    }

    private void OnRenameAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox box)
            box.PropertyChanged += (_, args) =>
            {
                if (args.Property == IsVisibleProperty && box.IsVisible)
                {
                    box.Focus();
                    box.SelectAll();
                }
            };
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: LayerItemViewModel item } box) return;
        if (e.Key == Key.Enter) Commit(box, item);
        else if (e.Key == Key.Escape) item.IsRenaming = false;
        else return;
        e.Handled = true;
        Rows.Focus();
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: LayerItemViewModel { IsRenaming: true } item } box) Commit(box, item);
    }

    private static void Commit(TextBox box, LayerItemViewModel item)
    {
        item.IsRenaming = false;
        if (!string.IsNullOrWhiteSpace(box.Text)) item.Name = box.Text.Trim();
    }
}
