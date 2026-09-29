using Strayta.Core;
using Strayta.Core.Text;
using Strayta.Psd;
using Strayta.Psd.Text;

namespace Strayta.Text.Tests;

/// <summary>Layout, hit testing and rendering with an installed font (tests are skipped on machines without fonts).</summary>
public class TextEngineTests
{
    private static string Font()
    {
        var font = FontCatalog.System.FallbackPostScriptName;
        if (font is null) Assert.Skip("No fonts installed.");
        return font;
    }

    private static TextStyle Style(double size = 40) => new() { FontPostScriptName = Font(), FontSize = size };

    [Fact]
    public void The_catalogue_indexes_installed_fonts_by_postscript_name()
    {
        string font = Font();
        var face = FontCatalog.System.Find(font);
        Assert.NotNull(face);
        Assert.Equal(font, face.PostScriptName);
        Assert.Contains(face.FamilyName, FontCatalog.System.Families);
        Assert.Contains(FontCatalog.System.FacesOf(face.FamilyName), f => f.PostScriptName == font);
        Assert.Null(FontCatalog.System.Find("Strayta-NoSuchFont"));
    }

    [Fact]
    public void Point_text_starts_at_its_anchor_on_the_first_baseline()
    {
        var layout = TextLayout.Create(TextLayerData.CreatePoint("Hello", Style(), 0, 0));
        Assert.Single(layout.Lines);
        var line = layout.Lines[0];
        Assert.Equal(0, line.Baseline);
        Assert.Equal(0, line.Left);
        Assert.True(line.Right > 60 && line.Ascent > 20 && line.Descent > 3, $"{line}");
        Assert.True(layout.InkBounds.Top < -20 && layout.InkBounds.Bottom < 2, $"{layout.InkBounds}");
    }

    [Fact]
    public void Lines_are_one_auto_leading_apart_and_align_around_the_anchor()
    {
        var data = TextLayerData.CreatePoint("One\nTwo lines", Style(40), 0, 0, new ParagraphStyle { Justification = TextJustification.Right });
        var layout = TextLayout.Create(data);
        Assert.Equal(2, layout.Lines.Count);
        Assert.Equal(48, layout.Lines[1].Baseline, 6); // 120% of 40
        Assert.Equal(0, layout.Lines[0].Right, 6);
        Assert.Equal(0, layout.Lines[1].Right, 6);

        var centered = TextLayout.Create(data.ApplyParagraphStyle(p => p with { Justification = TextJustification.Center }));
        Assert.Equal(-centered.Lines[1].Left, centered.Lines[1].Right, 6);
    }

    [Fact]
    public void Leading_tracking_and_scale_change_the_layout()
    {
        var plain = TextLayout.Create(TextLayerData.CreatePoint("ab\ncd", Style(40), 0, 0));
        var styled = TextLayout.Create(TextLayerData.CreatePoint("ab\ncd", Style(40) with { AutoLeading = false, Leading = 70, Tracking = 100, HorizontalScale = 2 }, 0, 0));
        Assert.Equal(70, styled.Lines[1].Baseline, 6);
        double plainWidth = plain.Lines[0].Right - plain.Lines[0].Left;
        double styledWidth = styled.Lines[0].Right - styled.Lines[0].Left;
        Assert.Equal(plainWidth * 2 + 2 * 4, styledWidth, 3); // twice as wide, plus 100/1000 em after each of 2 characters
    }

    [Fact]
    public void Paragraph_text_wraps_words_inside_the_box()
    {
        var data = TextLayerData.CreateParagraph("the quick brown fox jumps over the lazy dog", Style(30), new TextRect(0, 0, 200, 400));
        var layout = TextLayout.Create(data);
        Assert.True(layout.Lines.Count >= 3, $"{layout.Lines.Count} lines");
        foreach (var line in layout.Lines)
        {
            Assert.True(line.Right - 0.01 <= 200 || data.Text[line.End - 1] == ' ', $"line {line} fits");
            Assert.True(line.Start == 0 || data.Text[line.Start - 1] == ' ', "lines break after spaces");
        }
        Assert.Equal(layout.Lines[0].Ascent, layout.Lines[0].Baseline, 6); // first baseline one ascent below the top
        Assert.Equal(data.Text.Length, layout.Lines[^1].End);
    }

