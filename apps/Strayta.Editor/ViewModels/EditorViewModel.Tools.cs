using Strayta.Editor.Controls;

namespace Strayta.Editor.ViewModels;

// The tool strip: Photoshop-style slots, each holding a group of related tools that share a shortcut key.
public sealed partial class EditorViewModel
{
    public IReadOnlyList<ToolGroup> ToolGroups { get; private set; } = [];

    private ToolGroup _marqueeGroup = null!, _selectionGroup = null!;

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
        ToolGroups =
        [
            new ToolGroup(this, "V", new ToolInfo(CanvasTool.Move, "Move", "IconMove")),
            new ToolGroup(this, "H", new ToolInfo(CanvasTool.Hand, "Hand", "IconHand")),
            _marqueeGroup,
            new ToolGroup(this, "L", new ToolInfo(CanvasTool.Lasso, "Lasso", "IconLasso")),
            _selectionGroup,
            new ToolGroup(this, "B", new ToolInfo(CanvasTool.Brush, "Brush", "IconBrush")),
            new ToolGroup(this, "E", new ToolInfo(CanvasTool.Eraser, "Eraser", "IconEraser")),
        ];
    }

    partial void OnToolChanged(CanvasTool value)
    {
        foreach (var group in ToolGroups) group.OnToolChanged(value);
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
