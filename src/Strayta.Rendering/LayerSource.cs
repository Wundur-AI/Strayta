using Strayta.Core;

namespace Strayta.Rendering;

/// <summary>
/// Something that can be composited: produces straight RGB and coverage (alpha × mask) one row at a time.
/// Converting per row keeps memory flat regardless of layer count and keeps the hot loop cache-friendly.
/// </summary>
internal abstract class Source
{
    public required PixelRect Bounds { get; init; }

    /// <summary>
    /// Fills document row <paramref name="y"/>, columns [x0, x1), which must lie inside <see cref="Bounds"/>.
    /// <paramref name="rgb"/> gets 3 floats per pixel and may be empty when only coverage is wanted.
    /// </summary>
    public abstract void FillRow(int y, int x0, int x1, Span<float> rgb, Span<float> coverage);

    internal static void ApplyMask(LayerMask? mask, int y, int x0, Span<float> coverage)
    {
        if (mask is null) return;
        var b = mask.Bounds;
        float outside = mask.DefaultColor / 255f;
        int n = coverage.Length;

        // Columns [inStart, inEnd) of this row fall inside the mask's pixels; the rest use the default.
        bool rowInside = mask.Pixels is not null && y >= b.Top && y < b.Bottom;
        int inStart = rowInside ? Math.Clamp(b.Left - x0, 0, n) : n;
        int inEnd = rowInside ? Math.Clamp(b.Right - x0, 0, n) : n;

        if (outside != 1f)
        {
            for (int i = 0; i < inStart; i++) coverage[i] *= outside;
            for (int i = inEnd; i < n; i++) coverage[i] *= outside;
        }
        if (inEnd <= inStart) return;

        var p = mask.Pixels!;
        int src = (y - b.Top) * b.Width + (x0 + inStart - b.Left);
        switch (p.BitDepth)
        {
            case 8:
                var bytes = p.Data.AsSpan(src, inEnd - inStart);
                for (int i = 0; i < bytes.Length; i++) coverage[inStart + i] *= bytes[i] * (1f / 255f);
                break;
            default:
                for (int i = inStart; i < inEnd; i++) coverage[i] *= p.GetNormalized(src + i - inStart);
                break;
        }
    }

    /// <summary>
    /// <see cref="ApplyMask(LayerMask?, int, int, Span{float})"/> with a mask stroke in progress painted over the
    /// mask, exactly as <c>MaskBaker</c> will commit it: each sample moves toward the stroke's gray by coverage.
    /// </summary>
    internal static void ApplyMask(LayerMask? mask, StrokeOverlay? stroke, int y, int x0, Span<float> coverage)
    {
        if (mask is null) return;
        var sb = stroke?.Bounds ?? PixelRect.Empty;
        if (stroke is null || y < sb.Top || y >= sb.Bottom)
        {
            ApplyMask(mask, y, x0, coverage);
            return;
        }

        // Plain mask left and right of the stroke; per-pixel mask plus stroke across it.
        int n = coverage.Length;
        int s0 = Math.Clamp(sb.Left - x0, 0, n), s1 = Math.Clamp(sb.Right - x0, s0, n);
        ApplyMask(mask, y, x0, coverage[..s0]);
        ApplyMask(mask, y, x0 + s1, coverage[s1..]);
        float gray = stroke.Color.R, opacity = stroke.Opacity;
        for (int i = s0; i < s1; i++)
        {
            float m = SampleMask(mask, x0 + i, y);
            float cov = stroke.CoverageAt(x0 + i, y) * opacity;
            if (cov > 0f) m += (gray - m) * cov;
            coverage[i] *= m;
        }
    }

    public static float SampleMask(LayerMask mask, int docX, int docY)
    {
        var b = mask.Bounds;
        if (mask.Pixels is { } p && docX >= b.Left && docX < b.Right && docY >= b.Top && docY < b.Bottom)
            return p.GetNormalized((docY - b.Top) * b.Width + (docX - b.Left));
        return mask.DefaultColor / 255f;
    }
}

/// <summary>A pixel layer's stored raster, plus any stroke being painted on it.</summary>
internal sealed class LayerSource : Source
{
    private const float Inv255 = 1f / 255f;
    private const float Inv65535 = 1f / 65535f;

    private readonly PixelLayer _layer;
    private readonly Raster? _raster;
    private readonly byte[]? _palette;
    private readonly LayerMask? _mask;
    private readonly StrokeOverlay? _stroke;
    private readonly StrokeOverlay? _maskStroke;
    private readonly float[] _strokeColor;

