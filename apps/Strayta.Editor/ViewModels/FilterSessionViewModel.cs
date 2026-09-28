using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strayta.Core;
using Strayta.Editor.Controls;
using Strayta.Rendering;
using Strayta.Rendering.Filters;

namespace Strayta.Editor.ViewModels;

/// <summary>The filters of the Filter menu.</summary>
public enum FilterKind
{
    GaussianBlur,
    MotionBlur,
    BoxBlur,
    UnsharpMask,
    AddNoise,
    HighPass,
}

/// <summary>
/// One filter dialog: the filter's settings, the canvas preview (the Preview checkbox) and the dialog's own small
/// preview, which shows the target at 100% (or another zoom) around a point that dragging moves. Nothing touches the
/// document until OK (<see cref="EditorViewModel"/> applies <see cref="Filter"/>).
/// </summary>
public sealed partial class FilterSessionViewModel : ObservableObject, IDisposable
{
    /// <summary>The dialog preview's zoom steps, as in Photoshop's − and + buttons.</summary>
    private static readonly double[] ZoomSteps = [0.0625, 0.125, 0.25, 0.5, 1, 2, 3, 4, 8];

    /// <summary>Side of the dialog's preview square, in points.</summary>
    public const int PreviewSize = 280;

    private readonly DocumentViewModel _document;
    private readonly FilterTarget _target;
    private readonly ImageFilter _initial;
    private readonly Dictionary<int, PreviewDocument> _proxies = [];
    private CancellationTokenSource? _previewCancel;
    private bool _loading, _panning, _disposed;
    private int _zoomIndex = 4;
    private (double X, double Y) _center;

    public FilterSessionViewModel(DocumentViewModel document, FilterKind kind, ImageFilter initial)
    {
        _document = document;
        _target = document.OpenFilter ?? throw new InvalidOperationException("Begin the filter on the document first.");
        Kind = kind;
        _initial = initial;
        Load(initial);
        _center = StartCenter();
        Changed();
    }

    public FilterKind Kind { get; }

    /// <summary>The dialog's title, Photoshop's name for the filter.</summary>
    public string Title => DefaultFilter(Kind).Name;

    // ---- Settings (Photoshop's ranges) -----------------------------------------------------------------

    [ObservableProperty] public partial double Radius { get; set; }
    [ObservableProperty] public partial double Angle { get; set; }
    [ObservableProperty] public partial double Distance { get; set; }
    [ObservableProperty] public partial double Amount { get; set; }
    [ObservableProperty] public partial double Threshold { get; set; }
    [ObservableProperty] public partial bool GaussianNoise { get; set; }
    [ObservableProperty] public partial bool Monochromatic { get; set; }

    /// <summary>The Preview checkbox: show the filter on the canvas.</summary>
    [ObservableProperty] public partial bool Preview { get; set; } = true;

    /// <summary>The noise pattern: fixed for the dialog, so what is previewed is what OK applies.</summary>
    public int Seed { get; private set; }

    public bool HasRadius => Kind is FilterKind.GaussianBlur or FilterKind.BoxBlur or FilterKind.UnsharpMask or FilterKind.HighPass;
    public bool HasAngle => Kind == FilterKind.MotionBlur;
    public bool HasDistance => Kind == FilterKind.MotionBlur;
    public bool HasAmount => Kind is FilterKind.UnsharpMask or FilterKind.AddNoise;
    public bool HasThreshold => Kind == FilterKind.UnsharpMask;
    public bool HasNoiseOptions => Kind == FilterKind.AddNoise;
    public bool UniformNoise { get => !GaussianNoise; set => GaussianNoise = !value; }

    public double RadiusMinimum => Kind == FilterKind.BoxBlur ? BoxBlurFilter.MinRadius : GaussianBlurFilter.MinRadius;
    public double RadiusMaximum => Kind == FilterKind.BoxBlur ? BoxBlurFilter.MaxRadius : GaussianBlurFilter.MaxRadius;
    public string RadiusFormat => Kind == FilterKind.BoxBlur ? "0" : "0.0";
    public double AmountMinimum => Kind == FilterKind.AddNoise ? AddNoiseFilter.MinAmount : UnsharpMaskFilter.MinAmount;
    public double AmountMaximum => Kind == FilterKind.AddNoise ? AddNoiseFilter.MaxAmount : UnsharpMaskFilter.MaxAmount;
    public string AmountFormat => Kind == FilterKind.AddNoise ? "0.0" : "0";

