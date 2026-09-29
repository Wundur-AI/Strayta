using Avalonia.Controls;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

/// <summary>Edit › Fill… over a <see cref="FillDialogViewModel"/>; closes with true for OK.</summary>
public partial class FillWindow : Window
{
    // Avalonia's XAML loader needs a parameterless constructor; the window is always opened with settings.
    public FillWindow() : this(null!)
    {
    }

    public FillWindow(FillDialogViewModel fill)
    {
        InitializeComponent();
        DataContext = fill;
        OkButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
    }
}
