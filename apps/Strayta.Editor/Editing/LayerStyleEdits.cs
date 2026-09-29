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
/// One undo step that changes layer styles: the Layer Style dialog (its layer, plus every layer whose effects follow
/// the global light when the dialog moved it), Global Light, Scale Effects, Paste and Clear Layer Style, and showing
/// or hiding effects.
/// </summary>
public sealed class LayerStyleEdit : IEdit
{
    private readonly IReadOnlyList<(LayerNode Node, LayerStyleState Before, LayerStyleState After)> _changes;
    private readonly Document _document;
    private readonly float _angleBefore, _angleAfter, _altitudeBefore, _altitudeAfter;

    public LayerStyleEdit(string description, Document document,
        IReadOnlyList<(LayerNode Node, LayerStyleState Before, LayerStyleState After)> changes, float? globalAngle = null, float? globalAltitude = null)
    {
        Description = description;
        _document = document;
        _changes = changes;
        _angleBefore = document.GlobalLightAngle;
        _angleAfter = globalAngle ?? document.GlobalLightAngle;
        _altitudeBefore = document.GlobalLightAltitude;
        _altitudeAfter = globalAltitude ?? document.GlobalLightAltitude;
    }

    public string Description { get; }
    public bool ChangesStructure => false;

    /// <summary>The layers this step changes.</summary>
    public IEnumerable<LayerNode> Nodes => _changes.Select(c => c.Node);

    public void Do()
    {
        foreach (var (node, _, after) in _changes) after.ApplyTo(node);
        _document.GlobalLightAngle = _angleAfter;
        _document.GlobalLightAltitude = _altitudeAfter;
    }

    public void Undo()
    {
        foreach (var (node, before, _) in _changes) before.ApplyTo(node);
        _document.GlobalLightAngle = _angleBefore;
        _document.GlobalLightAltitude = _altitudeBefore;
    }
}