    /// <summary>The filter the dialog's settings describe.</summary>
    public ImageFilter Filter => Kind switch
    {
        FilterKind.GaussianBlur => new GaussianBlurFilter(Radius),
        FilterKind.BoxBlur => new BoxBlurFilter(Radius),
        FilterKind.MotionBlur => new MotionBlurFilter(Angle, Distance),
        FilterKind.UnsharpMask => new UnsharpMaskFilter(Amount, Radius, (int)Threshold),
        FilterKind.AddNoise => new AddNoiseFilter(Amount, GaussianNoise ? NoiseDistribution.Gaussian : NoiseDistribution.Uniform, Monochromatic, Seed),
        _ => new HighPassFilter(Radius),
    };

    /// <summary>Photoshop's starting settings for each filter.</summary>
    public static ImageFilter DefaultFilter(FilterKind kind) => kind switch
    {
        FilterKind.GaussianBlur => new GaussianBlurFilter(1),
        FilterKind.BoxBlur => new BoxBlurFilter(10),
        FilterKind.MotionBlur => new MotionBlurFilter(0, 10),
        FilterKind.UnsharpMask => new UnsharpMaskFilter(50, 1, 0),
        FilterKind.AddNoise => new AddNoiseFilter(12.5, NoiseDistribution.Uniform, false, Random.Shared.Next()),
        _ => new HighPassFilter(10),
    };

    /// <summary>The filter kind of a filter value.</summary>
    public static FilterKind KindOf(ImageFilter filter) => filter switch
    {
        GaussianBlurFilter => FilterKind.GaussianBlur,
        BoxBlurFilter => FilterKind.BoxBlur,
        MotionBlurFilter => FilterKind.MotionBlur,
        UnsharpMaskFilter => FilterKind.UnsharpMask,
        AddNoiseFilter => FilterKind.AddNoise,
        _ => FilterKind.HighPass,
    };

    /// <summary>Option-click on Cancel (Reset): back to the settings the dialog opened with.</summary>
    public void Reset() => Load(_initial);

    private void Load(ImageFilter filter)
    {
        _loading = true;
        try
        {
            switch (filter)
            {
                case GaussianBlurFilter g: Radius = g.Radius; break;
                case BoxBlurFilter b: Radius = b.Radius; break;
                case HighPassFilter p: Radius = p.Radius; break;
                case MotionBlurFilter m: (Angle, Distance) = (m.Angle, m.Distance); break;
                case UnsharpMaskFilter u: (Amount, Radius, Threshold) = (u.Amount, u.Radius, u.Threshold); break;
                case AddNoiseFilter n: (Amount, GaussianNoise, Monochromatic, Seed) = (n.Amount, n.Distribution == NoiseDistribution.Gaussian, n.Monochromatic, n.Seed); break;
            }
        }
        finally
        {
            _loading = false;
        }
        Changed();
    }

    // Values are kept on Photoshop's steps (tenths of a pixel, whole degrees and levels) and in range, whatever the
    // slider or a typed value produced.
    partial void OnRadiusChanged(double value) => Snap(value, RadiusMinimum, RadiusMaximum, Kind == FilterKind.BoxBlur ? 0 : 1, v => Radius = v);
    partial void OnAngleChanged(double value) => Snap(value, -360, 360, 0, v => Angle = v);
    partial void OnDistanceChanged(double value) => Snap(value, MotionBlurFilter.MinDistance, MotionBlurFilter.MaxDistance, 0, v => Distance = v);
    partial void OnAmountChanged(double value) => Snap(value, AmountMinimum, AmountMaximum, Kind == FilterKind.AddNoise ? 1 : 0, v => Amount = v);
    partial void OnThresholdChanged(double value) => Snap(value, 0, 255, 0, v => Threshold = v);

