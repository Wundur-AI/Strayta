using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Strayta.Core;
using Strayta.Core.Selection;

namespace Strayta.Segmentation;

/// <summary>
/// SAM 2's image features for one image. Computing them is the slow part (about a second on a laptop CPU); every
/// prompt afterwards only runs the small decoder, so these are cached per image.
/// </summary>
public sealed class SamEmbedding
{
    internal SamEmbedding(RgbaImage image, float[] imageEmbed, float[] highRes0, float[] highRes1, TimeSpan encodeTime)
    {
        Image = image;
        ImageEmbed = imageEmbed;
        HighRes0 = highRes0;
        HighRes1 = highRes1;
        EncodeTime = encodeTime;
    }

    /// <summary>The image the features were computed from (kept for its placement and size).</summary>
    public RgbaImage Image { get; }
    public PixelRect Placement => Image.Placement;
    public TimeSpan EncodeTime { get; }

    /// <summary>
    /// The same features for the same pixels at another position, e.g. after the layer was moved: moving does not
    /// change what the encoder sees, so it need not run again.
    /// </summary>
    public SamEmbedding MovedTo(PixelRect placement) => placement == Placement ? this
        : new SamEmbedding(new RgbaImage(Image.Pixels, Image.Width, Image.Height, placement), ImageEmbed, HighRes0, HighRes1, EncodeTime);

    internal float[] ImageEmbed { get; }
    internal float[] HighRes0 { get; }
    internal float[] HighRes1 { get; }
}

/// <summary>Timing of the last operation, for benchmarks and diagnostics.</summary>
public readonly record struct SegmentationTimings(double PreprocessMs, double InferenceMs, double PostprocessMs)
{
    public double TotalMs => PreprocessMs + InferenceMs + PostprocessMs;
}

/// <summary>
/// Runs the segmentation models locally with ONNX Runtime: SAM 2.1 for prompted object selection and BiRefNet
/// for Select Subject. Models load lazily on first use, off the caller's thread; everything here may be called
/// from any thread.
/// </summary>
public sealed class SegmentationEngine : IDisposable
{
    public const int SamSize = 1024;
    private const int SamMaskSize = 256;
    public const int SubjectSize = 512;

    private readonly SegmentationModels _models;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly ReaderWriterLockSlim _life = new();
    private bool _disposed;
    private InferenceSession? _encoder, _decoder, _subject;

    // Default run options; ONNX Runtime only reads them, so one instance serves concurrent runs.
    private static readonly RunOptions Run = new();

    // A few recent embeddings keyed by (source, content) identity: switching between two layers or documents does
    // not re-encode. Each costs 16 MB of features plus a reference to its source image.
    private readonly object _cacheLock = new();
    private readonly LinkedList<(object Source, object Content, Task<SamEmbedding> Task)> _cache = new();
    private const int CacheCapacity = 3;

    public SegmentationEngine(SegmentationModels models) => _models = models;

    private static readonly Lazy<SegmentationEngine> SharedEngine = new(() => new SegmentationEngine(SegmentationModels.Locate()));

    /// <summary>One engine for the whole app: the sessions hold a few hundred MB, so documents share them.</summary>
    public static SegmentationEngine Shared => SharedEngine.Value;

    public SegmentationModels Models => _models;
    public bool CanSelectObjects => _models.HasObjectModel;
    public bool CanSelectSubject => _models.HasSubjectModel;

    /// <summary>Timings of the last encode, decode or subject call.</summary>
    public SegmentationTimings LastTimings { get; private set; }

    // ---- Object selection (SAM 2.1) ---------------------------------------------------------------------

    /// <summary>
    /// The cached embedding for <paramref name="source"/> if it was computed from <paramref name="content"/>
    /// (compared by reference: pass the layer's pixel raster, or the render buffer, so any edit invalidates it).
    /// </summary>
    public SamEmbedding? TryGetEmbedding(object source, object content)
    {
        lock (_cacheLock)
        {
            foreach (var e in _cache)
                if (ReferenceEquals(e.Source, source) && ReferenceEquals(e.Content, content) && e.Task.IsCompletedSuccessfully)
                    return e.Task.Result;
        }
        return null;
    }

