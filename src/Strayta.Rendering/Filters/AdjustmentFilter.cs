using Strayta.Core;

namespace Strayta.Rendering.Filters;

/// <summary>
/// Image › Adjustments applied directly to pixels (Invert, Levels, Hue/Saturation, ...): the same color functions an
/// adjustment layer draws, run on a layer's straight colors (transparency kept) or on a mask or channel's gray values.
/// As a filter it gets the Filter menu's handling for free: the selection limits it with soft edges, it previews on
/// the canvas, it applies to a targeted mask, and with color channels targeted only those channels take it.
/// </summary>
/// <param name="Adjustment">The settings.</param>
/// <param name="Title">The menu command's name, used for the undo step (e.g. "Levels").</param>
public sealed record AdjustmentFilter(Adjustment Adjustment, string Title) : ImageFilter
{
    public override string Name => Title;
    public override int Reach => 0;
    public override bool ChangesFlatAreas => true;
    public override bool KeepsTransparency => true;
    public override ImageFilter Scaled(double scale) => this;

    public override void Apply(FilterImage image, CancellationToken cancel = default)
    {
        if (ColorTransform.Create(Adjustment) is not { } transform) return;
        int w = image.Width;
        var color = image.Color;
        var alpha = image.Alpha;
        Parallel.For(0, image.Height, new ParallelOptions { CancellationToken = cancel }, y =>
        {
            var rgb = new float[w * 3];
            int start = y * w;
            for (int x = 0; x < w; x++)
            {
                float a = alpha?[start + x] ?? 1f;
                float inv = a > 1e-6f ? 1f / a : 0f;
                if (color.Length >= 3)
                    for (int k = 0; k < 3; k++) rgb[x * 3 + k] = Math.Clamp(color[k][start + x] * inv, 0f, 1f);
                else
                    rgb[x * 3] = rgb[x * 3 + 1] = rgb[x * 3 + 2] = Math.Clamp(color[0][start + x] * inv, 0f, 1f);
            }
            transform.ApplyRow(rgb);
            for (int x = 0; x < w; x++)
            {
                float a = alpha?[start + x] ?? 1f;
                if (color.Length >= 3)
                    for (int k = 0; k < 3; k++) color[k][start + x] = Math.Clamp(rgb[x * 3 + k], 0f, 1f) * a;
                else
                    color[0][start + x] = Math.Clamp(0.299f * rgb[x * 3] + 0.587f * rgb[x * 3 + 1] + 0.114f * rgb[x * 3 + 2], 0f, 1f) * a;
            }
        });
    }
}
