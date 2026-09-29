using System.Buffers.Binary;
using System.Text;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Psd.Descriptors;

namespace Strayta.Psd;

/// <summary>
/// The adjustment blocks added after Photoshop 6: Exposure, Vibrance, Color Balance, Black &amp; White, Photo Filter,
/// Channel Mixer, Selective Color, Gradient Map and Color Lookup. Layouts follow Adobe's file format specification;
/// where it is silent (descriptor keys, the Gradient Map's per-stop fields and its version 3 method) they follow
/// Photoshop's key names as used elsewhere in its descriptors, and each reader tolerates the variants noted.
/// </summary>
public static partial class PsdAdjustments
{
    private static Adjustment? ParseExtended(string key, byte[] d) => key switch
    {
        "expA" => ParseExposure(d),
        "vibA" => ParseVibrance(d),
        "blnc" => ParseColorBalance(d),
        "blwh" => ParseBlackWhite(d),
        "phfl" => ParsePhotoFilter(d),
        "mixr" => ParseChannelMixer(d),
        "selc" => ParseSelectiveColor(d),
        "grdm" => ParseGradientMap(d),
        "clrL" => ParseColorLookup(d),
        _ => null,
    };

    private static float F32(byte[] d, int offset) => BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(offset));
    private static int I32(byte[] d, int offset) => BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(offset));
    private static ushort U16(byte[] d, int offset) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(offset));

    /// <summary>Version 1, then exposure, offset and gamma as big-endian 32-bit floats.</summary>
    private static ExposureAdjustment ParseExposure(byte[] d)
    {
        float exposure = F32(d, 2), offset = F32(d, 6), gamma = F32(d, 10);
        if (!float.IsFinite(exposure)) exposure = 0;
        if (!float.IsFinite(offset)) offset = 0;
        if (!float.IsFinite(gamma) || gamma <= 0) gamma = 1;
        return new ExposureAdjustment(exposure, offset, gamma);
    }

    /// <summary>A versioned descriptor with 'vibrance' and 'Strt' (saturation); absent keys are zero.</summary>
    private static VibranceAdjustment ParseVibrance(byte[] d)
    {
        var desc = DescriptorReader.ReadVersioned(d);
        return new VibranceAdjustment((int)(desc.Number("vibrance") ?? 0), (int)(desc.Number("Strt") ?? 0));
    }

    /// <summary>Shadows, midtones and highlights as three shorts each (cyan–red, magenta–green, yellow–blue), then the preserve luminosity flag.</summary>
    private static ColorBalanceAdjustment ParseColorBalance(byte[] d)
    {
        ColorBalanceTone Tone(int o) => new(I16(d, o), I16(d, o + 2), I16(d, o + 4));
        bool preserve = d.Length > 18 && d[18] != 0;
        return new ColorBalanceAdjustment(Tone(0), Tone(6), Tone(12), preserve);
    }

    /// <summary>A versioned descriptor: the six weights ('Rd  ', 'Yllw', 'Grn ', 'Cyn ', 'Bl  ', 'Mgnt'), 'useTint' and 'tintColor'.</summary>
    private static BlackWhiteAdjustment ParseBlackWhite(byte[] d)
    {
        var desc = DescriptorReader.ReadVersioned(d);
        var def = BlackWhiteAdjustment.Default;
        int W(string k, int fallback) => (int)Math.Round(desc.Number(k) ?? fallback);
        return new BlackWhiteAdjustment(
            W("Rd  ", def.Reds), W("Yllw", def.Yellows), W("Grn ", def.Greens), W("Cyn ", def.Cyans), W("Bl  ", def.Blues), W("Mgnt", def.Magentas),
            desc.Bool("useTint") ?? false,
            desc.Object("tintColor") is { } tint ? PsdEffects.ColorOf(tint) : BlackWhiteAdjustment.DefaultTint);
    }

    /// <summary>
    /// Version 3: the filter color as three 32-bit XYZ values; version 2: a color structure (space and four 16-bit
    /// components). Then density (percent, 32-bit) and the preserve luminosity flag.
    /// </summary>
    private static PhotoFilterAdjustment? ParsePhotoFilter(byte[] d)
    {
        int version = I16(d, 0);
        RgbColor color;
        int o;
        if (version == 3)
        {
            color = FromXyz(I32(d, 2), I32(d, 6), I32(d, 10));
            o = 14;
        }
        else if (version == 2)
        {
            color = ColorStructure(d, 2);
            o = 12;
        }
        else return null;
        int density = Math.Clamp(I32(d, o), 0, 100);
        bool preserve = d.Length > o + 4 && d[o + 4] != 0;
        return new PhotoFilterAdjustment(color, density, preserve);
    }

    /// <summary>
    /// XYZ (D50) stored as integers. The scale is not documented: values up to a few hundred are read as percent,
    /// larger ones as 16.16 fixed point.
    /// </summary>
    internal static RgbColor FromXyz(int xi, int yi, int zi)
    {
        double max = Math.Max(Math.Abs((double)xi), Math.Max(Math.Abs((double)yi), Math.Abs((double)zi)));
        double scale = max <= 2 ? 1 : max <= 400 ? 100 : max <= 400 * 65536.0 / 100 ? 65536 : 65536 * 100.0;
        double x = xi / scale, y = yi / scale, z = zi / scale;
        // Bradford-adapted D50 XYZ to linear sRGB.
        double r = 3.1338561 * x - 1.6168667 * y - 0.4906146 * z;
        double g = -0.9787684 * x + 1.9161415 * y + 0.0334540 * z;
        double b = 0.0719453 * x - 0.2289914 * y + 1.4052427 * z;
        return new RgbColor(Encode(r), Encode(g), Encode(b));

        static float Encode(double v) => GradientLut.LinearToSrgb((float)Math.Clamp(v, 0, 1));
    }

    /// <summary>
    /// Photoshop's color structure: a 16-bit color space then four 16-bit components (RGB 0..65535, HSB with hue in
    /// 1/182.04 degree steps, CMYK as 65535 minus the ink, Lab, grayscale 0..10000).
    /// </summary>
    internal static RgbColor ColorStructure(byte[] d, int o)
    {
        int space = I16(d, o);
        float c0 = U16(d, o + 2), c1 = U16(d, o + 4), c2 = U16(d, o + 6), c3 = U16(d, o + 8);
        switch (space)
        {
            case 0:
                return new RgbColor(c0 / 65535f, c1 / 65535f, c2 / 65535f);
            case 1:
            {
                float h = c0 / 65535f * 360f, s = c1 / 65535f, v = c2 / 65535f;
                return Hsb(h, s, v);
            }
            case 2:
            {
                float k = c3 / 65535f;
                return new RgbColor(c0 / 65535f * k, c1 / 65535f * k, c2 / 65535f * k);
            }
            case 7:
            {
                double l = c0 / 100.0, a = (short)c1 / 100.0, b = (short)c2 / 100.0;
                return FromLab(l, a, b);
            }
            case 8:
            {
                float g = 1f - Math.Clamp(c0 / 10000f, 0f, 1f);
                return new RgbColor(g, g, g);
            }
            default:
                return RgbColor.Black;
        }
    }

    private static RgbColor Hsb(float h, float s, float v)
    {
        h = ((h % 360) + 360) % 360 / 60f;
        int i = (int)h;
        float f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        return i switch
        {
            0 => new(v, t, p), 1 => new(q, v, p), 2 => new(p, v, t),
            3 => new(p, q, v), 4 => new(t, p, v), _ => new(v, p, q),
        };
    }

    /// <summary>CIE Lab (D50) to sRGB.</summary>
    private static RgbColor FromLab(double l, double a, double b)
    {
        double fy = (l + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
        static double F(double t) => t > 6.0 / 29 ? t * t * t : 3 * (6.0 / 29) * (6.0 / 29) * (t - 4.0 / 29);
        double x = 0.9642 * F(fx), y = F(fy), z = 0.8249 * F(fz);
        return FromXyzUnit(x, y, z);
    }

    private static RgbColor FromXyzUnit(double x, double y, double z)
    {
        double r = 3.1338561 * x - 1.6168667 * y - 0.4906146 * z;
        double g = -0.9787684 * x + 1.9161415 * y + 0.0334540 * z;
        double b = 0.0719453 * x - 0.2289914 * y + 1.4052427 * z;
        return new RgbColor(E(r), E(g), E(b));
        static float E(double v) => GradientLut.LinearToSrgb((float)Math.Clamp(v, 0, 1));
    }

    /// <summary>
    /// Version 1, the monochrome flag, then five-short records (four source weights — R, G, B, and K for CMYK — and
    /// the constant) for each output channel: red, green, blue, and a fourth that CMYK uses. In monochrome mode the
    /// first record holds the gray output.
    /// </summary>
    private static ChannelMixerAdjustment? ParseChannelMixer(byte[] d)
    {
        if (I16(d, 0) != 1) return null;
        bool mono = I16(d, 2) != 0;
        int records = Math.Min(4, (d.Length - 4) / 10);
        var def = ChannelMixerAdjustment.Default;
        MixerChannel Record(int i, MixerChannel fallback)
        {
            if (i >= records) return fallback;
            int o = 4 + i * 10;
            return new MixerChannel(I16(d, o), I16(d, o + 2), I16(d, o + 4), I16(d, o + 8));
        }
        var red = Record(0, def.Red);
        return mono
            ? new ChannelMixerAdjustment(true, def.Red, Record(1, def.Green), Record(2, def.Blue), red)
            : new ChannelMixerAdjustment(false, red, Record(1, def.Green), Record(2, def.Blue), def.Gray);
    }

    /// <summary>
    /// Version 1, the method (0 relative, 1 absolute), then ten records of cyan, magenta, yellow and black
    /// (-100..100): the first is unused, then reds, yellows, greens, cyans, blues, magentas, whites, neutrals, blacks.
    /// </summary>
    private static SelectiveColorAdjustment? ParseSelectiveColor(byte[] d)
    {
        if (I16(d, 0) != 1) return null;
        bool absolute = I16(d, 2) != 0;
        var ranges = new SelectiveColorValues[9];
        for (int i = 0; i < 9; i++)
        {
            int o = 4 + (i + 1) * 8;
            if (o + 8 > d.Length) break;
            ranges[i] = new SelectiveColorValues(I16(d, o), I16(d, o + 2), I16(d, o + 4), I16(d, o + 6));
        }
        return new SelectiveColorAdjustment(absolute, ranges);
    }

    /// <summary>The fields of a 'grdm' block, with where its variable parts sit (for patching).</summary>
    internal sealed record GradientMapLayout(int Version, int MethodOffset, int StopSize, int TailOffset);

    /// <summary>The four-character keys Photoshop uses for gradient methods.</summary>
    internal static readonly (string Key, GradientMethod Method)[] MethodKeys =
        [("Perc", GradientMethod.Perceptual), ("Lnr ", GradientMethod.Linear), ("Gcls", GradientMethod.Classic)];

    /// <summary>
    /// Version (1, or 3 with a four-character method after the flags), reversed and dithered flags, the name, color
    /// stops (location 0..4096, midpoint percent, color structure, and — as in Photoshop's .grd files — the stop's type:
    /// user, foreground or background), transparency stops (location, midpoint, opacity 0..255), then expansion (2),
    /// interpolation (smoothness 0..4096), length (32), mode, seed and the noise settings.
    /// Each stop's midpoint belongs to the segment that ends at it, as in the descriptors.
    /// </summary>
    private static GradientMapAdjustment? ParseGradientMap(byte[] d) => ReadGradientMap(d, out _);

    internal static GradientMapAdjustment? ReadGradientMap(byte[] d, out GradientMapLayout? layout)
    {
        layout = null;
        int version = I16(d, 0);
        if (version is not (1 or 3)) return null;
        bool reverse = d[2] != 0, dither = d[3] != 0;
        int o = 4;
        var method = GradientMethod.Classic;
        int methodOffset = -1;
        if (version == 3)
        {
            string key = Encoding.ASCII.GetString(d, o, 4);
            method = MethodKeys.FirstOrDefault(m => m.Key == key) is { Key: not null } found ? found.Method : GradientMethod.Classic;
            methodOffset = o;
            o += 4;
        }
        int chars = I32(d, o);
        if (chars < 0 || chars > 10_000) return null;
        string name = Encoding.BigEndianUnicode.GetString(d, o + 4, chars * 2).TrimEnd('\0');
        o += 4 + chars * 2;

        // Stops are 20 bytes (with the type) in the files Photoshop writes; the specification lists 18. Take whichever
        // leaves a consistent tail (expansion 2, length 32).
        foreach (int stopSize in new[] { 20, 18 })
        {
            if (TryStops(d, o, stopSize, out var gradient, out int tail))
            {
                layout = new GradientMapLayout(version, methodOffset, stopSize, tail);
                return new GradientMapAdjustment(gradient! with { Name = CleanName(name) }, reverse, dither, method);
            }
        }
        return null;
    }

    /// <summary>Photoshop names built-in gradients with a localization key ("$$$/DefaultGradient/…=Name"); the model keeps the display name.</summary>
    private static string CleanName(string name) => name.StartsWith("$$$/", StringComparison.Ordinal) && name.IndexOf('=') is int eq and > 0 ? name[(eq + 1)..] : name;

    private static bool TryStops(byte[] d, int o, int stopSize, out Gradient? gradient, out int tail)
    {
        gradient = null;
        tail = 0;
        if (o + 2 > d.Length) return false;
        int colorCount = I16(d, o);
        o += 2;
        if (colorCount is < 1 or > 1000 || o + colorCount * stopSize + 2 > d.Length) return false;
        var colors = new List<GradientColorStop>();
        for (int i = 0; i < colorCount; i++, o += stopSize)
        {
            float location = I32(d, o) / 4096f, midpoint = I32(d, o + 4) / 100f;
            var color = ColorStructure(d, o + 8);
            int type = stopSize >= 20 ? I16(d, o + 18) : 0;
            colors.Add(new GradientColorStop(location, midpoint, color)
            {
                Kind = type switch { 1 => GradientStopKind.Foreground, 2 => GradientStopKind.Background, _ => GradientStopKind.User },
            });
        }
        int opacityCount = I16(d, o);
        o += 2;
        if (opacityCount is < 0 or > 1000 || o + opacityCount * 10 + 6 > d.Length) return false;
        var opacities = new List<GradientOpacityStop>();
        for (int i = 0; i < opacityCount; i++, o += 10)
            opacities.Add(new GradientOpacityStop(I32(d, o) / 4096f, I32(d, o + 4) / 100f, U16(d, o + 8) / 255f));
        if (I16(d, o) != 2 || I16(d, o + 4) != 32) return false;
        tail = o;
        float smoothness = Math.Clamp(I16(d, o + 2) / 4096f, 0f, 1f);

        colors = colors.OrderBy(c => c.Location).ToList();
        opacities = opacities.OrderBy(c => c.Location).ToList();
        var colorMids = PsdEffects.ToSegmentStart(colors.Select(c => c.Midpoint).ToList());
        var opacityMids = PsdEffects.ToSegmentStart(opacities.Select(c => c.Midpoint).ToList());
        gradient = new Gradient(
            colors.Select((c, i) => c with { Midpoint = colorMids[i] }).ToList(),
            opacities.Select((c, i) => c with { Midpoint = opacityMids[i] }).ToList())
        {
            Smoothness = smoothness,
        };
        return true;
    }

    /// <summary>
    /// Version 1, then a versioned descriptor: 'lookupType' ('3DLUT', 'abstractProfile' or 'deviceLinkProfile'),
    /// 'Nm  ', 'Dthr', 'profile' (the ICC data), 'LUTFormat', 'LUT3DFileData' (the LUT file's bytes) and
    /// 'LUT3DFileName'.
    /// </summary>
    /// <summary>The LUT file bytes of a 'clrL' block, or null.</summary>
    internal static byte[]? ReadColorLookupData(byte[] d)
    {
        try
        {
            return ParseColorLookup(d)?.Data;
        }
        catch (Exception e) when (e is PsdFormatException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static ColorLookupAdjustment? ParseColorLookup(byte[] d)
    {
        if (I16(d, 0) != 1) return null;
        var desc = DescriptorReader.ReadVersioned(d, 2);
        var kind = desc.Enum("lookupType") switch
        {
            "abstractProfile" => ColorLookupKind.AbstractProfile,
            "deviceLinkProfile" => ColorLookupKind.DeviceLinkProfile,
            _ => ColorLookupKind.Lut3D,
        };
        bool dither = desc.Bool("Dthr") ?? false;
        if (kind == ColorLookupKind.Lut3D)
        {
            string format = (desc.Enum("LUTFormat") ?? "LUTFormatCUBE").Replace("LUTFormat", "", StringComparison.Ordinal);
            byte[] data = desc["LUT3DFileData"] is RawValue raw ? raw.Data : [];
            string name = desc.Text("LUT3DFileName") ?? desc.Text("Nm  ") ?? "";
            return new ColorLookupAdjustment(kind, name, format, data, dither);
        }
        byte[] profile = desc["profile"] is RawValue p ? p.Data : [];
        return new ColorLookupAdjustment(kind, desc.Text("Nm  ") ?? "", "ICC", profile, dither);
    }
}
