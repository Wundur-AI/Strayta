using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Editor.Editing;
using Strayta.Rendering;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.ViewModels;

// Edit › Puppet Warp: a mesh over the selected layer's opaque pixels, deformed by dragging pins. While it is open the
// render lanes draw the layer through the deformed mesh (DeformPreview with the mesh's triangles), bilinear at preview
// resolution while dragging and bicubic at full resolution once idle; Enter bakes it as one undo step, Esc leaves the
// layer as it was. Smart objects are baked into pixels (Photoshop would keep it as a smart filter), with a notice.
public sealed partial class DocumentViewModel
{
    private DeformPreview? _puppetPreview;

    /// <summary>The open Puppet Warp, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPuppetWarping))]
    public partial PuppetWarpSession? PuppetWarp { get; private set; }

    public bool IsPuppetWarping => PuppetWarp is not null;

    private PuppetWarpSession? _puppet => PuppetWarp;

    /// <summary>Time from the last pin drag to the preview frame showing it, for the self-test (median over a drag).</summary>
    public double LastPuppetCommitMs { get; private set; }

    /// <summary>Photoshop's rasterize prompt before Puppet Warp on type, shapes and fills (smart objects are baked without one); null when none.</summary>
    public string? PuppetRasterizePrompt()
    {
        if (SelectedLayer?.Node is not PixelLayer { Visible: true } node || !NeedsRasterizing(node)) return null;
        return node.Tags.Contains("text") ? "This type layer must be rasterized before proceeding. Its text will no longer be editable."
            : node.Tags.Contains("shape") ? "This shape layer must be rasterized before proceeding. It will no longer be editable as a shape."
            : "This layer must be rasterized before proceeding. Its fill will no longer be editable.";
    }

    /// <summary>Starts Puppet Warp on the selected layer; false (with a notice) when it has nothing to warp.</summary>
    public Task<bool> BeginPuppetWarpAsync()
    {
        if (IsPuppetWarping) return Task.FromResult(true);
        if (IsTransforming)
        {
            Notice = "Apply or cancel the transform first.";
            return Task.FromResult(false);
        }
        CommitType();
        string? problem = SelectedLayer?.Node switch
        {
            null => "Select a layer to warp.",
            LayerGroup => "Puppet Warp works on one layer at a time.",
            { Visible: false } n => $"\"{n.Name}\" is hidden. Show it to warp it.",
            not PixelLayer => "Adjustment layers have no pixels to warp.",
            PixelLayer { Pixels: null } n => $"\"{n.Name}\" has no pixels to warp.",
            _ => null,
        };
        if (problem is not null || SelectedLayer!.Node is not PixelLayer layer || PuppetWarpSession.Create(layer) is not { } session)
        {
            Notice = problem ?? $"\"{SelectedLayer?.Node.Name}\" has no opaque pixels to warp.";
            return Task.FromResult(false);
        }
        Notice = layer.Tags.Contains("smart-object")
            ? $"Puppet Warp on \"{layer.Name}\" will be applied to its pixels: the smart object becomes a pixel layer."
            : "Click the mesh to add pins, drag them to warp; Option-click removes a pin. Enter applies, Esc cancels.";
        PaintBlock = null;
        _puppetPreview = new DeformPreview([layer]);
        session.Changed += OnPuppetChanged;
        PuppetWarp = session;
        PropertyChanged += CommitPuppetOnSelectionChange;
        OnPuppetChanged();
        return Task.FromResult(true);
    }

    private void OnPuppetChanged()
    {
        if (PuppetWarp is not { } s || _puppetPreview is null) return;
        if (!s.HasPins)
        {
            _puppetPreview.Current = null;
            RequestRender();
            return;
        }
        // Snapshot the deformed positions: the render thread reads them while the next drag moves on.
        double[] x = [.. s.X], y = [.. s.Y];
        var mesh = s.Mesh;
        _puppetPreview.Current = new Deform(s.Version, null, null, null, (_, bounds, f) => mesh.Map(x, y, bounds, f));
        RequestRender();
    }

    private void CommitPuppetOnSelectionChange(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectedLayer) && PuppetWarp is { } s && SelectedLayer?.Node != s.Layer) _ = CommitPuppetWarpAsync();
    }

    private Action<CancellationToken>? PreparePuppet(PreviewDocument proxy, ResampleFilter filter) =>
        _puppetPreview is { Current: not null } p ? p.Prepare(proxy, filter) : null;

    /// <summary>Enter: bakes the warp into the layer (and its mask) at full resolution as one undo step "Puppet Warp".</summary>
    public async Task CommitPuppetWarpAsync()
    {
        if (PuppetWarp is not { } s || _baking) return;
        if (!s.IsDeformed)
        {
            EndPuppetWarp();
            RequestRender();
            return;
        }
        _baking = true;
        var clock = Stopwatch.StartNew();
        try
        {
            var layer = s.Layer;
            var state = TransformEdit.Read(layer);
            double[] x = [.. s.X], y = [.. s.Y];
            var mesh = s.Mesh;
            var clip = new PixelRect(-Model.Width, -Model.Height, 2 * Model.Width, 2 * Model.Height);
            // The preview's full-resolution result, when it is ready, is the answer.
            var ready = _puppetPreview?.FullResult(layer, state.Pixels!, state.Bounds, s.Version);
            var after = await Task.Run(() =>
            {
                var (pixels, bounds) = ready ?? MeshResampler.TransformRaster(ResampleSource.FromRaster(state.Pixels!), mesh.Map(x, y, state.Bounds), ResampleFilter.Bicubic, clip);
                var mask = state.Mask is { Pixels: { } plane, PositionRelativeToLayer: false } m
                    ? MeshResampler.TransformMask(m, ResampleSource.FromPlane(plane), mesh.Map(x, y, m.Bounds), ResampleFilter.Bicubic)
                    : state.Mask;
                return new TransformEdit.State(pixels, pixels is null ? PixelRect.Empty : bounds, mask, state.Source);
            });
            bool smart = layer.Tags.Contains("smart-object");
            EndPuppetWarp();
            if (smart && after.Source is Psd.PsdLayerRecord record)
                // Baked: the smart object becomes pixels, in the same history step.
                Apply(new CompositeEdit("Puppet Warp", new RasterizeEdit(layer), new TransformEdit([(layer, after with { Source = record.Rasterized() })], "Puppet Warp")));
            else Apply(new TransformEdit([(layer, after)], "Puppet Warp"));
            LastPuppetCommitMs = clock.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex)
        {
            Notice = $"Could not apply Puppet Warp: {ex.Message}";
        }
        finally
        {
            _baking = false;
        }
    }

    /// <summary>
    /// ⌘Z while Puppet Warp is open: steps back through the pins, then out of Puppet Warp (as undo leaves Free Transform).
    /// True when it handled the undo.
    /// </summary>
    private bool UndoInsidePuppetWarp()
    {
        if (PuppetWarp is not { } s) return false;
        if (!s.Undo()) CancelPuppetWarp();
        return true;
    }

    /// <summary>Esc: closes Puppet Warp without changing anything.</summary>
    public void CancelPuppetWarp()
    {
        if (PuppetWarp is null) return;
        EndPuppetWarp();
        RequestRender();
    }

    private void EndPuppetWarp()
    {
        if (PuppetWarp is { } s) s.Changed -= OnPuppetChanged;
        PropertyChanged -= CommitPuppetOnSelectionChange;
        PuppetWarp = null;
        _puppetPreview = null;
        Notice = "";
    }
}
