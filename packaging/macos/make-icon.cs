#:package SkiaSharp@3.119.4
#:package SkiaSharp.NativeAssets.macOS@3.119.4
// Draws Strayta's app icon (stacked, offset translucent layers on a dark rounded square) at every size macOS wants,
// into an .iconset folder that `iconutil -c icns` turns into AppIcon.icns.
// Usage: dotnet run --no-cache packaging/macos/make-icon.cs -- <output.iconset>
using SkiaSharp;

string dir = args.Length > 0 ? args[0] : "AppIcon.iconset";
Directory.CreateDirectory(dir);
foreach (int size in new[] { 16, 32, 64, 128, 256, 512, 1024 })
{
    using var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
    using (var canvas = new SKCanvas(bitmap))
    {
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(size / 1024f);
        // macOS icon grid: an 824×824 rounded square centred in the 1024 canvas.
        var body = new SKRoundRect(new SKRect(100, 100, 924, 924), 185, 185);
        using var bg = new SKPaint { IsAntialias = true };
        bg.Shader = SKShader.CreateLinearGradient(new SKPoint(100, 100), new SKPoint(924, 924),
            [new SKColor(0x1F, 0x2A, 0x44), new SKColor(0x0E, 0x12, 0x1E)], SKShaderTileMode.Clamp);
        canvas.DrawRoundRect(body, bg);

        // Three layers, back to front, each a rounded card offset up and to the right.
        (SKColor Color, float Dx, float Dy)[] layers =
        [
            (new SKColor(0x8B, 0x5C, 0xF6, 0xD8), 120, -120),
            (new SKColor(0x22, 0xD3, 0xEE, 0xD8), 0, 0),
            (new SKColor(0x3B, 0x82, 0xF6, 0xF0), -120, 120),
        ];
        foreach (var (color, dx, dy) in layers)
        {
            var card = new SKRoundRect(new SKRect(292 + dx, 292 + dy, 732 + dx, 732 + dy), 64, 64);
            using var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 0x50), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 18) };
            canvas.Save();
            canvas.Translate(0, 14);
            canvas.DrawRoundRect(card, shadow);
            canvas.Restore();
            using var fill = new SKPaint { IsAntialias = true, Color = color };
            canvas.DrawRoundRect(card, fill);
            using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6, Color = new SKColor(255, 255, 255, 0x60) };
            canvas.DrawRoundRect(card, edge);
        }
    }
    void Save(string name, SKBitmap bmp)
    {
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(dir, name), data.ToArray());
    }
    // iconset names: icon_NxN.png and icon_NxN@2x.png (the @2x of N is 2N pixels).
    if (size <= 512) Save($"icon_{size}x{size}.png", bitmap);
    if (size >= 32) Save($"icon_{size / 2}x{size / 2}@2x.png", bitmap);
}
Console.WriteLine($"Wrote {dir}");
