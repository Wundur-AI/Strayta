using Dock.Model.Mvvm.Controls;

namespace Strayta.Editor.ViewModels;

/// <summary>
/// Window › Character: font, size, leading, kerning, tracking, scale, baseline shift, color, the type style buttons and
/// anti-aliasing, for the selected characters or the selected type layer (<see cref="TypeOptions"/>).
/// </summary>
public sealed class CharacterToolViewModel(EditorViewModel editor) : Tool
{
    public EditorViewModel Editor { get; } = editor;
}

/// <summary>Window › Paragraph: alignment and justification, indents, space before and after, hyphenation (<see cref="TypeOptions"/>).</summary>
public sealed class ParagraphToolViewModel(EditorViewModel editor) : Tool
{
    public EditorViewModel Editor { get; } = editor;
}
