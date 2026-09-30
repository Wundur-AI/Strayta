using Strayta.Core;
using Strayta.Core.Text;
using Strayta.Editor.Editing;
using Strayta.Psd;
using Strayta.Psd.Text;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.ViewModels;

// Edit › Transform: the modes (Scale, Rotate, Skew, Distort, Perspective, Warp), Rotate 180° / 90° and the flips.
//
// Warp works in a frame that can be written back as the layer's own warp: a smart object's placed size (its warp goes
// into the placed-layer descriptor and it stays a smart object, drawn from its content while warping), type's bounds
// (Warp Text: the style goes into the type data and the text is drawn again, bent), or the box of pixels (baked).
public sealed partial class DocumentViewModel
{
    /// <summary>A smart object being warped: the layer, its placement data and its content image.</summary>
    private sealed record WarpedSmartObject(PixelLayer Layer, PsdSmartObject Info, Raster Content);

    /// <summary>Type being warped (Warp Text): the layer, its text, its bounds in text space and the flat text.</summary>
    private sealed record WarpedType(PixelLayer Layer, PsdLayerRecord Record, TextLayerData Data, TextRect Bounds, TextWarp.Flat Flat);

    /// <summary>What a warp commits for a live layer: new corners and warp for a smart object, a style for type.</summary>
    private sealed record WarpCommitInfo(LayerNode Layer, (double X, double Y)[] Corners, WarpSpec? Spec, WarpSpec? TypeSpec);

    private WarpedSmartObject? _warpSmartObject;
    private WarpedType? _warpType;

    private WarpedSmartObject? WarpSmartObject() => FreeTransform?.Warp is not null ? _warpSmartObject : null;

    /// <summary>
    /// The rasterize prompt Photoshop shows before <paramref name="mode"/> on the selected layer (type and shapes cannot
    /// be distorted or put in perspective live; shapes and fills cannot be warped live); null when none is needed.
    /// </summary>
    public string? TransformRasterizePrompt(TransformMode mode)
    {
        if (SelectedLayer?.Node is not PixelLayer { Visible: true } node || !NeedsRasterizing(node)) return null;
        bool perspective = mode is TransformMode.Distort or TransformMode.Perspective;
        bool warp = mode == TransformMode.Warp && !node.Tags.Contains("text");
        if (!perspective && !warp) return null;
        return node.Tags.Contains("text") ? "This type layer must be rasterized before proceeding. Its text will no longer be editable."
            : node.Tags.Contains("shape") ? "This shape layer must be rasterized before proceeding. It will no longer be editable as a shape."
            : "This layer must be rasterized before proceeding. Its fill will no longer be editable.";
    }

    /// <summary>
    /// Edit › Transform › Scale, Rotate, Skew, Distort, Perspective or Warp: opens the transform when needed and sets
    /// what handle drags do. False (with a notice) when the layer cannot take that mode.
    /// </summary>
    public bool SetTransformMode(TransformMode mode)
    {
        if (!IsTransforming && !BeginFreeTransform()) return false;
        var ft = FreeTransform!;
        if (mode == TransformMode.Warp)
        {
            if (!EnterWarp(ft)) return false;
        }
        else if (mode is TransformMode.Distort or TransformMode.Perspective && !ft.AllowsPerspective)
        {
            Notice = "Type, shape and fill layers cannot be distorted without rasterizing them.";
        }
        ft.Mode = mode;
        return true;
    }

    /// <summary>Sets up the warp for the transform's target the first time Warp mode is chosen.</summary>
    private bool EnterWarp(FreeTransform ft)
    {
        if (ft.Warp is not null) return true;
        var node = _transformNode;
        WarpTransform? warp = null;
        if (node is PixelLayer layer && layer.Tags.Contains("smart-object"))
        {
            if (Model.SourceData is PsdFile file && SmartObjects.Read(layer) is { } so && SmartObjects.Content(file, so.UniqueId, Model) is { } content
                && !SmartObjects.UnknownFilters((PsdLayerRecord)layer.SourceData!).Any())
            {
                _warpSmartObject = new WarpedSmartObject(layer, so, content);
                warp = new WarpTransform(so.Width, so.Height, so.Warp);
            }
            else
            {
                Notice = $"\"{layer.Name}\" cannot be drawn from its contents, so it cannot be warped. Rasterize it to warp its pixels.";
                return false;
            }
        }
        else if (node is PixelLayer text && text.Tags.Contains("text"))
        {
            if (text.SourceData is not PsdLayerRecord record || PsdTypeLayer.Read(record) is not { } data)
            {
                Notice = $"\"{text.Name}\" cannot be read as type.";
                return false;
            }
            if (data.Orientation != TextOrientation.Horizontal || data.FontsUsed.Any(f => !Text.FontCatalog.System.Contains(f)))
            {
                Notice = $"\"{text.Name}\" uses fonts that are not installed (or vertical type), so its text cannot be warped.";
                return false;
            }
            var bounds = PsdTypeWarp.TextBounds(record) ?? Text.TextLayout.Create(data).InkBounds;
            var place = TextWarp.Placement(data.Transform);
            if (bounds.Width <= 0 || TextWarp.DrawFlat(data, Model, TextWarp.ScaleFor(place.Then(ft.Map), bounds)) is not { } flat)
            {
                Notice = $"\"{text.Name}\" has no text to warp.";
                return false;
            }
            _warpType = new WarpedType(text, record, data, bounds, flat);
            var existing = PsdTypeWarp.Read(record);
            warp = new WarpTransform(bounds.Width, bounds.Height, existing is null ? null : existing with { Bounds = (0, 0, bounds.Width, bounds.Height) })
            {
                FitStyles = false, StylesOnly = true,
            };
            if (existing is null) warp.Style = "warpArc"; // Warp Text starts from a style
        }
        else if (_transformTargets.Any(n => NeedsRasterizing(n) || n != node && n.Tags.Contains("smart-object")))
        {
            Notice = "Warp works on one smart object, type layer or on pixels; rasterize the live layers in this group to warp it.";
            return false;
        }
        warp ??= new WarpTransform(ft.Original.Width, ft.Original.Height);
        ft.Warp = warp;
        ft.WarpFrame = WarpPlacement;
        warp.Changed += ft.RaiseChanged;
        OnTransformChanged();
        return true;
    }

