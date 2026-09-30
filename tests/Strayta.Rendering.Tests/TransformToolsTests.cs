using Strayta.Core;
using Strayta.Rendering.Transforms;

namespace Strayta.Rendering.Tests;

/// <summary>The math behind Edit › Transform (distort, perspective, warp), Puppet Warp and Liquify.</summary>
public class TransformToolsTests
{
    private static Raster Solid(int w, int h, byte r, byte g, byte b)
    {
        var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(w, h, 8)).ToArray();
        planes[0].Data.AsSpan().Fill(r);
        planes[1].Data.AsSpan().Fill(g);
        planes[2].Data.AsSpan().Fill(b);
        planes[3].Data.AsSpan().Fill(255);
        return new Raster(ColorMode.Rgb, planes[..3], planes[3]);
    }

    private static Raster Checker(int w, int h, int cell)
    {
        var planes = Enumerable.Range(0, 4).Select(_ => Plane.Create(w, h, 8)).ToArray();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                byte v = (byte)(((x / cell) + (y / cell)) % 2 == 0 ? 230 : 20);
                planes[0].Data[i] = v; planes[1].Data[i] = (byte)(255 - v); planes[2].Data[i] = 90; planes[3].Data[i] = 255;
            }
        return new Raster(ColorMode.Rgb, planes[..3], planes[3]);
    }

    private static byte AlphaAt((Raster? Pixels, PixelRect Bounds) r, int x, int y) =>
        r.Pixels is { } p && x >= r.Bounds.Left && y >= r.Bounds.Top && x < r.Bounds.Right && y < r.Bounds.Bottom
            ? p.Alpha!.Data[(y - r.Bounds.Top) * r.Bounds.Width + x - r.Bounds.Left] : (byte)0;

    // ---- Projective maps (distort, perspective) ----------------------------------------------------------

    [Fact]
    public void A_rect_to_quad_map_sends_the_corners_home_and_inverts()
    {
        (double X, double Y)[] quad = [(10, 20), (130, 5), (120, 90), (30, 70)];
        var p = Projective.RectToQuad(100, 50, quad);
        var corners = new[] { (0.0, 0.0), (100.0, 0.0), (100.0, 50.0), (0.0, 50.0) }.Select(c => p.Apply(c.Item1, c.Item2)).ToArray();
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(quad[i].X, corners[i].X, 9);
            Assert.Equal(quad[i].Y, corners[i].Y, 9);
        }
        Assert.False(p.IsAffine);
        var back = p.Invert().Apply(quad[2].X, quad[2].Y);
        Assert.Equal(100, back.X, 9);
        Assert.Equal(50, back.Y, 9);
        // A preview's map (scaled down by 4) agrees with the full one.
        var small = p.Rescaled(4).Apply(25, 10);
        var full = p.Apply(100, 40);
        Assert.Equal(full.X / 4, small.X, 9);
        Assert.Equal(full.Y / 4, small.Y, 9);
    }

    [Fact]
    public void A_parallelogram_is_an_affine_map()
    {
        var p = Projective.RectToQuad(100, 50, [(0, 0), (100, 0), (130, 50), (30, 50)]);
        Assert.True(p.IsAffine);
    }

    [Fact]
    public void A_perspective_resample_fills_the_quad_and_nothing_else()
    {
        var src = Solid(100, 100, 200, 40, 40);
        (double X, double Y)[] quad = [(40, 20), (160, 20), (190, 180), (10, 180)]; // a trapezoid wider at the bottom
        var map = Projective.RectToQuad(100, 100, quad);
        var result = ProjectiveResampler.TransformRaster(src, new PixelRect(0, 0, 100, 100), map, ResampleFilter.Bicubic);
        Assert.Equal(255, AlphaAt(result, 100, 100));
        Assert.Equal(255, AlphaAt(result, 20, 175));
        Assert.Equal(0, AlphaAt(result, 20, 25)); // outside the narrow top
        Assert.Equal(0, AlphaAt(result, 180, 30));
        // The shrunken top is averaged, not aliased: a fine checker becomes an even gray there.
        var fine = ProjectiveResampler.TransformRaster(Checker(100, 100, 1), new PixelRect(0, 0, 100, 100),
            Projective.RectToQuad(100, 100, [(90, 20), (110, 20), (190, 180), (10, 180)]), ResampleFilter.Bicubic);
        var red = fine.Pixels!.ColorPlanes[0];
        int at = (22 - fine.Bounds.Top) * fine.Bounds.Width + (100 - fine.Bounds.Left);
        Assert.InRange(red.Data[at], 90, 160);
    }

    // ---- Warps --------------------------------------------------------------------------------------------

    [Fact]
    public void Every_style_is_flat_without_a_bend_and_bends_with_one()
    {
        foreach (var style in WarpStyles.Names)
        {
            Assert.True(new WarpSpec { Style = style, Bounds = (0, 0, 100, 60) }.IsNone, style);
            var bent = WarpMesh.From(new WarpSpec { Style = style, Value = 50, Bounds = (0, 0, 100, 60) })!;
            var flat = WarpMesh.From(new WarpSpec { Style = style, Value = 0.001, Bounds = (0, 0, 100, 60) })!;
            double moved = 0, still = 0;
            for (int j = 0; j <= 6; j++)
                for (int i = 0; i <= 10; i++)
                {
                    var p = bent.Map(i * 10, j * 10);
                    var q = flat.Map(i * 10, j * 10);
                    moved = Math.Max(moved, Math.Abs(p.X - i * 10) + Math.Abs(p.Y - j * 10));
                    still = Math.Max(still, Math.Abs(q.X - i * 10) + Math.Abs(q.Y - j * 10));
                }
            Assert.True(moved > 2, $"{style} bends ({moved:F2})");
            Assert.True(still < 0.5, $"{style} is flat at a tiny bend ({still:F3})");
        }
    }

    [Fact]
    public void Symmetric_styles_mirror_left_and_right()
    {
        foreach (var style in new[] { "warpArc", "warpArch", "warpBulge", "warpInflate", "warpSqueeze", "warpFisheye" })
        {
            var mesh = WarpMesh.From(new WarpSpec { Style = style, Value = 40, Bounds = (0, 0, 100, 60) })!;
            for (int j = 0; j <= 6; j++)
            {
                var l = mesh.Map(20, j * 10);
                var r = mesh.Map(80, j * 10);
                Assert.Equal(100 - l.X, r.X, 1);
                Assert.Equal(l.Y, r.Y, 1);
            }
        }
    }

    [Fact]
    public void Resplitting_keeps_the_surface()
    {
        var identity = WarpEditing.Resplit(null, 90, 60, 3, 3);
        Assert.Equal(10 * 10, identity.Points.Count);
        Assert.Equal((45.0, 30.0), identity.Map(45, 30));

        var arc = WarpMesh.From(new WarpSpec { Style = "warpFlag", Value = 50, Bounds = (0, 0, 90, 60) })!;
        foreach (int n in new[] { 3, 4, 5 })
        {
            var split = WarpEditing.Resplit(arc, 90, 60, n, n);
            double worst = 0;
            for (int j = 0; j <= 12; j++)
                for (int i = 0; i <= 18; i++)
                {
                    var a = arc.Map(i * 5, j * 5);
                    var b = split.Map(i * 5, j * 5);
                    worst = Math.Max(worst, Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)));
                }
            Assert.True(worst < 0.05, $"{n}×{n}: {worst:F4} px");
        }
    }

    [Fact]
    public void Dragging_the_surface_moves_the_grabbed_point_exactly_and_locate_finds_it()
    {
        var mesh = WarpEditing.Identity(100, 80, 1, 1);
        var moved = new WarpMesh(mesh.SlicesX, mesh.SlicesY, WarpEditing.DragSurface(mesh, 30, 20, 45, 5));
        var p = moved.Map(30, 20);
        Assert.Equal(45, p.X, 9);
        Assert.Equal(5, p.Y, 9);
        var corner = moved.Map(100, 80); // the far corner hardly moves
        Assert.True(Math.Abs(corner.X - 100) + Math.Abs(corner.Y - 80) < 1, $"far corner at {corner}");
        var found = WarpEditing.Locate(moved, 45, 5, 0.5);
        Assert.NotNull(found);
        Assert.Equal(30, found!.Value.U, 3);
        Assert.Equal(20, found.Value.V, 3);
        Assert.Null(WarpEditing.Locate(moved, 400, 400, 1));
    }

    [Fact]
    public void Fitting_frames_a_style_in_its_box()
    {
        var arc = WarpMesh.From(new WarpSpec { Style = "warpArc", Value = 50, Bounds = (0, 0, 100, 50) })!;
        var fitted = WarpEditing.Fitted(arc, 100, 50);
        Assert.Equal(0, fitted.Min(p => p.X), 9);
        Assert.Equal(100, fitted.Max(p => p.X), 9);
        Assert.Equal(0, fitted.Min(p => p.Y), 9);
        Assert.Equal(50, fitted.Max(p => p.Y), 9);
    }

    [Fact]
    public void A_triangle_map_resample_of_the_identity_keeps_the_pixels()
    {
        var src = Checker(40, 30, 3);
        var grid = TriangleMap.Grid(40, 30, (u, v) => (u + 10, v + 5))!;
        var (pixels, bounds) = MeshResampler.TransformRaster(ResampleSource.FromRaster(src), grid, ResampleFilter.Bicubic);
        int worst = 0;
        for (int y = 0; y < 30; y++)
            for (int x = 0; x < 40; x++)
            {
                int i = (y + 5 - bounds.Top) * bounds.Width + (x + 10 - bounds.Left);
                worst = Math.Max(worst, Math.Abs(pixels!.ColorPlanes[0].Data[i] - src.ColorPlanes[0].Data[y * 40 + x]));
            }
        Assert.True(worst <= 1, $"worst {worst}");
    }

    // ---- Puppet Warp ------------------------------------------------------------------------------------

    private static (PuppetMesh Mesh, ArapSolver Solver) Bar()
    {
        var alpha = Plane.Create(200, 40, 8);
        alpha.Data.AsSpan().Fill(255);
        var mesh = PuppetMesh.Build(alpha, new PixelRect(0, 0, 200, 40), PuppetDensity.Normal, 2)!;
        return (mesh, new ArapSolver(mesh) { Iterations = 300 });
    }

    [Fact]
    public void The_puppet_mesh_covers_the_opaque_pixels_only()
    {
        var alpha = Plane.Create(100, 100, 8);
        for (int y = 0; y < 100; y++)
            for (int x = 0; x < 30; x++) alpha.Data[y * 100 + x] = 255; // a column on the left
        var mesh = PuppetMesh.Build(alpha, new PixelRect(0, 0, 100, 100), PuppetDensity.More, 2)!;
        Assert.True(mesh.Contains(15, 50));
        Assert.False(mesh.Contains(80, 50));
        Assert.All(Enumerable.Range(0, mesh.VertexCount), i => Assert.True(mesh.X[i] < 30 + 2 + 2 * mesh.Spacing));
    }

    [Fact]
    public void Pins_at_rest_leave_the_mesh_at_rest()
    {
        var (mesh, solver) = Bar();
        int a = mesh.NearestVertex(0, 20, 50), b = mesh.NearestVertex(200, 20, 50);
        solver.SetPins([a, b], [(mesh.X[a], mesh.Y[a]), (mesh.X[b], mesh.Y[b])]);
        solver.Solve();
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            Assert.Equal(mesh.X[i], solver.X[i], 6);
            Assert.Equal(mesh.Y[i], solver.Y[i], 6);
        }
        Assert.True(solver.Energy() < 1e-9);
    }

    [Fact]
    public void Moving_every_pin_alike_moves_the_mesh_rigidly()
    {
        var (mesh, solver) = Bar();
        int a = mesh.NearestVertex(0, 20, 50), b = mesh.NearestVertex(200, 20, 50);
        // Turn both pins 90° about the bar's middle: the whole bar turns.
        (double X, double Y) Turn(double x, double y) => (100 - (y - 20), 20 + (x - 100));
        solver.SetPins([a, b], [Turn(mesh.X[a], mesh.Y[a]), Turn(mesh.X[b], mesh.Y[b])]);
        solver.Solve();
        double worst = 0;
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            var (tx, ty) = Turn(mesh.X[i], mesh.Y[i]);
            worst = Math.Max(worst, Math.Max(Math.Abs(solver.X[i] - tx), Math.Abs(solver.Y[i] - ty)));
        }
        Assert.True(worst < 0.5, $"worst {worst:F3} px");
        Assert.Equal(Turn(mesh.X[a], mesh.Y[a]).X, solver.X[a], 9); // pins are exactly where they were put
    }

    [Fact]
    public void Bending_keeps_edges_near_their_length_and_lowers_the_energy()
    {
        var (mesh, solver) = Bar();
        int a = mesh.NearestVertex(0, 20, 50), m = mesh.NearestVertex(100, 20, 50), b = mesh.NearestVertex(200, 20, 50);
        solver.SetPins([a, m, b], [(mesh.X[a], mesh.Y[a]), (mesh.X[m], mesh.Y[m] + 40), (mesh.X[b], mesh.Y[b])]);
        solver.Iterations = 1;
        solver.Solve();
        double first = solver.Energy();
        solver.Iterations = 40;
        solver.Solve();
        double later = solver.Energy();
        Assert.True(later <= first + 1e-9, $"energy {first:F2} → {later:F2}");
        // Rigid-ish: the mesh's edges keep their lengths within a few percent on average.
        double ratio = 0;
        int n = 0;
        for (int i = 0; i < mesh.VertexCount; i++)
            foreach (int j in mesh.Neighbors[i])
            {
                double rest = Math.Sqrt(Math.Pow(mesh.X[i] - mesh.X[j], 2) + Math.Pow(mesh.Y[i] - mesh.Y[j], 2));
                double now = Math.Sqrt(Math.Pow(solver.X[i] - solver.X[j], 2) + Math.Pow(solver.Y[i] - solver.Y[j], 2));
                ratio += Math.Abs(now / rest - 1);
                n++;
            }
        Assert.True(ratio / n < 0.08, $"mean stretch {ratio / n:P1}");
        Assert.True(solver.Y[m] > mesh.Y[m] + 39.9);
    }

    [Fact]
    public void A_part_without_pins_stays_put()
    {
        var alpha = Plane.Create(200, 40, 8);
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 200; x++)
                if (x < 60 || x >= 140) alpha.Data[y * 200 + x] = 255; // two blocks with a gap
        var mesh = PuppetMesh.Build(alpha, new PixelRect(0, 0, 200, 40), PuppetDensity.More, 1)!;
        var solver = new ArapSolver(mesh);
        int a = mesh.NearestVertex(10, 20, 50);
        solver.SetPins([a], [(mesh.X[a] + 30, mesh.Y[a])]);
        solver.Solve();
        int far = mesh.NearestVertex(190, 20, 50);
        Assert.Equal(mesh.X[far], solver.X[far], 9);
        Assert.True(solver.X[mesh.NearestVertex(40, 20, 50)] > 60); // the pinned block moved with its pin
    }

    // ---- Liquify ------------------------------------------------------------------------------------------

    private static LiquifyField Field() => new(new PixelRect(0, 0, 200, 200), 2);

    [Fact]
    public void Forward_warp_carries_content_along_the_stroke()
    {
        var f = Field();
        var brush = new LiquifyBrush(80, 100, 100, 50);
        f.Apply(LiquifyTool.ForwardWarp, 100, 100, 110, 100, brush);
        // At the brush center the content now comes from about 10 px back along the stroke.
        var (sx, sy) = f.Source(110, 100);
        Assert.InRange(sx, 99, 103);
        Assert.Equal(100, sy, 3);
        Assert.Equal((150.0, 20.0), f.Source(150, 20)); // outside the brush nothing moves
        f.RestoreAll();
        Assert.True(f.IsIdentity);
    }

    [Fact]
    public void Reconstruct_and_smooth_undo_a_distortion_gradually()
    {
        var f = Field();
        var brush = new LiquifyBrush(80, 100, 100, 50);
        f.Apply(LiquifyTool.ForwardWarp, 100, 100, 115, 100, brush);
        double before = Math.Abs(f.Sample(115, 100).Dx);
        f.Apply(LiquifyTool.Reconstruct, 115, 100, 115, 100, brush);
        double after = Math.Abs(f.Sample(115, 100).Dx);
        Assert.True(after < before && after > 0, $"{before:F2} → {after:F2}");
        for (int i = 0; i < 200; i++) f.Apply(LiquifyTool.Reconstruct, 115, 100, 115, 100, brush);
        Assert.True(Math.Abs(f.Sample(115, 100).Dx) < 0.05);
    }

    [Fact]
    public void Pucker_pulls_in_bloat_pushes_out_twirl_turns_clockwise()
    {
        var brush = new LiquifyBrush(100, 100, 100, 100);
        var pucker = Field();
        pucker.Apply(LiquifyTool.Pucker, 0, 0, 100, 100, brush);
        Assert.True(pucker.Source(120, 100).X > 120); // what shows at 120 came from farther out: content moved in
        var bloat = Field();
        bloat.Apply(LiquifyTool.Bloat, 0, 0, 100, 100, brush);
        Assert.True(bloat.Source(120, 100).X < 120);
        var twirl = Field();
        twirl.Apply(LiquifyTool.TwirlClockwise, 0, 0, 100, 100, brush);
        // Clockwise on screen (y down): content right of the center moves down, so what shows there came from above.
        Assert.True(twirl.Source(120, 100).Y < 100);
        var counter = Field();
        counter.Apply(LiquifyTool.TwirlClockwise, 0, 0, 100, 100, brush, alt: true);
        Assert.True(counter.Source(120, 100).Y > 100);
    }

    [Fact]
    public void Push_left_moves_content_to_the_left_of_the_stroke()
    {
        var f = Field();
        var brush = new LiquifyBrush(80, 100, 100, 50);
        f.Apply(LiquifyTool.PushLeft, 100, 100, 110, 100, brush); // stroking right: left of it is up
        Assert.True(f.Source(110, 100).Y > 100); // content from below now shows here: it moved up
    }

    [Fact]
    public void Frozen_areas_do_not_move()
    {
        var f = Field();
        var brush = new LiquifyBrush(80, 100, 100, 50);
        for (int i = 0; i < 5; i++) f.Apply(LiquifyTool.FreezeMask, 0, 0, 100, 100, brush with { Size = 200 });
        f.Apply(LiquifyTool.ForwardWarp, 100, 100, 110, 100, brush);
        Assert.Equal((110.0, 100.0), f.Source(110, 100));
        f.ThawAll();
        f.Apply(LiquifyTool.ForwardWarp, 100, 100, 110, 100, brush);
        Assert.NotEqual(110.0, f.Source(110, 100).X);
    }

    [Fact]
    public void The_field_as_triangles_applies_the_distortion_to_pixels()
    {
        var src = Solid(100, 100, 10, 200, 30);
        var f = new LiquifyField(new PixelRect(0, 0, 200, 200), 4);
        f.Apply(LiquifyTool.ForwardWarp, 150, 50, 170, 50, new LiquifyBrush(60, 100, 100, 50)); // push the edge area right
        var (pixels, bounds) = MeshResampler.TransformRaster(ResampleSource.FromRaster(src), f.ToTriangles(new PixelRect(0, 0, 100, 100)), ResampleFilter.Bicubic);
        Assert.NotNull(pixels);
        Assert.Equal(255, AlphaAt((pixels, bounds), 50, 50));
        Assert.Equal(0, AlphaAt((pixels, bounds), 150, 150));
    }
}

