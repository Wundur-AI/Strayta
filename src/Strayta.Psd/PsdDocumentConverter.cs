using Strayta.Core;

namespace Strayta.Psd;

/// <summary>Maps the file-level <see cref="PsdFile"/> onto the format-agnostic <see cref="Document"/>.</summary>
internal static class PsdDocumentConverter
{

    public static Document Convert(PsdFile file)
    {
        float globalAngle = PsdEffects.GlobalAngleOf(file);
        var context = PsdEffects.ContextOf(file);
        var h = file.Header;
        var doc = new Document(h.Width, h.Height, h.ColorMode, h.BitDepth)
        {
            GlobalLightAngle = globalAngle,
            GlobalLightAltitude = PsdEffects.GlobalAltitudeOf(file),
            IccProfile = file.FindResource(1039)?.Data,
            SourceData = file,
            Palette = h.ColorMode == ColorMode.Indexed && file.ColorModeData.Length >= 768
                ? InterleavePalette(file.ColorModeData)
                : null,
        };

        if (PsdResolution.Read(file.FindResource(PsdResolution.ResourceId)?.Data) is { } ppi) doc.Resolution = ppi;
        doc.Guides = PsdGuides.Read(file.FindResource(PsdGuides.ResourceId)?.Data);
        doc.Composite = BuildComposite(file);
        doc.Patterns.AddRange(context.Patterns); // PsdPatterns.cs; pattern effects find theirs here too
        BuildLayerTree(file, doc.Root, context);
        return doc;
    }

    /// <summary>The file stores the palette as 256 reds, then 256 greens, then 256 blues.</summary>
    private static byte[] InterleavePalette(byte[] data)
    {
        var rgb = new byte[768];
        for (int i = 0; i < 256; i++)
        {
            rgb[i * 3] = data[i];
            rgb[i * 3 + 1] = data[256 + i];
            rgb[i * 3 + 2] = data[512 + i];
        }
        return rgb;
    }

    private static Raster? BuildComposite(PsdFile file)
    {
        var channels = file.CompositeChannels;
        if (channels.Count == 0) return null;

        var mode = file.Header.ColorMode;
        int colorCount = mode == ColorMode.Multichannel ? channels.Count : mode.ColorChannelCount();
        if (channels.Count < colorCount) return null;

        Plane? alpha = file.CompositeHasTransparency && channels.Count > colorCount ? channels[colorCount] : null;
        var color = channels.Take(colorCount).ToArray();
        if (alpha is not null && mode == ColorMode.Rgb)
            color = color.AsParallel().AsOrdered().Select(p => RemoveWhiteMatte(p, alpha)).ToArray();
        return new Raster(mode, color, alpha);
    }

    /// <summary>
    /// Photoshop stores a transparent composite's color already blended over white:
    /// stored = color * a + (1 - a). Recover color = (stored - (1 - a)) / a.
    /// </summary>
    private static Plane RemoveWhiteMatte(Plane stored, Plane alpha)
    {
        var result = Plane.Create(stored.Width, stored.Height, stored.BitDepth);
        int w = stored.Width;
        Parallel.For(0, stored.Height, y =>
        {
        for (int i = y * w; i < (y + 1) * w; i++)
        {
            float a = alpha.GetNormalized(i);
            float c = stored.GetNormalized(i);
            float v = a is > 0f and < 1f ? (c - (1f - a)) / a : c;
            switch (stored.BitDepth)
            {
                case 8: result.Data[i] = (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f); break;
                case 16: result.AsUInt16()[i] = (ushort)Math.Clamp(MathF.Round(v * 65535f), 0f, 65535f); break;
                default: result.AsSingle()[i] = v; break;
            }
        }
        });
        return result;
    }

