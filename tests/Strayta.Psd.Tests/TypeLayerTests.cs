using System.Text;
using Strayta.Core;
using Strayta.Core.Text;
using Strayta.Psd.Descriptors;
using Strayta.Psd.Text;

namespace Strayta.Psd.Tests;

/// <summary>
/// Type layers: engine data syntax, reading 'TySh' into <see cref="TextLayerData"/> and writing it back. The corpus
/// tests run over every type layer under $STRAYTA_CORPUS and the folders in $STRAYTA_TEXT_CORPUS (separated like
/// PATH), and are skipped when neither is set.
/// </summary>
public class TypeLayerTests
{
    // ---- Engine data syntax -------------------------------------------------------------------------

    [Fact]
    public void Engine_data_parses_and_writes_back_in_photoshops_layout()
    {
        var bytes = Encoding.Latin1.GetBytes(
            "\n\n<<\n\t/EngineDict\n\t<<\n\t\t/Editor\n\t\t<<\n\t\t\t/Text (þÿ\0H\0i\0\\(\0\r)\n\t\t>>\n\t\t/Runs [\n\t\t<<\n\t\t\t/A 1\n\t\t>>\n\t\t]\n"
            + "\t\t/Axis [ 1.0 .5 -.25 ]\n\t\t/Empty [ ]\n\t\t/Flag true\n\t\t/Name /Roman\n\t>>\n>>");
        var root = EngineData.Parse(bytes);
        var ed = root.Dict("EngineDict")!;
        Assert.Equal("Hi(\r", ed.Dict("Editor")!.String("Text"));
        Assert.Equal(0.5, ((EdNumber)ed.Array("Axis")!.Items[1]).Value);
        Assert.Equal("Roman", ((EdName)ed["Name"]!).Name);
        Assert.Equal(bytes, EngineData.Write(root));
    }

    [Theory]
    [InlineData(1.0, false, "1.0")]
    [InlineData(0.0, false, "0.0")]
    [InlineData(0.5, false, ".5")]
    [InlineData(-0.25, false, "-.25")]
    [InlineData(27.166666666, false, "27.16667")]
    [InlineData(12.0, true, "12")]
    [InlineData(1.33, false, "1.33")]
    public void Numbers_are_spelled_as_photoshop_does(double value, bool integer, string expected) =>
        Assert.Equal(expected, EdNumber.Format(value, integer));

    [Fact]
    public void Strings_escape_parentheses_and_backslashes_bytewise()
    {
        // U+2028 is 20 28 in UTF-16BE: its second byte is '(' and gets escaped too.
        var raw = EngineData.EncodeString("(\\\u2028");
        Assert.Equal(new byte[] { 0xFE, 0xFF, 0, (byte)'\\', (byte)'(', 0, (byte)'\\', (byte)'\\', 0x20, (byte)'\\', (byte)'(' }, raw);
        var d = new EdDict();
        d.Set("S", new EdString("(\\\u2028"));
        Assert.Equal("(\\\u2028", EngineData.Parse(EngineData.Write(d)).String("S"));
    }

    // ---- New layers -----------------------------------------------------------------------------------

    [Fact]
    public void A_new_point_layer_reads_back_as_written()
    {
        var style = new TextStyle { FontPostScriptName = "ArialMT", FontSize = 36, FillColor = new TextColor(1, 0.5, 0), Tracking = 50 };
        var data = TextLayerData.CreatePoint("Hello (world)\nSecond", style, 100, 200);
        var tySh = PsdTypeLayer.Encode(data, new TextBounds(new TextRect(0, -33, 220, 8), new TextRect(1, -26, 219, 7)));
        Assert.Equal(0, tySh.Length % 4);
        var back = PsdTypeLayer.Read(tySh);
        Assert.True(data.SameContent(back), back.ToString());
        Assert.Equal("ArialMT", back.StyleAt(0).FontPostScriptName);
        Assert.Equal(new TextTransform(1, 0, 0, 1, 100, 200), back.Transform);
        // Photoshop's descriptor text uses '\r' between paragraphs.
        Assert.Equal("Hello (world)\rSecond", TextDescriptor(tySh).Text("Txt "));
    }

