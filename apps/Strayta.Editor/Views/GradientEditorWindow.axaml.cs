using Avalonia.Controls;
using Strayta.Core;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

/// <summary>The Gradient Editor dialog over a <see cref="GradientEditorViewModel"/>; closes with the edited gradient, or null on Cancel.</summary>
public partial class GradientEditorWindow : Window
{
    // Avalonia's XAML loader needs a parameterless constructor; the window is always opened with an editor.
    public GradientEditorWindow() : this(null!)
    {
    }

    public GradientEditorWindow(GradientEditorViewModel editor)
    {
        InitializeComponent();
        DataContext = editor;
        OkButton.Click += (_, _) => Close(editor.Gradient);
        CancelButton.Click += (_, _) => Close(null);
        Closed += (_, _) => editor?.Detach();
    }

    /// <summary>Opens the editor on <paramref name="initial"/> over <paramref name="owner"/>; the result keeps foreground/background stops as such.</summary>
    public static async Task<Gradient?> EditAsync(Window owner, Gradient initial, RgbColor foreground, RgbColor background) =>
        await new GradientEditorWindow(new GradientEditorViewModel(initial, foreground, background)).ShowDialog<Gradient?>(owner);
}
