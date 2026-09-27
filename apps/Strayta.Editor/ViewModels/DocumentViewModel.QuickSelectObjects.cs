using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Strayta.Core;
using Strayta.Core.Selection;
using Strayta.Segmentation;

namespace Strayta.Editor.ViewModels;

// Quick Selection's Object-Aware mode: the brushed points become SAM prompts, and the object SAM finds steers the
// geodesic growth as a prior (QuickSelectionStroke.SetPrior) instead of replacing it.
public sealed partial class DocumentViewModel
{
    /// <summary>
    /// The brushed points of the Quick Selection "session" that produced a selection (positive: from strokes that
    /// added, negative: from strokes that subtracted). Brushing again on that selection (Shift or Option, or after
    /// undoing back to it) continues the session. Keyed weakly by the selection, so undo and redo find the right
    /// set and nothing outlives its selection.
    /// </summary>
    private readonly ConditionalWeakTable<SelectionMask, IReadOnlyList<PromptPoint>> _quickPrompts = new();

    /// <summary>At most this many points of each kind go to SAM; it needs a few well-spread points, not hundreds.</summary>
    private const int MaxPrompts = 16;

    private const float CandidateSlack = 0.1f; // SAM score below its best that a larger candidate may have
    private const float MinScore = 0.5f;       // below this SAM is guessing: no prior
    private const double ObjectArea = 0.3, StuffArea = 0.5; // mask area (share of the sampled image) for full / no trust

    /// <summary>The object-aware part of one Quick Selection drag. Touched on the UI thread only.</summary>
    private sealed class ObjectAwareDrag
    {
        public required Task<SamEmbedding?> Embedding { get; init; }
        public required bool Subtract { get; init; }
        /// <summary>Brushed points closer than this to the last kept one are skipped.</summary>
        public required float Spacing { get; init; }
        /// <summary>The session's points before this drag.</summary>
        public required IReadOnlyList<PromptPoint> Session { get; init; }
        /// <summary>This drag's points, thinned.</summary>
        public readonly List<Vector2> Points = [];
        public int Version, DecodedVersion;
        public bool Decoding, Ended, Failed;
        public Task Decode = Task.CompletedTask;
        /// <summary>A prior decoded but not yet handed to the stroke (Prior null: back to plain growth).</summary>
        public (QuickSelectionPrior? Prior, bool Set) Pending;
        public readonly List<double> DecodeMs = [];
        public int Applied;
        public double FirstPriorMs = double.NaN;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
    }

    /// <summary>Object-Aware numbers of the last finished drag, for the benchmark and self-test.</summary>
    public (int Decodes, IReadOnlyList<double> DecodeMs, int Applied, double FirstPriorMs, bool UsedPrior) LastObjectAwareStats { get; private set; }

    /// <summary>
    /// What Object-Aware samples: the same pixels as Quick Selection itself (the selected layer, or the whole image
    /// with Sample All Layers or when a group or adjustment layer is selected). The embedding cache is shared with
    /// Object Selection, keyed by that source.
    /// </summary>
    private bool QuickSelectSamplesAll => Editor.QuickSelectSampleAllLayers || SelectedLayer?.Node is not PixelLayer { Pixels: not null };

    /// <summary>
    /// Starts analyzing the image for Object-Aware Quick Selection in the background (when the tool is picked with
    /// the option on, or the option or sample source changes), so the first stroke is usually object-aware already.
    /// </summary>
    public Task PrepareQuickSelectObjects() =>
        Editor.QuickSelectObjectAware && Engine.CanSelectObjects ? ObjectEmbeddingAsync(quiet: true, sampleAll: QuickSelectSamplesAll) : Task.CompletedTask;

    private ObjectAwareDrag? StartObjectAware(SelectionMode mode, SelectionMask? before, float size)
    {
        if (!Editor.QuickSelectObjectAware || !Engine.CanSelectObjects) return null;
        // Continue the session that made the current selection; a fresh selection starts a fresh one.
        var session = mode != SelectionMode.Replace && before is not null && _quickPrompts.TryGetValue(before, out var s) ? s : [];
        return new ObjectAwareDrag
        {
            Embedding = ObjectEmbeddingAsync(quiet: true, sampleAll: QuickSelectSamplesAll),
            Subtract = mode == SelectionMode.Subtract,
            Spacing = Math.Max(size, Math.Max(Model.Width, Model.Height) / 40f),
            Session = session,
        };
    }