    private static void BuildLayerTree(PsdFile file, LayerGroup root, PsdEffectContext effectContext)
    {
        // Records run bottom to top. A bounding divider opens a group; the folder record above it closes it.
        var stack = new Stack<LayerGroup>();
        stack.Push(root);

        foreach (var record in file.Layers)
        {
            switch (record.SectionType)
            {
                case PsdSectionType.BoundingDivider:
                    stack.Push(new LayerGroup { SourceData = record });
                    break;

                case PsdSectionType.OpenFolder:
                case PsdSectionType.ClosedFolder:
                    var group = stack.Count > 1 ? stack.Pop() : new LayerGroup();
                    var divider = group.SourceData as PsdLayerRecord;
                    ApplyCommon(group, record, effectContext);
                    group.SourceData = new PsdGroupRecords(record, divider);
                    group.Expanded = record.SectionType == PsdSectionType.OpenFolder;
                    group.Mask = BuildMask(record);
                    group.Artboard = PsdArtboards.Read(record); // PsdArtboards.cs
                    var (_, sectionBlend) = PsdBlocks.ReadSection(record.FindBlock("lsct") ?? record.FindBlock("lsdk"));
                    if (sectionBlend is not null && PsdBlocks.TryMapBlendMode(sectionBlend, out var mode))
                        group.BlendMode = mode;
                    stack.Peek().Add(group);
                    break;

                default:
                    if (PsdAdjustments.Read(record) is var (kind, adjustment))
                    {
                        var adj = new AdjustmentLayer { Kind = kind, Adjustment = adjustment, Mask = BuildMask(record) };
                        ApplyCommon(adj, record, effectContext);
                        stack.Peek().Add(adj);
                    }
                    else
                    {
                        stack.Peek().Add(BuildPixelLayer(record, file.Header, effectContext));
                    }
                    break;
            }
        }

        // Unbalanced dividers: attach any groups left open rather than dropping their layers.
        while (stack.Count > 1)
        {
            var orphan = stack.Pop();
            stack.Peek().Add(orphan);
        }
    }

    private static void ApplyCommon(LayerNode node, PsdLayerRecord record, PsdEffectContext effectContext)
    {
        node.SourceData = record;
        node.Name = record.Name;
        node.Visible = !record.Hidden;
        node.Opacity = record.Opacity / 255f;
        node.FillOpacity = (PsdBlocks.ReadFillOpacity(record.FindBlock("iOpa")) ?? 255) / 255f;
        node.Clipped = record.Clipped;
        node.BlendMode = PsdBlocks.TryMapBlendMode(record.BlendModeKey, out var mode) ? mode : BlendMode.Normal;

        var tag = PsdLayerKinds.Classify(record) switch
        {
            PsdLayerKind.Text => "text",
            PsdLayerKind.SmartObject => "smart-object",
            PsdLayerKind.Fill => "fill",
            PsdLayerKind.Shape => "shape",
            PsdLayerKind.Adjustment => "adjustment",
            _ => null,
        };
        if (tag is not null) node.Tags.Add(tag);
        node.Effects = PsdEffects.Read(record, effectContext);
        if (node.Effects is not null) node.Tags.Add("effects");
        if (record.FindBlock("vmsk") is not null || record.FindBlock("vsms") is not null) node.Tags.Add("vector-mask");
    }

    private static PixelLayer BuildPixelLayer(PsdLayerRecord record, PsdHeader header, PsdEffectContext effectContext)
    {
        var layer = new PixelLayer
        {
            Bounds = record.Rect,
            TransparencyLocked = record.TransparencyLocked,
            Mask = BuildMask(record),
        };
        ApplyCommon(layer, record, effectContext);

        if (!record.Rect.IsEmpty)
        {
            int colorCount = header.ColorMode == ColorMode.Multichannel
                ? record.ChannelData.Keys.Count(k => k >= 0)
                : header.ColorMode.ColorChannelCount();

            var planes = new List<Plane>(colorCount);
            for (short c = 0; c < colorCount; c++)
                if (record.ChannelData.TryGetValue(c, out var p)) planes.Add(p);

            record.ChannelData.TryGetValue(PsdChannelId.Transparency, out var alpha);
            if (planes.Count == colorCount && (planes.Count > 0 || alpha is not null))
                layer.Pixels = new Raster(header.ColorMode, planes, alpha);
        }

        // A fill (or shape) layer's pixels are its fill already cut out by the vector mask, and its user mask is that
        // vector mask rasterized (mask flag bit 3: "came from rendering other data").
        if (layer.Mask is { } vector && record.Mask is { } m && (m.Flags & 0x08) != 0 && layer.Pixels?.Alpha is not null
            && (PsdLayerKinds.Classify(record) == PsdLayerKind.Fill || PsdShapeLayer.IsShape(record)))
            layer.Mask = new LayerMask
            {
                Bounds = vector.Bounds,
                Pixels = vector.Pixels,
                DefaultColor = vector.DefaultColor,
                Disabled = vector.Disabled,
                PositionRelativeToLayer = vector.PositionRelativeToLayer,
                AppliedToPixels = true,
            };

        return layer;
    }

    private static LayerMask? BuildMask(PsdLayerRecord record)
    {
        if (record.Mask is not { } m) return null;
        record.ChannelData.TryGetValue(PsdChannelId.UserMask, out var pixels);
        return new LayerMask
        {
            Bounds = m.Rect,
            Pixels = pixels,
            DefaultColor = m.DefaultColor,
            Disabled = m.Disabled,
            PositionRelativeToLayer = m.PositionRelativeToLayer,
        };
    }
}
