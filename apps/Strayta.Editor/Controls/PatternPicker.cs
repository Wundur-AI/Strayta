using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Strayta.Core.Painting;
using Strayta.Editor.Editing;

namespace Strayta.Editor.Controls;

/// <summary>
/// Photoshop's pattern picker: a swatch of the current pattern and a drop-down of the built-in patterns, the
/// person's own (Edit › Define Pattern) and those stored in the open document. <see cref="Pattern"/> binds two-way.
/// </summary>
public sealed class PatternPicker : UserControl
{
    public static readonly StyledProperty<Pattern?> PatternProperty =
        AvaloniaProperty.Register<PatternPicker, Pattern?>(nameof(Pattern), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Patterns of the active document, listed after the presets (may be null).</summary>
    public static readonly StyledProperty<IEnumerable<Pattern>?> DocumentPatternsProperty =
        AvaloniaProperty.Register<PatternPicker, IEnumerable<Pattern>?>(nameof(DocumentPatterns));

    private readonly Image _swatch = new() { Stretch = Stretch.Fill };
    private readonly WrapPanel _grid = new() { Width = 272 };
    private readonly Flyout _flyout;

    public PatternPicker()
    {
        _flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = new ScrollViewer { MaxHeight = 260, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _grid },
        };
        _flyout.Opening += (_, _) => FillGrid();
        var button = new Button
        {
            Padding = new Thickness(0, 0, 4, 0),
            Height = 22,
            MinHeight = 0,
            Flyout = _flyout,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 3,
                Children = { new Border { Width = 20, Height = 20, Child = _swatch }, new TextBlock { Text = "▾", FontSize = 10, VerticalAlignment = VerticalAlignment.Center } },
            },
        };
        ToolTip.SetTip(button, "Pattern");
        Content = button;
        VerticalAlignment = VerticalAlignment.Center;
        Redraw();
    }

    public Pattern? Pattern { get => GetValue(PatternProperty); set => SetValue(PatternProperty, value); }
    public IEnumerable<Pattern>? DocumentPatterns { get => GetValue(DocumentPatternsProperty); set => SetValue(DocumentPatternsProperty, value); }

    /// <summary>Everything the picker lists: built-in, the person's own, then the document's (by ID, first wins).</summary>
    public static IEnumerable<Pattern> Available(IEnumerable<Pattern>? document) =>
        PatternLibrary.BuiltIn.Concat(UserPresets.Shared.Patterns).Concat(document ?? []).DistinctBy(p => p.Id);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PatternProperty) Redraw();
    }

    private void Redraw() => _swatch.Source = (Pattern ?? PatternLibrary.BuiltIn[0]) is var p ? GradientImages.Thumbnail(p, 20) : null;

    private void FillGrid()
    {
        _grid.Children.Clear();
        foreach (var pattern in Available(DocumentPatterns))
        {
            var tile = new Button
            {
                Padding = new Thickness(0),
                Margin = new Thickness(2),
                BorderThickness = new Thickness(ReferenceEquals(pattern, Pattern) ? 2 : 1),
                Content = new Image { Width = 40, Height = 40, Source = GradientImages.Thumbnail(pattern, 40) },
            };
            ToolTip.SetTip(tile, $"{pattern.Name} ({pattern.Width}×{pattern.Height})");
            var chosen = pattern;
            tile.Click += (_, _) =>
            {
                Pattern = chosen;
                _flyout.Hide();
            };
            _grid.Children.Add(tile);
        }
    }
}
