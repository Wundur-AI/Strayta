using Strayta.Core;

namespace Strayta.Editor.Editing;

/// <summary>What a layer style covers: the effects and the blending options of the Layer Style dialog.</summary>
public sealed record LayerStyleState(LayerEffects? Effects, float Opacity, float FillOpacity, BlendMode BlendMode)
{
    public static LayerStyleState Of(LayerNode node) => new(node.Effects, node.Opacity, node.FillOpacity, node.BlendMode);

    public void ApplyTo(LayerNode node)
    {
        node.Effects = Effects;
        node.Opacity = Opacity;
        node.FillOpacity = FillOpacity;
        node.BlendMode = BlendMode;
    }
}

/// <summary>
/// One undo step that changes layer styles: the Layer Style dialog (its layer, plus every layer whose shadows follow
/// the global light when the dialog moved it), Paste and Clear Layer Style, and showing or hiding effects.
/// </summary>
public sealed class LayerStyleEdit : IEdit
{
    private readonly IReadOnlyList<(LayerNode Node, LayerStyleState Before, LayerStyleState After)> _changes;
    private readonly Document _document;
    private readonly float _angleBefore, _angleAfter;

    public LayerStyleEdit(string description, Document document,
        IReadOnlyList<(LayerNode Node, LayerStyleState Before, LayerStyleState After)> changes, float? globalAngle = null)
    {
        Description = description;
        _document = document;
        _changes = changes;
        _angleBefore = document.GlobalLightAngle;
        _angleAfter = globalAngle ?? document.GlobalLightAngle;
    }

    public string Description { get; }
    public bool ChangesStructure => false;

    /// <summary>The layers this step changes.</summary>
    public IEnumerable<LayerNode> Nodes => _changes.Select(c => c.Node);

    public void Do()
    {
        foreach (var (node, _, after) in _changes) after.ApplyTo(node);
        _document.GlobalLightAngle = _angleAfter;
    }

    public void Undo()
    {
        foreach (var (node, before, _) in _changes) before.ApplyTo(node);
        _document.GlobalLightAngle = _angleBefore;
    }

    /// <summary>
    /// <paramref name="effects"/> with every shadow that uses the global light turned to <paramref name="angle"/>, or
    /// the same instance when none changes (so unchanged layers keep their identity and their saved bytes).
    /// </summary>
    public static LayerEffects? WithGlobalAngle(LayerEffects? effects, float angle)
    {
        if (effects is null) return null;
        bool changed = false;
        var items = effects.Items.Select(e =>
        {
            LayerEffect turned = e switch
            {
                DropShadowEffect { UseGlobalLight: true } d when d.Angle != angle => d with { Angle = angle },
                InnerShadowEffect { UseGlobalLight: true } s when s.Angle != angle => s with { Angle = angle },
                _ => e,
            };
            changed |= !ReferenceEquals(turned, e);
            return turned;
        }).ToList();
        return changed ? effects with { Items = items } : effects;
    }
}