    /// <summary>A pointer position of the drag: kept as a prompt if far enough from the last one, and decoded soon.</summary>
    private void AddObjectAwarePoint(QuickSelectDrag drag, Vector2 p)
    {
        if (drag.Objects is not { Failed: false, Ended: false } o) return;
        if (o.Points.Count > 0 && Vector2.Distance(o.Points[^1], p) < o.Spacing) return;
        o.Points.Add(p);
        o.Version++;
        if (!o.Decoding) o.Decode = DecodeObjectAwareAsync(drag, o);
    }

    /// <summary>
    /// The prompt for this drag: its own points as positives (the thing being painted, whether to add or to take
    /// away) and the session's points of the other kind as negatives (what the earlier opposite strokes were not).
    /// </summary>
    /// <remarks>
    /// Earlier strokes of the same kind are left out on purpose: their region is already in (or out of) the
    /// selection, and they are often on other objects. Measured on a cap and a ball, positives on both made SAM
    /// return three-quarters of each, while the ball's own points with the cap's as negatives found the whole ball.
    /// Opposite points near this drag's are dropped: brushing over an earlier stroke reverses it, and a negative
    /// on top of a positive would only confuse SAM.
    /// </remarks>
    private static SamPrompt ObjectAwarePrompt(ObjectAwareDrag o)
    {
        var points = Spread(o.Points).Select(p => new PromptPoint(p.X, p.Y)).ToList();
        var opposite = o.Session.Where(q => q.Positive == o.Subtract && !Superseded(q, o)).Select(q => new Vector2(q.X, q.Y)).ToList();
        points.AddRange(Spread(opposite).Select(p => new PromptPoint(p.X, p.Y, Positive: false)));
        return new SamPrompt(points);

        // Evenly spaced along the list, always keeping the newest (the drag's latest intent).
        static IEnumerable<Vector2> Spread(List<Vector2> all) => all.Count <= MaxPrompts ? all
            : Enumerable.Range(0, MaxPrompts).Select(i => all[(int)Math.Round((double)i * (all.Count - 1) / (MaxPrompts - 1))]);
    }

    /// <summary>True if a session point lies where this drag brushed (within two point spacings of one of its points).</summary>
    private static bool Superseded(PromptPoint q, ObjectAwareDrag o)
    {
        var at = new Vector2(q.X, q.Y);
        foreach (var p in o.Points)
            if (Vector2.Distance(p, at) < 2 * o.Spacing) return true;
        return false;
    }

    /// <summary>
    /// Decodes the latest prompt whenever it changed, one decode at a time (about 20 ms each, so a fast drag
    /// coalesces into its newest points), and hands each result to the stroke's pump as a prior. Waits for the image
    /// embedding first; until it arrives the drag grows as plain Quick Selection.
    /// </summary>
    private async Task DecodeObjectAwareAsync(QuickSelectDrag drag, ObjectAwareDrag o)
    {
        o.Decoding = true;
        try
        {
            if (await o.Embedding is not { } embedding || await drag.Stroke is not { } stroke) return;
            while (!o.Ended && o.DecodedVersion != o.Version)
            {
                int version = o.Version;
                var prompt = ObjectAwarePrompt(o);
                var clock = Stopwatch.StartNew();
                var prior = await Task.Run(() => ObjectPrior(embedding, prompt, stroke.Image));
                o.DecodeMs.Add(clock.Elapsed.TotalMilliseconds);
                o.DecodedVersion = version;
                o.Pending = (prior, true);
                if (!drag.Pumping) drag.Pump = PumpAsync(drag);
            }
        }
        catch (Exception ex)
        {
            // Plain Quick Selection still works; say why the object-aware part stopped.
            o.Failed = true;
            o.Pending = (null, true);
            if (!drag.Pumping) drag.Pump = PumpAsync(drag);
            Notice = $"Object-Aware Quick Selection failed: {ex.Message}";
        }
        finally
        {
            o.Decoding = false;
        }
    }

