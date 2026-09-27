using System.Runtime.InteropServices;

namespace Strayta.Core;

/// <summary>
/// A single channel of pixel samples, row-major, in host byte order.
/// 1-bit sources are expanded to 8-bit when loaded, so <see cref="BitDepth"/> is 8, 16 or 32.
/// 32-bit samples are IEEE floats; 8- and 16-bit samples are unsigned integers.
/// </summary>
public sealed class Plane
{
    public Plane(int width, int height, int bitDepth, byte[] data)
    {
        if (bitDepth is not (8 or 16 or 32))
            throw new ArgumentOutOfRangeException(nameof(bitDepth), bitDepth, "Bit depth must be 8, 16 or 32.");
        long expected = (long)width * height * (bitDepth / 8);
        if (data.LongLength != expected)
            throw new ArgumentException($"Expected {expected} bytes for {width}x{height}@{bitDepth}, got {data.LongLength}.", nameof(data));

        Width = width;
        Height = height;
        BitDepth = bitDepth;
        Data = data;
    }

    public int Width { get; }
    public int Height { get; }
    public int BitDepth { get; }
    public byte[] Data { get; }

    public Span<byte> AsBytes() => Data;
    public Span<ushort> AsUInt16() => BitDepth == 16 ? MemoryMarshal.Cast<byte, ushort>(Data.AsSpan()) : throw WrongDepth(16);
    public Span<float> AsSingle() => BitDepth == 32 ? MemoryMarshal.Cast<byte, float>(Data.AsSpan()) : throw WrongDepth(32);

    /// <summary>Returns sample <paramref name="index"/> normalized to 0..1 (floats are returned as stored).</summary>
    public float GetNormalized(int index) => BitDepth switch
    {
        8 => Data[index] / 255f,
        16 => AsUInt16()[index] / 65535f,
        _ => AsSingle()[index],
    };

    public static Plane Create(int width, int height, int bitDepth) =>
        new(width, height, bitDepth, new byte[(long)width * height * (bitDepth / 8)]);

    private InvalidOperationException WrongDepth(int wanted) =>
        new($"Plane is {BitDepth}-bit, not {wanted}-bit.");
}
