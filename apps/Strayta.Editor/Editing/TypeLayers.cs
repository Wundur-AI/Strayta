using Strayta.Core;
using Strayta.Core.Text;
using Strayta.Psd;
using Strayta.Psd.Text;
using Strayta.Text;

namespace Strayta.Editor.Editing;

/// <summary>
/// Type layers in the editor: reading a layer's text, applying edited text (new PSD data plus pixels drawn by the
/// text engine, as one undoable step), creating new type layers, and finding fonts a document uses that are not
/// installed. Unedited type layers keep showing the pixels Photoshop stored.
/// </summary>
public static class TypeLayers
{
    /// <summary>The layer's text, or null when it is not a (readable) type layer.</summary>
    public static TextLayerData? Read(LayerNode node) =>
        node is PixelLayer { SourceData: PsdLayerRecord record } && node.Tags.Contains("text") ? PsdTypeLayer.Read(record) : null;

    /// <summary>
    /// The edit that gives <paramref name="layer"/> the text <paramref name="data"/>: re-rendered pixels and bounds, the
    /// layer's 'TySh' rewritten, and the layer renamed along with the text while its name still is its old text (as
    /// Photoshop names type layers). <paramref name="render"/> reports missing fonts.
    /// </summary>
    public static TextEdit Edit(Document doc, PixelLayer layer, TextLayerData data, out TextRenderResult render, FontCatalog? fonts = null)
    {
        render = TextRenderer.Render(data, new TextRenderOptions { Fonts = fonts, ColorMode = doc.ColorMode, BitDepth = doc.BitDepth });
        var record = layer.SourceData as PsdLayerRecord;
        var newRecord = record is null ? PsdTypeLayer.Create(data, render.Layout.ToTextBounds())
            : PsdTypeLayer.Apply(record, data, render.Layout.ToTextBounds());
        string name = layer.Name;
        if (Read(layer) is { } old && NameFor(old.Text) == layer.Name) name = NameFor(data.Text);
        return new TextEdit(layer, newRecord, render.Pixels, render.Pixels is null ? PixelRect.Empty : render.Bounds, name);
    }

    /// <summary>A new type layer showing <paramref name="data"/> (insert it with an <see cref="InsertEdit"/>).</summary>
    public static PixelLayer Create(Document doc, TextLayerData data, out TextRenderResult render, FontCatalog? fonts = null)
    {
        // Share the document's text resources (font list, style sheets) when it already has type.
        var donor = doc.Root.Descendants().Select(Read).FirstOrDefault(d => d is not null);
        if (donor is not null && data.SourceData is null) data = PsdTypeLayer.UseResourcesOf(data, donor);
        render = TextRenderer.Render(data, new TextRenderOptions { Fonts = fonts, ColorMode = doc.ColorMode, BitDepth = doc.BitDepth });
        var layer = new PixelLayer
        {
            Name = NameFor(data.Text),
            SourceData = PsdTypeLayer.Create(data, render.Layout.ToTextBounds()),
            Pixels = render.Pixels,
            Bounds = render.Pixels is null ? PixelRect.Empty : render.Bounds,
        };
        layer.Tags.Add("text");
        return layer;
    }

    /// <summary>Photoshop names a type layer after its text: the first line, trimmed, at most 255 characters.</summary>
    public static string NameFor(string text)
    {
        string first = text.Split('\n', TextLayerData.LineBreak)[0].Trim();
        return first.Length > 255 ? first[..255] : first;
    }

    /// <summary>PostScript names of fonts used by the document's type layers that are not installed.</summary>
    public static IReadOnlyList<string> MissingFonts(Document doc, FontCatalog? fonts = null)
    {
        var catalog = fonts ?? FontCatalog.System;
        return doc.Root.Descendants()
            .Select(Read)
            .OfType<TextLayerData>()
            .SelectMany(d => d.FontsUsed)
            .Distinct(StringComparer.Ordinal)
            .Where(f => f.Length > 0 && !catalog.Contains(f))
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>Replaces a type layer's text data, pixels, bounds and name together.</summary>
public sealed class TextEdit(PixelLayer layer, object newSource, Raster? newPixels, PixelRect newBounds, string newName) : ILayerPropertyEdit
{
    private readonly object? _oldSource = layer.SourceData;
    private readonly Raster? _oldPixels = layer.Pixels;
    private readonly PixelRect _oldBounds = layer.Bounds;
    private readonly string _oldName = layer.Name;

    public LayerNode Node => layer;
    public string Description => "Edit Type Layer";
    public bool ChangesStructure => newName != _oldName; // the Layers panel shows the new name

    public void Do() => Set(newSource, newPixels, newBounds, newName);
    public void Undo() => Set(_oldSource, _oldPixels, _oldBounds, _oldName);

    private void Set(object? source, Raster? pixels, PixelRect bounds, string name)
    {
        layer.SourceData = source;
        layer.Pixels = pixels;
        layer.Bounds = bounds;
        layer.Name = name;
        layer.Tags.Add("text");
    }
}
