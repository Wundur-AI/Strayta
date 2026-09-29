using Strayta.Core;
using Strayta.Core.Text;
using Strayta.Psd;
using Strayta.Psd.Text;
using Strayta.Rendering;
using Strayta.Text;

namespace Strayta.Inspect;

/// <summary>
/// Writes edited copies of real files, one per kind of text edit, for checking in Photoshop:
/// <c>textsamples &lt;out dir&gt; &lt;folder with Level_failed.psd and level_selection.psd&gt; &lt;folder with 51-TMP - Font Asset Icon.psd&gt;
/// [--fonts dir]</c>.
/// </summary>
internal static class TextSamples
{
    public static int Run(string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: textsamples <out> <main dir> <corpus dir> [--fonts dir]"); return 2; }
        string outDir = args[0], main = args[1], corpus = args[2];
        var fonts = FontCatalog.System;
        for (int i = 3; i + 1 < args.Length; i++)
            if (args[i] == "--fonts")
            {
                var extra = FontCatalog.FromFiles([]);
                extra.AddFolder(args[++i]);
                fonts = FontCatalog.Merge(fonts, extra);
            }
        Directory.CreateDirectory(outDir);
        string levelFailed = Path.Combine(main, "Level_failed.psd"), levelSelection = Path.Combine(main, "level_selection.psd");
        string icon = Path.Combine(corpus, "51-TMP - Font Asset Icon.psd");

        Edit(levelFailed, "Score", d => d.WithText("Best Score"), Path.Combine(outDir, "01-text-change.psd"), fonts);
        Edit(levelFailed, "Score", d => d.WithText("Best Score"), Path.Combine(outDir, "01b-text-change-keep-Txt2.psd"), fonts, keepTxt2: true);
        Edit(levelFailed, "Enemies killed", d => d.ApplyStyle(s => s with { FontSize = s.FontSize * 1.5 }), Path.Combine(outDir, "02-font-size.psd"), fonts);
        Edit(levelFailed, "Cash Rewards", d => d.ApplyStyle(s => s with { FillColor = new TextColor(1, 0.5, 0) }), Path.Combine(outDir, "03-colour.psd"), fonts);
        Edit(icon, "F", d => d.ApplyParagraphStyle(p => p with { Justification = TextJustification.Left }), Path.Combine(outDir, "04-alignment-box-left.psd"), fonts);
        // Point text centres on its anchor, so the anchor moves to the middle of the old text to keep it in place.
        Edit(levelSelection, "You have", d =>
        {
            double width = TextLayout.Create(d, fonts).Bounds.Width;
            return d.ApplyParagraphStyle(p => p with { Justification = TextJustification.Center })
                .WithTransform(d.Transform with { TX = d.Transform.TX + width / 2 });
        }, Path.Combine(outDir, "04b-alignment-point-center.psd"), fonts);
        Edit(levelFailed, "Goals acheived", d => d.WithText("Goals achieved this round"), Path.Combine(outDir, "05-point-longer-text.psd"), fonts);
        Edit(levelSelection, "You have", d =>
        {
            // Point text becomes paragraph text in a 420 px wide box starting at the old anchor, wrapping one paragraph.
            string text = d.Text.Replace('\n', ' ');
            var t = d.Transform;
            var size = d.StyleAt(0).FontSize;
            return d.WithText(text).WithLayout(kind: TextKind.Paragraph, box: new TextRect(0, 0, 420, size * 1.2 * 6))
                .WithTransform(t with { TY = t.TY - size });
        }, Path.Combine(outDir, "06-box-text-wrap.psd"), fonts);
        NewLayer(levelFailed, Path.Combine(outDir, "07-new-layer.psd"), fonts);
        NewDocument(Path.Combine(outDir, "07b-new-layer-new-document.psd"), fonts);
        return 0;
    }

    private static PixelLayer Find(Document doc, string name) =>
        doc.Root.Descendants().OfType<PixelLayer>().First(l => l.Tags.Contains("text") && l.Name.StartsWith(name, StringComparison.Ordinal));

