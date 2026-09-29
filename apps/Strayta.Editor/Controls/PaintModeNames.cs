using Strayta.Core.Painting;

namespace Strayta.Editor.Controls;

/// <summary>The painting modes for Mode menus (Brush, Clone Stamp, Gradient, Paint Bucket, Fill, Stroke), in Photoshop's order and names.</summary>
public static class PaintModeNames
{
    /// <summary>Every mode; the enum is declared in menu order, so a menu index is the enum value.</summary>
    public static IReadOnlyList<PaintMode> Modes { get; } = Enum.GetValues<PaintMode>();

    public static IReadOnlyList<string> Names { get; } = Modes.Select(Of).ToList();

    public static string Of(PaintMode mode) => mode switch
    {
        PaintMode.LinearDodge => "Linear Dodge (Add)",
        _ => System.Text.RegularExpressions.Regex.Replace(mode.ToString(), "(?<=[a-z])(?=[A-Z])", " "),
    };

    public static int IndexOf(PaintMode mode) => Math.Max(0, (int)mode);

    public static PaintMode? At(int index) => index >= 0 && index < Modes.Count ? Modes[index] : null;
}
