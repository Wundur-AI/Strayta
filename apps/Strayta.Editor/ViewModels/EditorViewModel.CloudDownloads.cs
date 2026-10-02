using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Strayta.Editor.ViewModels;

/// <summary>A file being downloaded from cloud storage before it can open (shown over the canvas area).</summary>
public sealed partial class CloudDownload : ObservableObject
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public CloudDownload(string path)
    {
        Name = Path.GetFileName(path);
        try { Size = FormatSize(new FileInfo(path).Length); } catch (IOException) { Size = ""; }
    }

    public string Name { get; }
    public string Title { get; } = OperatingSystem.IsWindows() ? "Downloading from OneDrive…" : "Downloading from iCloud…";
    public string Size { get; }

    /// <summary>"8.8 MB · 12 s" (the size the placeholder reports, and how long it has been waiting).</summary>
    [ObservableProperty] public partial string Detail { get; set; } = "";

    internal void Tick() =>
        Detail = string.Join(" · ", new[] { Size, $"{(int)_clock.Elapsed.TotalSeconds} s" }.Where(s => s.Length > 0));

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };
}

public sealed partial class EditorViewModel
{
    /// <summary>Files waiting on an iCloud or OneDrive download (usually none; see CloudFiles).</summary>
    public ObservableCollection<CloudDownload> CloudDownloads { get; } = new();

    public bool HasCloudDownloads => CloudDownloads.Count > 0;

    private DispatcherTimer? _cloudTimer;

    /// <summary>
    /// Shows that <paramref name="path"/> is downloading until the returned handle is disposed. Quick downloads
    /// finish before the notice appears, so it doesn't flash.
    /// </summary>
    internal IDisposable ShowCloudDownload(string path, TimeSpan? delay = null)
    {
        var download = new CloudDownload(path);
        var done = false;
        DispatcherTimer.RunOnce(() =>
        {
            if (done) return;
            download.Tick();
            CloudDownloads.Add(download);
            OnPropertyChanged(nameof(HasCloudDownloads));
            _cloudTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
            {
                foreach (var d in CloudDownloads) d.Tick();
            });
            _cloudTimer.Start();
        }, delay ?? TimeSpan.FromMilliseconds(400));
        return new Finish(() =>
        {
            done = true;
            if (!CloudDownloads.Remove(download)) return;
            OnPropertyChanged(nameof(HasCloudDownloads));
            if (CloudDownloads.Count == 0) _cloudTimer?.Stop();
        });
    }

    private sealed class Finish(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
