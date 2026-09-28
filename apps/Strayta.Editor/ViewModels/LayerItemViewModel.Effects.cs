using Strayta.Core;

namespace Strayta.Editor.ViewModels;

/// <summary>A row under a layer in the Layers panel: "Effects" (the whole style) or one effect, with its eye.</summary>
public sealed class EffectRowViewModel(LayerItemViewModel owner, string name, int index, bool visible, bool dimmed)
{
    public LayerItemViewModel Owner { get; } = owner;
    public string Name { get; } = name;

    /// <summary>The effect's position in the layer's effects, or -1 for the "Effects" row.</summary>
    public int Index { get; } = index;

    public bool IsMaster => Index < 0;
    public bool IsVisible { get; } = visible;

    /// <summary>Effects hidden by the "Effects" eye show a faint eye, like layers inside a hidden group.</summary>
    public double EyeOpacity => !IsVisible ? 0 : dimmed ? 0.3 : 1;

    /// <summary>Effect names sit further in than "Effects", as in Photoshop.</summary>
    public Avalonia.Thickness Indent => new(Owner.Depth * 18 + (IsMaster ? 44 : 60), 0, 0, 0);
}

// The Layers panel's fx badge and the expandable list of a layer's effects.
public sealed partial class LayerItemViewModel
{
    private bool _effectsExpanded;

    /// <summary>The effect rows are open under the layer (the triangle next to the fx badge).</summary>
    public bool EffectsExpanded
    {
        get => _effectsExpanded;
        set
        {
            if (SetProperty(ref _effectsExpanded, value)) OnPropertyChanged(nameof(ShowEffectRows));
        }
    }

    public bool ShowEffectRows => EffectsExpanded && Node.Effects is not null;

    /// <summary>"Effects" and then each effect, top of the stack first, as Photoshop lists them.</summary>
    public IReadOnlyList<EffectRowViewModel> EffectRows
    {
        get
        {
            if (Node.Effects is not { } fx) return [];
            var rows = new List<EffectRowViewModel> { new(this, "Effects", -1, fx.Enabled, dimmed: false) };
            var ordered = fx.Items.Select((e, i) => (Effect: e, Index: i))
                .OrderBy(p => LayerStyleViewModel.PageOf(p.Effect)).ToList();
            foreach (var (effect, index) in ordered)
                rows.Add(new(this, effect is UnsupportedEffect u ? u.Name : LayerStyleViewModel.NameOf(LayerStyleViewModel.PageOf(effect)),
                    index, effect.Enabled, dimmed: !fx.Enabled));
            return rows;
        }
    }

    /// <summary>The eye of an effect row: one effect, or all of them for "Effects" (one undo step either way).</summary>
    public void ToggleEffect(EffectRowViewModel row)
    {
        if (row.IsMaster) _document.SetEffectsVisible(Node, !row.IsVisible);
        else _document.SetEffectVisible(Node, row.Index, !row.IsVisible);
    }
}
