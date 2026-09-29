using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Editor.Editing;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.ViewModels;

/// <summary>
/// Properties for a pixel, type, shape or smart-object layer or a group, as in Photoshop: the Transform section's
/// W / H / X / Y in pixels (the visible content's bounds) with a link that keeps proportions, and the layer mask
/// section when the layer has one.
/// </summary>
public sealed partial class LayerPanel : PropertiesPanel
{
    public LayerPanel(DocumentViewModel document, LayerNode node) : base(document, node)
    {
        Mask = node.GetMask() is not null ? new MaskPanel(document, node) : null;
        Shape = node is PixelLayer p && ShapeLayers.IsShape(p) ? new ShapePanel(document, p) : null; // ShapePanel.cs
    }

    /// <summary>The Live Shape section of a shape layer, or null.</summary>
    public ShapePanel? Shape { get; }

    public override string Title => Node switch
    {
        LayerGroup => "Group",
        { } n when n.Tags.Contains("text") => "Type Layer",
        { } n when n.Tags.Contains("smart-object") => "Smart Object",
        { } n when n.Tags.Contains("shape") || n.Tags.Contains("fill") => "Shape Layer",
        _ => "Pixel Layer",
    };

    public override string Icon => "IconInfo";

    /// <summary>The layer mask section, or null when the layer has no mask.</summary>
    [ObservableProperty] public partial MaskPanel? Mask { get; private set; }

    /// <summary>A type layer shows its font, size, color and alignment too, as in Photoshop.</summary>
    public bool IsType => Node is PixelLayer && Node.Tags.Contains("text");

    /// <summary>The type settings (they act on the selected type layer, or on the characters being typed).</summary>
    public TypeOptions Type => Document.Editor.Type;

    /// <summary>W and H change together, keeping the current proportions (the chain between them).</summary>
    [ObservableProperty] public partial bool LinkSize { get; set; } = true;

    private IEnumerable<PixelLayer> Targets => Node switch
    {
        LayerGroup g => g.Descendants().OfType<PixelLayer>(),
        PixelLayer p => [p],
        _ => [],
    };

    /// <summary>The visible content's bounds (what Free Transform's box starts from).</summary>
    private PixelRect Bounds
    {
        get
        {
            var boxes = Targets.Select(Resampler.ContentBounds).Where(b => !b.IsEmpty).ToList();
            return boxes.Count == 0 ? PixelRect.Empty
                : boxes.Aggregate((a, b) => new PixelRect(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom)));
        }
    }

    public bool HasContent => !Bounds.IsEmpty;

    public double Width { get => Bounds.Width; set => _ = ResizeAsync(value, LinkSize ? value * Bounds.Height / Math.Max(1, Bounds.Width) : Bounds.Height); }

    public double Height { get => Bounds.Height; set => _ = ResizeAsync(LinkSize ? value * Bounds.Width / Math.Max(1, Bounds.Height) : Bounds.Width, value); }

    public double X { get => Bounds.Left; set => _ = MoveToAsync((int)Math.Round(value), Bounds.Top); }

    public double Y { get => Bounds.Top; set => _ = MoveToAsync(Bounds.Left, (int)Math.Round(value)); }

    /// <summary>Scales the content to <paramref name="width"/> × <paramref name="height"/> pixels about its top-left corner, as one undo step.</summary>
    public async Task ResizeAsync(double width, double height)
    {
        var box = Bounds;
        width = Math.Max(1, Math.Round(width));
        height = Math.Max(1, Math.Round(height));
        if (box.IsEmpty || (width == box.Width && height == box.Height)) return;
        await TransformAsync(t =>
        {
            t.WidthPercent = width / box.Width * 100;
            t.HeightPercent = height / box.Height * 100;
            t.X = box.Left + width / 2;
            t.Y = box.Top + height / 2;
        });
    }

    /// <summary>Moves the content so its top-left is at (<paramref name="left"/>, <paramref name="top"/>), as one undo step.</summary>
    public async Task MoveToAsync(int left, int top)
    {
        var box = Bounds;
        if (box.IsEmpty || (left == box.Left && top == box.Top)) return;
        int dx = left - box.Left, dy = top - box.Top;
        // Plain pixels move exactly; type, smart objects and shapes go through Free Transform so their data follows.
        if (Targets.Any(LiveContent.IsLive))
            await TransformAsync(t => (t.X, t.Y) = (t.Center.X + dx, t.Center.Y + dy));
        else
        {
            Document.SelectedLayer = Document.Layers.SelectMany(l => l.SelfAndDescendants()).FirstOrDefault(i => i.Node == Node) ?? Document.SelectedLayer;
            Document.MoveSelected(dx, dy);
        }
    }

    private async Task TransformAsync(Action<FreeTransform> set)
    {
        if (Document.IsTransforming) return; // an open Free Transform owns the layer until it is applied
        Document.SelectedLayer = Document.Layers.SelectMany(l => l.SelfAndDescendants()).FirstOrDefault(i => i.Node == Node) ?? Document.SelectedLayer;
        if (!Document.BeginFreeTransform() || Document.FreeTransform is not { } transform) return;
        set(transform);
        await Document.CommitTransformAsync();
    }

    protected override void Reload()
    {
        bool hasMask = Node?.GetMask() is not null;
        if (hasMask != Mask is not null)
        {
            Mask?.Dispose();
            Mask = hasMask ? new MaskPanel(Document, Node!) : null;
        }
        base.Reload();
    }

    public override void Dispose()
    {
        Mask?.Dispose();
        Shape?.Dispose();
        base.Dispose();
    }
}