    /// <summary>
    /// Returns the embedding for <paramref name="source"/>/<paramref name="content"/>, computing it in the
    /// background from <paramref name="image"/> if needed. Concurrent requests for the same image share one run.
    /// </summary>
    public Task<SamEmbedding> GetEmbeddingAsync(object source, object content, Func<RgbaImage> image)
    {
        lock (_cacheLock)
        {
            for (var node = _cache.First; node is not null; node = node.Next)
            {
                var e = node.Value;
                if (!ReferenceEquals(e.Source, source)) continue;
                if (ReferenceEquals(e.Content, content) && !e.Task.IsFaulted && !e.Task.IsCanceled)
                {
                    _cache.Remove(node);
                    _cache.AddFirst(node);
                    return e.Task;
                }
                _cache.Remove(node); // same source, stale content: never needed again
                break;
            }
            var task = Task.Run(() => Encode(image()));
            _cache.AddFirst((source, content, task));
            while (_cache.Count > CacheCapacity) _cache.RemoveLast();
            return task;
        }
    }

    /// <summary>Runs the SAM 2.1 image encoder. Slow (≈1 s); prefer <see cref="GetEmbeddingAsync"/>.</summary>
    public SamEmbedding Encode(RgbaImage image) => Use(() =>
    {
        var (encoder, _) = LoadSam();
        var sw = Stopwatch.StartNew();
        var tensor = ImagePreprocessor.ToTensor(image, SamSize, ImagePreprocessor.ImageNetMean, ImagePreprocessor.ImageNetStd);
        double pre = sw.Elapsed.TotalMilliseconds;
        using var input = OrtValue.CreateTensorValueFromMemory(tensor, [1, 3, SamSize, SamSize]);
        using var outputs = encoder.Run(Run, ["image"], [input], ["image_embed", "high_res_feats_0", "high_res_feats_1"]);
        var embed = outputs[0].GetTensorDataAsSpan<float>().ToArray();
        var hr0 = outputs[1].GetTensorDataAsSpan<float>().ToArray();
        var hr1 = outputs[2].GetTensorDataAsSpan<float>().ToArray();
        double total = sw.Elapsed.TotalMilliseconds;
        LastTimings = new SegmentationTimings(pre, total - pre, 0);
        return new SamEmbedding(image, embed, hr0, hr1, sw.Elapsed);
    });

    /// <summary>
    /// Runs the prompt decoder (a few milliseconds) and returns the best of SAM's three candidate masks as
    /// low-resolution logits over the embedding's placement.
    /// </summary>
    public MaskLogits Decode(SamEmbedding embedding, SamPrompt prompt) => Use(() =>
    {
        if (prompt.IsEmpty) throw new ArgumentException("The prompt has no points and no box.", nameof(prompt));
        var (_, decoder) = LoadSam();
        var sw = Stopwatch.StartNew();
        var (coords, labels) = prompt.ToModel(embedding.Placement, SamSize);
        int n = labels.Length;
        using var embed = OrtValue.CreateTensorValueFromMemory(embedding.ImageEmbed, [1, 256, 64, 64]);
        using var hr0 = OrtValue.CreateTensorValueFromMemory(embedding.HighRes0, [1, 32, 256, 256]);
        using var hr1 = OrtValue.CreateTensorValueFromMemory(embedding.HighRes1, [1, 64, 128, 128]);
        using var pc = OrtValue.CreateTensorValueFromMemory(coords, [1, n, 2]);
        using var pl = OrtValue.CreateTensorValueFromMemory(labels, [1, n]);
        using var maskIn = OrtValue.CreateTensorValueFromMemory(new float[SamMaskSize * SamMaskSize], [1, 1, SamMaskSize, SamMaskSize]);
        using var hasMask = OrtValue.CreateTensorValueFromMemory(new float[1], [1]);
        using var outputs = decoder.Run(Run,
            ["image_embed", "high_res_feats_0", "high_res_feats_1", "point_coords", "point_labels", "mask_input", "has_mask_input"],
            [embed, hr0, hr1, pc, pl, maskIn, hasMask],
            ["masks", "iou_predictions"]);
        double inference = sw.Elapsed.TotalMilliseconds;

        var shape = outputs[0].GetTensorTypeAndShape().Shape; // [1, candidates, h, w]
        int candidates = (int)shape[1], mh = (int)shape[2], mw = (int)shape[3];
        var scores = outputs[1].GetTensorDataAsSpan<float>();
        int best = 0;
        for (int i = 1; i < candidates; i++)
            if (scores[i] > scores[best]) best = i;
        var values = outputs[0].GetTensorDataAsSpan<float>().Slice(best * mh * mw, mh * mw).ToArray();
        // Object Selection picks one object: drop specks beside it and fill pinholes in it.
        MaskCleanup.Clean(values, mw, mh, minIslandFraction: 0.1f, maxHoleFraction: 0.02f);
        LastTimings = new SegmentationTimings(0, inference, sw.Elapsed.TotalMilliseconds - inference);
        return new MaskLogits(values, mw, mh, embedding.Placement, scores[best]);
    });