    [Fact]
    public void A_new_paragraph_layer_keeps_its_box_and_alignment()
    {
        var data = TextLayerData.CreateParagraph("Some words that wrap", new TextStyle { FontPostScriptName = "ArialMT", FontSize = 20 },
            new TextRect(10, 20, 110, 220), new ParagraphStyle { Justification = TextJustification.Center });
        var back = PsdTypeLayer.Read(PsdTypeLayer.Encode(data));
        Assert.Equal(TextKind.Paragraph, back.Kind);
        Assert.Equal(new TextRect(0, 0, 100, 200), back.Box);
        Assert.Equal(TextJustification.Center, back.ParagraphStyleAt(0).Justification);
        Assert.True(data.SameContent(back));
    }

    [Fact]
    public void Several_styles_and_paragraphs_round_trip()
    {
        var a = new TextStyle { FontPostScriptName = "ArialMT", FontSize = 30 };
        var data = TextLayerData.CreatePoint("Red and blue\nnext", a, 0, 50)
            .ApplyStyle(0, 3, s => s with { FillColor = new TextColor(1, 0, 0), FauxBold = true })
            .ApplyStyle(8, 4, s => s with { FontPostScriptName = "Helvetica-Bold", Underline = true, Caps = TextCaps.AllCaps })
            .ApplyParagraphStyle(13, 1, p => p with { Justification = TextJustification.Right, SpaceBefore = 6 });
        Assert.Equal(4, data.StyleRuns.Count);
        Assert.Equal(2, data.ParagraphRuns.Count);
        var back = PsdTypeLayer.Read(PsdTypeLayer.Encode(data));
        Assert.True(data.SameContent(back));
        Assert.Equal(["ArialMT", "Helvetica-Bold"], back.FontsUsed);
    }

    [Fact]
    public void Editing_keeps_runs_consistent()
    {
        var s = new TextStyle { FontSize = 10 };
        var data = TextLayerData.CreatePoint("abc\ndef", s, 0, 0).ApplyStyle(2, 1, x => x with { FontSize = 20 });
        var edited = data.ReplaceText(1, 1, "XYZ");
        Assert.Equal("aXYZc\ndef", edited.Text);
        Assert.Equal(new[] { 4, 1, 4 }, edited.StyleRuns.Select(r => r.Length));
        Assert.Equal(10, edited.StyleAt(1).FontSize); // typed text takes the style before it
        var joined = edited.ReplaceText(5, 1, "");
        Assert.Equal("aXYZcdef", joined.Text);
        Assert.Single(joined.Paragraphs);
        var empty = data.WithText("");
        Assert.Equal("", empty.Text);
        Assert.Single(empty.StyleRuns);
        Assert.Equal("q", empty.ReplaceText(0, 0, "q").Text);
    }

    // ---- Corpus ------------------------------------------------------------------------------------------

    public static TheoryData<string> TypeLayers()
    {
        var data = new TheoryData<string>();
        foreach (var (file, index) in CorpusTypeLayers()) data.Add($"{file}|{index}");
        if (data.Count == 0) data.Add("");
        return data;
    }

    private static IEnumerable<(string File, int Index)> CorpusTypeLayers()
    {
        var dirs = new List<string>();
        if (Environment.GetEnvironmentVariable("STRAYTA_CORPUS") is { Length: > 0 } c) dirs.Add(c);
        if (Environment.GetEnvironmentVariable("STRAYTA_TEXT_CORPUS") is { Length: > 0 } t) dirs.AddRange(t.Split(Path.PathSeparator));
        foreach (var dir in dirs.Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(dir, "*.ps?", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith("._")).Order())
            {
                PsdFile file;
                try
                {
                    file = PsdFile.Open(f, new PsdReadOptions { SkipLayerPixels = true, SkipComposite = true, MaxRawBlockBytes = long.MaxValue });
                }
                catch (PsdFormatException)
                {
                    continue;
                }
                for (int i = 0; i < file.Layers.Count; i++)
                    if (file.Layers[i].FindBlock("TySh") is not null) yield return (f, i);
            }
    }

    private static byte[] Load(string key, out PsdLayerRecord record)
    {
        var parts = key.Split('|');
        var file = PsdFile.Open(parts[0], new PsdReadOptions { SkipLayerPixels = true, SkipComposite = true, MaxRawBlockBytes = long.MaxValue });
        record = file.Layers[int.Parse(parts[1])];
        return record.FindBlock("TySh")!.Data!;
    }