    /// <summary>Counts a prior the stroke accepted (it refuses one that disagrees with the brushing).</summary>
    private static void NoteObjectAwarePrior(ObjectAwareDrag o)
    {
        o.Applied++;
        if (double.IsNaN(o.FirstPriorMs)) o.FirstPriorMs = o.Clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// SAM's answer for the prompt, as a prior on the stroke's working grid; null when SAM finds nothing it is sure of.
    /// </summary>
    /// <remarks>
    /// <para>For few points SAM offers a sub-part, a part and a whole object, and its own score often prefers the
    /// sub-part (a highlight on a sphere, the band on a bottle). Painting means "this thing", so of the candidates
    /// that honor the prompt (hold most positive points and no negative one) the largest that SAM rates within
    /// <see cref="CandidateSlack"/> of its best is used; more points settle SAM on the whole object anyway.</para>
    /// <para>The outline's uncertainty is one mask cell (SAM's masks are 256 cells across the sampled image): within
    /// it the image's own edges place the boundary. Trust is full for object-sized masks (up to
    /// <see cref="ObjectArea"/> of the image) and falls to zero at <see cref="StuffArea"/>: large masks are walls,
    /// sky, floors, where easing growth across all of it would fill a surprising area, so there the prior only
    /// keeps the growth from spilling into the objects around it and painting behaves as plain Quick Selection.</para>
    /// </remarks>
    private static QuickSelectionPrior? ObjectPrior(SamEmbedding embedding, SamPrompt prompt, QuickSelectionImage image)
    {
        var candidates = Engine.DecodeCandidates(embedding, prompt);
        var honoring = candidates.Where(Honors).ToList();
        if (honoring.Count == 0 && prompt.Points.Any(p => !p.Positive))
        {
            // SAM could not separate the painted thing from the negatives: the painted thing alone still helps.
            prompt = new SamPrompt(prompt.Points.Where(p => p.Positive).ToList());
            honoring = Engine.DecodeCandidates(embedding, prompt).Where(Honors).ToList();
        }
        if (honoring.Count == 0) return null;
        float best = honoring.Max(c => c.Score);
        var chosen = honoring.Where(c => c.Score >= best - CandidateSlack).MaxBy(Area)!;
        double area = Area(chosen);
        if (area == 0 || chosen.Score < MinScore) return null;
        var placement = chosen.Placement;
        float cell = Math.Max((float)placement.Width / chosen.Width, (float)placement.Height / chosen.Height);
        float trust = (float)Math.Clamp((StuffArea - area) / (StuffArea - ObjectArea), 0, 1);
        return QuickSelectionPrior.Sample(image, p => chosen.SignedDistance(p.X, p.Y), cell, trust);

        bool Honors(MaskLogits m)
        {
            int positives = 0, inside = 0;
            foreach (var p in prompt.Points)
            {
                bool isInside = m.SignedDistance(p.X, p.Y) > 0;
                if (!p.Positive && isInside) return false;
                if (p.Positive) positives++;
                if (p.Positive && isInside) inside++;
            }
            return inside * 2 > positives;
        }

        static double Area(MaskLogits m)
        {
            int n = 0;
            foreach (float v in m.Values)
                if (v > 0) n++;
            return (double)n / m.Values.Length;
        }
    }

    /// <summary>Mouse-up: waits for the prior of the final prompt (if the image is analyzed by now) before refining.</summary>
    private async Task FinishObjectAwareAsync(QuickSelectDrag drag)
    {
        if (drag.Objects is not { } o) return;
        if (o.Embedding.IsCompleted && !o.Failed)
        {
            if (!o.Decoding && o.DecodedVersion != o.Version) o.Decode = DecodeObjectAwareAsync(drag, o);
            await o.Decode;
        }
        o.Ended = true; // an embedding still on its way arrives too late for this drag
    }

    /// <summary>After the drag: its numbers, and the session's points for the next stroke on the new selection.</summary>
    private void RecordObjectAware(QuickSelectDrag drag, QuickSelectionStroke stroke, SelectionMask? result)
    {
        if (drag.Objects is not { } o) return;
        LastObjectAwareStats = (o.DecodeMs.Count, o.DecodeMs, o.Applied, o.FirstPriorMs, stroke.HasPrior);
        if (result is null) return;
        var session = o.Session.Where(q => q.Positive != o.Subtract || !Superseded(q, o)).ToList();
        session.AddRange(o.Points.Select(p => new PromptPoint(p.X, p.Y, Positive: !o.Subtract)));
        _quickPrompts.AddOrUpdate(result, session);
    }
}
