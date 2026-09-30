using Strayta.Core;
using Strayta.Core.Selection;
using Strayta.Psd;

namespace Strayta.Editor.Editing;

/// <summary>
/// Changes the document's saved selections and spot channels. They live in the <see cref="PsdFile"/> the document came
/// from (a stand-in for other documents), like saved paths, so the edit swaps that file: undo puts the old one back,
/// untouched channels keep their bytes, and crops and resizes remap them with everything else the file keeps.
/// </summary>
public sealed class ChannelsEdit(Document doc, object? before, object after, string description) : IEdit
{
    public string Description => description;
    public bool ChangesStructure => false;

    public void Do() => doc.SourceData = after;
    public void Undo() => doc.SourceData = before;

    /// <summary>The document's saved selections and spot channels, in the Channels panel's order.</summary>
    public static IReadOnlyList<DocumentChannel> Read(Document doc) =>
        doc.SourceData is PsdFile file ? PsdChannels.Read(file) : [];

    /// <summary>The edit that sets the document's channels to <paramref name="channels"/>.</summary>
    public static ChannelsEdit To(Document doc, IReadOnlyList<DocumentChannel> channels, string description)
    {
        var file = doc.SourceData as PsdFile ?? PsdPathResources.Empty(doc);
        return new ChannelsEdit(doc, doc.SourceData, PsdChannels.WithChannels(file, channels), description);
    }
}

/// <summary>Quick Mask mode's state: whether it is on, the mask being edited (white selected), and the selection.</summary>
public sealed record QuickMaskState(bool Active, Plane? Plane, SelectionMask? Selection);

/// <summary>
/// Entering or leaving Quick Mask mode, or painting in the quick mask. Like selections, the quick mask is not saved in
/// the file, so these steps do not mark the document as edited.
/// </summary>
public sealed class QuickMaskEdit(QuickMaskState before, QuickMaskState after, Action<QuickMaskState> set, string description) : IEdit
{
    public string Description => description;
    public bool ChangesStructure => false;
    public bool ChangesContent => false;

    public void Do() => set(after);
    public void Undo() => set(before);
}
