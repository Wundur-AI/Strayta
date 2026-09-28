using Avalonia.Controls;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

/// <summary>
/// Layer › Layer Style: a modal dialog over one <see cref="LayerStyleViewModel"/> session. The canvas behind it shows
/// every change live; the window closes with true for OK (Enter) and false for Cancel (Esc or closing it).
/// </summary>
public partial class LayerStyleWindow : Window
{
    // Avalonia's XAML loader needs a parameterless constructor; the window is always opened with a session.
    public LayerStyleWindow() : this(null!)
    {
    }

    public LayerStyleWindow(LayerStyleViewModel session)
    {
        InitializeComponent();
        DataContext = session;
        OkButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
    }
}
