using System.Diagnostics;
using Strayta.Core;
using Strayta.Editor.Editing;
using Strayta.Rendering;

namespace Strayta.Editor.ViewModels;

/// <summary>Layer styles: the Layer Style dialog's session, Copy / Paste / Clear Layer Style, and effect visibility.</summary>
public sealed partial class DocumentViewModel
{
    /// <summary>
    /// Why the selected layer cannot take a layer style, or null if it can. Styles on groups are kept and saved but not
    /// drawn yet, and adjustment layers have no pixels for effects to follow, so both are refused for now.
    /// </summary>
    private string? LayerStyleProblem(LayerNode? node) => node switch
    {
        null => "Select a layer to give it a layer style.",
        LayerGroup => "Layer styles on groups are not supported yet.",
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
        var pasted = style with { Effects = LayerStyleEdit.WithGlobalAngle(style.Effects, Model.GlobalLightAngle) };
        var before = LayerStyleState.Of(node);
        if (pasted == before) return;
        Apply(new LayerStyleEdit("Paste Layer Style", Model, [(node, before, pasted)]));
    }

    /// <summary>Layer › Layer Style › Clear Layer Style: removes the effects (the blending options stay).</summary>
    public void ClearLayerStyle()
    {
        if (SelectedLayer?.Node is not { Effects: not null } node) return;
        var before = LayerStyleState.Of(node);
        Apply(new LayerStyleEdit("Clear Layer Style", Model, [(node, before, before with { Effects = null })]));
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
    public async Task RunLayerStyleBenchmarkAsync()
    {
        var before = SelectedLayer?.Node is { } selected ? LayerStyleState.Of(selected) : null; // opening already previews the new shadow
        var session = BeginLayerStyle(LayerStylePage.DropShadow) ?? throw new InvalidOperationException(Notice);
        var layer = session.Layer;
        var shadow = (ShadowEntry)session.SelectedEntry!;
        foreach (var (name, drag) in new (string, Action<int>)[]
                 {
                     ("Size", i => shadow.Size = 10 + i % 120),
                     ("Distance", i => shadow.Distance = 5 + i % 60),
                 })
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
            Console.WriteLine($"STYLEBENCH drop shadow {name} layer={bounds?.Width}x{bounds?.Height} doc={Model.Width}x{Model.Height} " +
                              $"input={inputMs:F0}ms frames={frames} fps={frames / (inputMs / 1000):F1} median-frame={Pick(times, 0.5):F0}ms " +
                              $"p90={Pick(times, 0.9):F0}ms median-render={Pick(renders, 0.5):F1}ms factor={PreviewDocument.FactorForZoom(_viewZoom)} " +
                              (settle == fullShown.Task ? $"full-res={fullShown.Task.Result:F0}ms after input (incl. 400 ms idle delay)" : "full-res did not appear within 10 s"));
        }
        session.Cancel();
        Console.WriteLine($"STYLEBENCH cancel restores the layer: {LayerStyleState.Of(layer) == before}");
    }
}
