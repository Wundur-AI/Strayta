namespace Strayta.Core;

/// <summary>What a document channel holds and what its display color marks (Photoshop's Channel Options).</summary>
public enum ChannelKind
{
    /// <summary>A saved selection whose color marks the masked (unselected) areas: white is selected. Photoshop's default.</summary>
    MaskedAreas,

    /// <summary>A saved selection whose color marks the selected areas: black is selected.</summary>
    SelectedAreas,

    /// <summary>A spot color plate: black is full ink, white none; the color is the ink and the opacity its solidity.</summary>
    Spot,
}

/// <summary>
/// A document channel beyond the color channels: a saved selection (alpha channel) or a spot color plate, covering
/// the whole canvas. Values are stored as the channel shows in gray, so a <see cref="ChannelKind.SelectedAreas"/>
/// channel is black where it is selected. Channels are immutable; an edit makes a new one.
/// </summary>
/// <param name="Name">The name the Channels panel shows.</param>
/// <param name="Pixels">One plane the size of the canvas, at the document's bit depth.</param>
/// <param name="Kind">Saved selection (and what its color marks) or spot color.</param>
public sealed record DocumentChannel(string Name, Plane Pixels, ChannelKind Kind)
{
    /// <summary>The overlay color (or the spot ink) used to show the channel over the image.</summary>
    public RgbColor Color { get; init; } = new(1f, 0f, 0f);

    /// <summary>Overlay opacity 0..1 (50% by default), or a spot channel's solidity.</summary>
    public float Opacity { get; init; } = 0.5f;

    /// <summary>The file's identifier for the channel (PSD resource 1053); 0 when it has none yet.</summary>
    public int Id { get; init; }

    /// <summary>
    /// The color as the file stored it (e.g. a spot color's Lab or book color), kept so an unchanged channel is saved as
    /// it was; a format writer uses it only while <see cref="Color"/> is still what it was read as.
    /// </summary>
    public object? SourceColor { get; init; }

    public bool IsSpot => Kind == ChannelKind.Spot;

    /// <summary>True when the channel's color marks selected areas, so loading it as a selection inverts it.</summary>
    public bool InvertsSelection => Kind == ChannelKind.SelectedAreas;
}
