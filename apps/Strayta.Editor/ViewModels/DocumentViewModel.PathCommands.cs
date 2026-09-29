using System.Numerics;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Paths;
using Strayta.Core.Selection;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

// The Paths panel's commands on a document: save, rename, duplicate and delete paths, Fill Path, Stroke Path (with the
// current brush), Make Selection (⌘Return, with feather) and Make Work Path from the selection (with tolerance).
public sealed partial class DocumentViewModel
{
    /// <summary>The document's paths: the Work Path (if any) first, then saved paths.</summary>
    public IReadOnlyList<DocumentPath> DocumentPaths => DocumentPathsEdit.Read(Model);

    /// <summary>The path the Paths panel's commands use: the selected one, or the selected shape layer's outline.</summary>
    public VectorPath? CommandPath => PathTarget().Path;

    /// <summary>Selects a path in the Paths panel (-1 deselects: the selected layer's shape path is used again).</summary>
    public void SelectDocumentPath(int index)
    {
        FinishPen();
        _documentPathFocus = index >= 0;
        SelectedPathIndex = index;
        PathsChanged?.Invoke();
    }

    /// <summary>Makes the Work Path a saved path (Photoshop's Save Path), or adds an empty named path.</summary>
    public void SavePath(string? name = null)
    {
        var paths = DocumentPathsEdit.Read(Model);
        int work = paths.FindIndex(p => p.Kind == DocumentPathKind.Work);
        name ??= NextPathName(paths);
        if (work >= 0 && (SelectedPathIndex == work || SelectedPathIndex < 0))
        {
            paths[work] = paths[work] with { Name = name, Kind = DocumentPathKind.Saved, Id = 0, SourceData = null };
            // Saved paths follow the work path in the panel.
            var saved = paths[work];
            paths.RemoveAt(work);
            paths.Add(saved);
            Apply(DocumentPathsEdit.To(Model, paths, "Save Path"));
            SelectDocumentPath(paths.Count - 1);
            return;
        }
        paths.Add(new DocumentPath(name, new VectorPath([]) { InitialFillAll = false }, DocumentPathKind.Saved));
        Apply(DocumentPathsEdit.To(Model, paths, "New Path"));
        SelectDocumentPath(paths.Count - 1);
    }

    private static string NextPathName(IReadOnlyList<DocumentPath> paths)
    {
        for (int i = 1; ; i++)
            if (paths.All(p => p.Name != $"Path {i}")) return $"Path {i}";
    }

    public void RenamePath(int index, string name)
    {
        var paths = DocumentPathsEdit.Read(Model);
        if (index < 0 || index >= paths.Count || string.IsNullOrWhiteSpace(name) || paths[index].Name == name) return;
        paths[index] = paths[index].Kind == DocumentPathKind.Work
            ? paths[index] with { Name = name, Kind = DocumentPathKind.Saved, Id = 0, SourceData = null }
            : paths[index] with { Name = name };
        Apply(DocumentPathsEdit.To(Model, paths, "Rename Path"));
        PathsChanged?.Invoke();
    }

    public void DuplicatePath(int index)
    {
        var paths = DocumentPathsEdit.Read(Model);
        if (index < 0 || index >= paths.Count) return;
        var copy = paths[index] with { Name = paths[index].Name + " copy", Kind = DocumentPathKind.Saved, Id = 0, SourceData = null };
        paths.Add(copy);
        Apply(DocumentPathsEdit.To(Model, paths, "Duplicate Path"));
        SelectDocumentPath(paths.Count - 1);
    }

    public void DeletePath(int index)
    {
        var paths = DocumentPathsEdit.Read(Model);
        if (index < 0 || index >= paths.Count) return;
        paths.RemoveAt(index);
        Apply(DocumentPathsEdit.To(Model, paths, "Delete Path"));
        SelectDocumentPath(-1);
    }

    /// <summary>The path's coverage as a selection over the canvas (null: nothing inside).</summary>
    public SelectionMask? PathCoverage(VectorPath path)
    {
        var canvas = Model.Bounds;
        var area = PathRasterizer.Bounds(path, canvas);
        if (area.IsEmpty) return null;
        return SelectionMask.FromCoverage(area, PathRasterizer.Rasterize(path, area), canvas);
    }

    /// <summary>Make Selection (⌘Return loads the path as it is): the path's area, feathered by <paramref name="feather"/> pixels.</summary>
    public bool LoadPathAsSelection(float feather = 0, SelectionMode mode = SelectionMode.Replace)
    {
        if (CommandPath is not { } path)
        {
            Notice = "Select a path (or a shape layer) to make a selection from it.";
            return false;
        }
        var shape = PathCoverage(path);
        if (feather > 0) shape = SelectionModify.Feather(shape, feather, Model.Bounds);
        SetSelection(SelectionMask.Combine(Selection, shape, mode), "Make Selection");
        return true;
    }

    /// <summary>Fill Path: fills the path's area on the selected layer with <paramref name="options"/>, as one step.</summary>
    public async Task FillPathAsync(FillOptions options)
    {
        if (CommandPath is not { } path || PathCoverage(path) is not { } coverage)
        {
            Notice = "Select a path to fill.";
            return;
        }
        if (EditableLayer("fill") is not { } layer) return;
        await EditPixelsAsync(layer, "Fill Path", doc => FillPainter.Fill(layer, coverage, options, doc.ColorMode, doc.BitDepth));
    }

