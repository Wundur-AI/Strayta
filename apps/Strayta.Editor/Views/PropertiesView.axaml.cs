using Avalonia.Controls;
using Avalonia.Interactivity;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

public partial class PropertiesView : UserControl
{
    public PropertiesView() => InitializeComponent();

    private void OnResetCurve(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CurvesPanel panel }) panel.Reset();
    }

    private async void OnAutoLevels(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: LevelsPanel panel }) await panel.AutoAsync();
    }

    private async void OnAutoCurves(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CurvesPanel panel }) await panel.AutoAsync();
    }
}