    [Theory]
    [MemberData(nameof(TypeLayers))]
    public void Corpus_engine_data_rewrites_identically(string key)
    {
        if (key.Length == 0) Assert.Skip("Set STRAYTA_CORPUS (and optionally STRAYTA_TEXT_CORPUS) to run type layer corpus tests.");
        var engine = ((RawValue)TextDescriptor(Load(key, out _))["EngineData"]!).Data;
        Assert.Equal(engine, EngineData.Write(EngineData.Parse(engine)));
    }

    [Theory]
    [MemberData(nameof(TypeLayers))]
    public void Corpus_unchanged_text_writes_identical_bytes(string key)
    {
        if (key.Length == 0) Assert.Skip("Set STRAYTA_CORPUS (and optionally STRAYTA_TEXT_CORPUS) to run type layer corpus tests.");
        var tySh = Load(key, out var record);
        var data = PsdTypeLayer.Read(record)!;
        Assert.Same(tySh, PsdTypeLayer.Encode(data));
        // Also through the full encoder: the model regenerates the block byte for byte.
        Assert.Equal(tySh, PsdTypeLayer.Regenerate(tySh));
    }

    [Theory]
    [MemberData(nameof(TypeLayers))]
    public void Corpus_edits_read_back_as_intended(string key)
    {
        if (key.Length == 0) Assert.Skip("Set STRAYTA_CORPUS (and optionally STRAYTA_TEXT_CORPUS) to run type layer corpus tests.");
        Load(key, out var record);
        var data = PsdTypeLayer.Read(record)!;
        var edited = data.ReplaceText(0, Math.Min(1, data.Text.Length), "Ab(c)\\")
            .ApplyStyle(0, 2, s => s with { FontSize = s.FontSize * 1.5, FillColor = new TextColor(0.25, 0.5, 0.75) })
            .ApplyStyle(2, 3, s => s with { FontPostScriptName = "Strayta-TestFont", Tracking = 120 })
            .ApplyParagraphStyle(p => p with { Justification = TextJustification.Center });
        var written = PsdTypeLayer.Apply(record, edited);
        var back = PsdTypeLayer.Read(written)!;
        Assert.True(edited.SameContent(back), $"{edited} vs {back}");
        Assert.True(PsdTypeLayer.IsRegenerated(written.FindBlock("TySh")!.Data));

        // Keys the model does not cover survive in the edited runs.
        var original = TextDescriptor(record.FindBlock("TySh")!.Data!);
        var again = TextDescriptor(written.FindBlock("TySh")!.Data!);
        Assert.Equal(original.Items.Select(k => k.Key), again.Items.Select(k => k.Key));
        var oldRoot = EngineData.Parse(((RawValue)original["EngineData"]!).Data);
        var newRoot = EngineData.Parse(((RawValue)again["EngineData"]!).Data);
        Assert.Equal(oldRoot.Entries.Select(e => e.Key), newRoot.Entries.Select(e => e.Key));
        Assert.Equal(oldRoot.Dict("EngineDict")!.Entries.Select(e => e.Key), newRoot.Dict("EngineDict")!.Entries.Select(e => e.Key));
        Assert.Contains(newRoot.Dict("ResourceDict")!.Array("FontSet")!.Items, f => ((EdDict)f).String("Name") == "Strayta-TestFont");
    }

    private static Descriptor TextDescriptor(byte[] tySh) => new DescriptorReader(tySh, 56).ReadDescriptor();

    [Fact]
    public void A_type_layer_saved_in_a_document_opens_with_its_text()
    {
        var doc = new Document(300, 200, ColorMode.Rgb, 8);
        var data = TextLayerData.CreatePoint("Saved", new TextStyle { FontPostScriptName = "ArialMT", FontSize = 40 }, 20, 100);
        var layer = new PixelLayer { Name = "Saved", Bounds = new PixelRect(20, 70, 120, 110), SourceData = PsdTypeLayer.Create(data) };
        layer.Tags.Add("text");
        doc.Root.Add(layer);
        var ms = new MemoryStream();
        PsdWriter.Write(doc, ms);
        ms.Position = 0;
        var file = PsdFile.Read(ms);
        var record = file.Layers.Single();
        Assert.True(PsdTypeLayer.IsTypeLayer(record));
        Assert.True(data.SameContent(PsdTypeLayer.Read(record)));
        Assert.Equal(PsdLayerKind.Text, PsdLayerKinds.Classify(record));
    }
}
