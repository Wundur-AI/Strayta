using Dock.Model.Mvvm.Controls;

namespace Strayta.Editor.ViewModels;

/// <summary>
/// The Layers panel for the active document: the layer stack plus, as in Photoshop, the selected layer's
/// blend mode, opacity and fill at the top.
/// </summary>
public sealed class LayersToolViewModel(EditorViewModel editor) : Tool
{
    public EditorViewModel Editor { get; } = editor;
}

/// <summary>The Color panel: pick the foreground (brush) color.</summary>
public sealed class ColorToolViewModel(EditorViewModel editor) : Tool
{
    public EditorViewModel Editor { get; } = editor;
}

/// <summary>The Swatches panel: saved colors; click to use one, + to save the current color.</summary>
public sealed class SwatchesToolViewModel(EditorViewModel editor) : Tool
{
    public EditorViewModel Editor { get; } = editor;
}
