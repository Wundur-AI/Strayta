using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.ViewModels;

// The Perspective Crop tool (C group): a quadrilateral drawn over the image becomes an upright canvas, as one
// CanvasEdit. Like the Crop tool, the shape is an overlay; nothing changes until it is committed.
public sealed partial class DocumentViewModel
{
    /// <summary>The open perspective crop (the Perspective Crop tool is active on this document), or null.</summary>
    [ObservableProperty] public partial PerspectiveCropBox? PerspectiveCrop { get; private set; }

    /// <summary>Opens or closes the perspective crop to match the editor's tool.</summary>
    public void SyncPerspectiveCropSession()
    {
        bool wanted = Editor.Tool == CanvasTool.PerspectiveCrop;
        if (wanted && PerspectiveCrop is null)
        {
            if (IsTransforming) _ = CommitTransformAsync();
            var box = new PerspectiveCropBox();
            box.Changed += OnPerspectiveCropChanged;
            PerspectiveCrop = box;
            OnPerspectiveCropChanged();
        }
        else if (!wanted && PerspectiveCrop is { Locked: false } open)
        {
            open.Changed -= OnPerspectiveCropChanged;
            PerspectiveCrop = null;
            PerspectiveCropNotice = "";
        }
    }

    /// <summary>What committing will also do (live layers are rasterized: their data cannot be put in perspective).</summary>
    [ObservableProperty] public partial string PerspectiveCropNotice { get; private set; } = "";

    private void OnPerspectiveCropChanged()
    {
        int live = PerspectiveCrop is { HasShape: true }
            ? Model.Root.Descendants().Count(n => n is Core.PixelLayer { Pixels: not null } && CanvasOperations.IsLive(n))
            : 0;
        PerspectiveCropNotice = live == 0 ? ""
            : $"Perspective Crop rasterizes {live} type, smart object or shape layer{(live == 1 ? "" : "s")}.";
    }

    /// <summary>Esc: removes the shape.</summary>
    public void CancelPerspectiveCrop() => PerspectiveCrop?.Clear();

    /// <summary>Enter or ✓: straightens the shape onto a new canvas as one undoable step.</summary>
    public async Task CommitPerspectiveCropAsync()
    {
        if (PerspectiveCrop is not { Locked: false, HasShape: true } box) return;
        box.Locked = true;
        var quad = box.Corners.ToArray();
        var (width, height) = box.ResultSize;
        var fill = BackgroundFill();
        var doc = Model;
        bool done = await ChangeCanvasAsync("Perspective Crop", () => CanvasOperations.PerspectiveCrop(doc, quad, width, height, fill));
        box.Locked = false;
        if (done) box.Clear();
    }
}
