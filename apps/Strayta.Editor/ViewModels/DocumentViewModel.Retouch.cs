using System.Diagnostics;
using System.Runtime.CompilerServices;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;
using Strayta.Rendering;

namespace Strayta.Editor.ViewModels;

/// <summary>Which pixels the Clone Stamp and the healing tools sample (Photoshop's Sample menu).</summary>
public enum RetouchSample
{
    CurrentLayer,
    CurrentAndBelow,
    AllLayers,
}

// Retouching tools: Clone Stamp (S), Healing Brush and Spot Healing Brush (J).
//
// All three paint through the brush engine's PaintStroke, so they share its live overlay (the render lanes draw the
// stroke over its layer every frame), selection clipping, mask targeting and `[`/`]` sizing. The Clone Stamp's stroke
// carries a CloneSource: every painted pixel takes its color from the sampled image at an offset. The sampled image is
// fixed when the stroke starts (a layer's raster, which is never modified in place, or a flattened copy of the document
// rendered in the background), so a stroke never picks up pixels it painted itself.
//
// The healing tools heal on release, as Photoshop does: the Healing Brush shows the raw clone while dragging, the Spot
// Healing Brush a dark translucent stroke; release solves the Poisson heal (Core Healing / SpotHealing) and paints the
// healed patch through the same stroke as one undo step.
public sealed partial class DocumentViewModel
{
    /// <summary>A retouch stroke in progress: the tool, what it samples, and (for tools with a source point) the offset.</summary>
    private sealed class RetouchStroke
    {
        public required CanvasTool Tool { get; init; }
        public required Task<PixelSource> Image { get; init; }
        public required bool Mask { get; init; }
        public CloneSource? Source { get; init; }
        public HealMode HealMode { get; init; }
        public HealOptions Heal { get; init; }
        public SpotHealType SpotType { get; init; }
    }

    private RetouchStroke? _retouch;

    // A flattened copy of the document for Sample: Current & Below / All Layers, kept while the document is unchanged
    // (and prepared ahead of time once a source is set), so a stroke usually starts with its source ready.
    private (int Fingerprint, LayerNode? Below, Task<PixelSource> Image)? _retouchComposite;
    private PreviewDocument? _retouchSnapshot;
    private CpuRenderer? _retouchRenderer;
    private Task _retouchRender = Task.CompletedTask;

    /// <summary>
    /// The source point and aligned offset in the active clone source (shared by the Clone Stamp and Healing Brush;
    /// CloneSources.cs).
    /// </summary>
    internal CloneAligner CloneSourcePoint => ActiveCloneSource.AlignerFor(Editor.Tool);

    /// <summary>Time from release to the retouch edit being in the document, and the heal's share of it, for the self-test and benchmark.</summary>
    public (double TotalMs, double HealMs) LastRetouchTimings { get; private set; }

    /// <summary>The offset the last Spot Healing stroke healed from (destination − source).</summary>
    internal (int Dx, int Dy)? LastSpotOffset { get; private set; }

