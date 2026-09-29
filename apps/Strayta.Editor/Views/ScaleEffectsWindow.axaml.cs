using Avalonia.Controls;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

/// <summary>Layer › Layer Style › Scale Effects…: closes with true for OK, false for Cancel; the canvas previews live.</summary>
public partial class ScaleEffectsWindow : Window
{
    // Avalonia's XAML loader needs a parameterless constructor; the window is always opened with a session.
    public ScaleEffectsWindow() : this(null!)
    {
    }

    public ScaleEffectsWindow(ScaleEffectsViewModel session)
    {
        InitializeComponent();
        DataContext = session;
        OkButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
    }
}
