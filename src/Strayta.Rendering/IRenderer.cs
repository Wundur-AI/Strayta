using Strayta.Core;

namespace Strayta.Rendering;

/// <summary>
/// Flattens documents to pixels. Implementations may keep per-document state between calls (caches,
/// GPU resources), so one renderer should be used for one document at a time and not concurrently.
/// </summary>
public interface IRenderer : IDisposable
{
    /// <summary>Short backend name for diagnostics, e.g. "CPU".</summary>
    string Name { get; }

    /// <remarks>The returned image may be owned by the renderer and is valid until the next call.</remarks>
    RenderResult Render(Document document, RenderOptions? options = null);

    /// <summary>Discards cached state. Call after changing layer content; visibility changes are detected automatically.</summary>
    void Invalidate();
}

/// <summary>Chooses a rendering backend.</summary>
public static class Renderers
{
    /// <summary>
    /// The best available backend. Currently always the CPU renderer, which is also the reference
    /// implementation other backends are tested against.
    /// </summary>
    public static IRenderer CreateDefault() => new CpuRenderer();
}

/// <summary>
/// Multi-threaded float-precision renderer. It is exact and deterministic, which makes it the reference
/// for correctness, and it re-renders quickly after visibility changes by resuming from cached snapshots.
/// </summary>
public sealed class CpuRenderer(long cacheBudgetBytes = 1L << 30) : IRenderer
{
    private RenderCache _cache = new(cacheBudgetBytes);
    private Document? _document;

    public string Name => "CPU";

    public RenderResult Render(Document document, RenderOptions? options = null)
    {
        if (!ReferenceEquals(document, _document))
        {
            _document = document;
            _cache = new RenderCache(cacheBudgetBytes);
        }
        return Compositor.Render(document, options, _cache);
    }

    public void Invalidate() => _cache.Clear();

    public void Dispose()
    {
        _cache.Clear();
        _document = null;
    }
}
