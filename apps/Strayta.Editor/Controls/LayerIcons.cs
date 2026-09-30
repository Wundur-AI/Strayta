using Avalonia.Media;

namespace Strayta.Editor.Controls;

/// <summary>Stroke icons (24×24, like Themes/Icons.axaml) for Align, Distribute and the Layers panel menu.</summary>
public static class LayerIcons
{
    public static StreamGeometry AlignLeft { get; } = StreamGeometry.Parse("M4 3v18 M8 6h11v4H8Z M8 14h6v4H8Z");
    public static StreamGeometry AlignHorizontalCenter { get; } = StreamGeometry.Parse("M12 3v18 M6 6h12v4H6Z M8 14h8v4H8Z");
    public static StreamGeometry AlignRight { get; } = StreamGeometry.Parse("M20 3v18 M5 6h11v4H5Z M10 14h6v4h-6Z");
    public static StreamGeometry AlignTop { get; } = StreamGeometry.Parse("M3 4h18 M6 8v11h4V8Z M14 8v6h4V8Z");
    public static StreamGeometry AlignVerticalCenter { get; } = StreamGeometry.Parse("M3 12h18 M6 6v12h4V6Z M14 8v8h4V8Z");
    public static StreamGeometry AlignBottom { get; } = StreamGeometry.Parse("M3 20h18 M6 5v11h4V5Z M14 10v6h4v-6Z");

    public static StreamGeometry DistributeLeft { get; } = StreamGeometry.Parse("M4 3v18 M14 3v18 M6 8h4v8H6Z M16 6h4v12h-4Z");
    public static StreamGeometry DistributeHorizontalCenter { get; } = StreamGeometry.Parse("M8 3v18 M16 3v18 M6 8h4v8H6Z M14 6h4v12h-4Z");
    public static StreamGeometry DistributeRight { get; } = StreamGeometry.Parse("M10 3v18 M20 3v18 M4 8h4v8H4Z M14 6h4v12h-4Z");
    public static StreamGeometry DistributeTop { get; } = StreamGeometry.Parse("M3 4h18 M3 14h18 M8 6v4h8V6Z M6 16v4h12v-4Z");
    public static StreamGeometry DistributeVerticalCenter { get; } = StreamGeometry.Parse("M3 8h18 M3 16h18 M8 6v4h8V6Z M6 14v4h12v-4Z");
    public static StreamGeometry DistributeBottom { get; } = StreamGeometry.Parse("M3 10h18 M3 20h18 M8 4v4h8V4Z M6 14v4h12v-4Z");
    public static StreamGeometry DistributeHorizontalSpacing { get; } = StreamGeometry.Parse("M3 5v14 M21 5v14 M9 8h6v8H9Z");
    public static StreamGeometry DistributeVerticalSpacing { get; } = StreamGeometry.Parse("M5 3h14 M5 21h14 M8 9v6h8V9Z");

    /// <summary>Themes' IconLock as closed shapes, to draw filled for Lock All.</summary>
    public static StreamGeometry LockSolid { get; } = StreamGeometry.Parse("M6 11h12v10H6Z M8 11V7a4 4 0 0 1 8 0v4Z");

    public static StreamGeometry PanelMenu { get; } = StreamGeometry.Parse("M4 7h16 M4 12h16 M4 17h16");
    public static StreamGeometry Filter { get; } = StreamGeometry.Parse("M4 5h16l-6 7v6l-4 2v-8Z");
}
