using Strayta.Core.Selection;

namespace Strayta.Core.Tests;

public class SelectionFromCoverageTests
{
    [Fact]
    public void Coverage_is_trimmed_to_the_selected_pixels()
    {
        var coverage = new byte[10 * 10];
        coverage[3 * 10 + 4] = 255;
        coverage[5 * 10 + 6] = 100;
        var mask = SelectionMask.FromCoverage(new PixelRect(100, 200, 110, 210), coverage, PixelRect.FromSize(1000, 1000));
        Assert.NotNull(mask);
        Assert.Equal(new PixelRect(104, 203, 107, 206), mask!.Bounds);
        Assert.Equal(255, mask.CoverageAt(104, 203));
        Assert.Equal(100, mask.CoverageAt(106, 205));
        Assert.Equal(0, mask.CoverageAt(105, 204));
    }

    [Fact]
    public void Coverage_is_clipped_to_the_canvas()
    {
        var coverage = Enumerable.Repeat((byte)255, 20 * 20).ToArray();
        var mask = SelectionMask.FromCoverage(new PixelRect(-10, -10, 10, 10), coverage, PixelRect.FromSize(50, 50));
        Assert.NotNull(mask);
        Assert.Equal(new PixelRect(0, 0, 10, 10), mask!.Bounds);
        Assert.True(mask.IsRectangular); // solid coverage needs no mask
    }

    [Fact]
    public void Empty_coverage_is_no_selection()
    {
        Assert.Null(SelectionMask.FromCoverage(new PixelRect(0, 0, 4, 4), new byte[16], PixelRect.FromSize(10, 10)));
        Assert.Null(SelectionMask.FromCoverage(new PixelRect(20, 20, 24, 24), new byte[16], PixelRect.FromSize(10, 10)));
        Assert.Throws<ArgumentException>(() => SelectionMask.FromCoverage(new PixelRect(0, 0, 4, 4), new byte[15], PixelRect.FromSize(10, 10)));
    }
}