    private static void Edit(string source, string layerName, Func<TextLayerData, TextLayerData> change, string output, FontCatalog fonts, bool keepTxt2 = false)
    {
        var doc = PsdFile.OpenForEditing(source);
        var layer = Find(doc, layerName);
        var record = (PsdLayerRecord)layer.SourceData!;
        var data = PsdTypeLayer.Read(record)!;
        var edited = change(data);
        var render = TextRenderer.Render(edited, new TextRenderOptions { Fonts = fonts, ColorMode = doc.ColorMode, BitDepth = doc.BitDepth });
        layer.SourceData = PsdTypeLayer.Apply(record, edited, render.Layout.ToTextBounds());
        layer.Pixels = render.Pixels;
        layer.Bounds = render.Pixels is null ? PixelRect.Empty : render.Bounds;
        if (layer.Name == data.Text.Split('\n')[0].Trim()) layer.Name = edited.Text.Split('\n')[0].Trim();
        Save(doc, output, keepTxt2);
        Report(output, layer, render);
    }

    private static void NewLayer(string source, string output, FontCatalog fonts)
    {
        var doc = PsdFile.OpenForEditing(source);
        var donor = doc.Root.Descendants().OfType<PixelLayer>().Select(l => l.SourceData is PsdLayerRecord r ? PsdTypeLayer.Read(r) : null).First(d => d is not null)!;
        var style = new TextStyle { FontPostScriptName = "ArialMT", FontSize = 36, FillColor = new TextColor(1, 0.85, 0.2) };
        var data = PsdTypeLayer.UseResourcesOf(TextLayerData.CreatePoint("Made by Strayta", style, 60, 60), donor);
        Add(doc, data, output, fonts);
    }

    private static void NewDocument(string output, FontCatalog fonts)
    {
        var doc = new Document(600, 300, ColorMode.Rgb, 8);
        var white = Enumerable.Range(0, 3).Select(_ => new Plane(600, 300, 8, Enumerable.Repeat((byte)255, 600 * 300).ToArray())).ToArray();
        doc.Root.Add(new PixelLayer { Name = "Background", Bounds = doc.Bounds, Pixels = new Raster(ColorMode.Rgb, white, null) });
        var style = new TextStyle { FontPostScriptName = "ArialMT", FontSize = 40, FillColor = new TextColor(0.1, 0.2, 0.6) };
        Add(doc, TextLayerData.CreatePoint("Point text from Strayta", style, 30, 70), output, fonts, save: false);
        var box = TextLayerData.CreateParagraph("Paragraph text from Strayta wraps inside its box, centred.", style with { FontSize = 28, FillColor = TextColor.Black },
            new TextRect(30, 110, 330, 280), new ParagraphStyle { Justification = TextJustification.Center });
        Add(doc, box, output, fonts);
    }

    private static void Add(Document doc, TextLayerData data, string output, FontCatalog fonts, bool save = true)
    {
        var render = TextRenderer.Render(data, new TextRenderOptions { Fonts = fonts, ColorMode = doc.ColorMode, BitDepth = doc.BitDepth });
        var layer = new PixelLayer
        {
            Name = data.Text.Split('\n')[0],
            SourceData = PsdTypeLayer.Create(data, render.Layout.ToTextBounds()),
            Pixels = render.Pixels,
            Bounds = render.Bounds,
        };
        layer.Tags.Add("text");
        doc.Root.Add(layer);
        if (!save) return;
        Save(doc, output, keepTxt2: false);
        Report(output, layer, render);
    }

    private static void Save(Document doc, string output, bool keepTxt2)
    {
        using var renderer = new CpuRenderer();
        var composite = renderer.Render(doc).ToRaster(doc.ColorMode, doc.BitDepth);
        PsdWriter.Save(doc, output, new PsdWriteOptions { Composite = composite, KeepDocumentTextData = keepTxt2 });
        // Read back: the text must come out as written.
        var again = PsdFile.Open(output);
        int typeLayers = again.Layers.Count(PsdTypeLayer.IsTypeLayer);
        bool txt2 = again.GlobalBlocks.Any(b => b.Key == "Txt2");
        Console.WriteLine($"{Path.GetFileName(output)}: {typeLayers} type layers, Txt2 {(txt2 ? "kept" : "dropped")}");
    }

    private static void Report(string output, PixelLayer layer, TextRenderResult render) =>
        Console.WriteLine($"  \"{layer.Name}\" {layer.Bounds}{(render.MissingFonts.Count > 0 ? $" (missing {string.Join(", ", render.MissingFonts)}, drawn with {render.Layout.SubstituteFont})" : "")}");
}
