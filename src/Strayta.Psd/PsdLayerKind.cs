namespace Strayta.Psd;

public enum PsdLayerKind
{
    Pixel,
    Group,
    GroupEnd,
    Text,
    SmartObject,
    Fill,
    Shape,
    Adjustment,
}

public static class PsdLayerKinds
{
    private static readonly HashSet<string> AdjustmentKeys =
    [
        "levl", "curv", "brit", "blnc", "hue ", "hue2", "vibA", "mixr", "selc", "grdm", "phfl",
        "expA", "post", "thrs", "nvrt", "blwh", "clrL",
    ];

    /// <summary>Classifies a layer record by the tagged blocks it carries.</summary>
    public static PsdLayerKind Classify(PsdLayerRecord r)
    {
        switch (r.SectionType)
        {
            case PsdSectionType.OpenFolder or PsdSectionType.ClosedFolder: return PsdLayerKind.Group;
            case PsdSectionType.BoundingDivider: return PsdLayerKind.GroupEnd;
        }
        if (r.FindBlock("TySh") is not null) return PsdLayerKind.Text;
        if (r.FindBlock("SoLd") is not null || r.FindBlock("SoLE") is not null || r.FindBlock("PlLd") is not null) return PsdLayerKind.SmartObject;
        if (r.FindBlock("SoCo") is not null || r.FindBlock("GdFl") is not null || r.FindBlock("PtFl") is not null) return PsdLayerKind.Fill;
        if (r.Blocks.Any(b => AdjustmentKeys.Contains(b.Key))) return PsdLayerKind.Adjustment;
        if (r.FindBlock("vmsk") is not null || r.FindBlock("vsms") is not null) return PsdLayerKind.Shape;
        return PsdLayerKind.Pixel;
    }

    /// <summary>True when the record carries layer effects (drop shadow, stroke, ...).</summary>
    public static bool HasEffects(PsdLayerRecord r) => r.FindBlock("lfx2") is not null || r.FindBlock("lmfx") is not null;
}