    /// <summary>
    /// Decodes a prompt the way Object Selection means it: a single click selects the whole object under it.
    /// </summary>
    /// <remarks>
    /// For one point SAM offers a sub-part, a part and a whole, and its own scores often favor the smallest (a
    /// highlight band on a bottle, a letter on a label). Photoshop's click selects the object, so the click is
    /// decoded again with the first answer's bounding box, grown by a quarter, as a box prompt: the box tells SAM
    /// roughly how big the thing is, which usually brings back the whole object (a part that fills most of its own
    /// box, like a word on a label, stays a part). The second answer wins unless SAM is much less sure of it.
    /// </remarks>
    public MaskLogits DecodeObject(SamEmbedding embedding, SamPrompt prompt)
    {
        var first = Decode(embedding, prompt);
        if (prompt is not { Box: null, Points: [{ Positive: true } point] }) return first;
        var region = MaskUpscaler.PositiveRegion(first);
        if (region.IsEmpty) return first;
        float gx = region.Width * 0.125f, gy = region.Height * 0.125f;
        var p = embedding.Placement;
        var box = (Math.Max(p.Left, region.Left - gx), Math.Max(p.Top, region.Top - gy), Math.Min(p.Right, region.Right + gx), Math.Min(p.Bottom, region.Bottom + gy));
        var second = Decode(embedding, new SamPrompt([point], box));
        return second.Score >= first.Score - 0.15f ? second : first;
    }

    /// <summary>
    /// Decodes <paramref name="prompt"/> and upscales the mask to a document selection, with its edge snapped to
    /// the image's edges unless <paramref name="refineEdges"/> is false.
    /// </summary>
    public SelectionMask? SelectObject(SamEmbedding embedding, SamPrompt prompt, PixelRect canvas, bool refineEdges = true)
    {
        var sw = Stopwatch.StartNew();
        var logits = DecodeObject(embedding, prompt);
        var decode = LastTimings with { InferenceMs = sw.Elapsed.TotalMilliseconds };
        double before = sw.Elapsed.TotalMilliseconds;
        var mask = MaskUpscaler.ToSelection(logits, canvas, guide: refineEdges ? embedding.Image : null);
        LastTimings = decode with { PostprocessMs = decode.PostprocessMs + sw.Elapsed.TotalMilliseconds - before };
        return mask;
    }

    // ---- Select Subject (BiRefNet) ------------------------------------------------------------------------

