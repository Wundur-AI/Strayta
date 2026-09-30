using Strayta.Core;
using Strayta.Core.Text;
using Strayta.Psd.Descriptors;
using Strayta.Psd.Text;

namespace Strayta.Psd.Tests;

/// <summary>Warp Text: the warp in a type layer's 'TySh' block.</summary>
public class TypeWarpTests
{
    private static PsdLayerRecord TypeRecord()
    {
        var data = TextLayerData.CreatePoint("Warped", new TextStyle { FontPostScriptName = "ArialMT", FontSize = 40 }, 20, 100);
        return PsdTypeLayer.Create(data, new TextBounds(new TextRect(0, -36, 120, 9), new TextRect(1, -30, 118, 8)));
    }

    [Fact]
    public void A_new_type_layer_has_no_warp_and_its_bounds_are_readable()
    {
        var record = TypeRecord();
        Assert.Null(PsdTypeWarp.Read(record));
        Assert.Equal(new TextRect(0, -36, 120, 9), PsdTypeWarp.TextBounds(record));
    }

    [Fact]
    public void Setting_a_warp_keeps_the_text_and_reads_back()
    {
        var record = TypeRecord();
        var warped = PsdTypeWarp.WithWarp(record, new WarpSpec { Style = "warpArc", Value = 35, Perspective = -10, PerspectiveOther = 5, Vertical = true });
        var spec = PsdTypeWarp.Read(warped)!;
        Assert.Equal("warpArc", spec.Style);
        Assert.Equal(35, spec.Value);
        Assert.Equal(-10, spec.Perspective);
        Assert.Equal(5, spec.PerspectiveOther);
        Assert.True(spec.Vertical);
        Assert.Equal((0.0, -36.0, 120.0, 9.0), spec.Bounds);
        var data = PsdTypeLayer.Read(warped)!;
        Assert.Equal("Warped", data.Text);
        Assert.Equal("warpArc", data.Warp);
        var block = warped.FindBlock("TySh")!.Data!;
        Assert.Equal(0, block.Length % 4);
        // The text descriptor is untouched: everything before the warp is byte for byte the same.
        var before = record.FindBlock("TySh")!.Data!;
        var text = new DescriptorReader(before, 56);
        text.ReadDescriptor();
        Assert.Equal(before.AsSpan(0, text.Position).ToArray(), block.AsSpan(0, text.Position).ToArray());
        Assert.True(PsdTypeLayer.IsRegenerated(block));

        var cleared = PsdTypeWarp.WithWarp(warped, new WarpSpec());
        Assert.Null(PsdTypeWarp.Read(cleared));
        Assert.Null(PsdTypeLayer.Read(cleared)!.Warp);
    }
}
