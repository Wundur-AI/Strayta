using System.Diagnostics;
using System.Numerics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;

namespace Strayta.Editor.ViewModels;

/// <summary>How Select and Mask shows the refined selection (Photoshop's View Mode, the modes Strayta has).</summary>
public enum RefineView
{
    /// <summary>The unselected area tinted red at 50%, like Quick Mask.</summary>
    Overlay,
    OnBlack,
    OnWhite,
    /// <summary>The selection itself: white selected, black not.</summary>
    BlackAndWhite,
}

/// <summary>
/// One Select and Mask session: the settings, the Refine Edge brush, and a live preview of the refined selection over
/// the image. Nothing touches the document until the session is applied.
/// </summary>
/// <remarks>
/// Like the main canvas, the preview runs two lanes. The live lane refines a copy of the image scaled to the view
/// (one image pixel per device pixel or so), so every slider step or brush dab answers within a frame or two, and it
/// never queues: changes that arrive while it works are folded into one next run. The full lane refines at full
/// resolution once changes pause for a moment, is cancelled by the next change, and replaces the preview only if
/// nothing changed meanwhile. Zoomed in to 100% or more the live lane itself works at full resolution. Both lanes
/// draw the display image into its bitmap in the background; the UI thread only swaps it in.
/// </remarks>
public sealed partial class SelectAndMaskViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan FullDelay = TimeSpan.FromMilliseconds(150);

    private readonly byte[] _render;          // the document, straight RGBA, full resolution
    private readonly SampleImage _sample;     // the same, premultiplied, for edge detection
    private readonly SelectionMask? _selection;
    private readonly RefineBrushMask _brush;
    private readonly List<(Vector2 From, Vector2 To, float Diameter, bool Erase)> _strokes = [];
    private readonly Task<Level> _full;
    private Task<Level>? _preview;
    private int _previewFactor = 1;
    private int _version, _shownVersion = -1;
    private bool _previewRunning, _previewPending, _disposed;
    private CancellationTokenSource? _fullCancel;
    private Vector2 _lastBrush;
    private bool _brushErase;
    private readonly Stopwatch _sinceChange = new();

    /// <summary>Everything needed to refine and display at one scale (1 = full resolution).</summary>
    private sealed class Level
    {
        public required int Factor { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required byte[] Rgba { get; init; }
        public required SelectionRefiner Refiner { get; init; }
        public required RefineBrushMask Brush { get; init; }

        /// <summary>How many of the session's recorded strokes this level's brush mask holds.</summary>
        public int Strokes { get; set; }
    }

    public SelectAndMaskViewModel(DocumentViewModel document, byte[] render, SampleImage sample, SelectionMask? selection)
    {
        Document = document;
        _render = render;
        _sample = sample;
        _selection = selection;
        _brush = new RefineBrushMask(sample.Width, sample.Height);
        DocumentSize = new Avalonia.PixelSize(sample.Width, sample.Height);
        _full = Task.Run(() => new Level
        {
            Factor = 1, Width = sample.Width, Height = sample.Height, Rgba = render, Refiner = new SelectionRefiner(sample, selection), Brush = _brush,
        });
        Update();
    }

    public DocumentViewModel Document { get; }
    public Avalonia.PixelSize DocumentSize { get; }

    [ObservableProperty] public partial RefineView View { get; set; } = RefineView.Overlay;
    [ObservableProperty] public partial double Radius { get; set; }
    [ObservableProperty] public partial double Smooth { get; set; }
    [ObservableProperty] public partial double Feather { get; set; }
    [ObservableProperty] public partial double Contrast { get; set; }
    [ObservableProperty] public partial double ShiftEdge { get; set; }
    [ObservableProperty] public partial RefineOutput Output { get; set; } = RefineOutput.Selection;

    /// <summary>Refine Edge brush diameter in image pixels.</summary>
    [ObservableProperty] public partial double BrushSize { get; set; } = 60;

    /// <summary>True for the Refine Edge brush, false for the Hand.</summary>
    [ObservableProperty] public partial bool IsBrushTool { get; set; } = true;

    /// <summary>What the canvas shows: the image with the refined selection, at preview or full resolution.</summary>
    [ObservableProperty] public partial Bitmap? Preview { get; private set; }

    /// <summary>Resolution and timing of the image on screen, for the panel's footer.</summary>
    [ObservableProperty] public partial string Info { get; private set; } = "";

    public CanvasTool CanvasTool => IsBrushTool ? CanvasTool.Brush : CanvasTool.Hand;

    public bool IsHandTool { get => !IsBrushTool; set => IsBrushTool = !value; }

    partial void OnIsBrushToolChanged(bool value)
    {
        OnPropertyChanged(nameof(CanvasTool));
        OnPropertyChanged(nameof(IsHandTool));
    }

    // The panel's drop-downs pick by position, in the enums' order.
    public int ViewIndex { get => (int)View; set => View = (RefineView)value; }
    public int OutputIndex { get => (int)Output; set => Output = (RefineOutput)value; }

    /// <summary>`[` and `]`: the Refine Edge brush one step smaller or larger, like the painting tools.</summary>
    public void ResizeBrush(int direction)
    {
        double step = BrushSize < 10 ? 1 : BrushSize < 50 ? 5 : BrushSize < 100 ? 10 : 25;
        BrushSize = Math.Clamp(BrushSize + direction * step, 1, 1000);
    }

    public RefineSettings Settings => new((float)Radius, (float)Smooth, (float)Feather, (float)Contrast, (float)ShiftEdge);

    partial void OnViewChanged(RefineView value)
    {
        OnPropertyChanged(nameof(ViewIndex));
        Update();
    }

    partial void OnOutputChanged(RefineOutput value) => OnPropertyChanged(nameof(OutputIndex));
    partial void OnRadiusChanged(double value) => Update();
    partial void OnSmoothChanged(double value) => Update();
    partial void OnFeatherChanged(double value) => Update();
    partial void OnContrastChanged(double value) => Update();
    partial void OnShiftEdgeChanged(double value) => Update();

    // ---- Timings (self-test and benchmark) ------------------------------------------------------------

    /// <summary>Milliseconds from the last change to its preview on screen, and to the full-resolution image.</summary>
    public double LastPreviewLatencyMs { get; private set; }
    public double LastFullLatencyMs { get; private set; }

    /// <summary>The longest the UI thread spent handing a finished image to the canvas.</summary>
    public double MaxUiMs { get; private set; }

    /// <summary>True when the image on screen is the full-resolution result of the current settings.</summary>
    public bool IsFullShown { get; private set; }

    /// <summary>Raised on the UI thread whenever a new image is shown.</summary>
    public event Action? PreviewShown;

    // ---- View scale -----------------------------------------------------------------------------------

    /// <summary>
    /// The canvas zoom in device pixels per image pixel. Picks the preview lane's scale: the largest power of two
    /// (up to 8) that still gives at least one preview pixel per device pixel.
    /// </summary>
    public void SetViewScale(double devicePixelsPerImagePixel)
    {
        int factor = 1;
        while (factor < 8 && devicePixelsPerImagePixel * factor * 2 <= 1.0001) factor *= 2;
        if (factor == _previewFactor) return;
        _previewFactor = factor;
        _preview = null;
        Update();
    }

    private Task<Level> PreviewLevel()
    {
        if (_previewFactor == 1) return _full;
        if (_preview is { } p) return p;
        int f = _previewFactor;
        var strokes = _strokes.ToList();
        return _preview = Task.Run(() => BuildLevel(f, strokes));
    }

    /// <summary>A reduced copy of the image, selection and brush strokes: block averages of <paramref name="f"/>×<paramref name="f"/> pixels.</summary>
    private Level BuildLevel(int f, List<(Vector2 From, Vector2 To, float Diameter, bool Erase)> strokes)
    {
        int w = _sample.Width, h = _sample.Height, lw = (w + f - 1) / f, lh = (h + f - 1) / f;
        var straight = Reduce(_render, w, h, f, 4);
        var premultiplied = Reduce(_sample.Rgba, w, h, f, 4);
        var coverage = new byte[(long)w * h];
        if (_selection is { } s) Parallel.For(0, h, y => s.CopyRow(y, 0, coverage.AsSpan(y * w, w)));
        var reducedSelection = SelectionMask.FromCoverage(PixelRect.FromSize(lw, lh), Reduce(coverage, w, h, f, 1));
        var brush = new RefineBrushMask(lw, lh);
        foreach (var (from, to, d, erase) in strokes) brush.Paint(from / f, to / f, d / f, erase);
        return new Level
        {
            Factor = f, Width = lw, Height = lh, Rgba = straight,
            Refiner = new SelectionRefiner(new SampleImage(lw, lh, premultiplied), reducedSelection), Brush = brush, Strokes = strokes.Count,
        };
    }

    private static byte[] Reduce(byte[] src, int w, int h, int f, int channels)
    {
        int lw = (w + f - 1) / f, lh = (h + f - 1) / f;
        var dst = new byte[(long)lw * lh * channels];
        Parallel.For(0, lh, ly =>
        {
            Span<int> sum = stackalloc int[channels];
            int y0 = ly * f, y1 = Math.Min(y0 + f, h);
            for (int lx = 0; lx < lw; lx++)
            {
                int x0 = lx * f, x1 = Math.Min(x0 + f, w);
                sum.Clear();
                for (int y = y0; y < y1; y++)
                    for (int x = x0, i = (y * w + x0) * channels; x < x1; x++)
                        for (int c = 0; c < channels; c++, i++) sum[c] += src[i];
                int n = (y1 - y0) * (x1 - x0), o = (ly * lw + lx) * channels;
                for (int c = 0; c < channels; c++) dst[o + c] = (byte)((sum[c] + n / 2) / n);
            }
        });
        return dst;
    }

    // ---- Refine Edge brush -----------------------------------------------------------------------------

    /// <summary>Starts a Refine Edge stroke at an image position; Option (<paramref name="erase"/>) erases refinements.</summary>
    public bool BeginBrush(float x, float y, bool erase)
    {
        _brushErase = erase;
        _lastBrush = new Vector2(x, y);
        PaintBrush(_lastBrush, _lastBrush);
        return true;
    }

    public void ContinueBrush(float x, float y)
    {
        var p = new Vector2(x, y);
        PaintBrush(_lastBrush, p);
        _lastBrush = p;
    }

    public void EndBrush()
    {
    }

    /// <summary>
    /// Stamps the segment into the full-resolution mask and records it; the preview's reduced mask catches up when
    /// the preview lane next runs. Masks are only ever changed on the UI thread; the lanes refine from snapshots.
    /// </summary>
    private void PaintBrush(Vector2 from, Vector2 to)
    {
        float d = (float)BrushSize;
        _strokes.Add((from, to, d, _brushErase));
        _brush.Paint(from, to, d, _brushErase);
        Update();
    }

    /// <summary>Paints the strokes recorded since a reduced level last caught up (or was built) into its mask.</summary>
    private void CatchUp(Level level)
    {
        if (level.Factor == 1) return; // the full-resolution mask is painted directly
        for (; level.Strokes < _strokes.Count; level.Strokes++)
        {
            var (from, to, d, erase) = _strokes[level.Strokes];
            level.Brush.Paint(from / level.Factor, to / level.Factor, d / level.Factor, erase);
        }
    }

    // ---- Lanes -----------------------------------------------------------------------------------------

    /// <summary>
    /// Something changed: refresh the preview now, and the full-resolution image after a pause. When the view is
    /// zoomed in far enough that the preview is the full resolution, the live lane refines at full resolution
    /// directly (never cancelled, so a continuous drag still shows every result it can finish).
    /// </summary>
    private void Update()
    {
        if (_disposed) return;
        _version++;
        _sinceChange.Restart();
        IsFullShown = false;
        _fullCancel?.Cancel();
        _fullCancel = null;
        if (_previewFactor > 1)
        {
            _fullCancel = new CancellationTokenSource();
            _ = RunFullAfterPauseAsync(_version, _fullCancel.Token);
        }
        _ = RunLiveLaneAsync();
    }

    /// <summary>The live lane: at most one refine at a time; changes meanwhile fold into one more run with the latest state.</summary>
    private async Task RunLiveLaneAsync()
    {
        if (_previewRunning)
        {
            _previewPending = true;
            return;
        }
        _previewRunning = true;
        try
        {
            do
            {
                _previewPending = false;
                int version = _version;
                var level = await PreviewLevel();
                if (_disposed) return;
                CatchUp(level);
                var job = Job(level);
                var image = await Task.Run(() => Compose(level, job, CancellationToken.None));
                bool full = level.Factor == 1 && version == _version;
                if (!_disposed && (version > _shownVersion || full)) Show(image, level, version, full);
                else image.Bitmap.Dispose();
            } while (_previewPending);
        }
        finally
        {
            _previewRunning = false;
        }
    }

    /// <summary>The full-resolution lane behind a reduced preview: starts once changes pause, cancelled by the next change.</summary>
    private async Task RunFullAfterPauseAsync(int version, CancellationToken cancel)
    {
        try
        {
            await Task.Delay(FullDelay, cancel);
            var level = await _full;
            if (cancel.IsCancellationRequested) return;
            var job = Job(level);
            var image = await Task.Run(() => Compose(level, job, cancel), cancel);
            if (_disposed || cancel.IsCancellationRequested || version != _version)
            {
                image.Bitmap.Dispose();
                return;
            }
            Show(image, level, version, full: true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>What a lane refines, captured on the UI thread: settings scaled to the level, a brush snapshot, the view.</summary>
    private (RefineSettings Settings, RefineBrushSnapshot? Brush, RefineView View) Job(Level level) =>
        (Settings.Scaled(1f / level.Factor), level.Brush.Snapshot(), View);

    private sealed record Composed(WriteableBitmap Bitmap, double ComputeMs);

    /// <summary>Refines and draws the result straight into a new bitmap, all off the UI thread.</summary>
    private static Composed Compose(Level level, (RefineSettings Settings, RefineBrushSnapshot? Brush, RefineView View) job, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        var matte = level.Refiner.Refine(job.Settings, job.Brush, cancel);
        cancel.ThrowIfCancellationRequested();
        var bitmap = new WriteableBitmap(new Avalonia.PixelSize(level.Width, level.Height), new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Unpremul);
        using (var fb = bitmap.Lock())
            ComposeView(level.Rgba, matte, level.Width, level.Height, job.View, fb.Address, fb.RowBytes);
        return new Composed(bitmap, clock.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Draws the display image for a view mode (straight RGBA rows at <paramref name="address"/>) from the document's
    /// pixels and the refined coverage.
    /// </summary>
    private static unsafe void ComposeView(byte[] rgba, byte[] matte, int w, int h, RefineView view, IntPtr address, int rowBytes)
    {
        Parallel.For(0, h, y =>
        {
            var dst = new Span<byte>((byte*)address + (long)y * rowBytes, w * 4);
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x, p = i * 4, d = x * 4;
                int m = matte[i], ia = rgba[p + 3];
                switch (view)
                {
                    case RefineView.Overlay:
                    {
                        // Red at 50% over the unselected part, composited over the image (which may be transparent).
                        float o = (255 - m) / 510f, under = ia / 255f * (1 - o), a = o + under;
                        if (a <= 0)
                        {
                            dst.Slice(d, 4).Clear();
                            break;
                        }
                        dst[d] = MaskByte((255 * o + rgba[p] * under) / a);
                        dst[d + 1] = MaskByte(rgba[p + 1] * under / a);
                        dst[d + 2] = MaskByte(rgba[p + 2] * under / a);
                        dst[d + 3] = MaskByte(a * 255);
                        break;
                    }
                    case RefineView.OnBlack:
                    case RefineView.OnWhite:
                    {
                        int k = ia * m / 255, back = view == RefineView.OnWhite ? 255 : 0;
                        for (int c = 0; c < 3; c++) dst[d + c] = (byte)((rgba[p + c] * k + back * (255 - k)) / 255);
                        dst[d + 3] = 255;
                        break;
                    }
                    default:
                        dst[d] = dst[d + 1] = dst[d + 2] = (byte)m;
                        dst[d + 3] = 255;
                        break;
                }
            }
        });
    }

    private static byte MaskByte(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);

    private void Show(Composed image, Level level, int version, bool full)
    {
        var clock = Stopwatch.StartNew();
        var old = Preview;
        Preview = image.Bitmap;
        old?.Dispose();
        MaxUiMs = Math.Max(MaxUiMs, clock.Elapsed.TotalMilliseconds);
        _shownVersion = version;
        IsFullShown = full;
        double latency = _sinceChange.Elapsed.TotalMilliseconds;
        if (full) LastFullLatencyMs = latency;
        else LastPreviewLatencyMs = latency;
        Info = full
            ? $"{level.Width}×{level.Height} · {image.ComputeMs:0} ms"
            : $"Preview 1/{level.Factor} · {image.ComputeMs:0} ms";
        PreviewShown?.Invoke();
    }

    // ---- Result -----------------------------------------------------------------------------------------

    /// <summary>The refined selection at full resolution with the current settings and brush strokes.</summary>
    public async Task<SelectionMask?> ResultAsync()
    {
        var level = await _full;
        var settings = Settings;
        var brush = _brush.Snapshot();
        return await Task.Run(() => level.Refiner.RefineSelection(settings, brush));
    }

    /// <summary>Waits until the full-resolution image of the current settings is on screen (for tests and benchmarks).</summary>
    public async Task WaitForFullAsync(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!IsFullShown && clock.Elapsed < timeout)
            await Task.Delay(10);
    }

    public void Dispose()
    {
        _disposed = true;
        _fullCancel?.Cancel();
        Dispatcher.UIThread.Post(() =>
        {
            Preview?.Dispose();
            Preview = null;
        });
    }
}
