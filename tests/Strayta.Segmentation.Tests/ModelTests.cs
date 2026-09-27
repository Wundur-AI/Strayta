using Strayta.Core;
using Strayta.Core.Selection;

namespace Strayta.Segmentation.Tests;

/// <summary>
/// Runs the real models on synthetic images. The models are not in the repository (see models/MODELS.md), so these
/// are skipped until `dotnet build tools/FetchModels.proj` has been run.
/// </summary>
public sealed class ModelTests : IClassFixture<ModelTests.EngineFixture>
{
    public sealed class EngineFixture : IDisposable
    {
        public SegmentationEngine Engine { get; } = new(SegmentationModels.Locate());
        public void Dispose() => Engine.Dispose();
    }

    private readonly SegmentationEngine _engine;

    public ModelTests(EngineFixture fixture) => _engine = fixture.Engine;

    // A bright disc on a dark background, off center, in a non-square image.
    private const int W = 1200, H = 900;
    private const float Cx = 700, Cy = 420, R = 220;
    private static readonly RgbaImage Image = TestImages.Disc(W, H, Cx, Cy, R, (235, 200, 60), (30, 34, 40));
    private static readonly PixelRect Canvas = PixelRect.FromSize(W, H);

    private static double DiscIou(SelectionMask? mask) => TestImages.Iou(mask, Canvas, (x, y) => TestImages.InDisc(x, y, Cx, Cy, R));

    private void RequireObjectModel()
    {
        if (!_engine.CanSelectObjects) Assert.Skip(SegmentationModels.FetchHint);
    }

    [Fact]
    public void Box_around_the_disc_selects_it()
    {
        RequireObjectModel();
        var embedding = _engine.Encode(Image);
        var box = new PixelRect((int)(Cx - R - 30), (int)(Cy - R - 30), (int)(Cx + R + 30), (int)(Cy + R + 30));
        var mask = _engine.SelectObject(embedding, SamPrompt.Rectangle(box), Canvas);
        double iou = DiscIou(mask);
        Assert.True(iou > 0.9, $"IoU {iou:F3}");
    }

    [Fact]
    public void Click_on_the_disc_selects_it()
    {
        RequireObjectModel();
        var embedding = _engine.Encode(Image);
        var mask = _engine.SelectObject(embedding, SamPrompt.Click(Cx + 40, Cy - 30), Canvas);
        double iou = DiscIou(mask);
        Assert.True(iou > 0.9, $"IoU {iou:F3}");
    }

    [Fact]
    public void Candidates_are_all_three_masks_and_decode_picks_the_best()
    {
        RequireObjectModel();
        var embedding = _engine.Encode(Image);
        var prompt = new SamPrompt([new PromptPoint(Cx - 60, Cy), new PromptPoint(Cx + 60, Cy + 20)]);
        var candidates = _engine.DecodeCandidates(embedding, prompt);
        Assert.Equal(3, candidates.Count);
        var best = _engine.Decode(embedding, prompt);
        Assert.Equal(candidates.Max(c => c.Score), best.Score);
        // Brushed points inside the disc: the largest good candidate is the disc, with its edge where the disc's is.
        var disc = candidates.Where(c => c.Score > 0.7f).MaxBy(c => c.Values.Count(v => v > 0))!;
        Assert.True(disc.SignedDistance(Cx, Cy) > 100, $"{disc.SignedDistance(Cx, Cy)}");
        Assert.InRange(disc.SignedDistance(Cx + R, Cy), -6, 6);
        Assert.True(disc.SignedDistance(40, 40) < 0);
    }

    [Fact]
    public void A_moved_layer_reuses_its_embedding_at_the_new_place()
    {
        RequireObjectModel();
        var layer = TestImages.Disc(W, H, Cx, Cy, R, (235, 200, 60), (30, 34, 40), left: 300, top: 200);
        var embedding = _engine.Encode(layer).MovedTo(new PixelRect(500, 100, 500 + W, 100 + H));
        var canvas = PixelRect.FromSize(3000, 2000);
        var mask = _engine.SelectObject(embedding, SamPrompt.Click(500 + Cx, 100 + Cy), canvas);
        double iou = TestImages.Iou(mask, canvas, (x, y) => TestImages.InDisc(x - 500, y - 100, Cx, Cy, R));
        Assert.True(iou > 0.9, $"IoU {iou:F3}");
    }

    [Fact]
    public async Task Embeddings_are_cached_by_content_identity()
    {
        RequireObjectModel();
        object source = new(), content = new();
        int encodes = 0;
        RgbaImage Factory()
        {
            Interlocked.Increment(ref encodes);
            return Image;
        }
        var first = _engine.GetEmbeddingAsync(source, content, Factory);
        var concurrent = _engine.GetEmbeddingAsync(source, content, Factory);
        Assert.Same(first, concurrent);
        var a = await first;
        Assert.Same(a, _engine.TryGetEmbedding(source, content));
        var b = await _engine.GetEmbeddingAsync(source, new object(), Factory); // pixels changed
        Assert.NotSame(a, b);
        Assert.Equal(2, encodes);
        Assert.Null(_engine.TryGetEmbedding(source, content)); // the stale entry is gone
    }

    [Fact]
    public void Subject_of_a_disc_on_a_plain_background_is_the_disc()
    {
        if (!_engine.CanSelectSubject) Assert.Skip(SegmentationModels.FetchHint);
        var mask = _engine.SelectSubject(Image, Canvas);
        double iou = DiscIou(mask);
        Assert.True(iou > 0.9, $"IoU {iou:F3}");
    }
}
