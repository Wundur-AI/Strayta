using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Editor.Controls;

namespace Strayta.Editor.ViewModels;

// Zoom settings (Animated Zoom, Scrubby Zoom) and spring-loaded tool keys: holding a tool's key (Z for Zoom) switches to
// the tool only while the key is down, and releasing it returns to the tool you had, as in Photoshop. A quick tap
// switches for good.
public sealed partial class EditorViewModel
{
    /// <summary>Holding a tool key at least this long makes the switch temporary.</summary>
    public static readonly TimeSpan SpringLoadDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>Zoom steps (clicks, ⌘+ / ⌘−) glide to the new level; off, they jump (Photoshop's Animated Zoom preference).</summary>
    [ObservableProperty] public partial bool AnimatedZoom { get; set; } = true;

    /// <summary>Dragging with the Zoom tool zooms continuously; off, the drag draws a rectangle to zoom into.</summary>
    [ObservableProperty] public partial bool ScrubbyZoom { get; set; } = true;

    private (string Key, CanvasTool Previous, long Since)? _springKey;

    /// <summary>A tool key went down (before the tool is picked): remembers the tool to return to if it is held.</summary>
    public void SpringKeyDown(string key)
    {
        if (_springKey is { } held && held.Key == key) return; // the keyboard repeating a held key
        _springKey = (key, Tool, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// A tool key came up: if it was held long enough, the tool it picked was temporary and the previous one returns.
    /// Returns true when it did.
    /// </summary>
    public bool SpringKeyUp(string key, TimeSpan? heldFor = null)
    {
        if (_springKey is not { } held || held.Key != key) return false;
        _springKey = null;
        var elapsed = heldFor ?? Stopwatch.GetElapsedTime(held.Since);
        if (elapsed < SpringLoadDelay || Tool == held.Previous) return false;
        Tool = held.Previous;
        return true;
    }
}
