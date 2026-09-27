namespace Strayta.Psd;

/// <summary>Thrown when a file is not a valid PSD/PSB or uses a feature the reader cannot parse.</summary>
public sealed class PsdFormatException(string message, long offset = -1)
    : Exception(offset >= 0 ? $"{message} (at offset {offset})" : message)
{
    /// <summary>Byte offset in the file where the problem was detected, or -1.</summary>
    public long Offset { get; } = offset;
}