    /// <summary>Option-click with the Clone Stamp or Healing Brush: sets the source point.</summary>
    public void SetCloneSource(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Model.Width || y >= Model.Height) return;
        CloneSourcePoint.SetSource(x, y);
        ActiveCloneSource.Refresh();
        Notice = "";
        PrefetchRetouchSample();
    }

    /// <summary>
    /// Where the source is for the brush at image point (<paramref name="x"/>, <paramref name="y"/>): the sampled point
    /// under the brush while painting, or where the next stroke would start sampling. Null without a source point or
    /// for tools that have none. The canvas draws its crosshair and overlay there.
    /// </summary>
    public (float X, float Y)? RetouchSourceFor(float x, float y)
    {
        if (!Editor.UsesSourcePoint) return null;
        if (_retouch?.Source is { } s) return s.SourcePoint(x, y);
        var aligner = CloneSourcePoint;
        if (aligner.SourcePoint is not { } point) return null;
        // An aligned offset places the source relative to the pointer (through the slot's scale and angle); otherwise the
        // next stroke starts sampling at the source point itself.
        if (Editor.RetouchAligned && aligner.Offset is { } o)
            return new CloneSource(o.Dx, o.Dy, null, ActiveCloneSource.Transform, point.X + 0.5f, point.Y + 0.5f).SourcePoint(x, y);
        return (point.X + 0.5f, point.Y + 0.5f);
    }

    /// <summary>
    /// What the canvas's clone overlay shows for the brush at (<paramref name="x"/>, <paramref name="y"/>): the source
    /// point under the brush, the active source's placement and the panel's overlay options; null when there is no
    /// source to show.
    /// </summary>
    public CloneOverlay? CloneOverlayFor(float x, float y)
    {
        if (RetouchSourceFor(x, y) is not { } at) return null;
        var t = _retouch?.Source?.Transform ?? ActiveCloneSource.Transform;
        return new CloneOverlay(at.X, at.Y, t.ScaleX, t.ScaleY, t.Angle, Editor.CloneOverlayShow,
            Math.Clamp(Editor.CloneOverlayOpacity / 100, 0, 1), Editor.CloneOverlayClipped, Editor.CloneOverlayAutoHide);
    }

    /// <summary>
    /// Starts a Clone Stamp, Healing Brush or Spot Healing stroke on the selected layer, or on its mask when the mask is
    /// targeted (masks sample their own values); false, with a notice, when it cannot paint there.
    /// </summary>
    public bool BeginRetouchStroke(float x, float y)
    {
        if (_baking || IsTransforming || _stroke is not null) return false;
        var tool = Editor.Tool;
        if (BlockedByChannelTarget(tool == CanvasTool.CloneStamp ? "Clone Stamp" : "healing brush")) return false; // DocumentViewModel.Channels.cs
        LayerNode owner;
        bool mask = EditMask && SelectedLayer?.Node is { } n && n.GetMask() is not null;
        if (mask)
        {
            owner = SelectedLayer!.Node;
            string? problem = owner.Visible ? null : $"\"{owner.Name}\" is hidden.";
            if (Model.ColorMode is not (ColorMode.Rgb or ColorMode.Grayscale)) problem = $"Painting in {Model.ColorMode} documents is not supported yet.";
            if (problem is not null)
            {
                Notice = problem;
                PaintBlock = new PaintBlock(problem, CanRasterize: false, CanShow: !owner.Visible, CanNewLayer: false);
                return false;
            }
            Notice = "";
            PaintBlock = null;
        }
        else if (PaintableLayer() is { } layer) owner = layer;
        else return false;

        // Flow, airbrush and mode are the Clone Stamp's; the healing tools paint at full strength and blend on release.
        var brush = Editor.CurrentBrush;
        if (tool != CanvasTool.CloneStamp) brush = brush with { Flow = 1f, Mode = PaintMode.Normal, PressureOpacity = false };
        var image = SampleImageFor(owner, mask, Editor.CurrentRetouchSample);
        PaintStroke stroke;
        CloneSource? source = null;
        if (tool == CanvasTool.SpotHealing)
        {
            // Photoshop shows the spot being painted as a dark, translucent stroke; the heal replaces it on release.
            var preview = brush with { Opacity = 0.45f };
            stroke = mask
                ? PaintStroke.ForMask(owner, preview, 0f, Model.Bounds, Selection)
                : new PaintStroke((PixelLayer)owner, preview, new RgbColor(0.08f, 0.08f, 0.1f), erase: false, Model.Bounds, Selection);
        }
        else
        {
            var aligner = CloneSourcePoint;
            if (aligner.BeginStroke(x, y, Editor.RetouchAligned) is not { } offset || aligner.SourcePoint is not { } point)
            {
                Notice = $"Option-click to set a source point for the {Editor.ToolName} first.";
                return false;
            }
            ActiveCloneSource.Refresh();
            source = new CloneSource(offset.Dx, offset.Dy, image.IsCompletedSuccessfully ? image.Result : null,
                ActiveCloneSource.Transform, point.X + 0.5f, point.Y + 0.5f);
            if (!image.IsCompleted) _ = ShowSourceWhenReadyAsync(source, image);
            // The Healing Brush heals at full strength; its stroke only shows the raw clone until release.
            if (tool == CanvasTool.Healing) brush = brush with { Opacity = 1f };
            stroke = PaintStroke.Cloning(owner, mask, brush, source, Model.Bounds, Selection);
        }

        _stroke = stroke;
        _retouch = new RetouchStroke
        {
            Tool = tool, Image = image, Mask = mask, Source = source,
            HealMode = Editor.CurrentHealMode, Heal = Editor.CurrentHealOptions(brush.Size),
            SpotType = (SpotHealType)Math.Clamp(Editor.SpotTypeIndex, 0, 2),
        };
        StartStrokeInput(stroke, x, y); // pressure, smoothing, airbrush (DocumentViewModel.Brush.cs)
        RequestRender();
        return true;
    }

    private async Task ShowSourceWhenReadyAsync(CloneSource source, Task<PixelSource> image)
    {
        try
        {
            source.Image = await image;
            RequestRender();
        }
        catch (Exception ex)
        {
            Notice = $"Could not prepare the source: {ex.Message}";
        }
    }

    /// <summary>
    /// Release: clones or heals at full resolution in the background and records one undoable edit. The live overlay
    /// keeps showing the stroke until the new pixels are in, so nothing flickers. Called from <see cref="EndStrokeAsync"/>.
    /// </summary>
    private async Task EndRetouchStrokeAsync()
    {
        if (_retouch is not { } r || _stroke is not { } stroke) return;
        if (stroke.Bounds.IsEmpty)
        {
            (_stroke, _retouch) = (null, null);
            RequestRender();
            return;
        }
        var clock = Stopwatch.StartNew();
        _baking = true;
        try
        {
            var image = await r.Image;
            if (r.Source is { } s) s.Image = image;
            var doc = Model;
            var (canvas, mode, depth) = (doc.Bounds, doc.ColorMode, doc.BitDepth);
            var owner = stroke.Owner;
            var mask = r.Mask ? owner.GetMask() : null;
            double healMs = 0;
            var result = await Task.Run(() =>
            {
                var heal = Stopwatch.StartNew();
                (int Dx, int Dy)? found = null;
                var final = stroke;
                var paintMode = HealOptions.PaintModeFor(r.HealMode);
                switch (r.Tool)
                {
                    case CanvasTool.Healing:
                        // Replace paints the source as it is (a clone with the brush's soft edge); the rest heal, then blend.
                        final = r.HealMode == HealMode.Replace
                            ? stroke.WithSource(r.Source!, 1f)
                            : stroke.WithSource(new CloneSource(0, 0, Healing.HealStroke(image, r.Source!, stroke, canvas, r.Heal)), 1f, paintMode);
                        break;
                    case CanvasTool.SpotHealing:
                        final = stroke.WithSource(new CloneSource(0, 0, SpotHeal(image, stroke, canvas, r, out found)), 1f, paintMode);
                        break;
                }
                double healed = heal.Elapsed.TotalMilliseconds;
                if (mask is not null) return (Mask: MaskBaker.Bake(mask, final, depth), Pixels: default((Raster?, PixelRect)?), healed, found);
                var layer = (PixelLayer)owner;
                return (Mask: (LayerMask?)null, Pixels: ((Raster?, PixelRect)?)StrokeBaker.Bake(layer, final, mode, depth), healed, found);
            });
            healMs = result.healed;
            LastSpotOffset = result.found;
            string name = r.Tool switch
            {
                CanvasTool.CloneStamp => "Clone Stamp",
                CanvasTool.Healing => "Healing Brush",
                _ => "Spot Healing Brush",
            };
            (_stroke, _retouch) = (null, null);
            if (result.Mask is { } baked) Apply(new MaskEdit(owner, baked, name));
            else if (result.Pixels is { } px) Apply(new PixelsEdit((PixelLayer)owner, px.Item1, px.Item2, name));
            LastRetouchTimings = (clock.Elapsed.TotalMilliseconds, healMs);
            PrefetchRetouchSample();
        }
        catch (Exception ex)
        {
            (_stroke, _retouch) = (null, null);
            Notice = $"Could not finish the {Editor.ToolName}: {ex.Message}";
            RequestRender();
        }
        finally
        {
            _baking = false;
        }
    }

    // ---- Sampling ---------------------------------------------------------------------------------------

    /// <summary>The pixels a stroke on <paramref name="owner"/> samples, as they are now.</summary>
    private Task<PixelSource> SampleImageFor(LayerNode owner, bool mask, RetouchSample sample)
    {
        if (mask) return Task.FromResult(PixelSource.FromMask(owner.GetMask()!));
        if (sample == RetouchSample.CurrentLayer && owner is PixelLayer layer)
            return Task.FromResult(PixelSource.FromRaster(layer.Pixels, layer.Bounds));
        return CompositeSampleAsync(sample == RetouchSample.CurrentAndBelow ? owner : null);
    }

    /// <summary>Starts preparing the flattened image the next stroke will sample, when the current tool and options need one.</summary>
    private void PrefetchRetouchSample()
    {
        if (!Editor.IsRetouchTool || EditMask || SelectedLayer?.Node is not PixelLayer layer) return;
        var sample = Editor.CurrentRetouchSample;
        if (sample != RetouchSample.CurrentLayer) _ = CompositeSampleAsync(sample == RetouchSample.CurrentAndBelow ? layer : null);
    }

    /// <summary>
    /// The document flattened (only up to <paramref name="below"/> when given: Current &amp; Below), at the document's own
    /// bit depth. Rendered in the background from a snapshot of the layer tree, and reused while nothing changes.
    /// </summary>
    private Task<PixelSource> CompositeSampleAsync(LayerNode? below)
    {
        int fingerprint = ContentFingerprint();
        if (_retouchComposite is { } c && c.Fingerprint == fingerprint && ReferenceEquals(c.Below, below) && !c.Image.IsFaulted) return c.Image;
        var task = RenderCompositeAsync(_retouchRender, below);
        _retouchRender = task;
        _retouchComposite = (fingerprint, below, task);
        return task;
    }

    private async Task<PixelSource> RenderCompositeAsync(Task previous, LayerNode? below)
    {
        try { await previous; } catch (Exception) { /* a failed render does not stop the next one */ }
        // The snapshot is synced on the UI thread (after the previous render finished reading it), then rendered in
        // the background, so the render never reads the document while it is edited.
        if (_retouchSnapshot is not { } snapshot || snapshot.Proxy.Width != Model.Width || snapshot.Proxy.Height != Model.Height)
            _retouchSnapshot = snapshot = new PreviewDocument(Model, 1);
        var proxy = snapshot.Sync();
        HashSet<LayerNode>? hidden = null;
        if (below is not null)
        {
            hidden = new HashSet<LayerNode>(ReferenceEqualityComparer.Instance);
            for (var node = below; node.Parent is { } parent; node = parent)
                for (int i = parent.IndexOf(node) + 1; i < parent.Children.Count; i++)
                    if (snapshot.ProxyOf(parent.Children[i]) is { } p) hidden.Add(p);
        }
        var renderer = _retouchRenderer ??= new CpuRenderer(256L << 20);
        var (mode, depth) = (Model.ColorMode, Model.BitDepth);
        return await Task.Run(() =>
        {
            var result = renderer.Render(proxy, new RenderOptions { Hidden = hidden });
            return PixelSource.FromRaster(result.ToRaster(mode, depth), proxy.Bounds);
        });
    }

    /// <summary>
    /// A hash of everything that changes how the document looks (layer order, pixels, masks, visibility, opacity, blend
    /// modes, positions), so a flattened copy can be reused until one of them changes. Rasters and masks are compared
    /// by identity: edits always replace them.
    /// </summary>
    private int ContentFingerprint()
    {
        var hash = new HashCode();
        hash.Add(Model.Width);
        hash.Add(Model.Height);
        foreach (var node in Model.Root.Descendants())
        {
            hash.Add(RuntimeHelpers.GetHashCode(node));
            hash.Add(RuntimeHelpers.GetHashCode(node.Parent));
            hash.Add(node.Visible);
            hash.Add(node.Opacity);
            hash.Add(node.FillOpacity);
            hash.Add(node.BlendMode);
            hash.Add(node.Clipped);
            hash.Add(node.Effects is null ? 0 : RuntimeHelpers.GetHashCode(node.Effects));
            hash.Add(node.GetMask() is { } m ? RuntimeHelpers.GetHashCode(m) : 0);
            switch (node)
            {
                case PixelLayer p:
                    hash.Add(p.Pixels is null ? 0 : RuntimeHelpers.GetHashCode(p.Pixels));
                    hash.Add(p.Bounds);
                    break;
                case AdjustmentLayer a:
                    hash.Add(a.Adjustment?.GetHashCode() ?? 0); // adjustments are records: equal settings, equal hash
                    break;
            }
        }
        return hash.ToHashCode();
    }
}