/// <summary>Content-Aware Scale: seam carving keeps what stands out.</summary>
public class SeamCarverTests
{
    /// <summary>A flat gray field with a checkered block (high energy) at columns 40..59, rows 10..29.</summary>
    private static float[] Scene(int w, int h)
    {
        var o = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool block = x is >= 40 and < 60 && y is >= 10 and < 30;
                float v = block ? (((x + y) & 1) == 0 ? 1f : 0f) : 0.5f + 0.001f * (x % 3);
                o[i] = o[i + 1] = o[i + 2] = v;
                o[i + 3] = 1;
            }
        return o;
    }

    [Fact]
    public void Every_seam_takes_one_pixel_per_row()
    {
        int w = 100, h = 40;
        var order = SeamCarver.RemovalOrder(Scene(w, h), w, h, 30);
        for (int step = 0; step < 30; step++)
            for (int y = 0; y < h; y++)
                Assert.Equal(1, Enumerable.Range(0, w).Count(x => order[y * w + x] == step));
    }

    [Fact]
    public void Batched_seams_also_take_one_pixel_per_row_and_avoid_the_block()
    {
        int w = 100, h = 40;
        var order = SeamCarver.RemovalOrder(Scene(w, h), w, h, 40, null, batch: 8);
        for (int step = 0; step < 40; step++)
            for (int y = 0; y < h; y++)
                Assert.Equal(1, Enumerable.Range(0, w).Count(x => order[y * w + x] == step));
        for (int y = 10; y < 30; y++)
            for (int x = 40; x < 60; x++) Assert.Equal(int.MaxValue, order[y * w + x]);
    }

    [Fact]
    public void Narrowing_keeps_the_busy_block_whole()
    {
        int w = 100, h = 40;
        var scene = Scene(w, h);
        var order = SeamCarver.RemovalOrder(scene, w, h, 40);
        // No seam in the first 40 goes through the block.
        for (int y = 10; y < 30; y++)
            for (int x = 40; x < 60; x++) Assert.Equal(int.MaxValue, order[y * w + x]);
        var narrow = SeamCarver.ApplyWidth(scene, w, h, order, 60);
        // The checker survives: row 20 of the result still alternates over 20 pixels somewhere.
        int run = 0, best = 0;
        for (int x = 1; x < 60; x++)
        {
            float a = narrow[(20 * 60 + x) * 4], b = narrow[(20 * 60 + x - 1) * 4];
            run = Math.Abs(a - b) > 0.9f ? run + 1 : 0;
            best = Math.Max(best, run);
        }
        Assert.True(best >= 18, $"longest alternating run {best}");
    }

    [Fact]
    public void Widening_inserts_seams_and_retarget_reaches_the_size()
    {
        int w = 100, h = 40;
        var scene = Scene(w, h);
        var result = SeamCarver.Retarget(scene, w, h, 130, 30);
        Assert.Equal(130 * 30 * 4, result.Length);
        var wide = SeamCarver.Retarget(scene, w, h, 260, 40); // more than half: in rounds
        Assert.Equal(260 * 40 * 4, wide.Length);
    }

    [Fact]
    public void Protected_pixels_are_carved_last()
    {
        int w = 60, h = 20;
        var flat = new float[w * h * 4];
        for (int i = 0; i < w * h; i++) { flat[i * 4] = flat[i * 4 + 1] = flat[i * 4 + 2] = 0.5f; flat[i * 4 + 3] = 1; }
        var protect = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < 20; x++) protect[y * w + x] = 1;
        var order = SeamCarver.RemovalOrder(flat, w, h, 30, protect);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < 20; x++) Assert.Equal(int.MaxValue, order[y * w + x]);
    }
}
