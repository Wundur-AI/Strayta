using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

/// <summary>
/// Layer › Layer Style › Global Light…: the document's light angle and altitude, which every effect with "Use Global
/// Light" follows (shadows take the angle, bevels both). Changes show on the canvas at once; <see cref="Commit"/> is one
/// "Global Light" undo step, <see cref="Cancel"/> puts everything back.
/// </summary>
public sealed partial class GlobalLightViewModel : ObservableObject
{
    private readonly DocumentViewModel _document;
    private readonly float _angle, _altitude;
    private readonly Dictionary<LayerNode, LayerEffects> _layers = new(ReferenceEqualityComparer.Instance);
    private bool _closed;

    public GlobalLightViewModel(DocumentViewModel document)
    {
        _document = document;
        (_angle, _altitude) = (document.Model.GlobalLightAngle, document.Model.GlobalLightAltitude);
        foreach (var node in document.Model.Root.Descendants())
            if (node.Effects is { } fx && fx.Items.Any(e => e.UsesGlobalLight()))
                _layers[node] = fx;
        Angle = _angle;
        Altitude = _altitude;
    }

    /// <summary>Degrees counterclockwise from the right, -180..180.</summary>
    [ObservableProperty] public partial double Angle { get; set; }

    /// <summary>Degrees above the layer, 0..90.</summary>
    [ObservableProperty] public partial double Altitude { get; set; }

    /// <summary>How many layers have effects that follow the light (shown in the dialog).</summary>
    public int Users => _layers.Count;

    partial void OnAngleChanged(double value) => Preview();
    partial void OnAltitudeChanged(double value) => Preview();

    private IEnumerable<(LayerNode Node, LayerEffects Before, LayerEffects? After)> Changes() =>
        _layers.Select(kv => (kv.Key, kv.Value, kv.Value.WithGlobalLight((float)Angle, (float)Math.Clamp(Altitude, 0, 90))));

    private void Preview()
    {
        if (_closed || _document is null) return;
        foreach (var (node, _, after) in Changes()) node.Effects = after;
        _document.Model.GlobalLightAngle = (float)Angle;
        _document.Model.GlobalLightAltitude = (float)Math.Clamp(Altitude, 0, 90);
        _document.RequestRender();
    }

    private void Restore()
    {
        foreach (var (node, effects) in _layers) node.Effects = effects;
        _document.Model.GlobalLightAngle = _angle;
        _document.Model.GlobalLightAltitude = _altitude;
    }

    public void Commit()
    {
        if (_closed) return;
        var changes = Changes().Where(c => !ReferenceEquals(c.Before, c.After)).ToList();
        _closed = true;
        Restore();
        float angle = (float)Angle, altitude = (float)Math.Clamp(Altitude, 0, 90);
        if (changes.Count == 0 && angle == _angle && altitude == _altitude)
        {
            _document.RequestRender();
            return;
        }
        var steps = changes.Select(c => (c.Node, LayerStyleState.Of(c.Node), LayerStyleState.Of(c.Node) with { Effects = c.After })).ToList();
        _document.Apply(new LayerStyleEdit("Global Light", _document.Model, steps, angle, altitude));
    }

    public void Cancel()
    {
        if (_closed) return;
        _closed = true;
        Restore();
        _document.RequestRender();
    }
}

/// <summary>
/// Layer › Layer Style › Scale Effects…: every size, distance and pattern scale of the selected layer's effects
/// multiplied by one factor (1–1000%), as Photoshop does when a styled layer is resized; previewed live, OK is one
/// "Scale Effects" undo step.
/// </summary>
public sealed partial class ScaleEffectsViewModel : ObservableObject
{
    private readonly DocumentViewModel _document;
    private readonly LayerNode _layer;
    private readonly LayerEffects _original;
    private bool _closed;

    public ScaleEffectsViewModel(DocumentViewModel document, LayerNode layer)
    {
        _document = document;
        _layer = layer;
        _original = layer.Effects!;
    }

    public string LayerName => _layer.Name;

    /// <summary>Percent, 1..1000.</summary>
    [ObservableProperty] public partial double Scale { get; set; } = 100;

    [ObservableProperty] public partial bool Preview { get; set; } = true;

    partial void OnScaleChanged(double value) => Show();
    partial void OnPreviewChanged(bool value) => Show();

    private LayerEffects Result() => _original.Scaled((float)(Math.Clamp(Scale, 1, 1000) / 100));

    private void Show()
    {
        if (_closed) return;
        _layer.Effects = Preview ? Result() : _original;
        _document.RequestRender();
    }

    public void Commit()
    {
        if (_closed) return;
        _closed = true;
        var result = Result();
        _layer.Effects = _original;
        if (ReferenceEquals(result, _original) || result.Equals(_original))
        {
            _document.RequestRender();
            return;
        }
        var before = LayerStyleState.Of(_layer);
        _document.Apply(new LayerStyleEdit("Scale Effects", _document.Model, [(_layer, before, before with { Effects = result })]));
    }

    public void Cancel()
    {
        if (_closed) return;
        _closed = true;
        _layer.Effects = _original;
        _document.RequestRender();
    }
}
