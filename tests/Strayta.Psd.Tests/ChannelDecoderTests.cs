using System.Buffers.Binary;

namespace Strayta.Psd.Tests;

public class ChannelDecoderTests
{
    [Fact]
    public void UnpackBits_decodes_apple_reference_example()
    {
        // From Apple Technical Note TN1023.
        byte[] packed = [0xFE, 0xAA, 0x02, 0x80, 0x00, 0x2A, 0xFD, 0xAA, 0x03, 0x80, 0x00, 0x2A, 0x22, 0xF7, 0xAA];
        byte[] expected =
        [
            0xAA, 0xAA, 0xAA, 0x80, 0x00, 0x2A, 0xAA, 0xAA, 0xAA, 0xAA, 0x80, 0x00, 0x2A, 0x22,
            0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA,
        ];
        var output = new byte[expected.Length];

        ChannelDecoder.UnpackBits(packed, output);

        Assert.Equal(expected, output);
    }

    [Fact]
    public void UnpackBits_ignores_minus_128_noop_and_tolerates_overlong_rows()
    {
        byte[] packed = [0x80, 0xFC, 0x07]; // no-op, then repeat 0x07 five times
        var output = new byte[3];

        ChannelDecoder.UnpackBits(packed, output);

        Assert.Equal([7, 7, 7], output);
    }

    [Fact]
    public void Unpredict_8bit_accumulates_each_row_independently()
    {
        byte[] data = [10, 1, 1, 250, 20, 2];

        ChannelDecoder.Unpredict(data, width: 3, height: 2, depth: 8);

        Assert.Equal([10, 11, 12, 250, 14, 16], data); // 250+20 wraps to 14
    }

    [Fact]
    public void Unpredict_16bit_accumulates_big_endian_words()
    {
        var data = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(data, 1000);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), 300);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), 65535); // -1

        ChannelDecoder.Unpredict(data, width: 3, height: 1, depth: 16);

        Assert.Equal(1000, BinaryPrimitives.ReadUInt16BigEndian(data));
        Assert.Equal(1300, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)));
        Assert.Equal(1299, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4)));
    }

    [Fact]
    public void Unpredict_32bit_reverses_delta_and_byte_planes()
    {
        float[] values = [1.5f, -2.25f, 0.1f];
        int w = values.Length;

        // Encode the way Photoshop does: split big-endian bytes into planes, then delta the row.
        var planes = new byte[w * 4];
        Span<byte> be = stackalloc byte[4];
        for (int x = 0; x < w; x++)
        {
            BinaryPrimitives.WriteSingleBigEndian(be, values[x]);
            for (int b = 0; b < 4; b++) planes[b * w + x] = be[b];
        }
        var encoded = (byte[])planes.Clone();
        for (int i = encoded.Length - 1; i > 0; i--) encoded[i] -= encoded[i - 1];

        ChannelDecoder.Unpredict(encoded, w, 1, 32);

        for (int x = 0; x < w; x++)
            Assert.Equal(values[x], BinaryPrimitives.ReadSingleBigEndian(encoded.AsSpan(x * 4)));
    }

    [Fact]
    public void ToPlane_expands_bitmap_mode_with_set_bits_as_black()
    {
        var plane = ChannelDecoder.ToPlane([0b1010_0000], width: 3, height: 1, depth: 1);

        Assert.Equal(8, plane.BitDepth);
        Assert.Equal([0, 255, 0], plane.Data);
    }

    [Fact]
    public void ToPlane_swaps_16bit_samples_to_host_order()
    {
        var plane = ChannelDecoder.ToPlane([0x12, 0x34, 0xFF, 0x00], width: 2, height: 1, depth: 16);

        Assert.Equal([(ushort)0x1234, (ushort)0xFF00], plane.AsUInt16().ToArray());
    }
}
