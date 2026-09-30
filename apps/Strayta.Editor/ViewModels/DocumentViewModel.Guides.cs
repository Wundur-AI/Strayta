using Avalonia;
using Strayta.Core;
using Strayta.Editor.Editing;
using PixelRect = Strayta.Core.PixelRect;

namespace Strayta.Editor.ViewModels;

// A document's guides (saved in the file as PSD resource 1032, each change one history step), its ruler origin, the
// snapping targets its drags use, and the Info panel's readout for the pointer over it.
public sealed partial class DocumentViewModel
{
    /// <summary>The document's guides (the canvas draws and drags them).</summary>
    public IReadOnlyList<Guide> Guides => Model.Guides;

    /// <summary>
    /// Where the rulers' zero is, in document pixels (dragged from the rulers' corner; a double-click there resets it).
    /// Photoshop keeps it with the open document, not in the file.
    /// </summary>
    public Point RulerOrigin
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The document's resolution in pixels per inch (ruler units and the grid use it).</summary>
    public double Resolution => Model.Resolution;

    /// <summary>Called from <see cref="AfterChange"/>: guide steps (and crops, which move guides) redraw the guides.</summary>
    private void AfterGuideChange(IEdit edit)
    {
        if (edit is GuideEdit or CanvasEdit) OnPropertyChanged(nameof(Guides));
        if (edit is not CanvasEdit) return;
        OnPropertyChanged(nameof(Resolution));
        RefreshDocumentInfo();
    }

    private void SetGuides(IReadOnlyList<Guide> guides, string description) => Apply(new GuideEdit(Model, guides, description));

    /// <summary>Adds a guide (dragged from a ruler, or View › New Guide…).</summary>
    public void AddGuide(Guide guide) => SetGuides([.. Model.Guides, guide.At(Psd.PsdGuides.Quantize(guide.Position))], "New Guide");

    /// <summary>Moves guide <paramref name="index"/> to <paramref name="position"/> (document pixels); dragging it off the canvas deletes it.</summary>
    public void MoveGuide(int index, double position)
    {
        if ((uint)index >= (uint)Model.Guides.Count) return;
        var list = Model.Guides.ToList();
        var moved = list[index].At(Psd.PsdGuides.Quantize(position));
        if (moved == list[index]) return;
        list[index] = moved;
        SetGuides(list, "Move Guide");
    }

    public void DeleteGuide(int index)
    {
        if ((uint)index >= (uint)Model.Guides.Count) return;
        var list = Model.Guides.ToList();
        list.RemoveAt(index);
        SetGuides(list, "Delete Guide");
    }

    /// <summary>View › Clear Guides.</summary>
    public void ClearGuides()
    {
        if (Model.Guides.Count > 0) SetGuides([], "Clear Guides");
    }

    /// <summary>View › New Guide Layout: adds the layout's guides (after clearing the old ones when asked), skipping duplicates.</summary>
    public void AddGuideLayout(GuideLayout layout, bool clearExisting)
    {
        var existing = clearExisting ? [] : Model.Guides;
        var added = layout.GuidesFor(Model.Width, Model.Height).Where(g => !existing.Contains(g));
        var guides = existing.Concat(added).ToList();
        if (!guides.SequenceEqual(Model.Guides)) SetGuides(guides, "New Guide Layout");
    }

    // ---- Snapping ----------------------------------------------------------------------------------------

    /// <summary>
    /// A snapper for a drag starting now at <paramref name="zoom"/> (screen pixels per document pixel), with what View ›
    /// Snap To lists: visible guides, the visible grid, the edges and centers of visible layers (not the selected one when
    /// <paramref name="excludeSelected"/>, as it is the one moving) and the document's edges and center. Snapping off
    /// gives <see cref="Snapper.Off"/>. See <see cref="Snapper"/> for how tools use it.
    /// </summary>
    public Snapper CreateSnapper(double zoom, bool excludeSelected = false) => CreateSnapper(new SnapRequest(zoom, excludeSelected));

    /// <summary>A snapper for <paramref name="request"/>; see <see cref="CreateSnapper(double, bool)"/>.</summary>
    public Snapper CreateSnapper(SnapRequest request)
    {
        var e = Editor;
        double zoom = request.Zoom;
        bool excludeSelected = request.ExcludeSelectedLayer;
        if (!e.SnapEnabled || e.SnapTo == SnapTargets.None || zoom <= 0) return Snapper.Off;
        var xs = new List<(double, SnapKind)>();
        var ys = new List<(double, SnapKind)>();
        // Photoshop only snaps to guides and the grid while they show.
        if (e.SnapsTo(SnapTargets.Guides) && e.ShowGuides)
            for (int i = 0; i < Model.Guides.Count; i++)
                if (i != request.ExcludeGuide)
                    (Model.Guides[i].IsHorizontal ? ys : xs).Add((Model.Guides[i].Position, SnapKind.Guide));
        if (e.SnapsTo(SnapTargets.DocumentBounds))
        {
            xs.AddRange([(0, SnapKind.DocumentBounds), (Model.Width / 2.0, SnapKind.DocumentBounds), (Model.Width, SnapKind.DocumentBounds)]);
            ys.AddRange([(0, SnapKind.DocumentBounds), (Model.Height / 2.0, SnapKind.DocumentBounds), (Model.Height, SnapKind.DocumentBounds)]);
        }
        if (e.SnapsTo(SnapTargets.Layers))
        {
            var moving = excludeSelected ? SelectedLayer?.Node : null;
            foreach (var b in LayerSnapBounds(moving))
            {
                xs.AddRange([(b.Left, SnapKind.Layer), ((b.Left + b.Right) / 2.0, SnapKind.Layer), (b.Right, SnapKind.Layer)]);
                ys.AddRange([(b.Top, SnapKind.Layer), ((b.Top + b.Bottom) / 2.0, SnapKind.Layer), (b.Bottom, SnapKind.Layer)]);
            }
        }
        double grid = e.SnapsTo(SnapTargets.Grid) && e.ShowGrid ? e.GridSpacingPixels(Model) / Math.Max(1, e.GridSubdivisions) : 0;
        return new Snapper(Snapper.ScreenDistance / zoom, xs, ys, grid);
    }

