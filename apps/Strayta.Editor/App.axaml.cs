using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Strayta.Editor.Views;

namespace Strayta.Editor;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Lets screenshots and tests start in a given appearance (View > Appearance changes it at runtime).
        if (Environment.GetEnvironmentVariable("STRAYTA_THEME") is { } theme && theme is "Light" or "Dark")
            RequestedThemeVariant = theme == "Light" ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _ = Task.Run(Warmup.Run);
            var window = new MainWindow();
            desktop.MainWindow = window;
            foreach (var path in desktop.Args ?? [])
                _ = window.Editor.OpenAsync(path);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