    [Fact]
    public void Justified_lines_fill_the_box_except_the_last()
    {
        var data = TextLayerData.CreateParagraph("the quick brown fox jumps over the lazy dog again", Style(30), new TextRect(0, 0, 250, 400),
            new ParagraphStyle { Justification = TextJustification.JustifyLastLeft });
        var layout = TextLayout.Create(data);
        Assert.True(layout.Lines.Count >= 2);
        var first = layout.Lines[0];
        var caret = layout.GetCaret(first.End - 1); // before the trailing space
        Assert.Equal(250, caret.X, 1);
        Assert.True(layout.Lines[^1].Right < 249);
    }

    [Fact]
    public void Lines_that_do_not_fit_the_box_are_hidden()
    {
        var data = TextLayerData.CreateParagraph("one two three four five six seven", Style(30), new TextRect(0, 0, 120, 60));
        var layout = TextLayout.Create(data);
        Assert.Contains(layout.Lines, l => !l.Visible);
        Assert.True(layout.InkBounds.Bottom <= 60);
    }

    [Fact]
    public void Carets_and_hit_testing_agree()
    {
        var data = TextLayerData.CreatePoint("Hello\nWorld", Style(40), 0, 0);
        var layout = TextLayout.Create(data);
        for (int i = 0; i <= data.Text.Length; i++)
        {
            var c = layout.GetCaret(i);
            Assert.Equal(i, layout.HitTest(c.X, (c.Top + c.Bottom) / 2));
        }
        Assert.Equal(0, layout.HitTest(-100, -100));
        Assert.Equal(data.Text.Length, layout.HitTest(1000, 1000));
        Assert.Equal(1, layout.GetCaret(6).Line);
        Assert.Equal(0, layout.GetCaret(6).X, 6);
    }

    [Fact]
    public void Selections_cover_each_line_they_touch()
    {
        var data = TextLayerData.CreatePoint("Hello\nWorld", Style(40), 0, 0);
        var layout = TextLayout.Create(data);
        var rects = layout.GetSelectionRects(3, 5); // "lo\nWo"
        Assert.Equal(2, rects.Count);
        Assert.True(rects[0].Left > 0 && rects[1].Left == 0 && rects[1].Top > rects[0].Top);
        Assert.Empty(layout.GetSelectionRects(2, 0));
    }

    [Fact]
    public void Rendering_draws_the_fill_color_through_the_transform()
    {
        var style = Style(40) with { FillColor = new TextColor(0, 0, 1) };
        var data = TextLayerData.CreatePoint("Hi", style, 0, 0).WithTransform(new TextTransform(1, 0, 0, 1, 100.25, 80));
        var result = TextRenderer.Render(data);
        Assert.NotNull(result.Pixels);
        Assert.True(result.Bounds.Left >= 100 && result.Bounds.Bottom <= 82 && result.Bounds.Top >= 40, $"{result.Bounds}");
        Assert.Equal(255, result.Pixels.ColorPlanes[2].Data[0]);
        Assert.Contains((byte)255, result.Pixels.Alpha!.Data);

        var scaled = TextRenderer.Render(data.WithTransform(new TextTransform(2, 0, 0, 2, 100, 80)));
        Assert.InRange(scaled.Bounds.Width, result.Bounds.Width * 2 - 3, result.Bounds.Width * 2 + 3);
        var turned = TextRenderer.Render(data.WithTransform(new TextTransform(0, 1, -1, 0, 100, 80)));
        Assert.True(turned.Bounds.Height > turned.Bounds.Width, $"{turned.Bounds}");
    }

    [Fact]
    public void Missing_fonts_are_substituted_and_reported()
    {
        Font();
        var data = TextLayerData.CreatePoint("Hi", new TextStyle { FontPostScriptName = "Strayta-NoSuchFont", FontSize = 30 }, 0, 0);
        var result = TextRenderer.Render(data);
        Assert.Equal(["Strayta-NoSuchFont"], result.MissingFonts);
        Assert.NotNull(result.Layout.SubstituteFont);
        Assert.NotNull(result.Pixels);
    }

