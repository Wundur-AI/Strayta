using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.Editing;

/// <summary>
/// An open Liquify (Filter › Liquify): the layer, a displacement field over its area and the tool settings. The
/// preview is drawn at screen resolution (the field has one node per preview pixel); OK applies the field to the
/// full-resolution pixels. Strokes can be undone inside the workspace (⌘Z), and Restore All clears the distortion.
/// </summary>
public sealed class LiquifySession : ObservableObject
{
    private readonly float[] _preview; // premultiplied RGBA floats of the layer at preview scale
    private readonly List<(float[] Dx, float[] Dy)> _undo = [];
    private LiquifyTool _tool = LiquifyTool.ForwardWarp;
    private double _size = 100, _pressure = 50, _density = 50, _rate = 80;
    private bool _showMask = true;
    private (double X, double Y)? _last;

    /// <summary>The longest side of the preview, in pixels.</summary>
    public const int PreviewSize = 1400;

    public LiquifySession(PixelLayer layer, PixelRect canvas)
    {
        Layer = layer;
        var bounds = layer.Bounds;
        // The field covers the canvas and the layer (within a canvas-sized margin, like Free Transform keeps).
        var margin = new PixelRect(canvas.Left - canvas.Width, canvas.Top - canvas.Height, canvas.Right + canvas.Width, canvas.Bottom + canvas.Height);
        var domain = new PixelRect(Math.Min(canvas.Left, bounds.Left), Math.Min(canvas.Top, bounds.Top), Math.Max(canvas.Right, bounds.Right), Math.Max(canvas.Bottom, bounds.Bottom)).Intersect(margin);
        Scale = Math.Max(1, Math.Max(domain.Width, domain.Height) / (double)PreviewSize);
        Field = new LiquifyField(domain, Scale);
        PreviewWidth = Math.Max(1, (int)Math.Ceiling(domain.Width / Scale));
        PreviewHeight = Math.Max(1, (int)Math.Ceiling(domain.Height / Scale));
        _preview = PreviewSource(layer, domain, Scale, PreviewWidth, PreviewHeight);
        Preview = new byte[PreviewWidth * PreviewHeight * 4];
        _size = Math.Round(Math.Clamp(Math.Max(domain.Width, domain.Height) / 12.0, 10, 600));
        Render();
    }

    public PixelLayer Layer { get; }
    public LiquifyField Field { get; }

    /// <summary>Document pixels per preview pixel.</summary>
    public double Scale { get; }

    public int PreviewWidth { get; }
    public int PreviewHeight { get; }

    /// <summary>The liquified preview: premultiplied BGRA, <see cref="PreviewWidth"/> × <see cref="PreviewHeight"/>.</summary>
    public byte[] Preview { get; }

    /// <summary>Raised after the preview is redrawn.</summary>
    public event Action? Changed;

    public LiquifyTool Tool
    {
        get => _tool;
        set
        {
            if (SetProperty(ref _tool, value)) OnPropertyChanged(nameof(ToolIndex));
        }
    }

    /// <summary>The tool's index in <see cref="LiquifyTool"/> order (for the tool buttons).</summary>
    public int ToolIndex { get => (int)_tool; set => Tool = (LiquifyTool)Math.Clamp(value, 0, 8); }

    /// <summary>Brush size (diameter), document pixels.</summary>
    public double BrushSize { get => _size; set => SetProperty(ref _size, Math.Clamp(Math.Round(value), 1, 15000)); }

    public double BrushPressure { get => _pressure; set => SetProperty(ref _pressure, Math.Clamp(value, 1, 100)); }
    public double BrushDensity { get => _density; set => SetProperty(ref _density, Math.Clamp(value, 0, 100)); }
    public double BrushRate { get => _rate; set => SetProperty(ref _rate, Math.Clamp(value, 0, 100)); }

    /// <summary>Show Mask: the frozen areas tinted red in the preview.</summary>
    public bool ShowMask
    {
        get => _showMask;
        set
        {
            if (SetProperty(ref _showMask, value)) Changed?.Invoke();
        }
    }

    public LiquifyBrush Brush => new(_size, _pressure, _density, _rate);

    /// <summary>True for the tools that work while the pointer stays still (applied on a timer, at the rate).</summary>
    public bool ToolRepeats => _tool is LiquifyTool.TwirlClockwise or LiquifyTool.Pucker or LiquifyTool.Bloat or LiquifyTool.Reconstruct or LiquifyTool.Smooth;

    public bool CanUndo => _undo.Count > 0;

    /// <summary>Starts a stroke at document point (x, y) (one undo step in the workspace).</summary>
    public void BeginStroke(double x, double y, bool alt = false)
    {
        _undo.Add(Field.Snapshot());
        if (_undo.Count > 50) _undo.RemoveAt(0);
        _last = (x, y);
        if (_tool is not (LiquifyTool.ForwardWarp or LiquifyTool.PushLeft)) Apply(x, y, x, y, alt);
        else Render();
        OnPropertyChanged(nameof(CanUndo));
    }

