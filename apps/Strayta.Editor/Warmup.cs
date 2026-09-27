using Strayta.Core;
using Strayta.Rendering;
using Strayta.Editor.Controls;

namespace Strayta.Editor;

/// <summary>
/// Renders a tiny throwaway document at startup so the JIT compiles the pixel code paths
/// (blend modes, masks, groups, caching, conversion, scoring) before the user opens a real file.
/// </summary>
internal static class Warmup
{
    public static void Run()
    {
        try
        {
            const int size = 64;
            var doc = new Document(size, size, ColorMode.Rgb, 8);
            var modes = new[] { BlendMode.Normal, BlendMode.Multiply, BlendMode.Screen, BlendMode.Overlay, BlendMode.Color };
            var group = new LayerGroup { Name = "group", BlendMode = BlendMode.Normal, Opacity = 0.8f };
            for (int i = 0; i < modes.Length; i++)
            {
                var layer = Solid(size, (byte)(i * 50));
                layer.BlendMode = modes[i];
                layer.Clipped = i == 2;
                if (i == 1) layer.Mask = new LayerMask { Bounds = new PixelRect(0, 0, 8, 8), Pixels = Plane.Create(8, 8, 8), DefaultColor = 255 };
                (i < 3 ? doc.Root : group).Add(layer);
            }
            doc.Root.Add(group);

            using var renderer = Renderers.CreateDefault();
            var rgba = renderer.Render(doc).ToRgba8();
            renderer.Render(doc, new RenderOptions { Hidden = new HashSet<LayerNode> { group } });

            var reference = RgbaConverter.ToRgba8(Solid(size, 9).Pixels!);
            FidelityReport.Compare(rgba, reference, size, size, includeDiff: true);
            BitmapFactory.FromRgba(rgba, size, size).Dispose();
        }
        catch
        {
            // Warm-up is best effort; a failure here must never affect the app.
        }
    }

    private static PixelLayer Solid(int size, byte v)
    {
        Plane P(byte b) => new(size, size, 8, Enumerable.Repeat(b, size * size).ToArray());
        return new PixelLayer { Bounds = PixelRect.FromSize(size, size), Pixels = new Raster(ColorMode.Rgb, [P(v), P(128), P(255)], P(200)) };
    }
}
