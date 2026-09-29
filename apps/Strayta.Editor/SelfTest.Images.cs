using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Editor.ViewModels;
using Strayta.Imaging;
using Strayta.Rendering.Export;

namespace Strayta.Editor;

// Opening and saving standard images through the real view models.
internal static partial class SelfTest
{
    private static async Task RunImageStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"strayta-images-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // An opaque 8-bit PNG opens as a Background layer.
            string png = Path.Combine(dir, "photo.png");
            Plane P(byte v) => new(64, 48, 8, Enumerable.Repeat(v, 64 * 48).ToArray());
            using (var f = File.Create(png)) PngWriter.Write(f, new Raster(ColorMode.Rgb, [P(20), P(120), P(220)], null));
            await editor.OpenAsync(png);
            var doc = editor.ActiveDocument!;
            check(doc.Model.Root.Children is [PixelLayer { Name: "Background" }] && doc.ImageSource is { IsWritable: true } && doc.IsFlatImage,
                "a PNG opens as a single Background layer");

            // Paint on it and Save: written back to the same PNG.
            doc.SelectedLayer = doc.Layers[0];
            doc.BeginStroke(32, 24, new BrushSettings(10, 1f, 1f), new RgbColor(1, 0, 0), erase: false);
            await doc.EndStrokeAsync();
            await doc.SaveImageAsync(png, new ExportOptions { Format = ExportFormat.Png }, becomesFile: true);
            var reopened = (PixelLayer)ImageImporter.Open(png).Root.Children[0];
            check(reopened.Pixels!.ColorPlanes[0].Data[24 * 64 + 32] == 255 && !doc.IsModified,
                "painting then saving writes the change back into the PNG");

            // A single smart object (or type or shape layer) covering the canvas is not a flat image either: saving it as
            // PNG would lose the smart object.
            doc.SelectedLayer = doc.Layers[0];
            if (await doc.ConvertToSmartObjectAsync())
            {
                check(doc.Model.Root.Children is [PixelLayer only] && only.Tags.Contains("smart-object") && !doc.IsFlatImage,
                    "a PNG whose one layer became a smart object is no longer a flat image (Save asks for a PSD)");
                doc.Undo();
            }
            else check(false, "Convert to Smart Object works on a PNG's Background layer");

            // Adding a layer means a PNG can no longer hold it all.
            doc.NewLayer();
            check(!doc.IsFlatImage, "after adding a layer the document is no longer a flat image (Save asks for a PSD)");

            // 16-bit PNGs keep every value through open and save.
            string png16 = Path.Combine(dir, "deep.png");
            var deep = Plane.Create(8, 8, 16);
            for (int i = 0; i < 64; i++) deep.AsUInt16()[i] = (ushort)(i * 1021 + 7);
            using (var f = File.Create(png16)) PngWriter.Write(f, new Raster(ColorMode.Rgb, [deep, deep, deep], null));
            await editor.OpenAsync(png16);
            var doc16 = editor.ActiveDocument!;
            await doc16.SaveImageAsync(png16, new ExportOptions { Format = ExportFormat.Png }, becomesFile: true);
            var back16 = (PixelLayer)ImageImporter.Open(png16).Root.Children[0];
            check(doc16.Model.BitDepth == 16 && back16.Pixels!.ColorPlanes[1].AsUInt16().SequenceEqual(deep.AsUInt16()),
                "a 16-bit PNG opens and saves back bit-exactly");

            // JPEGs open too.
            string jpg = Path.Combine(dir, "shot.jpg");
            using (var f = File.Create(jpg)) JpegEncoder.Encode(f, Enumerable.Repeat((byte)200, 32 * 16 * 4).ToArray(), 32, 16, 95);
            await editor.OpenAsync(jpg);
            check(editor.ActiveDocument?.Model is { Width: 32, Height: 16 } && editor.ActiveDocument.ImageSource?.Format == SkiaSharp.SKEncodedImageFormat.Jpeg,
                "a JPEG opens");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
