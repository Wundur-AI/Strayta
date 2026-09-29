using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Strayta.Core.Text;
using Strayta.Psd.Descriptors;

namespace Strayta.Psd.Text;

/// <summary>
/// Reads and writes type layers ('TySh' blocks) as <see cref="TextLayerData"/>.
/// <para>
/// A 'TySh' block is: version (2 bytes, 1); the text transform as six doubles (xx, xy, yx, yy, tx, ty); the text
/// version (2 bytes, 50); a versioned descriptor ('TxLr': "Txt ", "textGridding", "Ornt", "AntA", "bounds",
/// "boundingBox", "TextIndex", and "EngineData" holding the text engine data, see <see cref="EngineData"/>); the warp
/// version (2 bytes, 1) and a versioned warp descriptor; four 32-bit values (zero in every file seen); zero padding
/// to a multiple of four bytes.
/// </para>
/// <para>
/// In the engine data, "EngineDict/Editor/Text" is the text with '\r' after every paragraph (so it always ends with
/// one), "StyleRun" and "ParagraphRun" hold style dictionaries ("RunArray") and how many characters each covers
/// ("RunLengthArray", counting that final '\r'). A run's style only lists what differs from the document's normal
/// style ("ResourceDict/StyleSheetSet" at "TheNormalStyleSheet"; paragraphs likewise), and fonts are indices into
/// "ResourceDict/FontSet". "EngineDict/Rendered/Shapes" says whether the text is point text (ShapeType 0) or paragraph
/// text (ShapeType 1, with "BoxBounds" [left top right bottom]).
/// </para>
/// Unedited layers are written back byte for byte; an edit rewrites the engine data in Photoshop's own layout, keeping
/// every key the model does not cover.
/// </summary>
public static class PsdTypeLayer
{
    private static readonly ConditionalWeakTable<byte[], object> Regenerated = new();

    /// <summary>Canonical key order of character style dictionaries (as in Photoshop's normal style sheet).</summary>
    private static readonly string[] StyleKeyOrder =
    [
        "Font", "FontSize", "FauxBold", "FauxItalic", "AutoLeading", "Leading", "HorizontalScale", "VerticalScale", "Tracking",
        "AutoKerning", "Kerning", "BaselineShift", "FontCaps", "FontBaseline", "Underline", "Strikethrough", "Ligatures", "DLigatures",
        "BaselineDirection", "Tsume", "StyleRunAlignment", "Language", "NoBreak", "FillColor", "StrokeColor", "FillFlag", "StrokeFlag",
        "FillFirst", "YUnderline", "OutlineWidth", "CharacterDirection", "HindiNumbers", "Kashida", "DiacriticPos",
    ];

    /// <summary>Canonical key order of paragraph property dictionaries.</summary>
    private static readonly string[] ParagraphKeyOrder =
    [
        "Justification", "FirstLineIndent", "StartIndent", "EndIndent", "SpaceBefore", "SpaceAfter", "AutoHyphenate",
        "HyphenatedWordSize", "PreHyphen", "PostHyphen", "ConsecutiveHyphens", "Zone", "WordSpacing", "LetterSpacing",
        "GlyphSpacing", "AutoLeading", "LeadingType", "Hanging", "Burasagari", "KinsokuOrder", "EveryLineComposer",
    ];

    /// <summary>True when the record is a type layer.</summary>
    public static bool IsTypeLayer(PsdLayerRecord record) => record?.FindBlock("TySh") is not null;

