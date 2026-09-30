using Strayta.Core;
using Strayta.Core.Painting;

namespace Strayta.Editor.ViewModels;

// Edit › Define Brush Preset…: the visible image inside the selection becomes a sampled brush tip.
public sealed partial class DocumentViewModel
{
    /// <summary>
    /// A brush tip from the visible image inside the selection (or the whole image without one), as Photoshop defines
    /// brushes: dark pixels paint, white and transparent ones do not, and the selection's soft edges fade the tip. Tips
    /// larger than 2500 pixels are refused (null, with a notice).
    /// </summary>
    public async Task<BrushTip?> DefineBrushTipAsync(string name)
    {
        var area = Selection?.Bounds.Intersect(Model.Bounds) ?? Model.Bounds;
        if (area.IsEmpty) return null;
        if (area.Width > 2500 || area.Height > 2500)
        {
            Notice = "Brush tips can be at most 2500 pixels across; select a smaller area.";
            return null;
        }
        var image = await CompositeSampleAsync(null); // the flattened image (DocumentViewModel.Retouch.cs)
        var selection = Selection;
        int w = area.Width, h = area.Height;
        var alpha = new byte[w * h];
        Span<float> c = stackalloc float[3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int dx = area.Left + x, dy = area.Top + y;
                float a = image.Read(dx, dy, c);
                float gray = image.ColorChannels >= 3 ? Toning.Luma(c[0], c[1], c[2]) : c[0];
                float paint = a * (1f - gray);
                if (selection is not null) paint *= selection.CoverageAt(dx, dy) / 255f;
                alpha[y * w + x] = (byte)MathF.Round(Math.Clamp(paint, 0f, 1f) * 255f);
            }
        return new BrushTip(name, w, h, alpha);
    }
}
