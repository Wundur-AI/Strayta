using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Controls;

/// <summary>
/// A tool-strip slot for a <see cref="ToolGroup"/> (its DataContext), as in Photoshop: a click picks the tool it
/// shows; pressing and holding, or a right-click, opens a menu of the group's tools with their shortcut.
/// </summary>
public sealed class ToolSlot : ToggleButton
{
    /// <summary>How long a press lasts before the group's menu opens (Photoshop opens it after a short hold).</summary>
    private static readonly TimeSpan HoldDelay = TimeSpan.FromMilliseconds(300);

    private readonly DispatcherTimer _hold;
    private bool _menuOpenedByHold;

    public ToolSlot()
    {
        _hold = new DispatcherTimer { Interval = HoldDelay };
        _hold.Tick += (_, _) =>
        {
            _hold.Stop();
            _menuOpenedByHold = true;
            OpenMenu();
        };
    }

    protected override Type StyleKeyOverride => typeof(ToggleButton);

    private ToolGroup? Group => DataContext as ToolGroup;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _menuOpenedByHold = false;
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            OpenMenu();
            e.Handled = true;
            return;
        }
        if (point.Properties.IsLeftButtonPressed && Group is { HasMore: true }) _hold.Start();
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _hold.Stop();
        base.OnPointerReleased(e);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _hold.Stop();
        base.OnPointerCaptureLost(e);
    }

    /// <summary>Picks the group's tool instead of toggling; IsChecked follows <see cref="ToolGroup.IsActive"/>.</summary>
    protected override void OnClick()
    {
        if (_menuOpenedByHold)
        {
            _menuOpenedByHold = false;
            return;
        }
        Group?.Activate();
    }

    private void OpenMenu()
    {
        if (Group is not { HasMore: true } group) return;
        var menu = new MenuFlyout { Placement = PlacementMode.RightEdgeAlignedTop };
        foreach (var tool in group.Tools)
        {
            var item = new MenuItem
            {
                Header = tool.Name,
                InputGesture = Enum.TryParse<Key>(group.Key, out var key) ? new KeyGesture(key) : null,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = tool == group.Current,
                Icon = new Avalonia.Controls.Shapes.Path
                {
                    Classes = { "icon", "small" },
                    Data = ResourceIcon.Instance.Convert(tool.Icon, typeof(Geometry), null, System.Globalization.CultureInfo.InvariantCulture) as Geometry,
                },
            };
            var chosen = tool.Tool;
            item.Click += (_, _) => group.Select(chosen);
            menu.Items.Add(item);
        }
        menu.ShowAt(this);
    }
}