    /// <summary>Bounds of the visible pixel layers other than <paramref name="except"/> (and what is inside it), clipped to the canvas.</summary>
    private IEnumerable<PixelRect> LayerSnapBounds(LayerNode? except)
    {
        var canvas = Model.Bounds;
        foreach (var node in Model.Root.Descendants())
        {
            if (node is not PixelLayer { Visible: true, Pixels: not null } layer || IsInside(node, except) || !VisibleUpTo(node)) continue;
            var b = layer.Bounds.Intersect(canvas);
            if (!b.IsEmpty && b != canvas) yield return b; // a full-canvas layer adds nothing the document bounds do not
        }
    }

    private static bool IsInside(LayerNode node, LayerNode? group)
    {
        for (LayerNode? n = node; n is not null; n = n.Parent)
            if (ReferenceEquals(n, group)) return true;
        return false;
    }

    private static bool VisibleUpTo(LayerNode node)
    {
        for (var g = node.Parent; g is not null; g = g.Parent)
            if (!g.Visible) return false;
        return true;
    }

    /// <summary>The box the Move tool moves (the selected layer's pixels, or everything in the selected group), or null.</summary>
    public PixelRect? MovingBounds()
    {
        if (HasMultipleSelected || SelectedLayer?.Node.LinkGroup is not (null or 0)) return SelectionMovingBounds(); // DocumentViewModel.LayerSelection.cs
        if (SelectedLayer?.Node is not { } node) return null;
        PixelRect? box = null;
        foreach (var layer in (node is LayerGroup g ? g.Descendants() : [node]).OfType<PixelLayer>())
        {
            if (layer.Pixels is null || layer.Bounds.IsEmpty) continue;
            var b = layer.Bounds;
            box = box is { } a ? new PixelRect(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom)) : b;
        }
        return box;
    }

    // ---- Info panel --------------------------------------------------------------------------------------

    /// <summary>
    /// The pointer moved over the canvas (<paramref name="image"/> in document pixels, null when it left), with the box
    /// being drawn if any (marquee, crop, text box). Updates the Info panel: position from the ruler origin in ruler
    /// units, the color of the latest full-resolution render there (no new render is started for it), and the size of
    /// the box, the Free Transform or the selection.
    /// </summary>
    public void UpdatePointerInfo(Point? image, Rect? box)
    {
        var info = Editor.Info;
        var unit = Editor.RulerUnit;
        info.Unit = RulerUnits.Suffix(unit);
        string Len(double px, double extent) => RulerUnits.Format(RulerUnits.ToUnits(px, unit, Model.Resolution, extent), unit);
        if (image is { } p)
        {
            info.X = Len(p.X - RulerOrigin.X, Model.Width);
            info.Y = Len(p.Y - RulerOrigin.Y, Model.Height);
            int x = (int)Math.Floor(p.X), y = (int)Math.Floor(p.Y);
            bool sixteen = Model.BitDepth >= 16;
            info.Depth = sixteen ? "16-bit" : "8-bit";
            if (_lastRender is { } rgba && rgba.Length == Model.Width * Model.Height * 4 && x >= 0 && y >= 0 && x < Model.Width && y < Model.Height
                && rgba[(y * Model.Width + x) * 4 + 3] > 0)
            {
                int i = (y * Model.Width + x) * 4;
                // 16-bit documents show Photoshop's 0–32768 scale (read from the 8-bit display render, so in steps of 128).
                string C(byte v) => sixteen ? ((int)Math.Round(v * 32768.0 / 255)).ToString() : v.ToString();
                (info.R, info.G, info.B) = (C(rgba[i]), C(rgba[i + 1]), C(rgba[i + 2]));
            }
            else (info.R, info.G, info.B) = ("", "", "");
        }
        else (info.X, info.Y, info.R, info.G, info.B) = ("", "", "", "", "");

        (double W, double H)? size = box is { } r ? (r.Width, r.Height)
            : FreeTransform is { } t ? (Math.Abs(t.WidthPercent / 100 * t.Original.Width), Math.Abs(t.HeightPercent / 100 * t.Original.Height))
            : Selection is { } s ? (s.Bounds.Width, s.Bounds.Height)
            : null;
        (info.W, info.H) = size is { } wh ? (Len(wh.W, Model.Width), Len(wh.H, Model.Height)) : ("", "");
        RefreshDocumentInfo();
    }

    /// <summary>The Info panel's document size, in ruler units.</summary>
    public void RefreshDocumentInfo()
    {
        if (!ReferenceEquals(Editor.ActiveDocument, this)) return;
        var unit = Editor.RulerUnit;
        string w = RulerUnits.Format(RulerUnits.ToUnits(Model.Width, unit, Model.Resolution, Model.Width), unit);
        string h = RulerUnits.Format(RulerUnits.ToUnits(Model.Height, unit, Model.Resolution, Model.Height), unit);
        Editor.Info.DocumentSize = $"{w} × {h} {RulerUnits.Suffix(unit)}";
    }
}
