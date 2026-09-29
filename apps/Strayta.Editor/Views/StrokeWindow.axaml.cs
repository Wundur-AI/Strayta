using Avalonia.Controls;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

/// <summary>Edit › Stroke… over a <see cref="StrokeDialogViewModel"/>; closes with true for OK.</summary>
public partial class StrokeWindow : Window
{
    // Avalonia's XAML loader needs a parameterless constructor; the window is always opened with settings.
    public StrokeWindow() : this(null!)
    {
    }

    public StrokeWindow(StrokeDialogViewModel stroke)
    {
        InitializeComponent();
        DataContext = stroke;
        OkButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
    }
}
