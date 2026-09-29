using System.Diagnostics;
using Strayta.Core;

namespace Strayta.Segmentation.Tests;

/// <summary>The Object Finder's filtering, merging and hit tests on synthetic masks, and one real run when the models are installed.</summary>
public sealed class ObjectFinderTests(ITestOutputHelper output)
{
    private static readonly PixelRect Placement = new(100, 50, 612, 306); // 512×256 document pixels over a 256×256 mask

    /// <summary>Logits for a disc on the 256×256 mask grid: +4 inside, −4 outside (crisp and stable).</summary>
    private static MaskLogits Disc(float cx, float cy, float r, float score = 0.95f)
    {
        var values = new float[256 * 256];
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
                values[y * 256 + x] = (x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy) <= r * r ? 4f : -4f;
        return new MaskLogits(values, 256, 256, Placement, score);
    }

    [Fact]
    public void Grid_points_cover_the_image_at_cell_centers()
    {
        var points = ObjectFinder.GridPoints(Placement, 4);
        Assert.Equal(16, points.Count);
        Assert.Equal(new PromptPoint(100 + 64, 50 + 32), points[0]);
        Assert.Equal(new PromptPoint(100 + 448, 50 + 224), points[^1]);
    }

    [Fact]
    public void Candidates_need_confidence_stability_and_a_sensible_size()
    {
        Assert.NotNull(ObjectFinder.Candidate(Disc(128, 128, 40)));
        Assert.Null(ObjectFinder.Candidate(Disc(128, 128, 40, score: 0.5f)));  // SAM is unsure
        Assert.Null(ObjectFinder.Candidate(Disc(128, 128, 1)));                // a speck
        Assert.Null(ObjectFinder.Candidate(Disc(128, 128, 400)));              // the whole image: background

        // A mask whose logits hover around zero changes a lot with the threshold: unstable.
        var soft = Disc(128, 128, 40);
        for (int i = 0; i < soft.Values.Length; i++) soft.Values[i] = soft.Values[i] > 0 ? 0.5f : -0.2f;
        Assert.Null(ObjectFinder.Candidate(soft));
    }

    [Fact]
    public void Duplicates_merge_keeping_the_better_one()
    {
        var kept = new List<FoundObject>();
        Assert.True(ObjectFinder.Merge(kept, ObjectFinder.Candidate(Disc(80, 80, 30, 0.85f))!));
        Assert.True(ObjectFinder.Merge(kept, ObjectFinder.Candidate(Disc(81, 80, 30, 0.95f))!));  // same object, better
        Assert.False(ObjectFinder.Merge(kept, ObjectFinder.Candidate(Disc(80, 81, 30, 0.9f))!)); // same object, worse
        Assert.True(ObjectFinder.Merge(kept, ObjectFinder.Candidate(Disc(190, 190, 20))!));     // another object
        Assert.Equal(2, kept.Count);
        Assert.Equal(0.95f, kept[0].Score);
    }

    [Fact]
    public void The_object_under_the_pointer_is_the_innermost_one()
    {
        var outer = ObjectFinder.Candidate(Disc(128, 128, 80))!;
        var inner = ObjectFinder.Candidate(Disc(128, 128, 20))!;
        var objects = new[] { outer, inner };
        // Mask cell (128, 128) is document (100 + 256, 50 + 128).
        Assert.Same(inner, ObjectFinder.ObjectAt(objects, 356, 178));
        Assert.Same(outer, ObjectFinder.ObjectAt(objects, 356 + 2 * 50, 178)); // 50 cells right: only the outer disc
        Assert.Null(ObjectFinder.ObjectAt(objects, 105, 55));
        // The outline is in document coordinates, around the disc.
        var loop = Assert.Single(inner.Outline);
        Assert.InRange(loop.Min(p => p.X), 356 - 2 * 21, 356 - 2 * 19);
        Assert.InRange(loop.Max(p => p.Y), 178 + 19, 178 + 21);
    }

    [Fact]
    public void Finds_the_objects_in_an_image()
    {
        using var engine = new SegmentationEngine(SegmentationModels.Locate());
        if (!engine.CanSelectObjects) Assert.Skip(SegmentationModels.FetchHint);
        var image = TestImages.Disc(800, 600, 300, 280, 140, (235, 200, 60), (30, 34, 40));
        var embedding = engine.Encode(image);
        var clock = Stopwatch.StartNew();
        var objects = engine.FindObjects(embedding, 8, CancellationToken.None);
        output.WriteLine($"{objects.Count} objects from 64 points in {clock.ElapsedMilliseconds} ms ({clock.ElapsedMilliseconds / 64.0:F1} ms per point)");
        var disc = ObjectFinder.ObjectAt(objects, 300, 280);
        Assert.NotNull(disc);
        Assert.True(disc.Contains(300 + 100, 280) && !disc.Contains(300 + 180, 280));

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => engine.FindObjects(embedding, 8, cancel.Token));
    }
}
