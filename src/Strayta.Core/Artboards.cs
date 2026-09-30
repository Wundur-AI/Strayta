namespace Strayta.Core;

/// <summary>What an artboard draws under its layers (the PSD's background type values).</summary>
public enum ArtboardBackground
{
    White = 1,
    Black = 2,
    Transparent = 3,
    /// <summary>The artboard's own <see cref="Artboard.Color"/>.</summary>
    Custom = 4,
}

/// <summary>
/// Makes a top-level <see cref="LayerGroup"/> an artboard: a page of the document with its own bounds and background.
/// Its layers are clipped to <see cref="Rect"/>, the background is drawn under them, and the canvas outside every
/// artboard is pasteboard (transparent in the flattened image).
/// </summary>
public sealed record Artboard
{
    /// <summary>The artboard's bounds in document pixels.</summary>
    public PixelRect Rect { get; init; }

    public ArtboardBackground Background { get; init; } = ArtboardBackground.White;

    /// <summary>The background color when <see cref="Background"/> is <see cref="ArtboardBackground.Custom"/>.</summary>
    public (byte R, byte G, byte B) Color { get; init; } = (255, 255, 255);

    /// <summary>The size preset the artboard was made from ("Custom", or a device name); informational.</summary>
    public string PresetName { get; init; } = "";

    /// <summary>The opaque color drawn under the layers, or null for a transparent artboard.</summary>
    public (byte R, byte G, byte B)? Fill => Background switch
    {
        ArtboardBackground.White => (255, 255, 255),
        ArtboardBackground.Black => (0, 0, 0),
        ArtboardBackground.Custom => Color,
        _ => null,
    };
}

/// <summary>A ready-made artboard size.</summary>
public sealed record ArtboardPreset(string Category, string Name, int Width, int Height)
{
    public override string ToString() => $"{Name} ({Width}×{Height})";
}

/// <summary>Artboard sizes for common screens, grouped the way the Artboard tool lists them.</summary>
public static class ArtboardPresets
{
    public static IReadOnlyList<ArtboardPreset> All { get; } =
    [
        new("iPhone", "iPhone 16 Pro", 1206, 2622),
        new("iPhone", "iPhone 16 Pro Max", 1320, 2868),
        new("iPhone", "iPhone 16", 1179, 2556),
        new("iPhone", "iPhone 16 Plus", 1290, 2796),
        new("iPhone", "iPhone SE", 750, 1334),
        new("iPad", "iPad Pro 13\"", 2064, 2752),
        new("iPad", "iPad Pro 11\"", 1668, 2420),
        new("iPad", "iPad Air", 1640, 2360),
        new("iPad", "iPad mini", 1488, 2266),
        new("Android", "Android Phone", 1080, 2400),
        new("Android", "Android Tablet", 1600, 2560),
        new("Web", "Web 1920", 1920, 1080),
        new("Web", "Web 1440", 1440, 900),
        new("Web", "Web 1366", 1366, 768),
        new("Web", "Web 1280", 1280, 800),
        new("Web", "Web 1024", 1024, 768),
        new("Watch", "Watch 45mm", 396, 484),
        new("Watch", "Watch 41mm", 352, 430),
    ];

    /// <summary>The preset with this exact size, if any.</summary>
    public static ArtboardPreset? Matching(int width, int height) =>
        All.FirstOrDefault(p => p.Width == width && p.Height == height);
}

/// <summary>Helpers for the artboards of a document.</summary>
public static class Artboards
{
    /// <summary>The document's artboards, top to bottom as the Layers panel lists them.</summary>
    public static IEnumerable<LayerGroup> Of(Document doc) =>
        doc.Root.Children.Reverse().OfType<LayerGroup>().Where(g => g.Artboard is not null);

    /// <summary>True when the document has at least one artboard.</summary>
    public static bool Any(Document doc) => doc.Root.Children.Any(c => c is LayerGroup { Artboard: not null });

    /// <summary>The artboard group that holds <paramref name="node"/> (or is it), or null.</summary>
    public static LayerGroup? Containing(LayerNode node)
    {
        for (LayerNode? n = node; n is not null; n = n.Parent)
            if (n is LayerGroup { Artboard: not null } g) return g;
        return null;
    }

    /// <summary>The topmost artboard whose bounds contain the point, or null.</summary>
    public static LayerGroup? At(Document doc, double x, double y) =>
        Of(doc).FirstOrDefault(g => g.Artboard!.Rect is var r && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom);
}
