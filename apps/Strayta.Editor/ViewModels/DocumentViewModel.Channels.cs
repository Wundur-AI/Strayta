using System.ComponentModel;
using System.Runtime.CompilerServices;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;
using Strayta.Editor.Editing;
using Strayta.Rendering;
using Strayta.Rendering.Filters;

namespace Strayta.Editor.ViewModels;

/// <summary>What painting, fills, filters and adjustments act on, as the Channels panel targets it.</summary>
public enum ChannelTargetKind
{
    /// <summary>The selected layer's color channels (all of them, or those targeted in the panel).</summary>
    Color,

    /// <summary>A saved selection or spot channel, painted in gray.</summary>
    Channel,

    /// <summary>The quick mask (Quick Mask mode).</summary>
    QuickMask,

    /// <summary>The selected layer's mask (targeted in the Layers panel).</summary>
    LayerMask,
}

/// <summary>Quick Mask Options: the overlay color and opacity, and whether the color marks masked or selected areas.</summary>
public sealed record QuickMaskOptions(RgbColor Color, float Opacity, bool SelectedAreas)
{
    public static QuickMaskOptions Default { get; } = new(new RgbColor(1f, 0f, 0f), 0.5f, false);
}

// The Channels panel's model of a document: which color channels, saved selections, spot channels, quick mask and layer
// mask are visible (their eyes) and which one editing acts on (the highlighted row); the channel commands (new,
// duplicate, delete, options, Save / Load Selection, Quick Mask); how a targeted channel is edited; and how the view
// is drawn (ChannelView over each render).
//
// Editing a saved selection or the quick mask reuses the layer-mask tools: a stand-in layer that belongs to no document
// carries the channel as a canvas-sized mask, so the brush, eraser, gradient and filters paint it exactly as they paint
// a mask, and their result (a MaskEdit on the stand-in) becomes a channel edit in RouteChannelEdit. Fills and clears go
// through a gray stand-in pixel layer the same way. With only some color channels targeted, a pixel edit of the layer is
// narrowed to those channels (ChannelRestriction).
public sealed partial class DocumentViewModel
{
    private readonly LayerGroup _channelOwner = new() { Name = "Channel" };
    private readonly PixelLayer _channelLayer = new() { Name = "Channel" };
    private bool[] _colorVisible = [], _colorTargeted = [];
    private readonly HashSet<int> _visibleChannels = [];
    private int _targetChannel;
    private bool _layerMaskVisible, _quickMaskTargeted = true, _quickMaskVisible = true, _retargeting;
    private QuickMaskState _quickMask = new(false, null, null);

    /// <summary>Raised when channels, their visibility or the target change (the Channels panel listens).</summary>
    public event Action? ChannelsChanged;

    /// <summary>Sets up the color channels' eyes (called by the constructor).</summary>
    private void InitChannels()
    {
        int n = ColorChannelCount;
        _colorVisible = Enumerable.Repeat(true, n).ToArray();
        _colorTargeted = Enumerable.Repeat(true, n).ToArray();
        PropertyChanged += OnChannelTargetChanged;
    }

    /// <summary>Red, green and blue (3) or gray (1); other color modes show only the composite (0).</summary>
    public int ColorChannelCount => Model.ColorMode switch
    {
        ColorMode.Rgb => 3,
        ColorMode.Grayscale => 1,
        _ => 0,
    };

    /// <summary>The document's saved selections and spot channels.</summary>
    public IReadOnlyList<DocumentChannel> Channels => ChannelsEdit.Read(Model);

    public bool IsColorVisible(int k) => k < _colorVisible.Length && _colorVisible[k];
    public bool IsColorTargeted(int k) => ChannelTarget == ChannelTargetKind.Color && k < _colorTargeted.Length && _colorTargeted[k];
    public bool IsCompositeVisible => _colorVisible.All(v => v);
    public bool IsCompositeTargeted => ChannelTarget == ChannelTargetKind.Color && _colorTargeted.All(v => v);
    public bool IsChannelVisible(int id) => _visibleChannels.Contains(id);
    public bool IsChannelTargeted(int id) => ChannelTarget == ChannelTargetKind.Channel && _targetChannel == id;
    public bool IsLayerMaskVisible => _layerMaskVisible;
    public bool IsQuickMask => _quickMask.Active;
    public bool IsQuickMaskVisible => _quickMaskVisible;
    public Plane? QuickMaskPlane => _quickMask.Plane;

    /// <summary>The targeted saved selection or spot channel, or null.</summary>
    public DocumentChannel? TargetedChannel => _targetChannel == 0 ? null : Channels.FirstOrDefault(c => c.Id == _targetChannel);

