using Strayta.Core;
using Strayta.Core.Text;
using Strayta.Editor.Editing;
using Strayta.Text;

namespace Strayta.Editor.ViewModels;

/// <summary>Type layers: missing-font warnings and text edits through the text engine (see <see cref="TypeLayers"/>).</summary>
public sealed partial class DocumentViewModel
{
    /// <summary>Fonts the document's type layers use that are not installed (known once <see cref="CheckFontsAsync"/> ran).</summary>
    public IReadOnlyList<string> MissingFonts { get; private set; } = [];

    /// <summary>
    /// Looks for fonts the type layers need that are not installed and, if there are any, says so in the notice:
    /// those layers keep showing Photoshop's pixels, and editing them would draw with a substitute while the file keeps
    /// the original font name (as Photoshop does).
    /// </summary>
    public async Task CheckFontsAsync()
    {
        if (!Model.Root.Descendants().Any(n => n.Tags.Contains("text"))) return;
        var missing = await Task.Run(() => TypeLayers.MissingFonts(Model));
        MissingFonts = missing;
        if (missing.Count > 0) Notice = MissingFontsNotice(missing);
        HasMissingFontsBanner = missing.Count > 0;
        OnPropertyChanged(nameof(MissingFontsMessage));
    }

    /// <summary>The banner over the canvas naming the missing fonts (dismissable; the status bar keeps the notice).</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] public partial bool HasMissingFontsBanner { get; private set; }

    public string MissingFontsMessage => MissingFonts.Count == 0 ? "" :
        $"This document uses font{(MissingFonts.Count == 1 ? "" : "s")} that {(MissingFonts.Count == 1 ? "is" : "are")} not installed: {string.Join(", ", MissingFonts)}. "
        + "Type using them shows the stored pixels and keeps the font names when saved; editing it with the Type tool asks for a replacement.";

    public void DismissMissingFonts() => HasMissingFontsBanner = false;

    private static string MissingFontsNotice(IReadOnlyList<string> missing) =>
        $"Missing font{(missing.Count == 1 ? "" : "s")}: {string.Join(", ", missing)}. Type using {(missing.Count == 1 ? "it" : "them")} shows "
        + $"the stored pixels; editing it would substitute {FontCatalog.System.FallbackPostScriptName ?? "another font"}.";

    /// <summary>Gives a type layer new text (one undo step) and redraws it. Returns the fonts it had to substitute.</summary>
    public IReadOnlyList<string> EditText(PixelLayer layer, TextLayerData data)
    {
        var edit = TypeLayers.Edit(Model, layer, data, out var render);
        Apply(edit);
        if (render.MissingFonts.Count > 0) Notice = MissingFontsNotice(render.MissingFonts);
        foreach (var w in render.Layout.Warnings) Notice = w;
        return render.MissingFonts;
    }

    /// <summary>Adds a new type layer above the selected layer (or on top) and selects it.</summary>
    public PixelLayer AddTextLayer(TextLayerData data)
    {
        var layer = TypeLayers.Create(Model, data, out var render);
        var anchor = SelectedLayer?.Node;
        var parent = anchor?.Parent ?? Model.Root;
        int index = anchor is null ? parent.Children.Count : parent.IndexOf(anchor) + 1;
        Apply(new InsertEdit(layer, parent, index, "New Type Layer"));
        SelectedLayer = Layers.SelectMany(l => l.SelfAndDescendants()).FirstOrDefault(i => i.Node == layer);
        if (render.MissingFonts.Count > 0) Notice = MissingFontsNotice(render.MissingFonts);
        return layer;
    }
}