    partial void OnGaussianNoiseChanged(bool value)
    {
        OnPropertyChanged(nameof(UniformNoise));
        Changed();
    }

    partial void OnMonochromaticChanged(bool value) => Changed();
    partial void OnPreviewChanged(bool value) => Changed();

    private void Snap(double value, double min, double max, int decimals, Action<double> set)
    {
        double snapped = Math.Round(Math.Clamp(value, min, max), decimals);
        if (snapped != value) set(snapped);
        else Changed();
    }

    private void Changed()
    {
        if (_loading || _disposed) return;
        _document.PreviewFilter(Preview ? Filter : null);
        RefreshPreview();
    }

    // ---- The dialog's preview -------------------------------------------------------------------------

    /// <summary>The dialog's preview: the target around the preview point, filtered, over a checkerboard.</summary>
    [ObservableProperty] public partial Bitmap? PreviewImage { get; private set; }

    /// <summary>Size to draw <see cref="PreviewImage"/> at, in points (zoomed in, each image pixel is several points).</summary>
    [ObservableProperty] public partial double PreviewWidth { get; private set; }
    [ObservableProperty] public partial double PreviewHeight { get; private set; }

    public string ZoomText => $"{ZoomSteps[_zoomIndex] * 100:0.##}%";
    public double Zoom => ZoomSteps[_zoomIndex];
    public bool CanZoomIn => _zoomIndex < ZoomSteps.Length - 1;
    public bool CanZoomOut => _zoomIndex > 0;

    /// <summary>Raised when the dialog preview shows a new image, with the milliseconds since its settings changed.</summary>
    public event Action<double>? PreviewShown;

    [RelayCommand]
    private void ZoomIn() => SetZoomIndex(_zoomIndex + 1);

    [RelayCommand]
    private void ZoomOut() => SetZoomIndex(_zoomIndex - 1);

    private void SetZoomIndex(int index)
    {
        index = Math.Clamp(index, 0, ZoomSteps.Length - 1);
        if (index == _zoomIndex) return;
        _zoomIndex = index;
        OnPropertyChanged(nameof(ZoomText));
        OnPropertyChanged(nameof(Zoom));
        OnPropertyChanged(nameof(CanZoomIn));
        OnPropertyChanged(nameof(CanZoomOut));
        RefreshPreview();
    }

    /// <summary>The document point at the middle of the dialog preview.</summary>
    public (double X, double Y) PreviewCenter => _center;

    /// <summary>A drag in the dialog preview starts: like Photoshop it shows the unfiltered image until released.</summary>
    public void BeginPan()
    {
        _panning = true;
        RefreshPreview();
    }

    /// <summary>The preview was dragged by (<paramref name="dx"/>, <paramref name="dy"/>) points: the image follows the pointer.</summary>
    public void PanBy(double dx, double dy)
    {
        var m = _document.Model;
        _center = (Math.Clamp(_center.X - dx / Zoom, 0, m.Width), Math.Clamp(_center.Y - dy / Zoom, 0, m.Height));
        RefreshPreview();
    }

    public void EndPan()
    {
        _panning = false;
        RefreshPreview();
    }

    /// <summary>Centers the preview on a document point (clicking the canvas in Photoshop does this).</summary>
    public void CenterOn(double x, double y)
    {
        var m = _document.Model;
        _center = (Math.Clamp(x, 0, m.Width), Math.Clamp(y, 0, m.Height));
        RefreshPreview();
    }

    /// <summary>Where the preview starts: the middle of the selection, else of the target's pixels on the canvas.</summary>
    private (double X, double Y) StartCenter()
    {
        var canvas = _document.Model.Bounds;
        var area = _target.Selection?.Bounds
                   ?? (_target.Mask ? canvas : ((PixelLayer)_target.Owner).Bounds.Intersect(canvas));
        if (area.IsEmpty) area = canvas;
        return ((area.Left + area.Right) / 2.0, (area.Top + area.Bottom) / 2.0);
    }

