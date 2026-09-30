namespace Strayta.Editor.Controls;

// Dodge, Burn, Sponge, Blur, Sharpen and Smudge paint like the brush on the canvas: the brush outline, pen pressure and
// StrokeBegin/StrokeMove/StrokeEnd (routed to the document in DocumentView.Toning.cs).
public sealed partial class ImageCanvas
{
    private bool IsToneTool => Tool is CanvasTool.Dodge or CanvasTool.Burn or CanvasTool.Sponge or CanvasTool.Blur or CanvasTool.Sharpen or CanvasTool.Smudge;
}
