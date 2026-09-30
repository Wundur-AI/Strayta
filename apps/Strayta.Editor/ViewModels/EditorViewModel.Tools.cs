using Strayta.Editor.Controls;

namespace Strayta.Editor.ViewModels;

// The tool strip: Photoshop-style slots, each holding a group of related tools that share a shortcut key.
public sealed partial class EditorViewModel
{
    public IReadOnlyList<ToolGroup> ToolGroups { get; private set; } = [];

    private ToolGroup _marqueeGroup = null!, _selectionGroup = null!, _fillGroup = null!, _healingGroup = null!;

    private void CreateToolGroups()
    {
        _marqueeGroup = new ToolGroup(this, "M",
            new(CanvasTool.RectSelect, "Rectangular Marquee", "IconMarqueeRect"),
            new(CanvasTool.EllipseSelect, "Elliptical Marquee", "IconMarqueeEllipse"));
        // Photoshop's order; the slot starts on the Magic Wand, the tool of the group used most.
        _selectionGroup = new ToolGroup(this, "W",
            new(CanvasTool.ObjectSelect, "Object Selection", "IconObjectSelect"),
            new(CanvasTool.QuickSelect, "Quick Selection", "IconQuickSelect"),
            new(CanvasTool.MagicWand, "Magic Wand", "IconMagicWand"));
        _selectionGroup.Current = _selectionGroup.Tools[2];
        // Photoshop's G slot: Gradient first, Paint Bucket behind it.
        _fillGroup = new ToolGroup(this, "G",
            new(CanvasTool.Gradient, "Gradient", "IconGradient"),
            new(CanvasTool.PaintBucket, "Paint Bucket", "IconBucket"));
        // Photoshop's J slot: Spot Healing first, Healing Brush behind it (EditorViewModel.Retouch.cs).
        _healingGroup = new ToolGroup(this, "J",
            new(CanvasTool.SpotHealing, "Spot Healing Brush", "IconSpotHealing"),
            new(CanvasTool.Healing, "Healing Brush", "IconHealing"));
        var (pen, pathSelect, shape) = CreatePathToolGroups(); // EditorViewModel.Shapes.cs
        var (focus, toning) = CreateToningToolGroups(); // EditorViewModel.Toning.cs
        // Photoshop's tool-strip order.
        ToolGroups =
        [
            new ToolGroup(this, "V", new ToolInfo(CanvasTool.Move, "Move", "IconMove"),
                new ToolInfo(CanvasTool.Artboard, "Artboard", "IconArtboard")), // EditorViewModel.Artboards.cs
            _marqueeGroup,
            new ToolGroup(this, "L", new ToolInfo(CanvasTool.Lasso, "Lasso", "IconLasso")),
            _selectionGroup,
            new ToolGroup(this, "C", new ToolInfo(CanvasTool.Crop, "Crop", "IconCrop"),
                new ToolInfo(CanvasTool.PerspectiveCrop, "Perspective Crop", "IconPerspectiveCrop")),
            new ToolGroup(this, "I", new ToolInfo(CanvasTool.Eyedropper, "Eyedropper", "IconEyedropper")),
            _healingGroup,
            new ToolGroup(this, "B", new ToolInfo(CanvasTool.Brush, "Brush", "IconBrush")),
            new ToolGroup(this, "S", new ToolInfo(CanvasTool.CloneStamp, "Clone Stamp", "IconCloneStamp")),
            new ToolGroup(this, "Y", new ToolInfo(CanvasTool.HistoryBrush, "History Brush", "IconHistoryBrush")), // EditorViewModel.History.cs
            new ToolGroup(this, "E", new ToolInfo(CanvasTool.Eraser, "Eraser", "IconEraser")),
            _fillGroup,
            focus,
            toning,
            // Photoshop has Pen and Type here, then Path Selection and Shape (EditorViewModel.Type.cs, EditorViewModel.Shapes.cs).
            pen,
            new ToolGroup(this, "T", new ToolInfo(CanvasTool.Type, "Horizontal Type", "IconType")),
            pathSelect,
            shape,
            new ToolGroup(this, "H", new ToolInfo(CanvasTool.Hand, "Hand", "IconHand")),
            new ToolGroup(this, "Z", new ToolInfo(CanvasTool.Zoom, "Zoom", "IconZoom")),
        ];
    }

    partial void OnToolChanged(CanvasTool value)
    {
        foreach (var group in ToolGroups) group.OnToolChanged(value);
        NotifyEverydayTools(); // EditorViewModel.Everyday.cs
        NotifyRetouchTools(); // EditorViewModel.Retouch.cs
        SyncCropTool(); // EditorViewModel.Crop.cs
        SyncTypeTool(); // EditorViewModel.Type.cs
        SyncPathTools(); // EditorViewModel.Shapes.cs
        NotifyArtboardOptions(); // EditorViewModel.Artboards.cs
        NotifyToningTools(); // EditorViewModel.Toning.cs
    }

    /// <summary>A tool-group shortcut: the key alone picks the group's current tool, with Shift it steps through the group.</summary>
    public bool HandleToolKey(string key, bool shift)
    {
        if (ToolGroups.FirstOrDefault(g => g.Key == key) is not { } group) return false;
        if (shift) group.Cycle();
        else group.Activate();
        return true;
    }
}