    /// <summary>Continues the stroke to (x, y); long moves are split so the brush stays smooth.</summary>
    public void StrokeTo(double x, double y, bool alt = false)
    {
        if (_last is not { } last) return;
        double dist = Math.Sqrt((x - last.X) * (x - last.X) + (y - last.Y) * (y - last.Y));
        double step = Math.Max(1, _size / 8);
        int n = Math.Max(1, (int)Math.Ceiling(dist / step));
        for (int i = 1; i <= n; i++)
        {
            double ax = last.X + (x - last.X) * (i - 1) / n, ay = last.Y + (y - last.Y) * (i - 1) / n;
            double bx = last.X + (x - last.X) * i / n, by = last.Y + (y - last.Y) * i / n;
            Field.Apply(_tool, ax, ay, bx, by, Brush, alt);
        }
        _last = (x, y);
        Render();
    }

    /// <summary>One tick of a stationary tool while the button is held.</summary>
    public void Tick(bool alt = false)
    {
        if (_last is not { } p || !ToolRepeats) return;
        Apply(p.X, p.Y, p.X, p.Y, alt);
    }

    public void EndStroke() => _last = null;

    private void Apply(double ax, double ay, double bx, double by, bool alt)
    {
        Field.Apply(_tool, ax, ay, bx, by, Brush, alt);
        Render();
    }

    /// <summary>⌘Z in the workspace: undoes the last stroke.</summary>
    public void Undo()
    {
        if (_undo.Count == 0) return;
        Field.Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        OnPropertyChanged(nameof(CanUndo));
        Render();
    }

    /// <summary>Restore All.</summary>
    public void RestoreAll()
    {
        _undo.Add(Field.Snapshot());
        Field.RestoreAll();
        OnPropertyChanged(nameof(CanUndo));
        Render();
    }

    /// <summary>Thaws the whole freeze mask.</summary>
    public void ThawAll()
    {
        Field.ThawAll();
        Render();
    }

    /// <summary>Redraws the preview from the field.</summary>
    public void Render()
    {
        Field.RenderPreview(_preview, PreviewWidth, PreviewHeight, Scale, Preview);
        if (_showMask) TintFrozen();
        Changed?.Invoke();
    }

    /// <summary>Frozen areas show red over the preview, as in Photoshop.</summary>
    private void TintFrozen()
    {
        var freeze = Field.Freeze;
        int columns = Field.Columns;
        Parallel.For(0, PreviewHeight, y =>
        {
            for (int x = 0; x < PreviewWidth; x++)
            {
                // Preview pixel centers sit halfway between nodes; the node at its top-left stands in.
                float f = freeze[Math.Min(y, Field.Rows - 1) * columns + Math.Min(x, columns - 1)];
                if (f <= 0) continue;
                int o = (y * PreviewWidth + x) * 4;
                float t = 0.5f * f;
                Preview[o] = (byte)(Preview[o] * (1 - t));
                Preview[o + 1] = (byte)(Preview[o + 1] * (1 - t));
                Preview[o + 2] = (byte)(Preview[o + 2] * (1 - t) + 255 * t);
                Preview[o + 3] = (byte)Math.Max(Preview[o + 3], (byte)(255 * t));
            }
        });
    }

    /// <summary>The layer reduced to the preview's pixels over <paramref name="domain"/>, as premultiplied RGBA floats.</summary>
    private static float[] PreviewSource(PixelLayer layer, PixelRect domain, double scale, int w, int h)
    {
        var data = new float[w * h * 4];
        if (layer.Pixels is not { } raster) return data;
        var (small, bounds) = Resampler.TransformRaster(raster, layer.Bounds,
            Affine.Translation(-domain.Left, -domain.Top).Then(Affine.Scale(1 / scale, 1 / scale)), ResampleFilter.Bilinear, new PixelRect(0, 0, w, h));
        if (small is null) return data;
        bool gray = small.ColorPlanes.Count == 1;
        for (int y = 0; y < bounds.Height; y++)
            for (int x = 0; x < bounds.Width; x++)
            {
                int i = y * bounds.Width + x;
                int tx = bounds.Left + x, ty = bounds.Top + y;
                if (tx < 0 || ty < 0 || tx >= w || ty >= h) continue;
                float a = small.Alpha?.GetNormalized(i) ?? 1f;
                int o = (ty * w + tx) * 4;
                float r = small.ColorPlanes[0].GetNormalized(i);
                data[o] = r * a;
                data[o + 1] = (gray ? r : small.ColorPlanes[1].GetNormalized(i)) * a;
                data[o + 2] = (gray ? r : small.ColorPlanes[2].GetNormalized(i)) * a;
                data[o + 3] = a;
            }
        return data;
    }
}
