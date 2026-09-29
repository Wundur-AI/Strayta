using Strayta.Core;
using Strayta.Core.Selection;
using Strayta.Rendering;

namespace Strayta.Editor.ViewModels;

// Photoshop's Sample menu for the Eyedropper: Current Layer, Current & Below, All Layers, and the two "No Adjustments"
// variants. Current Layer and All Layers reuse the Magic Wand's cached sample images; the others render the document
// with the layers above the selected one, or the adjustment layers, hidden. That render runs in the background from a
// snapshot of the layer tree and is reused until the document or the selected layer changes.
public sealed partial class DocumentViewModel
{
    private (int Version, LayerNode? Current, LayerSample Sample, Task<SampleImage?> Image)? _subsetSample;
    private PreviewDocument? _subsetSnapshot;
    private CpuRenderer? _subsetRenderer;
    private Task _subsetRender = Task.CompletedTask;

    /// <summary>The pixels a sampling tool reads for <paramref name="sample"/>, straight from cache when nothing changed.</summary>
    internal Task<SampleImage?> LayerSampleImageAsync(LayerSample sample)
    {
        switch (sample)
        {
            case LayerSample.CurrentLayer:
                return SampleImageAsync(sampleAll: false);
            case LayerSample.AllLayers:
                return SampleImageAsync(sampleAll: true);
        }
        var current = SelectedLayer?.Node;
        if (_subsetSample is { } c && c.Version == _modelVersion && ReferenceEquals(c.Current, current) && c.Sample == sample && !c.Image.IsFaulted)
            return c.Image;
        var task = RenderSubsetAsync(_subsetRender, current, sample);
        _subsetRender = task;
        _subsetSample = (_modelVersion, current, sample, task);
        return task;
    }

    private async Task<SampleImage?> RenderSubsetAsync(Task previous, LayerNode? current, LayerSample sample)
    {
        try { await previous; } catch (Exception) { /* a failed render does not stop the next one */ }
        // Synced on the UI thread after the previous render finished reading the snapshot, then rendered in the background.
        if (_subsetSnapshot is not { } snapshot || snapshot.Proxy.Width != Model.Width || snapshot.Proxy.Height != Model.Height)
            _subsetSnapshot = snapshot = new PreviewDocument(Model, 1);
        var proxy = snapshot.Sync();
        var hidden = new HashSet<LayerNode>(ReferenceEqualityComparer.Instance);
        bool below = sample is LayerSample.CurrentAndBelow or LayerSample.CurrentAndBelowNoAdjustments;
        if (below && current is not null)
            for (var node = current; node.Parent is { } parent; node = parent)
                for (int i = parent.IndexOf(node) + 1; i < parent.Children.Count; i++)
                    if (snapshot.ProxyOf(parent.Children[i]) is { } p) hidden.Add(p);
        if (sample is LayerSample.AllLayersNoAdjustments or LayerSample.CurrentAndBelowNoAdjustments)
            foreach (var node in Model.Root.Descendants())
                if (node is AdjustmentLayer && snapshot.ProxyOf(node) is { } p) hidden.Add(p);
        var renderer = _subsetRenderer ??= new CpuRenderer(256L << 20);
        int w = Model.Width, h = Model.Height;
        try
        {
            return await Task.Run(() =>
            {
                var rgba = renderer.Render(proxy, new RenderOptions { Hidden = hidden }).ToRgba8();
                return SampleImage.FromStraightRgba(rgba, w, h);
            });
        }
        catch (Exception ex)
        {
            Notice = $"Could not sample the image: {ex.Message}";
            return null;
        }
    }
}