    [Fact]
    public void Rendering_to_16_bit_grayscale()
    {
        var data = TextLayerData.CreatePoint("Hi", Style(30) with { FillColor = TextColor.White }, 10, 40);
        var result = TextRenderer.Render(data, new TextRenderOptions { ColorMode = ColorMode.Grayscale, BitDepth = 16 });
        Assert.Single(result.Pixels!.ColorPlanes);
        Assert.Equal(16, result.Pixels.BitDepth);
        Assert.Equal(65535, result.Pixels.ColorPlanes[0].AsUInt16()[0]);
    }

    [Fact]
    public void Bounds_for_the_file_come_from_the_layout()
    {
        var data = TextLayerData.CreatePoint("Hi", Style(30), 0, 0);
        var layout = TextLayout.Create(data);
        var bounds = layout.ToTextBounds();
        var back = PsdTypeLayer.Read(PsdTypeLayer.Encode(data, bounds));
        Assert.True(data.SameContent(back));
        Assert.True(bounds.Ink.Left >= bounds.Layout.Left - 1 && bounds.Ink.Top >= bounds.Layout.Top);
    }

    // ---- Corpus fidelity ---------------------------------------------------------------------------

    [Fact]
    public void Corpus_type_layers_match_photoshops_pixels()
    {
        var dirs = new List<string>();
        if (Environment.GetEnvironmentVariable("STRAYTA_CORPUS") is { Length: > 0 } c) dirs.Add(c);
        if (Environment.GetEnvironmentVariable("STRAYTA_TEXT_CORPUS") is { Length: > 0 } t) dirs.AddRange(t.Split(Path.PathSeparator));
        dirs = dirs.Where(Directory.Exists).ToList();
        if (dirs.Count == 0) Assert.Skip("Set STRAYTA_CORPUS (and optionally STRAYTA_TEXT_CORPUS) to compare with Photoshop's type pixels.");
        var fonts = FontCatalog.System;
        if (Environment.GetEnvironmentVariable("STRAYTA_FONT_DIRS") is { Length: > 0 } extra)
        {
            var more = FontCatalog.FromFiles([]);
            foreach (var d in extra.Split(Path.PathSeparator)) more.AddFolder(d);
            fonts = FontCatalog.Merge(fonts, more);
        }

        var scores = new List<(string Name, double Similarity)>();
        foreach (var f in dirs.SelectMany(d => Directory.EnumerateFiles(d, "*.ps?", SearchOption.AllDirectories)).Where(f => !Path.GetFileName(f).StartsWith("._")))
        {
            var doc = PsdFile.Open(f).ToDocument();
            foreach (var layer in doc.Root.Descendants().OfType<PixelLayer>())
            {
                if (layer.SourceData is not PsdLayerRecord record || PsdTypeLayer.Read(record) is not { } data || layer.Pixels is null) continue;
                if (data.Warp is not null || data.FontsUsed.Any(n => !fonts.Contains(n))) continue;
                var render = TextRenderer.Render(data, new TextRenderOptions { Fonts = fonts, ColorMode = doc.ColorMode, BitDepth = doc.BitDepth });
                Assert.NotNull(render.Pixels);
                var r = TextFidelity.Compare(render.Pixels, render.Bounds, layer.Pixels, layer.Bounds);
                scores.Add(($"{Path.GetFileName(f)}: {layer.Name}", r.Similarity));
                // Every layer lands where Photoshop put it (no better whole-pixel shift).
                Assert.True(r.OffsetX == 0 && r.OffsetY == 0, $"{Path.GetFileName(f)} \"{layer.Name}\" is off by ({r.OffsetX}, {r.OffsetY})");
            }
        }
        if (scores.Count == 0) Assert.Skip("No type layer in the corpus uses an installed font.");
        // Large type matches closely (similarity 0.95–0.98); 12 px Arial, which Photoshop hints, only to about 0.73.
        var worst = scores.MinBy(s => s.Similarity);
        Assert.True(worst.Similarity > 0.7, $"{worst.Name}: similarity {worst.Similarity:F3}");
    }
}
