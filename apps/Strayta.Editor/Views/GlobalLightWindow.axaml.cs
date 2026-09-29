using Avalonia.Controls;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

/// <summary>Layer › Layer Style › Global Light…: closes with true for OK, false for Cancel; the canvas previews live.</summary>
public partial class GlobalLightWindow : Window
{
    // Avalonia's XAML loader needs a parameterless constructor; the window is always opened with a session.
    public GlobalLightWindow() : this(null!)
    {
    }

    public GlobalLightWindow(GlobalLightViewModel session)
    {
        InitializeComponent();
        DataContext = session;
        OkButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
    }
}
