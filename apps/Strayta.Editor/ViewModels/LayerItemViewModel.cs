using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;

namespace Strayta.Editor.ViewModels;

/// <summary>
/// A layer as shown in the Layers and Properties panels. Setting a property records an undoable edit on
/// the owning document instead of changing the model directly.
/// </summary>
public sealed partial class LayerItemViewModel : ObservableObject
{
    private readonly DocumentViewModel _document;
    private bool _isExpanded;

    public LayerItemViewModel(LayerNode node, DocumentViewModel document, int depth = 0)
    {
        Node = node;
        _document = document;
        Depth = depth;
        _isExpanded = node is LayerGroup { Expanded: true };
        if (node is LayerGroup g)
            foreach (var child in g.Children.Reverse()) // top of the stack first, like Photoshop
                Children.Add(new LayerItemViewModel(child, document, depth + 1));
    }

    /// <summary>Nesting level: 0 for top-level layers.</summary>
    public int Depth { get; }

    /// <summary>Left margin for the row content (the eye column stays fixed, as in Photoshop).</summary>
    public Avalonia.Thickness Indent => new(Depth * 18, 0, 0, 0);

    public bool HasMask => Node switch
    {
        PixelLayer { Mask: not null } => true,
        AdjustmentLayer { Mask: not null } => true,
        LayerGroup { Mask: not null } => true,
        _ => false,
    };

    private LayerMask? Mask => Node switch
    {
        PixelLayer p => p.Mask,
        AdjustmentLayer a => a.Mask,
        LayerGroup g => g.Mask,
        _ => null,
    };

    /// <summary>Photoshop's target frame: the selected layer's pixels are what painting edits.</summary>
    public bool IsPixelTarget => ReferenceEquals(_document.SelectedLayer, this) && !_document.EditMask;

    /// <summary>Photoshop's target frame: the selected layer's mask is what painting edits.</summary>
    public bool IsMaskTarget => ReferenceEquals(_document.SelectedLayer, this) && _document.EditMask && HasMask;

    /// <summary>Disabled masks are crossed out, as in Photoshop.</summary>
    public bool MaskDisabled => Mask is { Disabled: true };

    internal void RefreshTarget()
    {
        OnPropertyChanged(nameof(IsPixelTarget));
        OnPropertyChanged(nameof(IsMaskTarget));
    }

    /// <summary>The mask as a small grayscale preview.</summary>
    public Bitmap? MaskThumbnail => Mask is { } m
        ? Thumbnails.GetMask(m, () => OnPropertyChanged(nameof(MaskThumbnail)))
        : null;

    /// <summary>True when a containing group is hidden, which hides this layer too.</summary>
    public bool AncestorHidden
    {
        get
        {
            for (var g = Node.Parent; g is not null; g = g.Parent)
                if (!g.Visible) return true;
            return false;
        }
    }

    /// <summary>Eye icon: hidden layers show none; layers hidden by a group show a faint one, as in Photoshop.</summary>
    public double EyeOpacity => !Node.Visible ? 0 : AncestorHidden ? 0.3 : 1;

    public bool IsSmartObject => Node.Tags.Contains("smart-object");
    public bool IsText => Node.Tags.Contains("text");
    public bool IsLocked => Node is PixelLayer { TransparencyLocked: true };

    public bool TransparencyLocked
    {
        get => Node is PixelLayer { TransparencyLocked: true };
        set
        {
            if (Node is PixelLayer p) Change("Lock", p.TransparencyLocked, value, (n, v) => ((PixelLayer)n).TransparencyLocked = v);
        }
    }

    public LayerNode Node { get; }
    public ObservableCollection<LayerItemViewModel> Children { get; } = [];

    public static IReadOnlyList<BlendMode> BlendModes { get; } = Enum.GetValues<BlendMode>();

    private static readonly IReadOnlyList<BlendMode> LayerBlendModes = BlendModes.Where(m => m != BlendMode.PassThrough).ToArray();

    /// <summary>
    /// Pass Through only applies to groups. Both lists are shared instances: handing the blend-mode menu a new
    /// list on every selection change made it reset and write a mode into the newly selected layer.
    /// </summary>
    public IReadOnlyList<BlendMode> BlendModeChoices => Node is LayerGroup ? BlendModes : LayerBlendModes;

    public bool IsGroup => Node is LayerGroup;
    public bool IsAdjustment => Node is AdjustmentLayer;
    public bool HasEffects => Node.Effects is not null;

