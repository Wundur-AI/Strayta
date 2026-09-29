namespace Strayta.Core.Text;

/// <summary>
/// Measured extents of laid-out text, in text space: <see cref="Layout"/> spans the lines' advance widths and their
/// ascent-to-descent heights (for paragraph text, the box), <see cref="Ink"/> the drawn glyphs. PSD stores both
/// ('bounds' and 'boundingBox') so other readers can place the layer without laying the text out.
/// </summary>
public readonly record struct TextBounds(TextRect Layout, TextRect Ink);
