using Avalonia.Media;
using Strayta.Core;

namespace Strayta.Editor.ViewModels;

// The layer workflow's parts of a Layers panel row: selection among several, locks, links, the color label and the
// thumbnail size chosen in the panel options.
public sealed partial class LayerItemViewModel
{
    /// <summary>True when this layer is one of the selected layers (the row is highlighted).</summary>
    public bool IsSelected => _document.IsNodeSelected(Node);

    internal void RefreshSelection()
    {
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(IsLinkedToSelection));
    }

    // ---- Locks (the header's buttons act on every selected layer) -------------------------------------------

    public bool LockPixels
    {
        get => (Node.Locks & (LayerLocks.Pixels | LayerLocks.All)) != 0;
        set => _document.SetLock(LayerLocks.Pixels, value);
    }

    public bool LockPosition
    {
        get => (Node.Locks & (LayerLocks.Position | LayerLocks.All)) != 0;
        set => _document.SetLock(LayerLocks.Position, value);
    }

    public bool LockAll
    {
        get => (Node.Locks & LayerLocks.All) != 0;
        set => _document.SetLock(LayerLocks.All, value);
    }

    /// <summary>With Lock All on, the other lock buttons show as on and cannot be changed, as in Photoshop.</summary>
    public bool CanChangeLocks => !LockAll;

    /// <summary>Lock All also freezes blend mode, opacity and fill.</summary>
    public bool CanChangeBlending => !Node.IsLocked(LayerLocks.All);

    /// <summary>The row's lock is drawn solid for Lock All and hollow for the others, as in Photoshop.</summary>
    public bool IsFullyLocked => LockAll;

    public string LockTip
    {
        get
        {
            if (LockAll) return "Locked: all";
            var parts = new List<string>();
            if ((Node.Locks & LayerLocks.Transparency) != 0) parts.Add("transparent pixels");
            if ((Node.Locks & LayerLocks.Pixels) != 0) parts.Add("image pixels");
            if ((Node.Locks & LayerLocks.Position) != 0) parts.Add("position");
            if ((Node.Locks & LayerLocks.ArtboardNesting) != 0) parts.Add("artboard nesting");
            return "Locked: " + string.Join(", ", parts);
        }
    }

    // ---- Links --------------------------------------------------------------------------------------------

    public bool IsLinked => Node.LinkGroup != 0;

    /// <summary>The link icon is highlighted on layers linked to the primary selected layer, as in Photoshop.</summary>
    public bool IsLinkedToSelection => IsLinked && _document.SelectedLayer?.Node is { } p && !ReferenceEquals(p, Node) && p.LinkGroup == Node.LinkGroup;

    // ---- Color label ---------------------------------------------------------------------------------------

    public LayerColor ColorLabel => Node.Color;

    /// <summary>
    /// The color behind the eye: the layer's own label, or its group's when it has none (a labeled group colors what it
    /// holds, as in Photoshop). Null for none.
    /// </summary>
    public IBrush? ColorBrush
    {
        get
        {
            for (LayerNode? n = Node; n is not null; n = n.Parent)
                if (n.Color != LayerColor.None) return LabelBrush(n.Color);
            return null;
        }
    }

    public static IBrush? LabelBrush(LayerColor color) => color switch
    {
        LayerColor.Red => Brush(0xE5, 0x48, 0x4D),
        LayerColor.Orange => Brush(0xF0, 0x8C, 0x2E),
        LayerColor.Yellow => Brush(0xE8, 0xC5, 0x2A),
        LayerColor.Green => Brush(0x46, 0xA7, 0x58),
        LayerColor.Blue => Brush(0x3E, 0x7B, 0xFA),
        LayerColor.Violet => Brush(0x8E, 0x4E, 0xC6),
        LayerColor.Gray => Brush(0x8B, 0x8D, 0x98),
        _ => null,
    };

    private static readonly Dictionary<uint, IBrush> Brushes = [];

    private static IBrush Brush(byte r, byte g, byte b)
    {
        uint key = (uint)(r << 16 | g << 8 | b);
        if (!Brushes.TryGetValue(key, out var brush)) Brushes[key] = brush = new SolidColorBrush(Color.FromArgb(0x99, r, g, b));
        return brush;
    }

    // ---- Thumbnail size (panel options) ---------------------------------------------------------------------

    private LayerThumbnailSize ThumbSize => _document.Editor.LayerThumbnailSize;

    public bool ShowThumbnails => ThumbSize != LayerThumbnailSize.None;

    public double ThumbWidth => ThumbSize switch { LayerThumbnailSize.Small => 22, LayerThumbnailSize.Large => 60, _ => 34 };

    public double ThumbHeight => ThumbSize switch { LayerThumbnailSize.Small => 18, LayerThumbnailSize.Large => 46, _ => 27 };

    public double RowHeight => ThumbSize switch { LayerThumbnailSize.None => 26, LayerThumbnailSize.Small => 26, LayerThumbnailSize.Large => 54, _ => 34 };

    internal void RefreshPanelLayout()
    {
        OnPropertyChanged(nameof(ShowThumbnails));
        OnPropertyChanged(nameof(ThumbWidth));
        OnPropertyChanged(nameof(ThumbHeight));
        OnPropertyChanged(nameof(RowHeight));
    }
}