    /// <summary>
    /// Stroke Path with the current brush: dabs along every subpath (curves followed within a quarter pixel), painted as
    /// one stroke on the selected layer, inside the selection, as one step.
    /// </summary>
    public async Task StrokePathAsync(BrushSettings brush, RgbColor color)
    {
        if (CommandPath is not { } path || path.IsEmpty)
        {
            Notice = "Select a path to stroke.";
            return;
        }
        if (EditableLayer("stroke") is not { } layer || _baking) return;
        var stroke = new PaintStroke(layer, brush, color, erase: false, Model.Bounds, Selection);
        foreach (var s in path.Subpaths)
        {
            var points = PathRasterizer.Flatten(s, 0.25);
            if (points.Count == 0) continue;
            if (s.Closed) points.Add(points[0]);
            stroke.Lift();
            foreach (var p in points) stroke.StrokeTo((float)p.X, (float)p.Y);
        }
        if (stroke.Bounds.IsEmpty) return;
        _baking = true;
        try
        {
            var doc = Model;
            var (pixels, bounds) = await Task.Run(() => StrokeBaker.Bake(layer, stroke, doc.ColorMode, doc.BitDepth));
            Apply(new PixelsEdit(layer, pixels, bounds, "Stroke Path"));
        }
        finally
        {
            _baking = false;
        }
    }

    /// <summary>
    /// Make Work Path: traces the selection's outline (50% coverage) and simplifies it within <paramref name="tolerance"/>
    /// pixels, rounding gentle turns into smooth points. Islands and holes stay one component, so holes stay holes.
    /// </summary>
    public bool MakeWorkPathFromSelection(double tolerance = 2)
    {
        if (Selection is not { } selection)
        {
            Notice = "Make a selection first: Make Work Path traces its outline.";
            return false;
        }
        var loops = SelectionOutline.Trace(selection);
        var subpaths = new List<Subpath>();
        foreach (var loop in loops)
        {
            var simplified = Simplify(loop, Math.Clamp(tolerance, 0.5, 10));
            if (simplified.Count < 3) continue;
            var sub = SmoothPolygon(simplified);
            subpaths.Add(subpaths.Count == 0 ? sub : sub with { RecordTail = ShapeGeometry.ContinuationTail });
        }
        if (subpaths.Count == 0) return false;
        var paths = DocumentPathsEdit.Read(Model);
        paths.RemoveAll(p => p.Kind == DocumentPathKind.Work);
        paths.Insert(0, new DocumentPath("Work Path", new VectorPath(subpaths) { InitialFillAll = false }, DocumentPathKind.Work));
        Apply(DocumentPathsEdit.To(Model, paths, "Make Work Path"));
        SelectDocumentPath(0);
        return true;
    }

    /// <summary>Douglas–Peucker on a closed loop.</summary>
    private static List<PathPoint> Simplify(IReadOnlyList<Vector2> loop, double tolerance)
    {
        var pts = loop.Select(v => new PathPoint(v.X, v.Y)).ToList();
        if (pts.Count < 4) return pts;
        // Split the loop at its two farthest-apart points and simplify each half.
        int far = 0;
        double best = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            double d = PathPoint.Distance(pts[0], pts[i]);
            if (d > best) (best, far) = (d, i);
        }
        var keep = new bool[pts.Count];
        keep[0] = keep[far] = true;
        Mark(0, far);
        Mark(far, pts.Count);
        return pts.Where((_, i) => keep[i]).ToList();

        void Mark(int a, int b)
        {
            var pa = pts[a];
            var pb = pts[b % pts.Count];
            double maxD = 0;
            int at = -1;
            for (int i = a + 1; i < b; i++)
            {
                double d = SegmentDistance(pts[i], pa, pb);
                if (d > maxD) (maxD, at) = (d, i);
            }
            if (at < 0 || maxD <= tolerance) return;
            keep[at] = true;
            Mark(a, at);
            Mark(at, b);
        }
    }

    private static double SegmentDistance(PathPoint p, PathPoint a, PathPoint b)
    {
        var ab = b - a;
        double len2 = ab.X * ab.X + ab.Y * ab.Y;
        if (len2 < 1e-12) return PathPoint.Distance(p, a);
        double t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0, 1);
        return PathPoint.Distance(p, a + ab * t);
    }

    /// <summary>A closed polygon as a path whose gentle turns are smooth points (Catmull–Rom handles) and sharp ones corners.</summary>
    private static Subpath SmoothPolygon(List<PathPoint> pts)
    {
        int n = pts.Count;
        var knots = new PathKnot[n];
        for (int i = 0; i < n; i++)
        {
            var prev = pts[(i - 1 + n) % n];
            var p = pts[i];
            var next = pts[(i + 1) % n];
            var d1 = p - prev;
            var d2 = next - p;
            double cos = (d1.X * d2.X + d1.Y * d2.Y) / Math.Max(1e-12, d1.Length * d2.Length);
            if (cos < 0.5)
            {
                knots[i] = PathKnot.Corner(p); // turns by more than 60°: a corner
                continue;
            }
            var tangent = (next - prev) * (1.0 / 6);
            knots[i] = new PathKnot(p - tangent, p, p + tangent, true);
        }
        return new Subpath(knots, true);
    }
}