    /// <summary>Low-resolution subject logits for <paramref name="image"/> (≈1.5 s on a laptop CPU).</summary>
    public MaskLogits SegmentSubject(RgbaImage image) => Use(() =>
    {
        var session = LoadSubject();
        var sw = Stopwatch.StartNew();
        var tensor = ImagePreprocessor.ToTensor(image, SubjectSize, ImagePreprocessor.ImageNetMean, ImagePreprocessor.ImageNetStd);
        double pre = sw.Elapsed.TotalMilliseconds;
        using var input = OrtValue.CreateTensorValueFromMemory(tensor, [1, 3, SubjectSize, SubjectSize]);
        string inputName = session.InputNames[0], outputName = session.OutputNames[0];
        using var outputs = session.Run(Run, [inputName], [input], [outputName]);
        var shape = outputs[0].GetTensorTypeAndShape().Shape;
        int mh = (int)shape[^2], mw = (int)shape[^1];
        var values = outputs[0].GetTensorDataAsSpan<float>()[..(mh * mw)].ToArray();
        // A subject may be several separate things (a product line-up), so only true specks are removed.
        MaskCleanup.Clean(values, mw, mh, minIslandFraction: 0.01f, maxHoleFraction: 0.005f);
        double inference = sw.Elapsed.TotalMilliseconds - pre;
        LastTimings = new SegmentationTimings(pre, inference, 0);
        return new MaskLogits(values, mw, mh, image.Placement);
    });

    /// <summary>Select › Subject: the main subject of <paramref name="image"/> as a document selection.</summary>
    public SelectionMask? SelectSubject(RgbaImage image, PixelRect canvas, bool refineEdges = true)
    {
        var logits = SegmentSubject(image);
        var t = LastTimings;
        var sw = Stopwatch.StartNew();
        var mask = MaskUpscaler.ToSelection(logits, canvas, guide: refineEdges ? image : null);
        LastTimings = t with { PostprocessMs = sw.Elapsed.TotalMilliseconds };
        return mask;
    }

    // ---- Loading ------------------------------------------------------------------------------------------

    /// <summary>Loads the SAM sessions now (e.g. when the tool is picked), so the first prompt does not wait for it.</summary>
    public Task PreloadObjectModelAsync() => CanSelectObjects ? Task.Run(LoadSam) : Task.CompletedTask;

    private (InferenceSession Encoder, InferenceSession Decoder) LoadSam()
    {
        if (_encoder is { } e && _decoder is { } d) return (e, d);
        if (!_models.HasObjectModel) throw new FileNotFoundException(SegmentationModels.FetchHint);
        _loadLock.Wait();
        try
        {
            _encoder ??= CreateSession(_models.SamEncoderPath!);
            _decoder ??= CreateSession(_models.SamDecoderPath!);
            return (_encoder, _decoder);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private InferenceSession LoadSubject()
    {
        if (_subject is { } s) return s;
        if (!_models.HasSubjectModel) throw new FileNotFoundException(SegmentationModels.FetchHint);
        _loadLock.Wait();
        try
        {
            return _subject ??= CreateSession(_models.SubjectPath!);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private static InferenceSession CreateSession(string path)
    {
        // CPU only: it runs everywhere, and the Core ML provider spends close to a minute compiling the encoder on
        // first load, which is worse than the second it saves per image.
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // The exported graphs trigger a harmless shape-merge warning on load; keep stderr clean.
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        return new InferenceSession(path, options);
    }

    /// <summary>Runs <paramref name="work"/> with the sessions guaranteed alive: <see cref="Dispose"/> waits for it.</summary>
    private T Use<T>(Func<T> work)
    {
        _life.EnterReadLock();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return work();
        }
        finally
        {
            _life.ExitReadLock();
        }
    }

    /// <summary>Releases the shared engine's sessions, if it was ever used (at app exit).</summary>
    public static void DisposeShared()
    {
        if (SharedEngine.IsValueCreated) SharedEngine.Value.Dispose();
    }

    /// <summary>Waits for running inferences, then frees the sessions. Later calls throw <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        _life.EnterWriteLock();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _encoder?.Dispose();
            _decoder?.Dispose();
            _subject?.Dispose();
        }
        finally
        {
            _life.ExitWriteLock();
        }
    }
}