    /// <summary>Reads a layer's text, or null when it is not a type layer or its data cannot be read.</summary>
    public static TextLayerData? Read(PsdLayerRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.FindBlock("TySh")?.Data is not { } data) return null;
        try
        {
            return Read(data);
        }
        catch (PsdFormatException)
        {
            return null;
        }
    }

    /// <summary>Reads a 'TySh' block. Throws <see cref="PsdFormatException"/> when it cannot be read.</summary>
    public static TextLayerData Read(byte[] tySh)
    {
        ArgumentNullException.ThrowIfNull(tySh);
        var source = TypeSource.Parse(tySh);
        return source.Parsed;
    }

    /// <summary>
    /// A 'TySh' block for <paramref name="data"/>. Data read from a block and not changed since gives back that
    /// block's exact bytes. <paramref name="bounds"/> (from laying the text out) updates the stored bounds.
    /// </summary>
    public static byte[] Encode(TextLayerData data, TextBounds? bounds = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var source = data.SourceData as TypeSource;
        if (source is not null && data.Equals(source.Parsed) && bounds is null) return source.Original;
        var encoded = (source ?? TypeSource.New()).Encode(data, bounds);
        Regenerated.AddOrUpdate(encoded, true);
        return encoded;
    }

    /// <summary>
    /// The record with its text replaced by <paramref name="data"/> (every other block kept). The caller supplies the
    /// matching pixels on the layer.
    /// </summary>
    public static PsdLayerRecord Apply(PsdLayerRecord record, TextLayerData data, TextBounds? bounds = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        var encoded = Encode(data, bounds);
        var blocks = record.Blocks.ToList();
        int at = blocks.FindIndex(b => b.Key == "TySh");
        var block = new TaggedBlock("8BIM", "TySh", 0, encoded.Length, encoded);
        if (at >= 0)
        {
            if (ReferenceEquals(blocks[at].Data, encoded)) return record;
            blocks[at] = block;
        }
        else blocks.Add(block);
        return record.WithBlocks(blocks, record.Mask);
    }

    /// <summary>A layer record for a new type layer (its pixels and name come from the layer it is attached to).</summary>
    public static PsdLayerRecord Create(TextLayerData data, TextBounds? bounds = null)
    {
        var encoded = Encode(data, bounds);
        return new PsdLayerRecord { Blocks = [new TaggedBlock("8BIM", "TySh", 0, encoded.Length, encoded)] };
    }

    /// <summary>
    /// True when <paramref name="tySh"/> was produced by <see cref="Encode"/> from edited text (so the document's cached
    /// text engine data, the global 'Txt2' block, no longer matches it).
    /// </summary>
    public static bool IsRegenerated(byte[]? tySh) => tySh is not null && Regenerated.TryGetValue(tySh, out _);

    /// <summary>Marks a copy of regenerated data (e.g. with its transform moved) as regenerated too.</summary>
    internal static void MarkRegenerated(byte[] tySh) => Regenerated.AddOrUpdate(tySh, true);

    /// <summary>
    /// A copy of <paramref name="data"/> whose new styles (those without source data) borrow the unknown settings and
    /// shared resources of <paramref name="donor"/>, a type layer of the same document, so a new layer matches the
    /// document's other text data (fonts list, style sheets).
    /// </summary>
    public static TextLayerData UseResourcesOf(TextLayerData data, TextLayerData donor)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(donor);
        if (donor.SourceData is not TypeSource d) return data;
        return new TextLayerData(data.Text, data.StyleRuns, data.ParagraphRuns)
        {
            Kind = data.Kind, Box = data.Box, Transform = data.Transform, Orientation = data.Orientation, AntiAlias = data.AntiAlias,
            Warp = data.Warp, SourceData = TypeSource.New(d.Root),
        };
    }

    /// <summary>Re-encodes a block from its parsed model without the unchanged-data shortcut (for tests).</summary>
    internal static byte[] Regenerate(byte[] tySh)
    {
        var source = TypeSource.Parse(tySh);
        return source.Encode(source.Parsed, null, force: true);
    }

    // ---- Parsing ----------------------------------------------------------------------------------

    /// <summary>What a 'TySh' block was read from, kept so an edit can be written back with everything else intact.</summary>
    internal sealed class TypeSource
    {
        public byte[] Original { get; private init; } = [];
        public TextLayerData Parsed { get; private set; } = null!;
        public short Version { get; private init; } = 1;
        public short TextVersion { get; private init; } = 50;
        public Descriptor Text { get; private init; } = null!;
        public byte[] WarpBytes { get; private init; } = [];
        public byte[] Tail { get; private init; } = new byte[16];
        public EdDict Root { get; private init; } = null!;

        /// <summary>Style and paragraph runs that covered only the final '\r' (see <c>Runs</c>).</summary>
        private EdDict? StyleTerminator { get; set; }
        private EdDict? ParagraphTerminator { get; set; }
        public byte[] EngineBytes { get; private init; } = [];

        public static TypeSource Parse(byte[] data)
        {
            try
            {
                if (data.Length < 56) throw new PsdFormatException("Type layer data is too short");
                short version = BinaryPrimitives.ReadInt16BigEndian(data);
                var t = new double[6];
                for (int i = 0; i < 6; i++) t[i] = BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(2 + i * 8));
                short textVersion = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(50));
                if (BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(52)) != 16) throw new PsdFormatException("Unsupported type layer descriptor version");
                var reader = new DescriptorReader(data, 56);
                var text = reader.ReadDescriptor();
                int warpStart = reader.Position;
                var warpReader = new DescriptorReader(data, warpStart + 6);
                var warp = warpReader.ReadDescriptor();
                int warpEnd = warpReader.Position;
                var tail = new byte[16];
                data.AsSpan(warpEnd, Math.Min(16, data.Length - warpEnd)).CopyTo(tail);
                if (text["EngineData"] is not RawValue { Data: var engine }) throw new PsdFormatException("Type layer has no engine data");
                var root = EngineData.Parse(engine);
                var source = new TypeSource
                {
                    Original = data,
                    Version = version,
                    TextVersion = textVersion,
                    Text = text,
                    WarpBytes = data[warpStart..warpEnd],
                    Tail = tail,
                    Root = root,
                    EngineBytes = engine,
                };
                source.Parsed = source.ToModel(new TextTransform(t[0], t[1], t[2], t[3], t[4], t[5]), text, warp);
                return source;
            }
            catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or InvalidCastException or NullReferenceException)
            {
                throw new PsdFormatException($"Unreadable type layer: {e.Message}");
            }
        }

        /// <summary>A source for a new layer: Photoshop's default resources, or those of another layer's engine data.</summary>
        public static TypeSource New(EdDict? resourcesFrom = null)
        {
            var root = DefaultEngineData();
            if (resourcesFrom is not null)
            {
                foreach (var key in new[] { "ResourceDict", "DocumentResources" })
                    if (resourcesFrom.Dict(key) is { } r) root.Set(key, r.Clone());
            }
            var text = new Descriptor
            {
                ClassId = "TxLr",
                Items =
                [
                    new("Txt ", new TextValue("")),
                    new("textGridding", new EnumValue("textGridding", "None")),
                    new("Ornt", new EnumValue("Ornt", "Hrzn")),
                    new("AntA", new EnumValue("Annt", "antiAliasSharp")),
                    new("bounds", Rect("bounds", new TextRect(0, 0, 0, 0))),
                    new("boundingBox", Rect("boundingBox", new TextRect(0, 0, 0, 0))),
                    new("TextIndex", new IntegerValue(0)),
                    new("EngineData", new RawValue("tdta", [])),
                ],
            };
            var warp = new Descriptor
            {
                ClassId = "warp",
                Items =
                [
                    new("warpStyle", new EnumValue("warpStyle", "warpNone")),
                    new("warpValue", new DoubleValue(0)),
                    new("warpPerspective", new DoubleValue(0)),
                    new("warpPerspectiveOther", new DoubleValue(0)),
                    new("warpRotate", new EnumValue("Ornt", "Hrzn")),
                ],
            };
            var warpBytes = new byte[2 + 0];
            BinaryPrimitives.WriteInt16BigEndian(warpBytes, 1);
            return new TypeSource { Text = text, WarpBytes = [.. warpBytes, .. DescriptorWriter.WriteVersioned(warp)], Root = root };
        }

        private EdDict EngineDict => Root.Dict("EngineDict") ?? throw new PsdFormatException("Engine data has no EngineDict");
        private EdDict Resources => Root.Dict("ResourceDict") ?? throw new PsdFormatException("Engine data has no ResourceDict");

        private TextLayerData ToModel(TextTransform transform, Descriptor text, Descriptor warp)
        {
            var ed = EngineDict;
            string raw = ed.Dict("Editor")?.String("Text") ?? "";
            // Every paragraph ends with '\r', the last one included; the model leaves that final one out.
            int length = raw.EndsWith('\r') ? raw.Length - 1 : raw.Length;
            string content = raw[..length].Replace('\r', '\n');

            var fonts = FontNames(Resources);
            var normalStyle = NormalStyle(Resources);
            var normalParagraph = NormalParagraph(Resources);
            var styles = Runs(ed.Dict("StyleRun"), length, run => ReadStyle(run, normalStyle, fonts), out var styleEnd);
            var paragraphs = Runs(ed.Dict("ParagraphRun"), length, run => ReadParagraph(run, normalParagraph), out var paragraphEnd);
            StyleTerminator = styleEnd;
            ParagraphTerminator = paragraphEnd;
            if (styles.Count == 0) styles.Add(new(length, ReadStyle(new EdDict(), normalStyle, fonts) with { SourceData = null }));
            if (paragraphs.Count == 0) paragraphs.Add(new(length, new ParagraphStyle()));

            var shape = ShapeOf(ed);
            bool box = shape?.Number("ShapeType") == 1;
            var boxBounds = shape?.Dict("Cookie")?.Dict("Photoshop")?.Array("BoxBounds") is { Items.Count: 4 } bb
                ? new TextRect(Num(bb.Items[0]), Num(bb.Items[1]), Num(bb.Items[2]), Num(bb.Items[3]))
                : default;
            string? warpStyle = warp.Enum("warpStyle") is { } ws && ws != "warpNone" ? ws : warp.Has("customEnvelopeWarp") ? "warpCustom" : null;

            return new TextLayerData(content, styles, paragraphs)
            {
                Kind = box ? TextKind.Paragraph : TextKind.Point,
                Box = boxBounds,
                Transform = transform,
                Orientation = text.Enum("Ornt") == "Vrtc" ? TextOrientation.Vertical : TextOrientation.Horizontal,
                AntiAlias = AntiAliasOf(text.Enum("AntA"), ed.Number("AntiAlias")),
                Warp = warpStyle,
                SourceData = this,
            };
        }

        private static EdDict? ShapeOf(EdDict ed) =>
            ed.Dict("Rendered")?.Dict("Shapes")?.Array("Children")?.Items.FirstOrDefault() as EdDict;

        /// <summary>
        /// Runs over the text without its final '\r'. A last run that covers only that '\r' (Photoshop writes one when
        /// the end of the text was formatted apart) comes back in <paramref name="terminator"/>.
        /// </summary>
        private static List<TextRun<T>> Runs<T>(EdDict? runs, int textLength, Func<EdDict, T> read, out EdDict? terminator)
        {
            terminator = null;
            var list = new List<TextRun<T>>();
            if (runs?.Array("RunArray") is not { } array || runs.Array("RunLengthArray") is not { } lengths) return list;
            int remaining = textLength;
            int count = Math.Min(array.Items.Count, lengths.Items.Count);
            for (int i = 0; i < count; i++)
            {
                if (array.Items[i] is not EdDict run) continue;
                int n = (int)Num(lengths.Items[i]);
                int take = Math.Clamp(n, 0, remaining);
                remaining -= take;
                if (take > 0 || list.Count == 0) list.Add(new(take, read(run)));
                else if (i == count - 1 && n == 1) terminator = run;
            }
            // The last run covered the final '\r' too; give any characters the runs did not cover to it.
            if (remaining > 0 && list.Count > 0) list[^1] = list[^1] with { Length = list[^1].Length + remaining };
            if (list.Count > 1 && list[0].Length == 0) list.RemoveAt(0);
            return list;
        }

        private static List<string> FontNames(EdDict resources) =>
            resources.Array("FontSet")?.Items.Select(f => (f as EdDict)?.String("Name") ?? "").ToList() ?? [];

        private static EdDict NormalStyle(EdDict resources)
        {
            int index = (int)(resources.Number("TheNormalStyleSheet") ?? 0);
            var sheets = resources.Array("StyleSheetSet")?.Items;
            return (sheets is not null && index < sheets.Count ? (sheets[index] as EdDict)?.Dict("StyleSheetData") : null) ?? new EdDict();
        }

        private static EdDict NormalParagraph(EdDict resources)
        {
            int index = (int)(resources.Number("TheNormalParagraphSheet") ?? 0);
            var sheets = resources.Array("ParagraphSheetSet")?.Items;
            return (sheets is not null && index < sheets.Count ? (sheets[index] as EdDict)?.Dict("Properties") : null) ?? new EdDict();
        }

        private static TextStyle ReadStyle(EdDict run, EdDict normal, List<string> fonts)
        {
            var own = run.Dict("StyleSheet")?.Dict("StyleSheetData") ?? new EdDict();
            EdValue? Get(string key) => own[key] ?? normal[key];
            double N(string key, double fallback) => Get(key) is EdNumber n ? n.Value : fallback;
            bool B(string key, bool fallback) => Get(key) is EdBool b ? b.Value : fallback;
            int fontIndex = (int)N("Font", 0);
            string font = fontIndex >= 0 && fontIndex < fonts.Count ? fonts[fontIndex] : "";
            var d = new TextStyle();
            return new TextStyle
            {
                FontPostScriptName = font,
                FontSize = N("FontSize", d.FontSize),
                AutoLeading = B("AutoLeading", d.AutoLeading),
                Leading = N("Leading", d.Leading),
                Tracking = (int)Math.Round(N("Tracking", 0)),
                Kerning = B("AutoKerning", true) ? TextKerning.Metrics : TextKerning.None,
                ManualKerning = (int)Math.Round(N("Kerning", 0)),
                BaselineShift = N("BaselineShift", 0),
                FillColor = ColorOf(Get("FillColor") as EdDict) ?? TextColor.Black,
                FauxBold = B("FauxBold", false),
                FauxItalic = B("FauxItalic", false),
                Caps = (TextCaps)Math.Clamp((int)N("FontCaps", 0), 0, 2),
                BaselinePosition = (TextBaselinePosition)Math.Clamp((int)N("FontBaseline", 0), 0, 2),
                Underline = B("Underline", false),
                Strikethrough = B("Strikethrough", false),
                HorizontalScale = N("HorizontalScale", 1),
                VerticalScale = N("VerticalScale", 1),
                Ligatures = B("Ligatures", true),
                DiscretionaryLigatures = B("DLigatures", false),
                SourceData = run,
            };
        }

        private static ParagraphStyle ReadParagraph(EdDict run, EdDict normal)
        {
            var own = run.Dict("ParagraphSheet")?.Dict("Properties") ?? new EdDict();
            EdValue? Get(string key) => own[key] ?? normal[key];
            double N(string key, double fallback) => Get(key) is EdNumber n ? n.Value : fallback;
            return new ParagraphStyle
            {
                Justification = (TextJustification)Math.Clamp((int)N("Justification", 0), 0, 6),
                FirstLineIndent = N("FirstLineIndent", 0),
                StartIndent = N("StartIndent", 0),
                EndIndent = N("EndIndent", 0),
                SpaceBefore = N("SpaceBefore", 0),
                SpaceAfter = N("SpaceAfter", 0),
                Hyphenate = Get("AutoHyphenate") is EdBool { Value: var h } ? h : true,
                AutoLeadingFactor = N("AutoLeading", 1.2),
                SourceData = run,
            };
        }

        /// <summary>Colors are "/Type 1" (RGB) with "/Values [ a r g b ]", 0..1; "/Type 0" is gray [ a g ].</summary>
        private static TextColor? ColorOf(EdDict? color)
        {
            if (color?.Array("Values") is not { } values) return null;
            var v = values.Items.Select(Num).ToArray();
            return v.Length switch
            {
                >= 4 => new TextColor(v[1], v[2], v[3], v[0]),
                2 => new TextColor(v[1], v[1], v[1], v[0]),
                _ => null,
            };
        }

        private static EdDict ColorDict(TextColor c) => Dict(
            ("Type", EdNumber.Int(1)),
            ("Values", new EdArray([EdNumber.Real(c.A), EdNumber.Real(c.R), EdNumber.Real(c.G), EdNumber.Real(c.B)])));

        private static TextAntiAlias AntiAliasOf(string? descriptor, double? engine) => descriptor switch
        {
            "Anno" or "AnNo" => TextAntiAlias.None,
            "antiAliasSharp" => TextAntiAlias.Sharp,
            "AnCr" => TextAntiAlias.Crisp,
            "AnSt" => TextAntiAlias.Strong,
            "AnSm" => TextAntiAlias.Smooth,
            "antiAliasPlatformLCD" => TextAntiAlias.PlatformLcd,
            "antiAliasPlatformGray" => TextAntiAlias.PlatformGray,
            _ => engine switch
            {
                0 => TextAntiAlias.None,
                1 => TextAntiAlias.Crisp,
                2 => TextAntiAlias.Strong,
                3 => TextAntiAlias.Smooth,
                _ => TextAntiAlias.Sharp,
            },
        };

        private static (string Enum, int Engine) AntiAliasCodes(TextAntiAlias a) => a switch
        {
            TextAntiAlias.None => ("Anno", 0),
            TextAntiAlias.Crisp => ("AnCr", 1),
            TextAntiAlias.Strong => ("AnSt", 2),
            TextAntiAlias.Smooth => ("AnSm", 3),
            TextAntiAlias.PlatformLcd => ("antiAliasPlatformLCD", 5),
            TextAntiAlias.PlatformGray => ("antiAliasPlatformGray", 6),
            _ => ("antiAliasSharp", 4),
        };

        // ---- Writing ------------------------------------------------------------------------------

        public byte[] Encode(TextLayerData data, TextBounds? bounds, bool force = false)
        {
            var old = Parsed;
            bool engineUnchanged = !force && old is not null && EngineBytes.Length > 0 && data.Text == old.Text && data.Kind == old.Kind && data.Box == old.Box
                && data.AntiAlias == old.AntiAlias && data.Orientation == old.Orientation
                && data.StyleRuns.SequenceEqual(old.StyleRuns) && data.ParagraphRuns.SequenceEqual(old.ParagraphRuns);
            byte[] engine = engineUnchanged ? EngineBytes : EngineData.Write(BuildEngineData(data));

            var (aaEnum, _) = AntiAliasCodes(data.AntiAlias);
            var items = Text.Items.ToList();
            void Put(string key, DescriptorValue value)
            {
                int i = items.FindIndex(kv => kv.Key == key);
                if (i >= 0) items[i] = new(key, value);
                else items.Add(new(key, value));
            }
            Put("Txt ", new TextValue(data.Text.Replace('\n', '\r')));
            Put("Ornt", new EnumValue("Ornt", data.Orientation == TextOrientation.Vertical ? "Vrtc" : "Hrzn"));
            Put("AntA", new EnumValue("Annt", aaEnum));
            if (bounds is { } b)
            {
                Put("bounds", Rect(Text.Object("bounds"), "bounds", data.Kind == TextKind.Paragraph ? data.Box : b.Layout));
                Put("boundingBox", Rect(Text.Object("boundingBox"), "boundingBox", b.Ink));
            }
            else if (data.Kind == TextKind.Paragraph && (old is null || data.Box != old.Box || old.Kind != TextKind.Paragraph))
                Put("bounds", Rect(Text.Object("bounds"), "bounds", data.Box));
            Put("EngineData", new RawValue("tdta", engine));
            var text = new Descriptor { Name = Text.Name, ClassId = Text.ClassId, Items = items };

            var o = new MemoryStream();
            Span<byte> b8 = stackalloc byte[8];
            BinaryPrimitives.WriteInt16BigEndian(b8, Version);
            o.Write(b8[..2]);
            var t = data.Transform;
            foreach (double v in new[] { t.XX, t.XY, t.YX, t.YY, t.TX, t.TY })
            {
                BinaryPrimitives.WriteDoubleBigEndian(b8, v);
                o.Write(b8);
            }
            BinaryPrimitives.WriteInt16BigEndian(b8, TextVersion);
            o.Write(b8[..2]);
            o.Write(DescriptorWriter.WriteVersioned(text));
            o.Write(WarpBytes);
            o.Write(Tail);
            while (o.Length % 4 != 0) o.WriteByte(0);
            return o.ToArray();
        }

        private EdDict BuildEngineData(TextLayerData data)
        {
            var root = Root.Clone();
            var ed = root.Dict("EngineDict") ?? throw new PsdFormatException("Engine data has no EngineDict");
            var resources = root.Dict("ResourceDict") ?? throw new PsdFormatException("Engine data has no ResourceDict");
            var documentResources = root.Dict("DocumentResources");

            string raw = data.Text.Replace('\n', '\r') + "\r";
            (ed.Dict("Editor") ?? Add(ed, "Editor")).Set("Text", new EdString(raw));

            // Character runs; the last run also covers the final '\r'.
            var normalStyle = NormalStyle(resources);
            WriteRuns(ed.Dict("StyleRun") ?? Add(ed, "StyleRun"), data.StyleRuns, Parsed?.StyleRuns, StyleTerminator,
                style => WriteStyle(style, normalStyle, resources, documentResources));
            var normalParagraph = NormalParagraph(resources);
            WriteRuns(ed.Dict("ParagraphRun") ?? Add(ed, "ParagraphRun"), data.ParagraphRuns, Parsed?.ParagraphRuns, ParagraphTerminator,
                style => WriteParagraph(style, normalParagraph));

            ed.Set("AntiAlias", EdNumber.Int(AntiAliasCodes(data.AntiAlias).Engine));
            WriteShape(ed, data);
            return root;
        }

        /// <summary>
        /// "RunArray" and "RunLengthArray": the last run also covers the final '\r', unless the file had a run of its
        /// own for it and the end of the text still has the style it had then.
        /// </summary>
        private static void WriteRuns<T>(EdDict target, IReadOnlyList<TextRun<T>> runs, IReadOnlyList<TextRun<T>>? parsed, EdDict? terminator,
            Func<T, EdDict> write)
        {
            bool separateEnd = terminator is not null && parsed is { Count: > 0 } && runs.Count > 0 && runs[^1].Length > 0
                && Equals(runs[^1].Style, parsed[^1].Style);
            var array = new EdArray();
            var lengths = new EdArray();
            for (int i = 0; i < runs.Count; i++)
            {
                array.Items.Add(write(runs[i].Style));
                lengths.Items.Add(EdNumber.Int(runs[i].Length + (i == runs.Count - 1 && !separateEnd ? 1 : 0)));
            }
            if (separateEnd)
            {
                array.Items.Add(terminator!.Clone());
                lengths.Items.Add(EdNumber.Int(1));
            }
            target.Set("RunArray", array);
            target.Set("RunLengthArray", lengths);
        }

        private static EdDict Add(EdDict parent, string key)
        {
            var d = new EdDict();
            parent.Set(key, d);
            return d;
        }

        private static EdDict WriteStyle(TextStyle style, EdDict normal, EdDict resources, EdDict? documentResources)
        {
            var run = style.SourceData is EdDict source ? source.Clone() : Dict(("StyleSheet", Dict(("StyleSheetData", new EdDict()))));
            var sheet = run.Dict("StyleSheet") ?? Add(run, "StyleSheet");
            var own = sheet.Dict("StyleSheetData") ?? Add(sheet, "StyleSheetData");

            // Always name the font and size (Photoshop does); other settings only where they differ from what the
            // run already says or, failing that, the normal style.
            int font = FontIndex(style.FontPostScriptName, resources, documentResources);
            if (own["Font"] is not EdNumber { Value: var f } || f != font) own.Set("Font", EdNumber.Int(font), StyleKeyOrder);
            if (own["FontSize"] is not EdNumber { Value: var size } || Math.Abs(size - style.FontSize) > 1e-9)
                own.Set("FontSize", EdNumber.Real(style.FontSize), StyleKeyOrder);
            EdValue? Current(string key) => own[key] ?? normal[key];
            void Real(string key, double value, double fallback)
            {
                double current = Current(key) is EdNumber n ? n.Value : fallback;
                if (Math.Abs(current - value) > 1e-6) own.Set(key, EdNumber.Real(value), StyleKeyOrder);
            }
            void Int(string key, long value, long fallback)
            {
                long current = Current(key) is EdNumber n ? (long)Math.Round(n.Value) : fallback;
                if (current != value) own.Set(key, EdNumber.Int(value), StyleKeyOrder);
            }
            void Bool(string key, bool value, bool fallback)
            {
                bool current = Current(key) is EdBool b ? b.Value : fallback;
                if (current != value) own.Set(key, new EdBool(value), StyleKeyOrder);
            }
            Bool("FauxBold", style.FauxBold, false);
            Bool("FauxItalic", style.FauxItalic, false);
            Bool("AutoLeading", style.AutoLeading, true);
            Real("Leading", style.Leading, 0);
            Real("HorizontalScale", style.HorizontalScale, 1);
            Real("VerticalScale", style.VerticalScale, 1);
            Int("Tracking", style.Tracking, 0);
            Bool("AutoKerning", style.Kerning != TextKerning.None, true);
            Int("Kerning", style.ManualKerning, 0);
            Real("BaselineShift", style.BaselineShift, 0);
            Int("FontCaps", (int)style.Caps, 0);
            Int("FontBaseline", (int)style.BaselinePosition, 0);
            Bool("Underline", style.Underline, false);
            Bool("Strikethrough", style.Strikethrough, false);
            Bool("Ligatures", style.Ligatures, true);
            Bool("DLigatures", style.DiscretionaryLigatures, false);
            if (ColorOf(Current("FillColor") as EdDict) is not { } fill || !Close(fill, style.FillColor))
                own.Set("FillColor", ColorDict(style.FillColor), StyleKeyOrder);
            return run;
        }

        private static bool Close(TextColor a, TextColor b) =>
            Math.Abs(a.R - b.R) < 5e-6 && Math.Abs(a.G - b.G) < 5e-6 && Math.Abs(a.B - b.B) < 5e-6 && Math.Abs(a.A - b.A) < 5e-6;

        private static EdDict WriteParagraph(ParagraphStyle style, EdDict normal)
        {
            var run = style.SourceData is EdDict source
                ? source.Clone()
                : Dict(
                    ("ParagraphSheet", Dict(("DefaultStyleSheet", EdNumber.Int(0)), ("Properties", new EdDict()))),
                    ("Adjustments", Dict(
                        ("Axis", new EdArray([EdNumber.Real(1), EdNumber.Real(0), EdNumber.Real(1)])),
                        ("XY", new EdArray([EdNumber.Real(0), EdNumber.Real(0)])))));
            var sheet = run.Dict("ParagraphSheet") ?? Add(run, "ParagraphSheet");
            var own = sheet.Dict("Properties") ?? Add(sheet, "Properties");
            EdValue? Current(string key) => own[key] ?? normal[key];
            void Real(string key, double value, double fallback)
            {
                double current = Current(key) is EdNumber n ? n.Value : fallback;
                if (Math.Abs(current - value) > 1e-6) own.Set(key, EdNumber.Real(value), ParagraphKeyOrder);
            }
            long justification = Current("Justification") is EdNumber j ? (long)j.Value : 0;
            if (justification != (long)style.Justification) own.Set("Justification", EdNumber.Int((long)style.Justification), ParagraphKeyOrder);
            Real("FirstLineIndent", style.FirstLineIndent, 0);
            Real("StartIndent", style.StartIndent, 0);
            Real("EndIndent", style.EndIndent, 0);
            Real("SpaceBefore", style.SpaceBefore, 0);
            Real("SpaceAfter", style.SpaceAfter, 0);
            bool hyphenate = Current("AutoHyphenate") is EdBool h ? h.Value : true;
            if (hyphenate != style.Hyphenate) own.Set("AutoHyphenate", new EdBool(style.Hyphenate), ParagraphKeyOrder);
            Real("AutoLeading", style.AutoLeadingFactor, 1.2);
            return run;
        }

        /// <summary>The font's index in the FontSet, adding it (to the document's copy too) when it is new.</summary>
        private static int FontIndex(string postScriptName, EdDict resources, EdDict? documentResources)
        {
            var set = resources.Array("FontSet") ?? new EdArray();
            resources.Set("FontSet", set);
            for (int i = 0; i < set.Items.Count; i++)
                if ((set.Items[i] as EdDict)?.String("Name") == postScriptName) return i;
            EdDict Entry() => Dict(
                ("Name", new EdString(postScriptName)),
                ("Script", EdNumber.Int(0)),
                ("FontType", EdNumber.Int(1)),
                ("Synthetic", EdNumber.Int(0)));
            set.Items.Add(Entry());
            if (documentResources?.Array("FontSet") is { } docSet && !docSet.Items.Any(f => (f as EdDict)?.String("Name") == postScriptName))
                docSet.Items.Add(Entry());
            return set.Items.Count - 1;
        }

        private static void WriteShape(EdDict ed, TextLayerData data)
        {
            var shape = ShapeOf(ed);
            if (shape is null)
            {
                var rendered = ed.Dict("Rendered") ?? Add(ed, "Rendered");
                var shapes = rendered.Dict("Shapes") ?? Add(rendered, "Shapes");
                shape = new EdDict();
                shapes.Set("Children", new EdArray([shape]));
            }
            int type = data.Kind == TextKind.Paragraph ? 1 : 0;
            shape.Set("ShapeType", EdNumber.Int(type));
            var cookie = shape.Dict("Cookie") ?? Add(shape, "Cookie");
            var ps = cookie.Dict("Photoshop") ?? Add(cookie, "Photoshop");
            var baseDict = ps.Dict("Base")?.Clone() ?? Dict(
                ("TransformPoint0", new EdArray([EdNumber.Real(1), EdNumber.Real(0)])),
                ("TransformPoint1", new EdArray([EdNumber.Real(0), EdNumber.Real(1)])),
                ("TransformPoint2", new EdArray([EdNumber.Real(0), EdNumber.Real(0)])));
            baseDict.Set("ShapeType", EdNumber.Int(type));
            // Photoshop writes ShapeType first in Base.
            var st = baseDict.Entries.First(e => e.Key == "ShapeType");
            baseDict.Entries.Remove(st);
            baseDict.Entries.Insert(0, st);
            var rebuilt = new EdDict();
            rebuilt.Set("ShapeType", EdNumber.Int(type));
            if (type == 1)
            {
                var b = data.Box;
                bool same = ps.Array("BoxBounds") is { Items.Count: 4 } old
                    && new TextRect(Num(old.Items[0]), Num(old.Items[1]), Num(old.Items[2]), Num(old.Items[3])) == b;
                rebuilt.Set("BoxBounds", same ? ps.Array("BoxBounds")!
                    : new EdArray([EdNumber.Real(b.Left), EdNumber.Real(b.Top), EdNumber.Real(b.Right), EdNumber.Real(b.Bottom)]));
            }
            else
                rebuilt.Set("PointBase", ps.Array("PointBase") ?? new EdArray([EdNumber.Real(0), EdNumber.Real(0)]));
            rebuilt.Set("Base", baseDict);
            foreach (var (k, v) in ps.Entries)
                if (k is not ("ShapeType" or "BoxBounds" or "PointBase" or "Base")) rebuilt.Set(k, v);
            cookie.Set("Photoshop", rebuilt);
        }

        private static double Num(EdValue v) => v is EdNumber n ? n.Value : 0;

        private static EdDict Dict(params (string Key, EdValue Value)[] entries)
        {
            var d = new EdDict();
            foreach (var (k, v) in entries) d.Entries.Add(new(k, v));
            return d;
        }

        // ---- Defaults for new layers --------------------------------------------------------------

        /// <summary>
        /// Engine data for a new type layer, following the structure Photoshop writes: an empty editor text, one
        /// style and paragraph run, the rendering settings, and resources with Photoshop's normal style and
        /// paragraph sheets and the standard kinsoku (Japanese line breaking) and mojikumi sets.
        /// </summary>
        private static EdDict DefaultEngineData()
        {
            EdNumber R(double v) => EdNumber.Real(v);
            EdNumber I(long v) => EdNumber.Int(v);
            EdArray A(params EdValue[] items) => new(items);
            EdBool B(bool v) => new(v);
            EdString S(string v) => new(v);

            EdDict ParagraphDefaults() => Dict(
                ("Justification", I(0)), ("FirstLineIndent", R(0)), ("StartIndent", R(0)), ("EndIndent", R(0)),
                ("SpaceBefore", R(0)), ("SpaceAfter", R(0)), ("AutoHyphenate", B(true)), ("HyphenatedWordSize", I(6)),
                ("PreHyphen", I(2)), ("PostHyphen", I(2)), ("ConsecutiveHyphens", I(8)), ("Zone", R(36)),
                ("WordSpacing", A(R(.8), R(1), R(1.33))), ("LetterSpacing", A(R(0), R(0), R(0))), ("GlyphSpacing", A(R(1), R(1), R(1))),
                ("AutoLeading", R(1.2)), ("LeadingType", I(0)), ("Hanging", B(false)), ("Burasagari", B(false)),
                ("KinsokuOrder", I(0)), ("EveryLineComposer", B(false)));
            EdDict Black() => Dict(("Type", I(1)), ("Values", A(R(1), R(0), R(0), R(0))));
            EdDict StyleDefaults() => Dict(
                ("Font", I(1)), ("FontSize", R(12)), ("FauxBold", B(false)), ("FauxItalic", B(false)), ("AutoLeading", B(true)),
                ("Leading", R(0)), ("HorizontalScale", R(1)), ("VerticalScale", R(1)), ("Tracking", I(0)), ("AutoKerning", B(true)),
                ("Kerning", I(0)), ("BaselineShift", R(0)), ("FontCaps", I(0)), ("FontBaseline", I(0)), ("Underline", B(false)),
                ("Strikethrough", B(false)), ("Ligatures", B(true)), ("DLigatures", B(false)), ("BaselineDirection", I(2)),
                ("Tsume", R(0)), ("StyleRunAlignment", I(2)), ("Language", I(0)), ("NoBreak", B(false)), ("FillColor", Black()),
                ("StrokeColor", Black()), ("FillFlag", B(true)), ("StrokeFlag", B(false)), ("FillFirst", B(true)), ("YUnderline", I(1)),
                ("OutlineWidth", R(1)), ("CharacterDirection", I(0)), ("HindiNumbers", B(false)), ("Kashida", I(1)), ("DiacriticPos", I(2)));
            EdDict Kinsoku(string name, string noStart, string noEnd) => Dict(
                ("Name", S(name)), ("NoStart", S(noStart)), ("NoEnd", S(noEnd)), ("Keep", S("―‥")), ("Hanging", S("、。.,")));
            EdDict ResourcesDict() => Dict(
                ("KinsokuSet", A(
                    Kinsoku("PhotoshopKinsokuHard",
                        "、。，．・：；？！ー―’”）〕］｝〉》」』】ヽヾゝゞ々ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮヵヶ゛゜?!)]},.:;℃℉¢％‰",
                        "‘“（〔［｛〈《「『【([{￥＄£＠§〒＃"),
                    Kinsoku("PhotoshopKinsokuSoft",
                        "、。，．・：；？！’”）〕］｝〉》」』】ヽヾゝゞ々",
                        "‘“（〔［｛〈《「『【"))),
                ("MojiKumiSet", A(
                    Dict(("InternalName", S("Photoshop6MojiKumiSet1"))), Dict(("InternalName", S("Photoshop6MojiKumiSet2"))),
                    Dict(("InternalName", S("Photoshop6MojiKumiSet3"))), Dict(("InternalName", S("Photoshop6MojiKumiSet4"))))),
                ("TheNormalStyleSheet", I(0)),
                ("TheNormalParagraphSheet", I(0)),
                ("ParagraphSheetSet", A(Dict(("Name", S("Normal RGB")), ("DefaultStyleSheet", I(0)), ("Properties", ParagraphDefaults())))),
                ("StyleSheetSet", A(Dict(("Name", S("Normal RGB")), ("StyleSheetData", StyleDefaults())))),
                ("FontSet", A(
                    Dict(("Name", S("AdobeInvisFont")), ("Script", I(0)), ("FontType", I(0)), ("Synthetic", I(0))),
                    Dict(("Name", S("MyriadPro-Regular")), ("Script", I(0)), ("FontType", I(0)), ("Synthetic", I(0))))),
                ("SuperscriptSize", R(.583)), ("SuperscriptPosition", R(.333)), ("SubscriptSize", R(.583)), ("SubscriptPosition", R(.333)),
                ("SmallCapSize", R(.7)));

            var engineDict = Dict(
                ("Editor", Dict(("Text", S("\r")))),
                ("ParagraphRun", Dict(
                    ("DefaultRunData", Dict(
                        ("ParagraphSheet", Dict(("DefaultStyleSheet", I(0)), ("Properties", new EdDict()))),
                        ("Adjustments", Dict(("Axis", A(R(1), R(0), R(1))), ("XY", A(R(0), R(0))))))),
                    ("RunArray", A()), ("RunLengthArray", A()), ("IsJoinable", I(1)))),
                ("StyleRun", Dict(
                    ("DefaultRunData", Dict(("StyleSheet", Dict(("StyleSheetData", new EdDict()))))),
                    ("RunArray", A()), ("RunLengthArray", A()), ("IsJoinable", I(2)))),
                ("GridInfo", Dict(
                    ("GridIsOn", B(false)), ("ShowGrid", B(false)), ("GridSize", R(18)), ("GridLeading", R(22)),
                    ("GridColor", Dict(("Type", I(1)), ("Values", A(R(0), R(0), R(0), R(1))))),
                    ("GridLeadingFillColor", Dict(("Type", I(1)), ("Values", A(R(0), R(0), R(0), R(1))))),
                    ("AlignLineHeightToGridFlags", B(false)))),
                ("AntiAlias", I(4)),
                ("UseFractionalGlyphWidths", B(true)),
                ("Rendered", Dict(
                    ("Version", I(1)),
                    ("Shapes", Dict(
                        ("WritingDirection", I(0)),
                        ("Children", A(Dict(
                            ("ShapeType", I(0)),
                            ("Procession", I(0)),
                            ("Lines", Dict(("WritingDirection", I(0)), ("Children", A()))),
                            ("Cookie", Dict(("Photoshop", Dict(
                                ("ShapeType", I(0)),
                                ("PointBase", A(R(0), R(0))),
                                ("Base", Dict(
                                    ("ShapeType", I(0)),
                                    ("TransformPoint0", A(R(1), R(0))),
                                    ("TransformPoint1", A(R(0), R(1))),
                                    ("TransformPoint2", A(R(0), R(0)))))))))))))))));
            return Dict(("EngineDict", engineDict), ("ResourceDict", ResourcesDict()), ("DocumentResources", ResourcesDict()));
        }
    }

    private static ObjectValue Rect(string name, TextRect r) => Rect(null, name, r);

    /// <summary>A bounds descriptor, keeping the existing one's name, class and unit ("#Pnt" or "#Pxl").</summary>
    private static ObjectValue Rect(Descriptor? existing, string name, TextRect r)
    {
        string unit = existing?["Left"] is UnitFloatValue u ? u.Unit : "#Pnt";
        return new(new Descriptor
        {
            Name = existing?.Name ?? "",
            ClassId = existing?.ClassId ?? name,
            Items =
            [
                new("Left", new UnitFloatValue(unit, r.Left)),
                new("Top ", new UnitFloatValue(unit, r.Top)),
                new("Rght", new UnitFloatValue(unit, r.Right)),
                new("Btom", new UnitFloatValue(unit, r.Bottom)),
            ],
        });
    }
}
