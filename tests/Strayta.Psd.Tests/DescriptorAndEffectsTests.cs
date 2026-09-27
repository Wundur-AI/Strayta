using Strayta.Core;
using Strayta.Psd.Descriptors;

namespace Strayta.Psd.Tests;

public class DescriptorReaderTests
{
    [Fact]
    public void Reads_every_common_value_type()
    {
        var bytes = new DescriptorWriter().Descriptor("null", w => w
            .Bool("enab", true)
            .Long("Cnt ", -7)
            .Double("Rd  ", 12.5)
            .Unit("Opct", "#Prc", 75)
            .Text("Nm  ", "Héllo")
            .Enum("Md  ", "BlnM", "Mltp")
            .Enum("longKeyName", "BlnM", "linearDodge")
            .Object("Clr ", "RGBC", 3, c => c.Double("Rd  ", 1).Double("Grn ", 2).Double("Bl  ", 3))
            .ListOfObjects("list", "Pnt ", (1, p => p.Long("x   ", 1)), (1, p => p.Long("x   ", 2))), count: 9).ToArray();

        var d = new DescriptorReader(bytes).ReadDescriptor();

        Assert.Equal("null", d.ClassId);
        Assert.True(d.Bool("enab"));
        Assert.Equal(-7, d.Number("Cnt "));
        Assert.Equal(12.5, d.Number("Rd  "));
        Assert.Equal(new UnitFloatValue("#Prc", 75), d["Opct"]);
        Assert.Equal("Héllo", d.Text("Nm  "));
        Assert.Equal("Mltp", d.Enum("Md  "));
        Assert.Equal("linearDodge", d.Enum("longKeyName"));
        Assert.Equal(2, d.Object("Clr ")!.Number("Grn "));
        Assert.Equal(2, d.List("list")!.Count);
    }

    [Fact]
    public void Rejects_truncated_and_unknown_data()
    {
        var bytes = new DescriptorWriter().Descriptor("null", w => w.Double("Rd  ", 1), count: 1).ToArray();
        Assert.Throws<PsdFormatException>(() => new DescriptorReader(bytes[..^3]).ReadDescriptor());

        var unknown = new DescriptorWriter().Descriptor("null", w => w.Key("abcd"), count: 1).ToArray()
            .Concat("zzzz"u8.ToArray()).ToArray();
        Assert.Throws<PsdFormatException>(() => new DescriptorReader(unknown).ReadDescriptor());
    }
}

public class EffectParsingTests
{
    private static LayerNode ReadLayer(byte[] lfx2)
    {
        var layer = new PsdTestBuilder.Layer
        {
            Name = "fx", Left = 0, Top = 0, Right = 2, Bottom = 2,
            Channels = { [0] = new byte[4], [1] = new byte[4], [2] = new byte[4] },
            ExtraBlocks = { ("lfx2", lfx2) },
        };
        var doc = PsdFile.Read(new MemoryStream(new PsdTestBuilder(2, 2) { Layers = { layer } }.Build())).ToDocument();
        return Assert.Single(doc.Root.Children);
    }

