using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Strayta.Editor.Controls;

/// <summary>
/// The font family menu of the options bar and the Character panel: a button naming the family, opening a searchable
/// list (typing filters by any part of the name) where each family is shown in its own font, as Photoshop previews
/// them. Only the visible rows are created, so a few hundred families stay quick.
/// </summary>
public sealed class FontFamilyPicker : UserControl
{
    public static readonly StyledProperty<string?> FamilyProperty =
        AvaloniaProperty.Register<FontFamilyPicker, string?>(nameof(Family), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IReadOnlyList<string>?> FamiliesProperty =
        AvaloniaProperty.Register<FontFamilyPicker, IReadOnlyList<string>?>(nameof(Families));

    private readonly Button _button;
    private readonly TextBlock _label;
    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly Flyout _flyout;
    private readonly Dictionary<string, FontFamily> _fonts = new(StringComparer.Ordinal);

    public FontFamilyPicker()
    {
        _label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var arrow = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M0,0 L4,4 L8,0"), Stroke = Brushes.Gray, StrokeThickness = 1.2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        var content = new DockPanel();
        DockPanel.SetDock(arrow, Avalonia.Controls.Dock.Right);
        content.Children.Add(arrow);
        content.Children.Add(_label);

        _search = new TextBox { PlaceholderText = "Search fonts", Margin = new Thickness(0, 0, 0, 6), FontSize = 12 };
        _list = new ListBox
        {
            Height = 360,
            Width = 280,
            ItemTemplate = new FuncDataTemplate<string?>((name, _) => new TextBlock
            {
                Text = name,
                FontSize = 14,
                FontFamily = FontFor(name),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Padding = new Thickness(2, 1),
            }),
        };
        var panel = new DockPanel { Margin = new Thickness(2) };
        DockPanel.SetDock(_search, Avalonia.Controls.Dock.Top);
        panel.Children.Add(_search);
        panel.Children.Add(_list);
        _flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };

        _button = new Button
        {
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 2),
            MinHeight = 22,
            FontSize = 11,
            Flyout = _flyout,
        };
        ToolTip.SetTip(_button, "Font family");

        _flyout.Opened += (_, _) =>
        {
            _search.Text = "";
            Filter();
            _search.Focus();
        };
        _search.TextChanged += (_, _) => Filter();
        _search.KeyDown += OnSearchKey;
        _list.Tapped += (_, _) => Choose();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Choose();
        };
        Content = _button;
    }

    /// <summary>The chosen family (null or a "[Missing-Font]" name when the text has no single installed family).</summary>
    public string? Family { get => GetValue(FamilyProperty); set => SetValue(FamilyProperty, value); }

    /// <summary>The families to choose from.</summary>
    public IReadOnlyList<string>? Families { get => GetValue(FamiliesProperty); set => SetValue(FamiliesProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FamilyProperty) _label.Text = Family ?? "";
    }

    /// <summary>Opens or closes the family menu (as a click on the picker does), optionally typing a search; used by the self-test.</summary>
    internal void ShowMenu(bool open, string? search = null)
    {
        if (open) _flyout.ShowAt(_button);
        else _flyout.Hide();
        if (open && search is not null) _search.Text = search;
    }

    /// <summary>
    /// The family to draw a list row in. Recycling a row while the list is refilled (reopening the menu) builds the
    /// template once with no item, so a null name gets the default font.
    /// </summary>
    private FontFamily FontFor(string? name)
    {
        if (string.IsNullOrEmpty(name)) return FontFamily.Default;
        if (!_fonts.TryGetValue(name, out var font)) _fonts[name] = font = new FontFamily(name);
        return font;
    }

    private void Filter()
    {
        var all = Families ?? [];
        string q = _search.Text?.Trim() ?? "";
        var items = q.Length == 0 ? all : all.Where(f => f.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        _list.ItemsSource = items;
        _list.SelectedItem = q.Length == 0 && Family is { } current && items.Contains(current) ? current : items.FirstOrDefault();
        if (_list.SelectedItem is { } selected) _list.ScrollIntoView(selected);
    }

    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        var items = _list.ItemsSource as IReadOnlyList<string> ?? [];
        int index = _list.SelectedIndex;
        switch (e.Key)
        {
            case Key.Down when items.Count > 0:
                _list.SelectedIndex = Math.Min(items.Count - 1, index + 1);
                break;
            case Key.Up when items.Count > 0:
                _list.SelectedIndex = Math.Max(0, index - 1);
                break;
            case Key.Enter:
                Choose();
                break;
            case Key.Escape:
                _flyout.Hide();
                break;
            default:
                return;
        }
        if (_list.SelectedItem is { } selected) _list.ScrollIntoView(selected);
        e.Handled = true;
    }

    private void Choose()
    {
        if (_list.SelectedItem is string family) Family = family;
        _flyout.Hide();
    }
}
