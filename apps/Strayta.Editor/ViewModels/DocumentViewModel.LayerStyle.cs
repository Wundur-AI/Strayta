using System.Diagnostics;
using Strayta.Core;
using Strayta.Editor.Editing;
using Strayta.Rendering;

namespace Strayta.Editor.ViewModels;

/// <summary>Layer styles: the Layer Style dialog's session, Copy / Paste / Clear Layer Style, and effect visibility.</summary>
public sealed partial class DocumentViewModel
{
    /// <summary>
    /// Why the selected layer cannot take a layer style, or null if it can. Groups can (their effects follow the
    /// group's flattened content); adjustment layers have no pixels for effects to follow.
    /// </summary>
    private string? LayerStyleProblem(LayerNode? node) => node switch
    {
        null => "Select a layer to give it a layer style.",
        AdjustmentLayer => "Adjustment layers cannot have layer styles.",
        _ => null,
    };

    /// <summary>Starts a Layer Style dialog session on the selected layer at <paramref name="page"/>, or null (with a notice).</summary>
    public LayerStyleViewModel? BeginLayerStyle(LayerStylePage page)
    {
        if (IsTransforming || CropBox is not null) return null;
        var node = SelectedLayer?.Node;
        if (LayerStyleProblem(node) is { } problem)
        {
            Notice = problem;
            return null;
        }
        return new LayerStyleViewModel(this, node!, page);
    }

    /// <summary>Layer › Layer Style › Copy Layer Style: the effects and blending options, for any open document.</summary>
    public LayerStyleState? CopyLayerStyle()
    {
        if (SelectedLayer?.Node is not { } node) return null;
        Notice = node.Effects is null ? $"Copied the blending options of \"{node.Name}\" (it has no effects)." : $"Copied the layer style of \"{node.Name}\".";
        return LayerStyleState.Of(node);
    }

    /// <summary>Layer › Layer Style › Paste Layer Style. Shadows that use the global light take this document's.</summary>
    public void PasteLayerStyle(LayerStyleState style)
    {
        if (SelectedLayer?.Node is not { } node) return;
        if (LayerStyleProblem(node) is { } problem)
        {
            Notice = problem;
            return;
        }
        var pasted = style with { Effects = style.Effects.WithGlobalLight(Model.GlobalLightAngle, Model.GlobalLightAltitude) };
        // A group keeps Pass Through when the copied layer was plain Normal; a layer cannot take Pass Through.
        if (node is LayerGroup && pasted.BlendMode == BlendMode.Normal && node.BlendMode == BlendMode.PassThrough) pasted = pasted with { BlendMode = BlendMode.PassThrough };
        if (node is not LayerGroup && pasted.BlendMode == BlendMode.PassThrough) pasted = pasted with { BlendMode = BlendMode.Normal };
        var before = LayerStyleState.Of(node);
        if (pasted == before) return;
        Apply(new LayerStyleEdit("Paste Layer Style", Model, [(node, before, pasted)]));
    }

    /// <summary>
    /// Layer › Layer Style › Clear Layer Style. In Photoshop a layer style is the effects together with the Blending
    /// Options (Copy and Paste Layer Style carry both), and clearing it resets both: the effects go, and the blend
    /// mode, opacity and fill opacity return to their defaults (Normal, or Pass Through for a group; 100%). (In
    /// Photoshop, removing only the effects is dragging the Layers panel's "Effects" row to the trash.)
    /// </summary>
    public void ClearLayerStyle()
    {
        if (SelectedLayer?.Node is not { } node) return;
        var before = LayerStyleState.Of(node);
        var cleared = new LayerStyleState(null, 1f, 1f, node is LayerGroup ? BlendMode.PassThrough : BlendMode.Normal);
        if (cleared == before) return;
        Apply(new LayerStyleEdit("Clear Layer Style", Model, [(node, before, cleared)]));
    }

    /// <summary>
    /// Layer › Layer Style › Global Light…: a session that moves the document's light (angle and altitude) and every
    /// effect that uses it, previewed live; OK is one "Global Light" undo step.
    /// </summary>
    public GlobalLightViewModel BeginGlobalLight() => new(this);

    /// <summary>Layer › Layer Style › Scale Effects… on the selected layer, or null (with a notice) when it has none.</summary>
    public ScaleEffectsViewModel? BeginScaleEffects()
    {
        if (SelectedLayer?.Node is not { Effects: not null } node)
        {
            Notice = "The selected layer has no layer effects to scale.";
            return null;
        }
        return new ScaleEffectsViewModel(this, node);
    }