    private LayerSource(PixelLayer layer, byte[]? palette, StrokeOverlay? stroke, StrokeOverlay? maskStroke)
    {
        _layer = layer;
        _raster = layer.Pixels;
        _palette = palette;
        _mask = layer.Mask is { Disabled: false } m ? m : null;
        _stroke = stroke;
        _maskStroke = maskStroke;
        var c = stroke?.Color ?? default;
        _strokeColor = layer.Pixels?.ColorMode == ColorMode.Grayscale
            ? [0.299f * c.R + 0.587f * c.G + 0.114f * c.B, 0, 0]
            : [c.R, c.G, c.B];
    }

    /// <summary>Where the layer (and a brush stroke growing it) can show within <paramref name="area"/>.</summary>
    public static PixelRect VisibleBounds(PixelLayer layer, PixelRect area, StrokeOverlay? stroke)
    {
        var bounds = Coverage.Visible(layer, area, StrokeOverlay.ForMaskOf(stroke, layer));
        if (stroke is { Erase: false, TargetsMask: false } s && ReferenceEquals(s.Target, layer))
        {
            var painted = s.Bounds.Intersect(area);
            if (Coverage.MaskReach(layer.Mask, area) is { } reach) painted = painted.Intersect(reach);
            bounds = bounds.IsEmpty ? painted : painted.IsEmpty ? bounds : new PixelRect(
                Math.Min(bounds.Left, painted.Left), Math.Min(bounds.Top, painted.Top),
                Math.Max(bounds.Right, painted.Right), Math.Max(bounds.Bottom, painted.Bottom));
        }
        return bounds;
    }

    public static LayerSource? From(PixelLayer layer, Document doc, StrokeOverlay? stroke = null)
    {
        var active = stroke is { TargetsMask: false } && ReferenceEquals(stroke.Target, layer) ? stroke : null;
        if (layer.Pixels is null && active is null) return null;
        // Skip fully transparent or masked-out areas; fill layers often cover the whole canvas with a mask
        // revealing a small region.
        var bounds = VisibleBounds(layer, doc.Bounds, stroke);
        return bounds.IsEmpty ? null : new LayerSource(layer, doc.Palette, active, StrokeOverlay.ForMaskOf(stroke, layer)) { Bounds = bounds };
    }

    public override void FillRow(int y, int x0, int x1, Span<float> rgb, Span<float> coverage)
    {
        int n = x1 - x0;
        var lb = _layer.Bounds;
        // Columns of this row covered by the stored raster; everything else starts transparent.
        int r0 = _raster is null || y < lb.Top || y >= lb.Bottom ? x1 : Math.Clamp(lb.Left, x0, x1);
        int r1 = _raster is null || y < lb.Top || y >= lb.Bottom ? x1 : Math.Clamp(lb.Right, x0, x1);

        if (r0 > x0 || r1 < x1)
        {
            coverage[..n].Clear();
            if (!rgb.IsEmpty) rgb[..(n * 3)].Clear();
        }
        if (r1 > r0) FillRaster(y, r0, r1, rgb.IsEmpty ? rgb : rgb[((r0 - x0) * 3)..], coverage[(r0 - x0)..]);

        if (_stroke is { } s && y >= s.Bounds.Top && y < s.Bounds.Bottom)
            ApplyStroke(s, y, x0, n, rgb, coverage);

        ApplyMask(_mask, _maskStroke, y, x0, coverage[..n]);
    }

    /// <summary>Paints the stroke over the layer's pixels exactly as <c>StrokeBaker</c> will when it is committed.</summary>
    private void ApplyStroke(StrokeOverlay s, int y, int x0, int n, Span<float> rgb, Span<float> coverage)
    {
        int from = Math.Max(x0, s.Bounds.Left), to = Math.Min(x0 + n, s.Bounds.Right);
        for (int x = from; x < to; x++)
        {
            float cov = s.CoverageAt(x, y) * s.Opacity;
            if (cov <= 0f) continue;
            int i = x - x0;
            float a = coverage[i];
            if (s.Erase)
            {
                coverage[i] = a * (1f - cov);
                continue;
            }
            float na = cov + a * (1f - cov);
            if (!rgb.IsEmpty)
            {
                bool gray = _raster?.ColorMode == ColorMode.Grayscale;
                for (int k = 0; k < 3; k++)
                {
                    float brush = gray ? _strokeColor[0] : _strokeColor[k];
                    rgb[i * 3 + k] = (brush * cov + rgb[i * 3 + k] * a * (1f - cov)) / na;
                }
            }
            coverage[i] = na;
        }
    }