    [Fact]
    public void Reads_drop_shadow_and_stroke_and_skips_disabled_effects()
    {
        var d = new DescriptorWriter().Descriptor("null", w => w
            .Bool("masterFXSwitch", true)
            .Object("DrSh", "DrSh", 8, s => s
                .Bool("enab", true).Enum("Md  ", "BlnM", "Mltp").Rgb("Clr ", 255, 0, 0)
                .Unit("Opct", "#Prc", 50).Bool("uglg", false).Unit("lagl", "#Ang", 90)
                .Unit("Dstn", "#Pxl", 4).Unit("blur", "#Pxl", 6))
            .Object("FrFX", "FrFX", 5, s => s
                .Bool("enab", true).Enum("Styl", "FStl", "InsF").Enum("PntT", "FrFl", "SClr")
                .Unit("Sz  ", "#Pxl", 3).Rgb("Clr ", 0, 0, 255))
            .Object("SoFi", "SoFi", 2, s => s.Bool("enab", false).Rgb("Clr ", 0, 255, 0)), count: 4).ToArray();

        var fx = ReadLayer(DescriptorWriter.EffectsBlock(d)).Effects!.Items;

        Assert.Equal(2, fx.Count);
        var shadow = Assert.IsType<DropShadowEffect>(fx[0]);
        Assert.Equal((BlendMode.Multiply, 0.5f, 90f, 4f, 6f), (shadow.BlendMode, shadow.Opacity, shadow.Angle, shadow.Distance, shadow.Size));
        Assert.Equal(new RgbColor(1, 0, 0), shadow.Color);
        var stroke = Assert.IsType<StrokeEffect>(fx[1]);
        Assert.Equal((StrokePosition.Inside, 3f), (stroke.Position, stroke.Size));
    }

    [Fact]
    public void Master_switch_off_means_no_effects()
    {
        var d = new DescriptorWriter().Descriptor("null", w => w
            .Bool("masterFXSwitch", false)
            .Object("SoFi", "SoFi", 2, s => s.Bool("enab", true).Rgb("Clr ", 0, 255, 0)), count: 2).ToArray();
        Assert.Null(ReadLayer(DescriptorWriter.EffectsBlock(d)).Effects);
    }

    [Fact]
    public void Unsupported_effects_are_reported()
    {
        var d = new DescriptorWriter().Descriptor("null", w => w
            .Object("ebbl", "ebbl", 1, s => s.Bool("enab", true)), count: 1).ToArray();
        var effect = Assert.Single(ReadLayer(DescriptorWriter.EffectsBlock(d)).Effects!.Items);
        Assert.Equal("Bevel & Emboss", Assert.IsType<UnsupportedEffect>(effect).Name);
    }

    [Fact]
    public void Reads_gradient_overlay_stops()
    {
        var d = new DescriptorWriter().Descriptor("null", w => w
            .Object("GrFl", "GrFl", 4, s => s
                .Bool("enab", true).Unit("Angl", "#Ang", 45).Enum("Type", "GrdT", "Rdl ")
                .Object("Grad", "Grdn", 2, g => g
                    .ListOfObjects("Clrs", "Clrt",
                        (3, c => c.Rgb("Clr ", 0, 0, 0).Long("Lctn", 0).Long("Mdpn", 50)),
                        (3, c => c.Rgb("Clr ", 255, 255, 255).Long("Lctn", 4096).Long("Mdpn", 50)))
                    .ListOfObjects("Trns", "TrnS",
                        (3, t => t.Unit("Opct", "#Prc", 100).Long("Lctn", 0).Long("Mdpn", 50)),
                        (3, t => t.Unit("Opct", "#Prc", 0).Long("Lctn", 4096).Long("Mdpn", 50))))), count: 1).ToArray();

        var overlay = Assert.IsType<GradientOverlayEffect>(Assert.Single(ReadLayer(DescriptorWriter.EffectsBlock(d)).Effects!.Items));

        Assert.Equal(GradientStyle.Radial, overlay.Style);
        var (color, opacity) = overlay.Gradient.Sample(0.5f);
        Assert.Equal(0.5f, color.R, 3);
        Assert.Equal(0.5f, opacity, 3);
    }
}

public class GradientTests
{
    [Fact]
    public void Midpoint_moves_the_halfway_blend()
    {
        var g = new Gradient(
            [new(0, 0.25f, RgbColor.Black), new(1, 0.5f, new RgbColor(1, 1, 1))],
            []);
        Assert.Equal(0.5f, g.Sample(0.25f).Color.R, 3);
        Assert.Equal(0f, g.Sample(-1f).Color.R);
        Assert.Equal(1f, g.Sample(2f).Color.R);
    }
}
