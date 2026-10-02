using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
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
            _ = Strayta.Text.FontCatalog.WarmUp(); // index the installed fonts for type layers in the background
            StartupTiming.Mark("framework ready");
            var window = new MainWindow();
            window.Opened += (_, _) => StartupTiming.Mark("window shown");
            // Self-test and benchmark runs open a real window: keep it from taking the keyboard, so typing in another
            // app can't reach it (a stray Delete once removed a layer mid-test) and the person's work isn't interrupted.
            if (Environment.GetEnvironmentVariable("STRAYTA_SELFTEST") is { Length: > 0 } || StartupTiming.Enabled) window.ShowActivated = false;
            desktop.MainWindow = window;
            foreach (var path in desktop.Args ?? [])
                _ = window.Editor.OpenAsync(path);
            // Files opened from the Finder (double-click, Open With, dropped on the Dock icon) arrive as activations
            // on macOS, not as command-line arguments.
            if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
                activatable.Activated += (_, e) =>
                {
                    if (e is FileActivatedEventArgs files)
                        foreach (var item in files.Files)
                            if (item.TryGetLocalPath() is { } path) _ = window.Editor.OpenAsync(path);
                };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void OnAbout(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner }) _ = new AboutWindow().ShowDialog(owner);
    }
}