    private void FillRaster(int y, int x0, int x1, Span<float> rgb, Span<float> coverage)
    {
        var raster = _raster!;
        int n = x1 - x0;
        int start = (y - _layer.Bounds.Top) * _layer.Bounds.Width + (x0 - _layer.Bounds.Left);
        var planes = raster.ColorPlanes;

        if (!rgb.IsEmpty)
        {
            switch (raster.ColorMode)
            {
                case ColorMode.Rgb when planes.Count >= 3:
                    FillChannel(planes[0], start, n, rgb, 0);
                    FillChannel(planes[1], start, n, rgb, 1);
                    FillChannel(planes[2], start, n, rgb, 2);
                    break;

                case ColorMode.Grayscale or ColorMode.Duotone or ColorMode.Bitmap when planes.Count >= 1:
                    FillChannel(planes[0], start, n, rgb, 0);
                    for (int i = 0; i < n; i++) rgb[i * 3 + 1] = rgb[i * 3 + 2] = rgb[i * 3];
                    break;

                default:
                    for (int i = 0; i < n; i++)
                    {
                        var (r, g, b) = SampleOther(start + i);
                        rgb[i * 3] = r;
                        rgb[i * 3 + 1] = g;
                        rgb[i * 3 + 2] = b;
                    }
                    break;
            }
        }

        if (raster.Alpha is { } alpha) FillPlane(alpha, start, n, coverage);
        else coverage[..n].Fill(1f);
    }

    /// <summary>Writes plane samples into every third slot of <paramref name="rgb"/> starting at <paramref name="channel"/>.</summary>
    private static void FillChannel(Plane p, int start, int n, Span<float> rgb, int channel)
    {
        switch (p.BitDepth)
        {
            case 8:
                var b = p.Data.AsSpan(start, n);
                for (int i = 0; i < n; i++) rgb[i * 3 + channel] = b[i] * Inv255;
                break;
            case 16:
                var s = p.AsUInt16().Slice(start, n);
                for (int i = 0; i < n; i++) rgb[i * 3 + channel] = s[i] * Inv65535;
                break;
            default:
                var f = p.AsSingle().Slice(start, n);
                for (int i = 0; i < n; i++) rgb[i * 3 + channel] = f[i];
                break;
        }
    }

    private static void FillPlane(Plane p, int start, int n, Span<float> dst)
    {
        switch (p.BitDepth)
        {
            case 8:
                var b = p.Data.AsSpan(start, n);
                for (int i = 0; i < n; i++) dst[i] = b[i] * Inv255;
                break;
            case 16:
                var s = p.AsUInt16().Slice(start, n);
                for (int i = 0; i < n; i++) dst[i] = s[i] * Inv65535;
                break;
            default:
                p.AsSingle().Slice(start, n).CopyTo(dst);
                break;
        }
    }

    private (float, float, float) SampleOther(int i)
    {
        var p = _raster!.ColorPlanes;
        switch (_raster.ColorMode)
        {
            case ColorMode.Cmyk when p.Count >= 4:
                float k = p[3].GetNormalized(i);
                return (p[0].GetNormalized(i) * k, p[1].GetNormalized(i) * k, p[2].GetNormalized(i) * k);
            case ColorMode.Indexed when _palette is not null && p.Count >= 1:
                int idx = p[0].Data[i] * 3;
                return (_palette[idx] * Inv255, _palette[idx + 1] * Inv255, _palette[idx + 2] * Inv255);
            default:
                float v = p.Count > 0 ? p[0].GetNormalized(i) : 0f;
                return (v, v, v);
        }
    }
}

/// <summary>The result of an isolated group, read back from its premultiplied buffer.</summary>
internal sealed class BufferSource : Source
{
    private readonly RenderBuffer _buffer;
    private readonly LayerMask? _mask;
    private readonly StrokeOverlay? _maskStroke;

    public BufferSource(RenderBuffer buffer, LayerMask? mask, StrokeOverlay? maskStroke = null)
    {
        _buffer = buffer;
        _mask = mask is { Disabled: false } ? mask : null;
        _maskStroke = maskStroke;
    }

    public override void FillRow(int y, int x0, int x1, Span<float> rgb, Span<float> coverage)
    {
        int n = x1 - x0;
        var px = _buffer.Pixels;
        int t = _buffer.IndexOf(x0, y);
        for (int i = 0; i < n; i++, t += 4)
        {
            float a = px[t + 3];
            coverage[i] = a;
            if (rgb.IsEmpty) continue;
            float inv = a > 0f ? 1f / a : 0f;
            rgb[i * 3] = px[t] * inv;
            rgb[i * 3 + 1] = px[t + 1] * inv;
            rgb[i * 3 + 2] = px[t + 2] * inv;
        }
        ApplyMask(_mask, _maskStroke, y, x0, coverage[..n]);
    }
}
