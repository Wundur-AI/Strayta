namespace Strayta.Rendering;

/// <summary>
/// Lets repeated renders of the same document skip work that has not changed.
/// The compositor splits the root of the layer stack into steps and snapshots the canvas before some
/// of them; a re-render restarts from the latest snapshot below the first step that changed.
/// Only visibility changes are detected. Call <see cref="Clear"/> after editing layer content.
/// A cache must not be shared by concurrent renders.
/// </summary>
public sealed class RenderCache(long memoryBudgetBytes = 1L << 30)
{
    public long MemoryBudgetBytes { get; } = memoryBudgetBytes;

    internal List<(object Node, int Signature)> Steps { get; set; } = [];
    internal SortedDictionary<int, RenderBuffer> Checkpoints { get; } = [];
    internal RenderBuffer? LastResult { get; set; }

    private readonly Stack<RenderBuffer> _spare = new();

    /// <summary>A buffer for a new snapshot, reusing a discarded one of the same size when possible.</summary>
    internal RenderBuffer Rent(Core.PixelRect bounds)
    {
        while (_spare.TryPop(out var b))
            if (b.Bounds == bounds) return b;
        return new RenderBuffer(bounds);
    }

    internal void Recycle(RenderBuffer buffer)
    {
        if (_spare.Count < 2) _spare.Push(buffer);
    }

    public void Clear()
    {
        Steps = [];
        Checkpoints.Clear();
        _spare.Clear();
        LastResult = null;
    }
}
