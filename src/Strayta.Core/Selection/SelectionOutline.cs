using System.Numerics;

namespace Strayta.Core.Selection;

/// <summary>
/// The boundary of a selection as closed polygons along pixel edges, for drawing marching ants. A pixel counts as
/// selected at 50% coverage or more, like Photoshop's outline of a soft selection.
/// </summary>
public static class SelectionOutline
{
    /// <summary>
    /// Traces the outline in document coordinates. With <paramref name="factor"/> above 1 the mask is sampled on a
    /// coarser grid of factor × factor blocks, so zoomed-out views get an outline about as detailed as the screen
    /// instead of millions of vertices. Each loop runs with the selected area on its right (y down), and only
    /// corners are kept.
    /// </summary>
    public static IReadOnlyList<Vector2[]> Trace(SelectionMask? selection, int factor = 1)
    {
        if (selection is null) return [];
        var b = selection.Bounds;
        if (selection.IsRectangular)
            return [[new(b.Left, b.Top), new(b.Right, b.Top), new(b.Right, b.Bottom), new(b.Left, b.Bottom)]];

        factor = Math.Max(1, factor);
        int gw = (b.Width + factor - 1) / factor, gh = (b.Height + factor - 1) / factor;

        // Inside/outside grid with a one-cell empty border, so neighbors never need bounds checks.
        int stride = gw + 2;
        var grid = new bool[stride * (gh + 2)];
        Parallel.For(0, gh, () => new byte[b.Width], (gy, _, row) =>
        {
            int y = Math.Min(b.Top + gy * factor + factor / 2, b.Bottom - 1);
            selection.CopyRow(y, b.Left, row);
            int dst = (gy + 1) * stride + 1;
            for (int gx = 0; gx < gw; gx++)
                grid[dst + gx] = row[Math.Min(gx * factor + factor / 2, b.Width - 1)] >= 128;
            return row;
        }, _ => { });

        bool In(int cx, int cy) => grid[(cy + 1) * stride + cx + 1];

        // Boundary edges, directed so the inside is on the right. Vertex (vx, vy) is the top-left corner of cell
        // (vx, vy). Every loop has horizontal edges, so marking those (by their left end) is enough to trace each
        // loop once.
        var hSeen = new bool[(gw + 1) * (gh + 1)];
        var loops = new List<Vector2[]>();
        var points = new List<Vector2>();

        for (int vy = 0; vy <= gh; vy++)
        {
            for (int vx = 0; vx < gw; vx++)
            {
                // Horizontal edge from (vx, vy) to (vx + 1, vy): a boundary when the cells above and below differ.
                if (In(vx, vy) == In(vx, vy - 1) || hSeen[vy * (gw + 1) + vx]) continue;
                // Inside below means walking east; start at whichever end makes the direction right.
                int dir = In(vx, vy) ? East : West;
                int sx = dir == East ? vx : vx + 1;
                TraceLoop(sx, vy, dir);
            }
        }
        return loops;

        void TraceLoop(int x, int y, int dir)
        {
            points.Clear();
            int startX = x, startY = y, startDir = dir;
            int lastDir = -1;
            do
            {
                MarkSeen(x, y, dir);
                if (dir != lastDir) points.Add(new Vector2(b.Left + Math.Min(x * factor, b.Width), b.Top + Math.Min(y * factor, b.Height)));
                lastDir = dir;
                (x, y) = Step(x, y, dir);
                // Prefer turning right: at a vertex where two selected cells touch diagonally this keeps them as
                // separate outlines.
                int right = (dir + 1) & 3, left = (dir + 3) & 3;
                dir = IsBoundary(x, y, right) ? right : IsBoundary(x, y, dir) ? dir : left;
            }
            while (x != startX || y != startY || dir != startDir);
            if (lastDir == startDir && points.Count > 1) points.RemoveAt(0); // the start was mid-way along a side
            loops.Add([.. points]);
        }

        // Is the edge leaving vertex (x, y) in direction d a boundary with the inside on its right?
        bool IsBoundary(int x, int y, int d) => d switch
        {
            East => In(x, y) && !In(x, y - 1),
            South => In(x - 1, y) && !In(x, y),
            West => In(x - 1, y - 1) && !In(x - 1, y),
            _ => In(x, y - 1) && !In(x - 1, y - 1),
        };

        void MarkSeen(int x, int y, int d)
        {
            if (d == East) hSeen[y * (gw + 1) + x] = true;
            else if (d == West) hSeen[y * (gw + 1) + x - 1] = true;
        }
    }

    // Clockwise order (y down), so +1 is a right turn.
    private const int East = 0, South = 1, West = 2, North = 3;

    private static (int, int) Step(int x, int y, int d) => d switch
    {
        East => (x + 1, y),
        South => (x, y + 1),
        West => (x - 1, y),
        _ => (x, y - 1),
    };
}