    /// <summary>What editing acts on now.</summary>
    public ChannelTargetKind ChannelTarget =>
        _quickMask.Active && _quickMaskTargeted && _quickMask.Plane is not null ? ChannelTargetKind.QuickMask
        : TargetedChannel is not null ? ChannelTargetKind.Channel
        : EditMask && SelectedLayer?.Node.GetMask() is not null ? ChannelTargetKind.LayerMask
        : ChannelTargetKind.Color;

    /// <summary>True when only some color channels are targeted, so pixel edits change only those.</summary>
    public bool RestrictsColorChannels => ChannelTarget == ChannelTargetKind.Color && _colorTargeted.Length > 1 && !_colorTargeted.All(t => t);

    /// <summary>Quick Mask Options (the same for every document, as in Photoshop).</summary>
    public QuickMaskOptions QuickMaskSettings => Editor.QuickMaskSettings;

    // ---- Targeting and eyes ------------------------------------------------------------------------------------

    /// <summary>Clicking a layer (or its thumbnail) in the Layers panel targets the image again, as in Photoshop.</summary>
    private void OnChannelTargetChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SelectedLayer) or nameof(EditMask)) || _retargeting) return;
        if (_targetChannel != 0 || (_quickMask.Active && _quickMaskTargeted && EditMask))
        {
            _targetChannel = 0;
            if (_quickMask.Active && EditMask) _quickMaskTargeted = false;
            if (!_colorVisible.Any(v => v)) ShowAllColors();
            RequestRender();
        }
        ChannelsChanged?.Invoke();
    }

    private void ShowAllColors()
    {
        Array.Fill(_colorVisible, true);
        Array.Fill(_colorTargeted, true);
        _visibleChannels.RemoveWhere(id => Channels.FirstOrDefault(c => c.Id == id) is not { IsSpot: true });
    }

    private void Retarget(Action change)
    {
        _retargeting = true;
        try
        {
            change();
        }
        finally
        {
            _retargeting = false;
        }
        ChannelsChanged?.Invoke();
        RequestRender();
    }

    /// <summary>The composite row (⌘2): every color channel visible and targeted; saved selections hidden.</summary>
    public void TargetComposite() => Retarget(() =>
    {
        _targetChannel = 0;
        _quickMaskTargeted = false;
        ShowAllColors();
        if (EditMask) EditMask = false;
    });

    /// <summary>A color channel's row (⌘3–⌘5): only it, in gray; with <paramref name="add"/> (Shift) it joins the target.</summary>
    public void TargetColor(int k, bool add = false) => Retarget(() =>
    {
        if (k < 0 || k >= _colorVisible.Length) return;
        bool wasColor = ChannelTarget == ChannelTargetKind.Color;
        _targetChannel = 0;
        _quickMaskTargeted = false;
        if (EditMask) EditMask = false;
        if (add && wasColor)
        {
            // Shift-click adds the channel to the target, or takes it out (one always stays).
            bool remove = _colorTargeted[k] && _colorTargeted.Count(t => t) > 1;
            _colorTargeted[k] = _colorVisible[k] = !remove;
            return;
        }
        for (int i = 0; i < _colorVisible.Length; i++) _colorVisible[i] = _colorTargeted[i] = i == k;
        _visibleChannels.RemoveWhere(id => Channels.FirstOrDefault(c => c.Id == id) is not { IsSpot: true });
    });

    /// <summary>A saved selection's or spot channel's row: it alone, in gray (Shift: shown over the image as well).</summary>
    public void TargetChannel(int id, bool add = false) => Retarget(() =>
    {
        if (Channels.All(c => c.Id != id)) return;
        _targetChannel = id;
        _quickMaskTargeted = false;
        if (!add)
        {
            Array.Fill(_colorVisible, false);
            _visibleChannels.Clear();
        }
        _visibleChannels.Add(id);
    });

    /// <summary>The Quick Mask row (in Quick Mask mode).</summary>
    public void TargetQuickMask() => Retarget(() =>
    {
        if (!_quickMask.Active) return;
        _quickMaskTargeted = true;
        _quickMaskVisible = true;
        _targetChannel = 0;
    });

    /// <summary>The layer mask row: targets the selected layer's mask, as clicking its thumbnail in the Layers panel does.</summary>
    public void TargetLayerMask() => Retarget(() =>
    {
        if (SelectedLayer?.Node.GetMask() is null) return;
        _targetChannel = 0;
        _quickMaskTargeted = false;
        EditMask = true;
        if (!_colorVisible.Any(v => v) && !_layerMaskVisible) ShowAllColors();
    });

    /// <summary>A color channel's eye.</summary>
    public void SetColorVisible(int k, bool visible) => Retarget(() =>
    {
        if (k < 0 || k >= _colorVisible.Length) return;
        _colorVisible[k] = visible;
    });

    /// <summary>The composite's eye: all color channels on or off.</summary>
    public void SetCompositeVisible(bool visible) => Retarget(() => Array.Fill(_colorVisible, visible));

    /// <summary>A saved selection's or spot channel's eye.</summary>
    public void SetChannelVisible(int id, bool visible) => Retarget(() =>
    {
        if (visible) _visibleChannels.Add(id);
        else _visibleChannels.Remove(id);
    });

    /// <summary>The layer mask's eye (\): shows the mask as a red overlay over the image, or alone when the color channels are hidden.</summary>
    public void SetLayerMaskVisible(bool visible) => Retarget(() => _layerMaskVisible = visible && SelectedLayer?.Node.GetMask() is not null);

    /// <summary>The \ key: toggles the targeted layer mask's overlay.</summary>
    public void ToggleLayerMaskOverlay()
    {
        if (SelectedLayer?.Node.GetMask() is null)
        {
            Notice = "The selected layer has no layer mask.";
            return;
        }
        SetLayerMaskVisible(!_layerMaskVisible);
    }

    /// <summary>
    /// Option-click on a layer's mask thumbnail: shows the mask alone in gray (or goes back to the image), as Photoshop
    /// does.
    /// </summary>
    public void ToggleLayerMaskAlone() => Retarget(() =>
    {
        if (SelectedLayer?.Node.GetMask() is null) return;
        bool alone = _layerMaskVisible && !_colorVisible.Any(v => v);
        _layerMaskVisible = !alone;
        Array.Fill(_colorVisible, alone);
    });

    /// <summary>The quick mask's eye.</summary>
    public void SetQuickMaskVisible(bool visible) => Retarget(() => _quickMaskVisible = visible);

    /// <summary>
    /// ⌘2 composite, ⌘3–⌘5 red, green, blue (⌘3 is the first saved selection in a grayscale document), then the saved
    /// selections in order.
    /// </summary>
    public void TargetByShortcut(int number)
    {
        int colors = ColorChannelCount;
        if (number == 2)
        {
            TargetComposite();
            return;
        }
        if (colors > 1 && number >= 3 && number < 3 + colors)
        {
            TargetColor(number - 3);
            return;
        }
        int index = number - (colors > 1 ? 3 + colors : 3);
        if (index >= 0 && index < Channels.Count) TargetChannel(Channels[index].Id);
    }

    /// <summary>The shortcut shown on a row (⌘2 …), or "" when it has none.</summary>
    public string ShortcutFor(int colorIndex = -1, int channelIndex = -1)
    {
        int colors = ColorChannelCount;
        int number = colorIndex >= 0 ? (colors > 1 ? 3 + colorIndex : 2) : channelIndex >= 0 ? (colors > 1 ? 3 + colors : 3) + channelIndex : 2;
        return number <= 9 ? $"⌘{number}" : "";
    }

    // ---- Channel commands ------------------------------------------------------------------------------------

    private int NextChannelId() => Channels.Select(c => c.Id).Append(0).Max() + 1;

    private string NextChannelName(string stem)
    {
        var names = Channels.Select(c => c.Name).ToHashSet();
        for (int i = 1; ; i++)
            if (!names.Contains($"{stem} {i}")) return $"{stem} {i}";
    }

    /// <summary>The name the next new saved selection gets ("Alpha 1", "Alpha 2", ...).</summary>
    public string SuggestedChannelName => NextChannelName("Alpha");

    /// <summary>The name the next new spot channel gets.</summary>
    public string SuggestedSpotName => NextChannelName("Spot Color");

    private bool CanEditChannels()
    {
        if (Model.ColorMode is ColorMode.Rgb or ColorMode.Grayscale) return true;
        Notice = $"Channels in {Model.ColorMode} documents are not supported yet.";
        return false;
    }

    private void SetChannels(IReadOnlyList<DocumentChannel> channels, string description)
    {
        Apply(ChannelsEdit.To(Model, channels, description));
        _visibleChannels.RemoveWhere(id => channels.All(c => c.Id != id));
        if (_targetChannel != 0 && channels.All(c => c.Id != _targetChannel))
        {
            _targetChannel = 0;
            if (!_colorVisible.Any(v => v)) ShowAllColors();
        }
        ChannelsChanged?.Invoke();
        RequestRender();
    }

    /// <summary>
    /// The panel's New Channel button and New Channel…: a saved selection with nothing selected (black for masked areas,
    /// white for selected areas), targeted and shown alone, as in Photoshop.
    /// </summary>
    public DocumentChannel? NewChannel(string? name = null, ChannelKind kind = ChannelKind.MaskedAreas, RgbColor? color = null, float opacity = 0.5f)
    {
        if (!CanEditChannels()) return null;
        var plane = ChannelSelection.Solid(Model.Width, Model.Height, Model.BitDepth, kind == ChannelKind.SelectedAreas ? 1f : 0f);
        var channel = new DocumentChannel(string.IsNullOrWhiteSpace(name) ? SuggestedChannelName : name.Trim(), plane, kind)
        {
            Id = NextChannelId(),
            Color = color ?? new RgbColor(1f, 0f, 0f),
            Opacity = Math.Clamp(opacity, 0f, 1f),
        };
        SetChannels([.. Channels, channel], "New Channel");
        TargetChannel(channel.Id);
        return channel;
    }

    /// <summary>
    /// New Spot Channel…: an ink plate named after the ink; the selected area is filled with full ink (nothing when there
    /// is no selection). It shows over the image, printed in its color.
    /// </summary>
    public DocumentChannel? NewSpotChannel(string? name, RgbColor color, float solidity)
    {
        if (!CanEditChannels()) return null;
        var plane = Selection is { } s
            ? ChannelSelection.FromSelection(s, Model.Bounds, Model.BitDepth, invert: true)
            : ChannelSelection.Solid(Model.Width, Model.Height, Model.BitDepth, 1f);
        var channel = new DocumentChannel(string.IsNullOrWhiteSpace(name) ? SuggestedSpotName : name.Trim(), plane, ChannelKind.Spot)
        {
            Id = NextChannelId(),
            Color = color,
            Opacity = Math.Clamp(solidity, 0f, 1f),
        };
        SetChannels([.. Channels, channel], "New Spot Channel");
        Retarget(() =>
        {
            _targetChannel = channel.Id;
            _visibleChannels.Add(channel.Id);
            Array.Fill(_colorVisible, true);
        });
        return channel;
    }

    /// <summary>
    /// Select › Save Selection… and the panel's Save Selection button: into a new channel (<paramref name="intoId"/> 0), or
    /// combined with an existing one by <paramref name="mode"/> (replace, add, subtract, intersect).
    /// </summary>
    public DocumentChannel? SaveSelection(int intoId = 0, SelectionMode mode = SelectionMode.Replace, string? name = null)
    {
        if (!CanEditChannels()) return null;
        if (Selection is not { } selection)
        {
            Notice = "Make a selection first: Save Selection stores the selected area in a channel.";
            return null;
        }
        var channels = Channels;
        if (intoId != 0 && channels.FirstOrDefault(c => c.Id == intoId) is { IsSpot: false } existing)
        {
            var plane = ChannelSelection.Combine(existing.Pixels, selection, mode, Model.Bounds, existing.InvertsSelection);
            var updated = existing with { Pixels = plane };
            SetChannels(channels.Select(c => c.Id == intoId ? updated : c).ToList(), "Save Selection");
            return updated;
        }
        var channel = new DocumentChannel(string.IsNullOrWhiteSpace(name) ? SuggestedChannelName : name.Trim(),
            ChannelSelection.FromSelection(selection, Model.Bounds, Model.BitDepth), ChannelKind.MaskedAreas)
        { Id = NextChannelId() };
        SetChannels([.. channels, channel], "Save Selection");
        return channel;
    }

    /// <summary>
    /// Select › Load Selection… and ⌘-clicking a channel's thumbnail: the channel's selected area becomes the selection,
    /// optionally inverted and combined with the current one.
    /// </summary>
    public void LoadSelection(int id, bool invert = false, SelectionMode mode = SelectionMode.Replace)
    {
        if (Channels.FirstOrDefault(c => c.Id == id) is not { } channel) return;
        var loaded = channel.IsSpot
            ? ChannelSelection.ToSelection(channel.Pixels, Model.Bounds, invert: !invert) // the ink is what a spot channel selects
            : ChannelSelection.ToSelection(channel, Model.Bounds, invert);
        SetSelection(SelectionMask.Combine(Selection, loaded, mode), "Load Selection");
    }

    /// <summary>⌘-clicking a color channel or the composite: its brightness becomes the selection (the composite's luminosity).</summary>
    public void LoadColorChannelAsSelection(int k, SelectionMode mode = SelectionMode.Replace)
    {
        if (LastFullRender is not { } rgba || rgba.Length != Model.Width * Model.Height * 4)
        {
            Notice = "Wait for the document to finish rendering.";
            return;
        }
        int n = Model.Width * Model.Height;
        var coverage = new byte[n];
        Parallel.For(0, Model.Height, y =>
        {
            for (int i = y * Model.Width, end = i + Model.Width; i < end; i++)
            {
                float a = rgba[i * 4 + 3] / 255f;
                float r = rgba[i * 4] / 255f * a + 1 - a, g = rgba[i * 4 + 1] / 255f * a + 1 - a, b = rgba[i * 4 + 2] / 255f * a + 1 - a;
                float v = k switch { 0 => r, 1 => g, 2 => b, _ => 0.299f * r + 0.587f * g + 0.114f * b };
                coverage[i] = (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
            }
        });
        SetSelection(SelectionMask.Combine(Selection, SelectionMask.FromCoverage(Model.Bounds, coverage, Model.Bounds), mode), "Load Selection");
    }

    /// <summary>⌘-clicking the layer mask row: the mask's revealed area becomes the selection.</summary>
    public void LoadLayerMaskAsSelection(SelectionMode mode = SelectionMode.Replace)
    {
        if (SelectedLayer?.Node.GetMask() is not { } mask) return;
        SetSelection(SelectionMask.Combine(Selection, SelectionLayerMask.ToSelection(mask, Model.Bounds), mode), "Load Selection");
    }

    /// <summary>Duplicate Channel…: a copy after the original, named "<name> copy" unless given a name.</summary>
    public DocumentChannel? DuplicateChannel(int id, string? name = null)
    {
        if (Channels.FirstOrDefault(c => c.Id == id) is not { } channel) return null;
        var copy = channel with
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"{channel.Name} copy" : name.Trim(),
            Id = NextChannelId(),
            Pixels = new Plane(channel.Pixels.Width, channel.Pixels.Height, channel.Pixels.BitDepth, (byte[])channel.Pixels.Data.Clone()),
        };
        var list = Channels.ToList();
        list.Insert(list.IndexOf(channel) + 1, copy);
        SetChannels(list, "Duplicate Channel");
        TargetChannel(copy.Id);
        return copy;
    }

    /// <summary>Delete Channel.</summary>
    public void DeleteChannel(int id)
    {
        if (Channels.FirstOrDefault(c => c.Id == id) is not { } channel) return;
        SetChannels(Channels.Where(c => c.Id != id).ToList(), "Delete Channel");
        if (!_colorVisible.Any(v => v)) Retarget(ShowAllColors);
        Notice = $"Deleted \"{channel.Name}\".";
    }

    /// <summary>
    /// Channel Options…: name, what the color marks (switching between masked and selected areas inverts the channel so
    /// it still holds the same selection), spot color, and the color and opacity (solidity for a spot channel).
    /// </summary>
    public void SetChannelOptions(int id, string name, ChannelKind kind, RgbColor color, float opacity)
    {
        if (Channels.FirstOrDefault(c => c.Id == id) is not { } channel) return;
        var pixels = channel.Pixels;
        bool flip = channel.Kind != ChannelKind.Spot && kind != ChannelKind.Spot && channel.Kind != kind;
        if (flip) pixels = ChannelSelection.Invert(pixels);
        var updated = channel with
        {
            Name = string.IsNullOrWhiteSpace(name) ? channel.Name : name.Trim(),
            Kind = kind,
            Color = color,
            Opacity = Math.Clamp(opacity, 0f, 1f),
            Pixels = pixels,
            SourceColor = color == channel.Color ? channel.SourceColor : null,
        };
        if (updated == channel) return;
        SetChannels(Channels.Select(c => c.Id == id ? updated : c).ToList(), "Channel Options");
    }

    /// <summary>Renames a channel (double-clicking its name).</summary>
    public void RenameChannel(int id, string name)
    {
        if (Channels.FirstOrDefault(c => c.Id == id) is not { } channel || string.IsNullOrWhiteSpace(name) || channel.Name == name.Trim()) return;
        SetChannels(Channels.Select(c => c.Id == id ? c with { Name = name.Trim() } : c).ToList(), "Rename Channel");
    }

    /// <summary>Moves a channel up or down the list.</summary>
    public void MoveChannel(int id, int direction)
    {
        var list = Channels.ToList();
        int i = list.FindIndex(c => c.Id == id), j = i + direction;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        SetChannels(list, "Channel Order");
    }

    // ---- Quick Mask ------------------------------------------------------------------------------------------

    private void SetQuickMask(QuickMaskState state)
    {
        // Entering and leaving swap the selection; painting in the quick mask leaves any selection made meanwhile.
        if (state.Active != _quickMask.Active) Selection = state.Selection;
        _quickMask = state;
        if (state.Active) _quickMaskVisible = true;
        ChannelsChanged?.Invoke();
        OnPropertyChanged(nameof(IsQuickMask));
    }

    /// <summary>
    /// Q: enters Quick Mask mode (the selection becomes a red overlay to paint: black masks, white selects; no selection
    /// gives an empty mask), or leaves it, turning the mask back into the selection. Each is one undo step.
    /// </summary>
    public void ToggleQuickMask()
    {
        if (!CanEditChannels()) return;
        var options = QuickMaskSettings;
        if (!_quickMask.Active)
        {
            var plane = ChannelSelection.FromSelection(Selection, Model.Bounds, Model.BitDepth, invert: options.SelectedAreas);
            _quickMaskTargeted = true;
            _targetChannel = 0;
            if (EditMask)
            {
                _retargeting = true;
                EditMask = false;
                _retargeting = false;
            }
            Apply(new QuickMaskEdit(_quickMask with { Selection = Selection }, new QuickMaskState(true, plane, null), SetQuickMask, "Quick Mask"));
            return;
        }
        var mask = _quickMask.Plane!;
        SelectionMask? selection = mask.Width == Model.Width && mask.Height == Model.Height
            ? ChannelSelection.ToSelection(mask, Model.Bounds, invert: options.SelectedAreas)
            : Selection;
        // A fully selected mask means no selection, as everything is editable either way.
        if (selection is { IsRectangular: true } s && s.Bounds == Model.Bounds) selection = null;
        Apply(new QuickMaskEdit(_quickMask with { Selection = Selection }, new QuickMaskState(false, null, selection), SetQuickMask, "Exit Quick Mask"));
    }

    /// <summary>
    /// Quick Mask Options: switching between masked and selected areas inverts the mask being edited, so it keeps
    /// meaning the same selection.
    /// </summary>
    public void ApplyQuickMaskOptions(QuickMaskOptions before, QuickMaskOptions after)
    {
        if (_quickMask is { Active: true, Plane: { } plane } && before.SelectedAreas != after.SelectedAreas)
            Apply(new QuickMaskEdit(_quickMask, _quickMask with { Plane = ChannelSelection.Invert(plane) }, SetQuickMask, "Quick Mask Options"));
        else RequestRender();
        ChannelsChanged?.Invoke();
    }

    // ---- Editing a targeted channel -----------------------------------------------------------------------------

    /// <summary>The plane of the targeted saved selection or quick mask, or null when editing acts on the layer.</summary>
    private Plane? TargetPlane() => ChannelTarget switch
    {
        ChannelTargetKind.Channel => TargetedChannel!.Pixels,
        ChannelTargetKind.QuickMask => _quickMask.Plane is { } q && q.Width == Model.Width && q.Height == Model.Height ? q : null,
        _ => null,
    };

    /// <summary>
    /// When a saved selection or the quick mask is targeted: the stand-in layer carrying it as a mask, for the mask
    /// tools (brush, eraser, gradient, filters) to paint. Null otherwise.
    /// </summary>
    internal LayerNode? ChannelMaskOwner()
    {
        if (TargetPlane() is not { } plane) return null;
        _channelOwner.Mask = ChannelSelection.AsMask(plane);
        return _channelOwner;
    }

    /// <summary>When a saved selection or the quick mask is targeted: a gray stand-in layer holding it (Fill, Clear, Stroke).</summary>
    private PixelLayer? ChannelPixelLayer()
    {
        if (TargetPlane() is not { } plane) return null;
        _channelLayer.Pixels = new Raster(ColorMode.Grayscale, [plane], null);
        _channelLayer.Bounds = Model.Bounds;
        return _channelLayer;
    }

    /// <summary>The document a stand-in edit is computed for: gray for a channel, the document itself otherwise.</summary>
    private Core.Document EditDocumentFor(PixelLayer layer) =>
        ReferenceEquals(layer, _channelLayer) ? _channelDocument ??= new Core.Document(Model.Width, Model.Height, ColorMode.Grayscale, Model.BitDepth) : Model;

    private Core.Document? _channelDocument;

    /// <summary>A notice for tools that do not paint channels yet; true when a channel is targeted.</summary>
    internal bool BlockedByChannelTarget(string tool)
    {
        if (ChannelTarget is not (ChannelTargetKind.Channel or ChannelTargetKind.QuickMask)) return false;
        Notice = $"The {tool} does not paint channels yet. Use the Brush, Eraser, Gradient, Fill or a filter, or click the composite channel.";
        return true;
    }

    /// <summary>
    /// Turns an edit made through a stand-in into a channel edit, and narrows a layer pixel edit to the targeted color
    /// channels. Called by <see cref="Apply"/> for every edit.
    /// </summary>
    private IEdit RouteChannelEdit(IEdit edit)
    {
        switch (edit)
        {
            case MaskEdit me when ReferenceEquals(me.Node, _channelOwner):
                return ChannelPlaneEdit(me.Mask is null ? null : ChannelSelection.FromMask(me.Mask, Model.Width, Model.Height, Model.BitDepth), me.Description) ?? edit;
            case PixelsEdit pe when ReferenceEquals(pe.Layer, _channelLayer):
            {
                if (TargetPlane() is not { } old) return edit;
                var (kept, _) = ChannelRestriction.Keep(new Raster(ColorMode.Grayscale, [old], null), Model.Bounds, pe.Pixels, pe.Bounds, [true]);
                return ChannelPlaneEdit(kept?.ColorPlanes[0], pe.Description) ?? edit;
            }
            case PixelsEdit pe when RestrictsColorChannels && pe.Layer.Parent is not null:
            {
                var (pixels, bounds) = ChannelRestriction.Keep(pe.Layer.Pixels, pe.Layer.Bounds, pe.Pixels, pe.Bounds, _colorTargeted);
                return new PixelsEdit(pe.Layer, pixels, bounds, pe.Description);
            }
        }
        return edit;
    }

    private IEdit? ChannelPlaneEdit(Plane? plane, string description)
    {
        if (plane is null) return null;
        switch (ChannelTarget)
        {
            case ChannelTargetKind.QuickMask:
                return new QuickMaskEdit(_quickMask, _quickMask with { Plane = plane }, SetQuickMask, description);
            case ChannelTargetKind.Channel when TargetedChannel is { } channel:
            {
                var updated = channel with { Pixels = plane };
                return ChannelsEdit.To(Model, Channels.Select(c => c.Id == channel.Id ? updated : c).ToList(), description);
            }
            default:
                return null;
        }
    }

    // ---- Drawing the view ----------------------------------------------------------------------------------------

    /// <summary>The last full-resolution image as the Channels panel's view shows it (for the self-test), or null.</summary>
    internal byte[]? ChannelViewOfLastRender()
    {
        if (LastFullRender is not { } rgba) return null;
        var copy = (byte[])rgba.Clone();
        PrepareChannelView(1)?.Invoke(copy, Model.Width, Model.Height, CancellationToken.None);
        return copy;
    }

    private static readonly ConditionalWeakTable<Plane, Dictionary<int, Plane>> Reduced = new();

    /// <summary>A channel plane averaged down by <paramref name="factor"/> (cached), for previews at a reduced scale.</summary>
    private static Plane Reduce(Plane plane, int factor)
    {
        if (factor == 1) return plane;
        var cache = Reduced.GetValue(plane, _ => []);
        lock (cache)
            if (cache.TryGetValue(factor, out var hit)) return hit;
        int w = Math.Max(1, (plane.Width + factor - 1) / factor), h = Math.Max(1, (plane.Height + factor - 1) / factor);
        var o = Plane.Create(w, h, plane.BitDepth);
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float sum = 0;
                int n = 0;
                for (int sy = y * factor; sy < Math.Min(plane.Height, (y + 1) * factor); sy++)
                    for (int sx = x * factor; sx < Math.Min(plane.Width, (x + 1) * factor); sx++, n++)
                        sum += plane.GetNormalized(sy * plane.Width + sx);
                float v = n == 0 ? 0 : sum / n;
                switch (o.BitDepth)
                {
                    case 8: o.Data[y * w + x] = (byte)MathF.Round(v * 255f); break;
                    case 16: o.AsUInt16()[y * w + x] = (ushort)MathF.Round(v * 65535f); break;
                    default: o.AsSingle()[y * w + x] = v; break;
                }
            }
        });
        lock (cache) cache[factor] = o;
        return o;
    }

    /// <summary>
    /// Called by a render lane on the UI thread: the Channels panel's view to apply to the rendered image on the render
    /// thread (RGBA, the view's size), or null when the view is simply the image. A channel being painted shows the
    /// stroke, gradient or filter live.
    /// </summary>
    private Action<byte[], int, int, CancellationToken>? PrepareChannelView(int factor)
    {
        var sources = new List<(Func<CancellationToken, IChannelSampler> Sampler, RgbColor Color, float Opacity, ChannelOverlayKind Kind)>();
        var stroke = _stroke;
        var gradient = _gradient;
        var filter = _filterSession;
        int depth = Model.BitDepth;
        var canvas = PixelRect.FromSize(Math.Max(1, (Model.Width + factor - 1) / factor), Math.Max(1, (Model.Height + factor - 1) / factor));

        // A channel being edited through the stand-in: live stroke, gradient or filter on its plane.
        Func<CancellationToken, IChannelSampler> Edited(Plane plane, bool targeted)
        {
            IChannelSampler basic = new PlaneSampler(plane, factor);
            if (!targeted) return _ => basic;
            if (stroke is { TargetsMask: true } s && ReferenceEquals(s.Owner, _channelOwner)) return _ => new StrokeSampler(basic, s, factor);
            if (gradient is { Spec.IsDegenerate: false } g && ReferenceEquals(g.Owner, _channelOwner))
            {
                var spec = factor == 1 ? g.Spec : g.Spec.Scaled(1f / factor);
                var selection = g.Selection;
                return cancel =>
                {
                    var mask = GradientPainter.ApplyToMask(ChannelSelection.AsMask(Reduce(plane, factor)), spec, selection, canvas, depth, factor, cancel);
                    return new ViewPlaneSampler(ChannelSelection.FromMask(mask, canvas.Width, canvas.Height, depth));
                };
            }
            if (filter is { Filter: { } f } fs && ReferenceEquals(fs.Target.Owner, _channelOwner))
            {
                var scaled = factor == 1 ? f : f.Scaled(1.0 / factor);
                var scope = new FilterScope(canvas) { Selection = fs.Target.Selection, Factor = factor };
                return cancel =>
                {
                    var mask = FilterEngine.ApplyToMask(ChannelSelection.AsMask(Reduce(plane, factor)), scaled, scope, depth, cancel);
                    return new ViewPlaneSampler(ChannelSelection.FromMask(mask, canvas.Width, canvas.Height, depth));
                };
            }
            return _ => basic;
        }

        var target = ChannelTarget;
        var channels = Channels;
        // The targeted channel first, so it is the one shown in gray when the color channels are hidden.
        foreach (var c in channels.OrderByDescending(c => target == ChannelTargetKind.Channel && c.Id == _targetChannel))
        {
            if (!_visibleChannels.Contains(c.Id) || c.Pixels.Width != Model.Width || c.Pixels.Height != Model.Height) continue;
            sources.Add((Edited(c.Pixels, target == ChannelTargetKind.Channel && c.Id == _targetChannel), c.Color, c.Opacity,
                c.IsSpot ? ChannelOverlayKind.Spot : ChannelOverlayKind.Mask));
        }
        if (_quickMask is { Active: true, Plane: { } qm } && _quickMaskVisible && qm.Width == Model.Width && qm.Height == Model.Height)
        {
            var o = QuickMaskSettings;
            sources.Insert(target == ChannelTargetKind.QuickMask ? 0 : sources.Count, (Edited(qm, target == ChannelTargetKind.QuickMask), o.Color, o.Opacity, ChannelOverlayKind.Mask));
        }
        if (_layerMaskVisible && SelectedLayer?.Node is { } owner && owner.GetMask() is { } layerMask)
        {
            IChannelSampler maskSampler = new MaskSampler(layerMask, factor);
            if (stroke is { TargetsMask: true } s && ReferenceEquals(s.Owner, owner)) maskSampler = new StrokeSampler(maskSampler, s, factor);
            sources.Insert(target == ChannelTargetKind.LayerMask ? 0 : sources.Count, (_ => maskSampler, new RgbColor(1f, 0f, 0f), 0.5f, ChannelOverlayKind.Mask));
        }

        var colors = _colorVisible.Length == 0 ? [true] : (bool[])_colorVisible.Clone();
        if (sources.Count == 0 && colors.All(v => v)) return null;
        return (rgba, width, height, cancel) =>
        {
            var overlays = sources.Select(s => new ChannelOverlay(s.Sampler(cancel), s.Color, s.Opacity, s.Kind)).ToList();
            new ChannelView(colors, overlays).Apply(rgba, width, height, cancel);
        };
    }
}
