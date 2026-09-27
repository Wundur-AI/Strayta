using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Strayta.Core;

namespace Strayta.Editor.Controls;

/// <summary>
/// Small layer previews for the Layers panel. Each is built by sampling only the pixels it needs (never
/// converting the whole layer), one at a time on a single background worker so expanding a group full of
/// big layers never competes with the UI. Cached per raster; rasters are replaced (never modified) on edits,
/// so a painted layer simply gets a new thumbnail.
/// </summary>
public static class Thumbnails
{
    public const int Size = 36;
    private const int SamplesPerAxis = 3; // per thumbnail pixel

    private sealed class Entry
    {
        public Bitmap? Bitmap;
        public List<Action> Waiting = [];
    }

    private static readonly ConditionalWeakTable<Raster, Entry> Cache = new();
    private static readonly BlockingCollection<(Raster, byte[]?, Entry)> Queue = new();

    static Thumbnails()
    {
        var worker = new Thread(Work) { IsBackground = true, Name = "Strayta thumbnails", Priority = ThreadPriority.BelowNormal };
        worker.Start();
    }

    /// <summary>Returns the thumbnail if ready; otherwise queues it and calls <paramref name="ready"/> on the UI thread.</summary>
    public static Bitmap? Get(Raster raster, byte[]? palette, Action ready)
    {
        var entry = Cache.GetValue(raster, _ => new Entry());
        lock (entry)
        {
            if (entry.Bitmap is not null) return entry.Bitmap;
            entry.Waiting.Add(ready);
            if (entry.Waiting.Count > 1) return null; // already queued
        }
        Queue.Add((raster, palette, entry));
        return null;
    }

    private static void Work()
    {
        foreach (var (raster, palette, entry) in Queue.GetConsumingEnumerable())
        {
            var (rgba, w, h) = Render(raster, palette);
            Dispatcher.UIThread.Post(() =>
            {
                var bitmap = BitmapFactory.FromRgba(rgba, w, h);
                List<Action> callbacks;
                lock (entry)
                {
                    entry.Bitmap = bitmap;
                    callbacks = entry.Waiting;
                    entry.Waiting = [];
                }
                foreach (var c in callbacks) c();
            }, DispatcherPriority.Background);
        }
    }

    private static readonly ConditionalWeakTable<LayerMask, Entry> MaskCache = new();

    /// <summary>Like <see cref="Get"/>, for a layer mask shown in grayscale (white reveals).</summary>
    public static Bitmap? GetMask(LayerMask mask, Action ready)
    {
        var entry = MaskCache.GetValue(mask, _ => new Entry());
        lock (entry)
        {
            if (entry.Bitmap is not null) return entry.Bitmap;
            entry.Waiting.Add(ready);
            if (entry.Waiting.Count > 1) return null;
        }
        byte fill = mask.DefaultColor;
        var raster = mask.Pixels is { } p
            ? new Raster(ColorMode.Grayscale, [p], null)
            : new Raster(ColorMode.Grayscale, [new Plane(1, 1, 8, [fill])], null);
        Queue.Add((raster, null, entry));
        return null;
    }

    /// <summary>Samples a small grid inside each thumbnail pixel, alpha-weighted, keeping the aspect ratio.</summary>
    internal static (byte[] Rgba, int Width, int Height) Render(Raster raster, byte[]? palette)
    {
        double scale = Math.Min(1.0, (double)Size / Math.Max(raster.Width, raster.Height));
        int w = Math.Max(1, (int)Math.Round(raster.Width * scale)), h = Math.Max(1, (int)Math.Round(raster.Height * scale));
        var planes = raster.ColorPlanes;
        var o = new byte[w * h * 4];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double r = 0, g = 0, b = 0, a = 0;
                for (int sy = 0; sy < SamplesPerAxis; sy++)
                    for (int sx = 0; sx < SamplesPerAxis; sx++)
                    {
                        int px = Math.Min(raster.Width - 1, (int)((x + (sx + 0.5) / SamplesPerAxis) * raster.Width / w));
                        int py = Math.Min(raster.Height - 1, (int)((y + (sy + 0.5) / SamplesPerAxis) * raster.Height / h));
                        int i = py * raster.Width + px;
                        double alpha = raster.Alpha?.GetNormalized(i) ?? 1.0;
                        var (cr, cg, cb) = Color(raster, planes, palette, i);
                        r += cr * alpha; g += cg * alpha; b += cb * alpha; a += alpha;
                    }
                int oi = (y * w + x) * 4;
                if (a > 0)
                {
                    o[oi] = RgbaConverter.ToByte((float)(r / a));
                    o[oi + 1] = RgbaConverter.ToByte((float)(g / a));
                    o[oi + 2] = RgbaConverter.ToByte((float)(b / a));
                }
                o[oi + 3] = RgbaConverter.ToByte((float)(a / (SamplesPerAxis * SamplesPerAxis)));
            }
        }
        return (o, w, h);
    }

    private static (float, float, float) Color(Raster raster, IReadOnlyList<Plane> p, byte[]? palette, int i)
    {
        switch (raster.ColorMode)
        {
            case ColorMode.Rgb when p.Count >= 3:
                float r = p[0].GetNormalized(i), g = p[1].GetNormalized(i), b = p[2].GetNormalized(i);
                return raster.BitDepth == 32 ? (RgbaConverter.LinearToSrgb(r), RgbaConverter.LinearToSrgb(g), RgbaConverter.LinearToSrgb(b)) : (r, g, b);
            case ColorMode.Cmyk when p.Count >= 4:
                float k = p[3].GetNormalized(i);
                return (p[0].GetNormalized(i) * k, p[1].GetNormalized(i) * k, p[2].GetNormalized(i) * k);
            case ColorMode.Indexed when palette is not null && p.Count >= 1:
                int idx = p[0].Data[i] * 3;
                return (palette[idx] / 255f, palette[idx + 1] / 255f, palette[idx + 2] / 255f);
            default:
                float v = p.Count > 0 ? p[0].GetNormalized(i) : 0f;
                return (v, v, v);
        }
    }
}
