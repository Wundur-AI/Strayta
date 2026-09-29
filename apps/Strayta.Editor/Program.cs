using Avalonia;
using Dock.Settings;

namespace Strayta.Editor;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        CrashLog.Install();
        BuildAvaloniaApp().AfterSetup(_ => CrashLog.InstallDispatcherHook()).StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // Size the drag preview to the dragged tab or panel (Theme.axaml draws it as a translucent outline).
            .ShowDockablePreviewOnDrag(true)
            .SetDragPreviewOpacity(1)
            // Floating panels belong to the main window, so they stay in front of it when you click it.
            .UseFloatingWindowOwnerPolicy(DockFloatingWindowOwnerPolicy.AlwaysOwned)
            .LogToTrace();
}
