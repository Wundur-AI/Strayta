using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Strayta.Core;
using Strayta.Editor.Editing;
using Strayta.Editor.Views;

namespace Strayta.Editor.Controls;

/// <summary>
/// Photoshop's gradient picker: a swatch of the current gradient (click it to open the Gradient Editor) and an arrow
/// that drops down the presets. <see cref="Gradient"/> is the Core <see cref="Core.Gradient"/> (the type layer effects
/// use), bound two-way; foreground and background stops are kept as such and shown with <see cref="ForegroundColor"/>
/// and <see cref="BackgroundColor"/>.
/// </summary>
/// <example>
/// In a Layer Style page: <c>&lt;c:GradientPicker Gradient="{Binding Overlay.Gradient}" /&gt;</c>.
/// </example>
public sealed class GradientPicker : UserControl
{
    public static readonly StyledProperty<Gradient?> GradientProperty =
        AvaloniaProperty.Register<GradientPicker, Gradient?>(nameof(Gradient), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<RgbColor> ForegroundColorProperty =
        AvaloniaProperty.Register<GradientPicker, RgbColor>(nameof(ForegroundColor), RgbColor.Black);
    public static readonly StyledProperty<RgbColor> BackgroundColorProperty =
        AvaloniaProperty.Register<GradientPicker, RgbColor>(nameof(BackgroundColor), new RgbColor(1, 1, 1));
    public static readonly StyledProperty<bool> ReverseProperty = AvaloniaProperty.Register<GradientPicker, bool>(nameof(Reverse));

    private readonly Image _swatch = new() { Stretch = Stretch.Fill };
    private readonly WrapPanel _grid = new() { Width = 272 };
    private readonly Flyout _flyout;

    public GradientPicker()
    {
        var swatchButton = new Button
        {
            Padding = new Thickness(0),
            Width = 64,
            Height = 20,
            MinHeight = 0,
            BorderThickness = new Thickness(1),
            Content = _swatch,
        };
        ToolTip.SetTip(swatchButton, "Click to edit the gradient");
        swatchButton.Click += async (_, _) => await EditAsync();

        var edit = new Button { Content = "Edit…", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        edit.Click += async (_, _) =>
        {
            _flyout!.Hide();
            await EditAsync();
        };
        _flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new ScrollViewer { MaxHeight = 260, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = _grid },
                    edit,
                },
            },
        };
        _flyout.Opening += (_, _) => FillGrid();
        var arrow = new Button { Content = "▾", Padding = new Thickness(4, 0), Height = 20, MinHeight = 0, FontSize = 10, Flyout = _flyout };
        ToolTip.SetTip(arrow, "Gradient presets");

        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { swatchButton, arrow } };
        VerticalAlignment = VerticalAlignment.Center;
        Redraw();
    }

    public Gradient? Gradient { get => GetValue(GradientProperty); set => SetValue(GradientProperty, value); }
    public RgbColor ForegroundColor { get => GetValue(ForegroundColorProperty); set => SetValue(ForegroundColorProperty, value); }
    public RgbColor BackgroundColor { get => GetValue(BackgroundColorProperty); set => SetValue(BackgroundColorProperty, value); }

    /// <summary>Shows the swatch reversed (the Gradient tool's Reverse option).</summary>
    public bool Reverse { get => GetValue(ReverseProperty); set => SetValue(ReverseProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GradientProperty || change.Property == ForegroundColorProperty || change.Property == BackgroundColorProperty
            || change.Property == ReverseProperty)
            Redraw();
    }

    private void Redraw()
    {
        var g = (Gradient ?? GradientPresets.ForegroundToBackground).Resolve(ForegroundColor, BackgroundColor);
        _swatch.Source = GradientImages.Strip(g, 128, 1, reverse: Reverse);
    }

    private void FillGrid()
    {
        _grid.Children.Clear();
        foreach (var preset in GradientPresets.All)
        {
            var tile = new Button
            {
                Padding = new Thickness(0),
                Margin = new Thickness(2),
                BorderThickness = new Thickness(1),
                Content = new Image { Width = 36, Height = 36, Stretch = Stretch.Fill, Source = GradientImages.Strip(preset.Resolve(ForegroundColor, BackgroundColor), 36, 1) },
            };
            ToolTip.SetTip(tile, preset.Name);
            var chosen = preset;
            tile.Click += (_, _) =>
            {
                Gradient = chosen;
                _flyout.Hide();
            };
            _grid.Children.Add(tile);
        }
    }

    private async Task EditAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var result = await GradientEditorWindow.EditAsync(owner, Gradient ?? GradientPresets.ForegroundToBackground, ForegroundColor, BackgroundColor);
        if (result is not null) Gradient = result;
    }
}
