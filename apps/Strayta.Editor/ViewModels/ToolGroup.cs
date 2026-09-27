using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Editor.Controls;

namespace Strayta.Editor.ViewModels;

/// <summary>A tool as the tool strip shows it: its name and the icon's resource key (Icons.axaml).</summary>
public sealed record ToolInfo(CanvasTool Tool, string Name, string Icon);

/// <summary>
/// One slot of the tool strip, as in Photoshop: related tools share a slot and a shortcut key. The slot shows the
/// tool used last; the key picks it and Shift plus the key steps through the group.
/// </summary>
public sealed partial class ToolGroup : ObservableObject
{
    private readonly EditorViewModel _editor;

    public ToolGroup(EditorViewModel editor, string key, params ToolInfo[] tools)
    {
        _editor = editor;
        Key = key;
        Tools = tools;
        Current = tools[0];
    }

    /// <summary>The shortcut key shared by the group ("W").</summary>
    public string Key { get; }

    public IReadOnlyList<ToolInfo> Tools { get; }

    public bool HasMore => Tools.Count > 1;

    /// <summary>The tool the slot shows and its key picks: the one used last.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tip))]
    public partial ToolInfo Current { get; set; }

    public bool IsActive => Contains(_editor.Tool);

    public string Tip => HasMore
        ? $"{Current.Name} ({Key})\nShift+{Key} switches · hold or right-click for {string.Join(", ", Tools.Where(t => t != Current).Select(t => t.Name))}"
        : $"{Current.Name} ({Key})";

    public bool Contains(CanvasTool tool) => Tools.Any(t => t.Tool == tool);

    /// <summary>Picks the tool the slot shows (a click or the key).</summary>
    public void Activate() => _editor.Tool = Current.Tool;

    /// <summary>Shift plus the key: the next tool of the group once it is active, else the one it shows.</summary>
    public void Cycle()
    {
        if (IsActive)
        {
            int i = Tools.ToList().FindIndex(t => t.Tool == _editor.Tool);
            Current = Tools[(i + 1) % Tools.Count];
        }
        Activate();
    }

    public void Select(CanvasTool tool)
    {
        if (Tools.FirstOrDefault(t => t.Tool == tool) is { } info) Current = info;
        Activate();
    }

    /// <summary>Called when the editor's tool changes, however it was chosen, so the slot remembers it.</summary>
    internal void OnToolChanged(CanvasTool tool)
    {
        if (Tools.FirstOrDefault(t => t.Tool == tool) is { } info) Current = info;
        OnPropertyChanged(nameof(IsActive));
    }
}
