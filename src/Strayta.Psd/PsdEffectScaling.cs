using Strayta.Core;
using Strayta.Psd.Descriptors;

namespace Strayta.Psd;

/// <summary>
/// Image Size's "Scale Styles": layer effects grow or shrink with the image, so a drop shadow keeps its look at the
/// new size. The modeled settings in pixels (distance, size, stroke width) are scaled, and so is every pixel value
/// of each effect's own descriptor (bevel size and soften, satin distance and size, contour ranges in pixels, ...),
/// together with a pattern overlay's scale, so effects Strayta does not edit follow too when the style is saved.
/// </summary>
public static class PsdEffectScaling
{
    /// <summary>A copy of <paramref name="effects"/> with every size and distance multiplied by <paramref name="factor"/>.</summary>
    public static LayerEffects? Scale(LayerEffects? effects, double factor)
    {
        if (effects is null || factor == 1 || !double.IsFinite(factor) || factor <= 0) return effects;
        float k = (float)factor;
        var items = effects.Items.Select(e =>
        {
            LayerEffect scaled = e switch
            {
                DropShadowEffect d => d with { Distance = d.Distance * k, Size = d.Size * k },
                InnerShadowEffect i => i with { Distance = i.Distance * k, Size = i.Size * k },
                OuterGlowEffect o => o with { Size = o.Size * k },
                InnerGlowEffect i => i with { Size = i.Size * k },
                StrokeEffect s => s with { Size = s.Size * k },
                _ => e,
            };
            return scaled.SourceData is PsdEffectSource source
                ? scaled with { SourceData = source with { Descriptor = ScaleDescriptor(source.Descriptor, factor, source.Type) } }
                : scaled;
        }).ToList();
        return effects with { Items = items };
    }

    /// <summary>
    /// The layer record with its style blocks ('lfx2', 'lmfx', 'lrFX') replaced by <paramref name="effects"/> encoded as
    /// 'lfx2'. The writer rewrites styles only when the model differs from the record, and effects it cannot edit
    /// compare equal whatever their settings, so a scaled bevel or satin reaches the file through the record.
    /// </summary>
    public static PsdLayerRecord WithEffects(PsdLayerRecord record, LayerEffects? effects)
    {
        ArgumentNullException.ThrowIfNull(record);
        string[] keys = ["lfx2", "lmfx", "lrFX"];
        var blocks = record.Blocks.ToList();
        int at = blocks.FindIndex(b => keys.Contains(b.Key));
        blocks.RemoveAll(b => keys.Contains(b.Key));
        if (effects is not null)
        {
            var data = PsdEffectsWriter.Encode(effects);
            var block = new TaggedBlock("8BIM", "lfx2", 0, data.Length, data);
            blocks.Insert(at < 0 ? blocks.Count : Math.Min(at, blocks.Count), block);
        }
        return record.WithBlocks(blocks, record.Mask);
    }

    /// <summary>Multiplies every '#Pxl' value (and a pattern overlay's percentage scale) of an effect descriptor.</summary>
    private static Descriptor ScaleDescriptor(Descriptor d, double factor, string type) => new()
    {
        Name = d.Name,
        ClassId = d.ClassId,
        Items = d.Items.Select(kv => new KeyValuePair<string, DescriptorValue>(kv.Key, kv.Value switch
        {
            UnitFloatValue { Unit: "#Pxl" } u => u with { Value = u.Value * factor },
            UnitFloatValue { Unit: "#Prc" } u when kv.Key == "Scl " && type == "patternFill" => u with { Value = u.Value * factor },
            ObjectValue o when o.Value.ClassId != "Clr " && o.Value.ClassId != "RGBC" => new ObjectValue(ScaleDescriptor(o.Value, factor, "")),
            _ => kv.Value,
        })).ToList(),
    };
}
