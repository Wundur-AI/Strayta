using Avalonia.Controls;
using Avalonia.Interactivity;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

/// <summary>File › Export › Export As over an <see cref="ExportAsViewModel"/>; closes with true when Export… was clicked.</summary>
public partial class ExportAsWindow : Window
{
    // Avalonia's XAML loader needs a parameterless constructor; the window is always opened with a model.
    public ExportAsWindow() : this(null!)
    {
    }

    public ExportAsWindow(ExportAsViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        ExportButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
        AddScaleButton.Click += (_, _) => model.AddScale();
        ResetCanvasButton.Click += (_, _) =>
        {
            model.CanvasWidth = 0;
            model.CanvasHeight = 0;
        };
        Opened += async (_, _) => await model.RenderSelectedAsync();
    }

    private void OnRemoveScale(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ExportScaleRow row } && DataContext is ExportAsViewModel model) model.RemoveScale(row);
    }
}
