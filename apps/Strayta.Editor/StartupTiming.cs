using System.Diagnostics;

namespace Strayta.Editor;

/// <summary>
/// STRAYTA_STARTUP_TIMING=1 prints how long after the process started each step of launching and opening a file
/// took (window shown, file read, first full render), for measuring cold-start work such as JIT compilation.
/// </summary>
internal static class StartupTiming
{
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("STRAYTA_STARTUP_TIMING") == "1";
    private static readonly DateTime Start = Process.GetCurrentProcess().StartTime;

    public static void Mark(string what)
    {
        if (Enabled) Console.WriteLine($"STARTUP {(DateTime.Now - Start).TotalMilliseconds,7:F0} ms  {what}");
    }
}