    /// <summary>The map from the warp's frame to the document (before the warp itself).</summary>
    public Projective WarpPlacement()
    {
        if (FreeTransform is not { } ft) return Projective.Identity;
        if (_warpSmartObject is { } so) return SmartObjectTransform.Placement(so.Info).Then(ft.Map);
        if (_warpType is { } t)
            return Projective.Translation(t.Bounds.Left, t.Bounds.Top).Then(TextWarp.Placement(t.Data.Transform)).Then(ft.Map);
        return Projective.Translation(ft.Original.Left, ft.Original.Top).Then(ft.Map);
    }

    /// <summary>The deform of a warp on a live layer drawn from its own content (smart objects, type), or null.</summary>
    private Deform? LiveWarpDeform(int version, FreeTransform ft, WarpTransform warp)
    {
        var mesh = warp.DocumentMesh(WarpPlacement());
        if (_warpSmartObject is { } so)
        {
            double kx = so.Info.Width / so.Content.Width, ky = so.Info.Height / so.Content.Height;
            return new Deform(version, null, null, Single(so.Layer, new DeformContent(so.Content, (u, v) => mesh.Map(u * kx, v * ky))));
        }
        if (_warpType is { } t)
        {
            var flat = t.Flat;
            double l = t.Bounds.Left, top = t.Bounds.Top;
            return new Deform(version, null, null, Single(t.Layer, new DeformContent(flat.Pixels, (u, v) =>
            {
                var (x, y) = flat.ToText(u, v);
                return mesh.Map(x - l, y - top);
            })));
        }
        return null;
    }

    private static Dictionary<LayerNode, DeformContent> Single(LayerNode node, DeformContent content) =>
        new(ReferenceEqualityComparer.Instance) { [node] = content };

    /// <summary>The corners and warp a committed warp writes into a smart object or type layer.</summary>
    private WarpCommitInfo? WarpCommit(FreeTransform ft)
    {
        if (ft.Warp is not { } warp) return null;
        var spec = warp.ToSpec();
        if (_warpSmartObject is { } so)
        {
            var place = WarpPlacement();
            var corners = spec is { Style: "warpCustom", Mesh: { } points }
                ? SmartObjectTransform.CornersForPoints(points, place)
                : new[] { (0.0, 0.0), (so.Info.Width, 0.0), (so.Info.Width, so.Info.Height), (0.0, so.Info.Height) }.Select(p => place.Apply(p.Item1, p.Item2)).ToArray();
            return new WarpCommitInfo(so.Layer, corners, warp.IsIdentity ? new WarpSpec { Bounds = spec.Bounds } : spec, null);
        }
        if (_warpType is { } t)
        {
            var b = t.Bounds;
            return new WarpCommitInfo(t.Layer, [], null, spec with { Bounds = (b.Left, b.Top, b.Right, b.Bottom) });
        }
        return null;
    }

    /// <summary>Type through a committed Warp Text: the transform moved, the style written into the type data, the text drawn again.</summary>
    private static TransformEdit.State? WarpedTypeResult(TransformEdit.State s, Affine map, WarpSpec spec, Document doc)
    {
        if (s.Source is not PsdLayerRecord record) return null;
        var moved = TransformedSource(record, map, doc) as PsdLayerRecord ?? record;
        var warped = PsdTypeWarp.WithWarp(moved, spec);
        if (PsdTypeLayer.Read(warped) is not { } data) return null;
        var drawn = data.Warp is null
            ? Text.TextRenderer.Render(data, new Text.TextRenderOptions { ColorMode = doc.ColorMode, BitDepth = doc.BitDepth }) is var r ? (r.Pixels, r.Bounds) : default
            : TextWarp.Render(warped, data, doc);
        if (drawn is not { } result) return null;
        var mask = Resampler.TransformMask(s.Mask, map, ResampleFilter.Bicubic);
        return new TransformEdit.State(result.Pixels, result.Pixels is null ? PixelRect.Empty : result.Bounds, mask, warped);
    }

    // ---- Rotate and flip ---------------------------------------------------------------------------

    /// <summary>
    /// Edit › Transform › Rotate 180°, Rotate 90° Clockwise / Counter Clockwise, Flip Horizontal / Vertical: applied to
    /// the open transform's box, or straight to the selected layer as one undo step when none is open.
    /// </summary>
    public async Task TransformActionAsync(string action)
    {
        bool open = IsTransforming;
        if (!open && !BeginFreeTransform()) return;
        var ft = FreeTransform!;
        switch (action)
        {
            case "rotate180": ft.Rotate(180); break;
            case "rotate90cw": ft.Rotate(90); break;
            case "rotate90ccw": ft.Rotate(-90); break;
            case "flipH": ft.FlipHorizontal(); break;
            case "flipV": ft.FlipVertical(); break;
            default: return;
        }
        if (open) return;
        _transformDescription = action switch
        {
            "rotate180" => "Rotate 180°",
            "rotate90cw" => "Rotate 90° Clockwise",
            "rotate90ccw" => "Rotate 90° Counter Clockwise",
            "flipH" => "Flip Horizontal",
            _ => "Flip Vertical",
        };
        await CommitTransformAsync();
    }
}
