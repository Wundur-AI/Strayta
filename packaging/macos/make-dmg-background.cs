#:package SkiaSharp@3.119.4
#:package SkiaSharp.NativeAssets.macOS@3.119.4
// Draws the disk image window's background (660×400 points, at 1× and 2×): the app sits on the left, Applications on
// the right, with an arrow and a caption between them. tiffutil combines the two into one HiDPI TIFF for dmgbuild.
// Usage: dotnet run --no-cache packaging/macos/make-dmg-background.cs -- <output-dir>
using SkiaSharp;

string dir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(dir);
foreach (int scale in new[] { 1, 2 })
{
    const int W = 660, H = 400;
    using var bitmap = new SKBitmap(W * scale, H * scale, SKColorType.Rgba8888, SKAlphaType.Premul);
    using (var canvas = new SKCanvas(bitmap))
    {
        canvas.Scale(scale);
        using var bg = new SKPaint();
        bg.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(W, H),
            [new SKColor(0x1F, 0x2A, 0x44), new SKColor(0x10, 0x14, 0x22)], SKShaderTileMode.Clamp);
        canvas.DrawRect(0, 0, W, H, bg);

        // Faint offset layers in the corner, echoing the icon.
        (SKColor Color, float Dx)[] layers = [(new SKColor(0x8B, 0x5C, 0xF6, 0x22), 40), (new SKColor(0x22, 0xD3, 0xEE, 0x1C), 0), (new SKColor(0x3B, 0x82, 0xF6, 0x18), -40)];
        foreach (var (color, dx) in layers)
        {
            using var p = new SKPaint { IsAntialias = true, Color = color };
            canvas.DrawRoundRect(new SKRoundRect(new SKRect(500 + dx, 250 - dx, 760 + dx, 510 - dx), 34, 34), p);
        }

        // Arrow from the app (x 180) to Applications (x 480), at icon height (y 190).
        using var arrow = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0xB0), StrokeWidth = 5, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        canvas.DrawLine(265, 190, 385, 190, arrow);
        using var head = new SKPath();
        head.MoveTo(370, 175); head.LineTo(390, 190); head.LineTo(370, 205);
        canvas.DrawPath(head, arrow);

        using var typeface = SKTypeface.FromFamilyName("Helvetica Neue", SKFontStyle.Normal) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, 15);
        using var text = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 0xC8) };
        canvas.DrawText("Drag Strayta to Applications to install", W / 2f, 318, SKTextAlign.Center, font, text);
    }
    using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(Path.Combine(dir, scale == 1 ? "dmg-background.png" : "dmg-background@2x.png"), data.ToArray());
}
Console.WriteLine($"Wrote {dir}");