    private async void RefreshPreview()
    {
        if (_disposed) return;
        _previewCancel?.Cancel();
        var cts = _previewCancel = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        var model = _document.Model;
        double zoom = Zoom;
        int factor = zoom < 1 ? (int)Math.Round(1 / zoom) : 1;

        // Everything the background work reads is gathered here, on the UI thread.
        LayerNode? source = factor == 1 ? _target.Owner : Proxy(factor).ProxyOf(_target.Owner);
        if (source is null) return;
        var canvas = factor == 1 ? model.Bounds : Proxy(factor).Proxy.Bounds;
        int span = zoom >= 1 ? (int)Math.Ceiling(PreviewSize / zoom) : PreviewSize;
        var area = Centered(_center.X / factor, _center.Y / factor, span, canvas);
        var filter = _panning ? null : factor == 1 ? Filter : Filter.Scaled(1.0 / factor);
        var scope = new FilterScope(canvas) { Selection = _target.Selection, Factor = factor, PreserveTransparency = _target.LockTransparency };
        var mask = _target.Mask ? source.GetMask() : null;
        var (pixels, bounds) = source is PixelLayer p ? (p.Pixels, p.Bounds) : (null, PixelRect.Empty);
        var (mode, depth) = (model.ColorMode, model.BitDepth);
        bool isMask = _target.Mask;

        try
        {
            var rgba = await Task.Run(() =>
            {
                if (isMask)
                {
                    var plane = FilterEngine.PreviewMask(mask!, filter, scope, area, depth, cts.Token);
                    return GrayToRgba(plane);
                }
                var raster = FilterEngine.PreviewLayer(pixels, bounds, filter, scope, area, mode, depth, cts.Token);
                return OverChecker(RgbaConverter.ToRgba8(raster), area.Width, area.Height);
            }, cts.Token);
            if (cts.IsCancellationRequested || _disposed) return;
            PreviewImage = BitmapFactory.FromRgba(rgba, area.Width, area.Height);
            PreviewWidth = area.Width * Math.Max(zoom, 1);
            PreviewHeight = area.Height * Math.Max(zoom, 1);
            PreviewShown?.Invoke(clock.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A reduced copy of the document for zoomed-out previews (synced once: the document cannot change meanwhile).</summary>
    private PreviewDocument Proxy(int factor)
    {
        if (!_proxies.TryGetValue(factor, out var proxy))
        {
            proxy = _proxies[factor] = new PreviewDocument(_document.Model, factor);
            proxy.Sync();
        }
        return proxy;
    }

    /// <summary>A square of <paramref name="span"/> pixels centered on a point, kept inside the canvas where it fits.</summary>
    private static PixelRect Centered(double cx, double cy, int span, PixelRect canvas)
    {
        int w = Math.Min(span, canvas.Width), h = Math.Min(span, canvas.Height);
        int left = Math.Clamp((int)Math.Round(cx - w / 2.0), canvas.Left, canvas.Right - w);
        int top = Math.Clamp((int)Math.Round(cy - h / 2.0), canvas.Top, canvas.Bottom - h);
        return new PixelRect(left, top, left + w, top + h);
    }

    private static byte[] GrayToRgba(Plane plane)
    {
        int n = plane.Width * plane.Height;
        var rgba = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            byte v = RgbaConverter.ToByte(plane.GetNormalized(i));
            rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = v;
            rgba[i * 4 + 3] = 255;
        }
        return rgba;
    }

    /// <summary>Composites straight RGBA over an 8-pixel gray checkerboard, as Photoshop shows transparency.</summary>
    private static byte[] OverChecker(byte[] rgba, int w, int h)
    {
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                int a = rgba[i + 3];
                if (a == 255) continue;
                int bg = ((x >> 3) ^ (y >> 3)) % 2 == 0 ? 255 : 204;
                for (int c = 0; c < 3; c++) rgba[i + c] = (byte)((rgba[i + c] * a + bg * (255 - a) + 127) / 255);
                rgba[i + 3] = 255;
            }
        return rgba;
    }

    // ---- Closing ---------------------------------------------------------------------------------------

    /// <summary>Cancel or Esc: the canvas goes back to the unfiltered image.</summary>
    public void Cancel() => _document.CancelFilter();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _previewCancel?.Cancel();
    }
}