    /// <summary>A small preview of the layer's pixels; null for groups, adjustments and empty layers.</summary>
    public Bitmap? Thumbnail => Node is PixelLayer { Pixels: { } raster }
        ? Thumbnails.Get(raster, _document.Model.Palette, () => OnPropertyChanged(nameof(Thumbnail)))
        : null;

    /// <summary>Icon shown instead of a thumbnail.</summary>
    public StreamGeometry? PlaceholderIcon => Node switch
    {
        LayerGroup { Artboard: not null } => Icon("IconArtboard"), // artboards (Artboards.cs)
        LayerGroup => Icon("IconFolder"),
        AdjustmentLayer => Icon("IconAdjust"),
        _ => null,
    };

    private static StreamGeometry? Icon(string key) =>
        Avalonia.Application.Current is { } app && Avalonia.Controls.ResourceNodeExtensions.TryFindResource(app, key, out var r)
            ? r as StreamGeometry
            : null;

    public string Kind => Node switch
    {
        LayerGroup => "Group",
        AdjustmentLayer a => "Adjust",
        _ when Node.Tags.Contains("text") => "Text",
        _ when Node.Tags.Contains("smart-object") => "Smart",
        _ when Node.Tags.Contains("fill") => "Fill",
        _ when Node.Tags.Contains("shape") => "Shape",
        _ => "Pixel",
    };

    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (Node is AdjustmentLayer adj) parts.Add(adj.Kind);
            if (Node.BlendMode is not (BlendMode.Normal or BlendMode.PassThrough)) parts.Add(BlendModeNames.Of(Node.BlendMode));
            if (Node.Opacity < 1f) parts.Add($"{Node.Opacity * 100:F0}%");
            if (Node.Clipped) parts.Add("clipped");
            if (Node.Effects is not null) parts.Add("fx");
            return string.Join(" · ", parts);
        }
    }

    public bool HasUnsupported =>
        Node.Effects?.Items.Any(e => e is UnsupportedEffect) == true || Node is AdjustmentLayer { Adjustment: null }
        || SmartFilterRows.Any(r => r.IsUnknown); // LayerItemViewModel.SmartFilters.cs

    private bool _isRenaming;

    /// <summary>True while the name is being edited in place in the Layers panel.</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        set => SetProperty(ref _isRenaming, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            // Folder state is saved with the file but is not an undoable edit, as in Photoshop.
            if (!SetProperty(ref _isExpanded, value)) return;
            if (Node is LayerGroup g) g.Expanded = value;
            _document.RefreshRows();
        }
    }

    public string Name
    {
        get => Node.Name;
        set => Change(nameof(Name), Node.Name, value ?? "", (n, v) => n.Name = v);
    }

    public bool IsVisible
    {
        get => Node.Visible;
        set => Change("Visibility", Node.Visible, value, (n, v) => n.Visible = v);
    }

    /// <summary>Opacity in percent, 0..100.</summary>
    public double Opacity
    {
        get => Math.Round(Node.Opacity * 100);
        set => Change(nameof(Opacity), Node.Opacity, (float)Math.Clamp(value / 100, 0, 1), (n, v) => n.Opacity = v);
    }

    /// <summary>Fill opacity in percent, 0..100.</summary>
    public double Fill
    {
        get => Math.Round(Node.FillOpacity * 100);
        set => Change(nameof(Fill), Node.FillOpacity, (float)Math.Clamp(value / 100, 0, 1), (n, v) => n.FillOpacity = v);
    }

    public BlendMode BlendMode
    {
        get => Node.BlendMode;
        set
        {
            // Only modes this layer offers; anything else is a menu resetting while its list changes.
            if (!BlendModeChoices.Contains(value)) return;
            Change("Blend Mode", Node.BlendMode, value, (n, v) => n.BlendMode = v);
        }
    }

    private void Change<T>(string property, T before, T after, Action<LayerNode, T> set)
    {
        if (EqualityComparer<T>.Default.Equals(before, after)) return;
        _document.Apply(new PropertyEdit<T>(Node, property, before, after, set));
    }

    /// <summary>Re-reads every property from the model (after edits, undo or redo).</summary>
    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        foreach (var c in Children) c.Refresh();
    }

    public IEnumerable<LayerItemViewModel> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.SelfAndDescendants())
                yield return d;
    }
}
