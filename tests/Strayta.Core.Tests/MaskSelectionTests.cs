using Strayta.Core.Painting;
using Strayta.Core.Selection;

namespace Strayta.Core.Tests;

/// <summary>Where the Selections and Masks features meet: painting into a mask honors the selection.</summary>
public class MaskSelectionTests
{
    [Fact]
    public void Mask_strokes_are_confined_to_the_selection()
    {
        var canvas = new PixelRect(0, 0, 40, 40);
        var selection = SelectionMask.Rectangle(new PixelRect(0, 0, 20, 40), canvas);
        var stroke = PaintStroke.ForMask(new PixelLayer(), new BrushSettings(10, 1f, 1f), 0f, canvas, selection);

        stroke.StrokeTo(5, 20);
        stroke.StrokeTo(35, 20);

        Assert.True(stroke.TargetsMask);
        Assert.Equal(1f, stroke.CoverageAt(10, 20));   // inside the selection
        Assert.Equal(0f, stroke.CoverageAt(30, 20));   // outside it
        Assert.True(stroke.Bounds.Right <= 20);
    }
}
