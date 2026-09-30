namespace Strayta.Core.Tests;

public class LayerAlignmentTests
{
    private static readonly PixelRect A = new(10, 10, 30, 20), B = new(50, 40, 60, 80), C = new(100, 5, 140, 25);

    [Fact]
    public void Align_edges_to_the_reference()
    {
        var boxes = new[] { A, B, C };
        var reference = LayerAlignment.Union(boxes);
        Assert.Equal(new PixelRect(10, 5, 140, 80), reference);

        Assert.Equal([(0, 0), (-40, 0), (-90, 0)], LayerAlignment.Align(boxes, AlignEdge.Left, reference));
        Assert.Equal([(110, 0), (80, 0), (0, 0)], LayerAlignment.Align(boxes, AlignEdge.Right, reference));
        Assert.Equal([(0, -5), (0, -35), (0, 0)], LayerAlignment.Align(boxes, AlignEdge.Top, reference));
        Assert.Equal([(0, 60), (0, 0), (0, 55)], LayerAlignment.Align(boxes, AlignEdge.Bottom, reference));
    }

    [Fact]
    public void Centres_round_down_to_whole_pixels()
    {
        // Reference centre 75; a 21-wide box centred there cannot be exact: it lands a half pixel left.
        var boxes = new[] { new PixelRect(0, 0, 21, 3) };
        var reference = new PixelRect(0, 0, 150, 10);
        var (dx, _) = LayerAlignment.Align(boxes, AlignEdge.HorizontalCenter, reference)[0];
        Assert.Equal(64, dx); // (150 - 21) / 2 = 64.5
        var (_, dy) = LayerAlignment.Align(boxes, AlignEdge.VerticalCenter, reference)[0];
        Assert.Equal(3, dy); // (10 - 3) / 2 = 3.5
    }

    [Fact]
    public void Align_to_the_canvas_for_one_layer()
    {
        var canvas = PixelRect.FromSize(200, 100);
        Assert.Equal([(0, 35)], LayerAlignment.Align([A], AlignEdge.VerticalCenter, canvas));
        Assert.Equal([(170, 0)], LayerAlignment.Align([A], AlignEdge.Right, canvas));
    }

    [Fact]
    public void Empty_boxes_never_move()
    {
        var result = LayerAlignment.Align([PixelRect.Empty, A], AlignEdge.Left, new PixelRect(0, 0, 5, 5));
        Assert.Equal((0, 0), result[0]);
        Assert.Equal((-10, 0), result[1]);
    }

    [Fact]
    public void Distribute_left_edges_evenly_between_the_outermost()
    {
        var boxes = new[] { new PixelRect(0, 0, 10, 10), new PixelRect(70, 0, 80, 10), new PixelRect(20, 0, 30, 10), new PixelRect(90, 0, 95, 10) };
        var moves = LayerAlignment.Distribute(boxes, DistributeMode.Left);
        // Lefts sorted: 0, 20, 70, 90 → evenly 0, 30, 60, 90.
        Assert.Equal([(0, 0), (-10, 0), (10, 0), (0, 0)], moves);
    }

    [Fact]
    public void Distribute_centres()
    {
        var boxes = new[] { new PixelRect(0, 0, 10, 10), new PixelRect(0, 12, 10, 20), new PixelRect(0, 90, 10, 110) };
        var moves = LayerAlignment.Distribute(boxes, DistributeMode.VerticalCenter);
        // Centres 5, 16, 100 → middle goes to 52.5, rounded away from zero to a 37-pixel move (16 → 53).
        Assert.Equal([(0, 0), (0, 37), (0, 0)], moves);
    }

    [Fact]
    public void Distribute_spacing_makes_equal_gaps()
    {
        var boxes = new[] { new PixelRect(0, 0, 10, 5), new PixelRect(12, 0, 42, 5), new PixelRect(100, 0, 110, 5) };
        var moves = LayerAlignment.Distribute(boxes, DistributeMode.HorizontalSpacing);
        // Span 110, widths 50 → gaps of 30: the middle box starts at 40.
        Assert.Equal([(0, 0), (28, 0), (0, 0)], moves);
    }

    [Fact]
    public void Distribute_needs_three_boxes()
    {
        Assert.All(LayerAlignment.Distribute([A, B], DistributeMode.Left), m => Assert.Equal((0, 0), m));
    }
}

public class LayerLockTests
{
    [Fact]
    public void Lock_all_implies_every_lock()
    {
        var layer = new PixelLayer { Locks = LayerLocks.All };
        Assert.True(layer.IsLocked(LayerLocks.Position));
        Assert.True(layer.IsLocked(LayerLocks.Pixels));
        Assert.True(layer.TransparencyLocked);
        Assert.True(layer.HasAnyLock());
    }

    [Fact]
    public void Group_locks_apply_to_their_layers()
    {
        var group = new LayerGroup { Locks = LayerLocks.Position };
        var inside = new PixelLayer();
        group.Add(inside);
        Assert.True(inside.IsLocked(LayerLocks.Position));
        Assert.False(inside.IsLocked(LayerLocks.Pixels));
        Assert.False(inside.HasAnyLock()); // not its own lock

        var free = new LayerGroup();
        var locked = new PixelLayer { Locks = LayerLocks.Position };
        free.Add(locked);
        Assert.False(free.IsLocked(LayerLocks.Position));
        Assert.True(free.IsPositionLockedWithin()); // moving the group would move the locked layer
    }

    [Fact]
    public void Transparency_lock_maps_onto_the_lock_bits()
    {
        var layer = new PixelLayer { TransparencyLocked = true };
        Assert.Equal(LayerLocks.Transparency, layer.Locks);
        layer.Locks |= LayerLocks.Position;
        layer.TransparencyLocked = false;
        Assert.Equal(LayerLocks.Position, layer.Locks);
    }
}