    /// <summary>The eye of one effect in the Layers panel (undoable, as in Photoshop).</summary>
    public void SetEffectVisible(LayerNode node, int index, bool visible)
    {
        if (node.Effects is not { } fx || index < 0 || index >= fx.Items.Count || fx.Items[index].Enabled == visible) return;
        var items = fx.Items.ToList();
        items[index] = items[index] with { Enabled = visible };
        // Showing an effect while the whole style is hidden shows the style too.
        var after = fx with { Items = items, Enabled = fx.Enabled || visible };
        var before = LayerStyleState.Of(node);
        Apply(new LayerStyleEdit(visible ? "Show Effect" : "Hide Effect", Model, [(node, before, before with { Effects = after })]));
    }

    /// <summary>The "Effects" eye in the Layers panel: shows or hides the whole style.</summary>
    public void SetEffectsVisible(LayerNode node, bool visible)
    {
        if (node.Effects is not { } fx || fx.Enabled == visible) return;
        var before = LayerStyleState.Of(node);
        Apply(new LayerStyleEdit(visible ? "Show Layer Effects" : "Hide Layer Effects", Model,
            [(node, before, before with { Effects = fx with { Enabled = visible } })]));
    }

    /// <summary>
    /// Opens the dialog on the selected layer with a drop shadow and drags its Size slider (and then Distance) for
    /// about two seconds each with 120 Hz input through the dialog's view model, reporting frames reaching the
    /// screen, the preview render time and when full resolution follows; then cancels and checks nothing changed
    /// (STRAYTA_STYLEBENCH).
    /// </summary>
    public async Task RunLayerStyleBenchmarkAsync(bool bevel = false)
    {
        var before = SelectedLayer?.Node is { } selected ? LayerStyleState.Of(selected) : null; // opening already previews the new effect
        var session = BeginLayerStyle(bevel ? LayerStylePage.BevelEmboss : LayerStylePage.DropShadow) ?? throw new InvalidOperationException(Notice);
        var layer = session.Layer;
        string effectName = bevel ? "bevel" : "drop shadow";
        var drags = new List<(string, Action<int>)>();
        if (session.SelectedEntry is BevelEntry b)
        {
            b.TechniqueIndex = 1; // Chisel Hard: the exact distance field, the heavier technique
            drags.Add(("Size", i => b.Size = 5 + i % 60));
            drags.Add(("Depth", i => b.Depth = 100 + i % 400));
        }
        else
        {
            var shadow = (ShadowEntry)session.SelectedEntry!;
            drags.Add(("Size", i => shadow.Size = 10 + i % 120));
            drags.Add(("Distance", i => shadow.Distance = 5 + i % 60));
        }
        foreach (var (name, drag) in drags)
        {
            int frames = 0;
            var times = new List<double>();
            var renders = new List<double>();
            var clock = Stopwatch.StartNew();
            double last = 0;
            void OnFrame(bool full)
            {
                if (full) return;
                frames++;
                renders.Add(LastFrameTimings.Render);
                times.Add(clock.Elapsed.TotalMilliseconds - last);
                last = clock.Elapsed.TotalMilliseconds;
            }
            FrameDisplayed += OnFrame;
            for (int i = 1; i <= 240; i++)
            {
                drag(i);
                await Task.Delay(8);
            }
            double inputMs = clock.Elapsed.TotalMilliseconds;
            var fullShown = new TaskCompletionSource<double>();
            void OnFull(bool full) { if (full) fullShown.TrySetResult(clock.Elapsed.TotalMilliseconds - inputMs); }
            FrameDisplayed += OnFull;
            var settle = await Task.WhenAny(fullShown.Task, Task.Delay(10000));
            FrameDisplayed -= OnFull;
            FrameDisplayed -= OnFrame;

            times.Sort();
            renders.Sort();
            double Pick(List<double> v, double q) => v.Count == 0 ? double.NaN : v[(int)(v.Count * q)];
            var bounds = (layer as PixelLayer)?.Bounds;
            Console.WriteLine($"STYLEBENCH {effectName} {name} layer={bounds?.Width}x{bounds?.Height} doc={Model.Width}x{Model.Height} " +
                              $"input={inputMs:F0}ms frames={frames} fps={frames / (inputMs / 1000):F1} median-frame={Pick(times, 0.5):F0}ms " +
                              $"p90={Pick(times, 0.9):F0}ms median-render={Pick(renders, 0.5):F1}ms factor={PreviewDocument.FactorForZoom(_viewZoom)} " +
                              (settle == fullShown.Task ? $"full-res={fullShown.Task.Result:F0}ms after input (incl. 400 ms idle delay)" : "full-res did not appear within 10 s"));
        }
        session.Cancel();
        Console.WriteLine($"STYLEBENCH cancel restores the layer: {LayerStyleState.Of(layer) == before}");
    }
}
