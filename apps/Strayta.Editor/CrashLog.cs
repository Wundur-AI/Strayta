using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Strayta.Editor;

/// <summary>
/// Writes unhandled exceptions to a log file (~/Library/Logs/Strayta on macOS, the local app-data folder elsewhere),
/// so a crash leaves a stack trace behind even when the app wasn't started from a terminal or IDE.
/// </summary>
internal static class CrashLog
{
    public static string Folder { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "Strayta")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Strayta", "Logs");

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("Unhandled exception", e.ExceptionObject as Exception, fatal: e.IsTerminating);
        // A faulted fire-and-forget task doesn't crash the app, but it means something silently failed.
        TaskScheduler.UnobservedTaskException += (_, e) => Write("Unobserved task exception", e.Exception, fatal: false);
    }

    /// <summary>Also catches exceptions on the UI thread; call once the dispatcher exists (the app still crashes as before).</summary>
    public static void InstallDispatcherHook() =>
        Dispatcher.UIThread.UnhandledException += (_, e) => Write("Unhandled UI-thread exception", e.Exception, fatal: !e.Handled);

    private static void Write(string kind, Exception? exception, bool fatal)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, $"{(fatal ? "crash" : "error")}-{DateTime.Now:yyyy-MM-dd-HHmmss}.log");
            File.WriteAllText(path,
                $"{kind} at {DateTime.Now:O}\n" +
                $"Strayta {typeof(CrashLog).Assembly.GetName().Version} on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}), .NET {Environment.Version}\n\n" +
                $"{exception}\n");
            Console.Error.WriteLine($"{kind}; details in {path}");
        }
        catch
        {
            // Logging must never turn one crash into another.
        }
    }
}
