namespace Strayta.Core;

/// <summary>
/// What a layer's locks protect, with the bit values PSD stores in the layer's protection block ('lspf'). Unknown bits
/// are kept, so a file's value survives a round trip.
/// </summary>
[Flags]
public enum LayerLocks : uint
{
    None = 0,

    /// <summary>Lock Transparent Pixels: painting keeps each pixel's transparency.</summary>
    Transparency = 0x01,

    /// <summary>Lock Image Pixels: the layer's pixels cannot be painted or filtered.</summary>
    Pixels = 0x02,

    /// <summary>Lock Position: the layer cannot be moved or transformed.</summary>
    Position = 0x04,

    /// <summary>Prevent Auto-Nesting into and out of artboards.</summary>
    ArtboardNesting = 0x08,

    /// <summary>Lock All: everything above, and the layer's blending settings too.</summary>
    All = 0x8000_0000,
}

/// <summary>The color label shown behind a layer's eye in the Layers panel (PSD's 'lclr' block), in Photoshop's order.</summary>
public enum LayerColor
{
    None = 0,
    Red = 1,
    Orange = 2,
    Yellow = 3,
    Green = 4,
    Blue = 5,
    Violet = 6,
    Gray = 7,
}

/// <summary>How locks combine: Lock All implies every lock, and a group's locks apply to everything inside it.</summary>
public static class LayerLockRules
{
    /// <summary>The locks in effect on <paramref name="node"/>: its own and its groups', with Lock All expanded.</summary>
    public static LayerLocks EffectiveLocks(this LayerNode node)
    {
        var locks = LayerLocks.None;
        for (LayerNode? n = node; n is not null; n = n.Parent) locks |= Expand(n.Locks);
        return locks;
    }

    /// <summary>Lock All written out as the locks it implies.</summary>
    public static LayerLocks Expand(LayerLocks locks) =>
        (locks & LayerLocks.All) != 0
            ? locks | LayerLocks.Transparency | LayerLocks.Pixels | LayerLocks.Position | LayerLocks.ArtboardNesting
            : locks;

    /// <summary>True when <paramref name="lockKind"/> (or Lock All) is set on the layer or one of its groups.</summary>
    public static bool IsLocked(this LayerNode node, LayerLocks lockKind) => (node.EffectiveLocks() & lockKind) != 0;

    /// <summary>True when the layer's position is locked, or any layer inside a group is (moving the group would move it).</summary>
    public static bool IsPositionLockedWithin(this LayerNode node) =>
        node.IsLocked(LayerLocks.Position)
        || node is LayerGroup g && g.Descendants().Any(d => (Expand(d.Locks) & LayerLocks.Position) != 0);

    /// <summary>True when any lock is set on the layer itself (the panel's lock icon).</summary>
    public static bool HasAnyLock(this LayerNode node) =>
        (node.Locks & (LayerLocks.Transparency | LayerLocks.Pixels | LayerLocks.Position | LayerLocks.ArtboardNesting | LayerLocks.All)) != 0;
}
