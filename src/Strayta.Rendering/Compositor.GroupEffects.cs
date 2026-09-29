using Strayta.Core;

namespace Strayta.Rendering;

// Layer styles on groups: the group's flattened content is the shape its effects follow.
public sealed partial class Compositor
{
    /// <summary>
    /// A group with layer styles renders its children in isolation (as Photoshop does once a group has effects, even
    /// in Pass Through), and that composite, with the group's mask, is drawn with the group's effects exactly like a
    /// layer's pixels: shadows and glows around it, overlays on it, fill opacity fading the content only.
    /// </summary>
    private void RenderGroupWithEffects(LayerGroup group, PixelRect bounds, RenderBuffer target, Source? clip, float clipOpacity)
    {
        var isolated = new RenderBuffer(bounds);
        RenderChildren(group.Children, isolated);
        var content = new BufferSource(isolated, group.Mask, MaskStroke(group)) { Bounds = bounds };
        CompositeWithEffects(group, content, target, clip, clipOpacity);
    }

    /// <summary>Draws <paramref name="content"/> with <paramref name="node"/>'s effects; Pass Through draws as Normal.</summary>
    private void CompositeWithEffects(LayerNode node, Source content, RenderBuffer target, Source? clip, float clipOpacity)
    {
        var mode = node.BlendMode == BlendMode.PassThrough ? BlendMode.Normal : node.BlendMode;
        EffectRenderer.Render(node, content, target.Bounds, (src, m, opacity, fill) =>
            Composite(target, src, ReferenceEquals(src, content) ? mode : m, opacity, fill, clip, clipOpacity));
    }

    /// <summary>What cannot be reproduced exactly about a layer's styles.</summary>
    private static void GroupEffectWarnings(LayerNode node, List<string> warnings)
    {
        if (node is LayerGroup { BlendMode: BlendMode.PassThrough } && EffectRenderer.HasRenderable(node.Effects))
            warnings.Add($"Group \"{node.Name}\" has layer styles and is rendered isolated, as if its mode were Normal.");
        foreach (var e in node.Effects?.Visible ?? [])
        {
            var pattern = e switch
            {
                PatternOverlayEffect o => o.Fill?.Pattern,
                StrokeEffect { FillType: StrokeFillType.Pattern } s => s.PatternFill?.Pattern,
                BevelEffect { UseTexture: true } b => b.Texture?.Pattern,
                _ => null,
            };
            if (pattern is { Pixels: null })
                warnings.Add($"Pattern \"{pattern.Name}\" used on \"{node.Name}\" is not in the file, so it is not drawn.");
        }
    }
}
