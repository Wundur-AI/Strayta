using Strayta.Core;
using Strayta.Core.Paths;

namespace Strayta.Psd;

/// <summary>
/// The document's paths as image resources: the work path (1025, no name) and saved paths (2000–2997, named by the
/// resource's Pascal-string name), each a list of path records (<see cref="PsdPaths"/>) in fractions of the canvas.
/// The clipping path's name (2999) is left as it is. Paths live in the <see cref="PsdFile"/> the document came from,
/// so crops and resizes remap them along with everything else the file keeps.
/// </summary>
public static class PsdPathResources
{
    public const int WorkPathId = 1025, FirstSavedId = 2000, LastSavedId = 2997;

    /// <summary>The work path (first, when there is one) and the saved paths, in resource order.</summary>
    public static List<DocumentPath> Read(PsdFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var (w, h) = (file.Header.Width, file.Header.Height);
        var list = new List<DocumentPath>();
        foreach (var r in file.Resources)
        {
            if (r.Id is not (WorkPathId or (>= FirstSavedId and <= LastSavedId))) continue;
            var path = PsdPaths.Decode(r.Data, 0, w, h);
            var kind = r.Id == WorkPathId ? DocumentPathKind.Work : DocumentPathKind.Saved;
            var p = new DocumentPath(kind == DocumentPathKind.Work ? "Work Path" : r.Name, path, kind, r.Id) { SourceData = r };
            if (kind == DocumentPathKind.Work) list.Insert(0, p);
            else list.Add(p);
        }
        return list;
    }

    /// <summary>
    /// <paramref name="file"/> with its path resources replaced by <paramref name="paths"/>: unchanged paths keep their
    /// resource bytes; new saved paths take the next free ID from 2000. Other resources keep their order.
    /// </summary>
    public static PsdFile WithPaths(PsdFile file, IReadOnlyList<DocumentPath> paths)
    {
        ArgumentNullException.ThrowIfNull(file);
        var (w, h) = (file.Header.Width, file.Header.Height);
        var used = paths.Where(p => p.Kind == DocumentPathKind.Saved && p.Id is >= FirstSavedId and <= LastSavedId).Select(p => p.Id).ToHashSet();
        int next = FirstSavedId;
        var encoded = new List<ImageResource>();
        foreach (var p in paths)
        {
            int id = p.Kind == DocumentPathKind.Work ? WorkPathId : p.Id is >= FirstSavedId and <= LastSavedId ? p.Id : NextFree();
            string name = p.Kind == DocumentPathKind.Work ? "" : p.Name;
            var data = PsdPaths.Encode(p.Path, w, h);
            if (p.SourceData is ImageResource r && r.Id == id && r.Name == name && r.Data.AsSpan().SequenceEqual(data)) encoded.Add(r);
            else encoded.Add(new ImageResource("8BIM", id, name, data));
        }
        var resources = new List<ImageResource>();
        bool placed = false;
        foreach (var r in file.Resources)
        {
            if (r.Id is WorkPathId or (>= FirstSavedId and <= LastSavedId))
            {
                // The new set goes where the old paths were.
                if (!placed) resources.AddRange(encoded);
                placed = true;
                continue;
            }
            resources.Add(r);
        }
        if (!placed) resources.AddRange(encoded);
        return file.With(file.Header, resources, file.CompositeChannels);

        int NextFree()
        {
            while (used.Contains(next)) next++;
            used.Add(next);
            return next;
        }
    }

    /// <summary>
    /// A stand-in file for a document that did not come from a PSD (a new document, an image), so paths have a place
    /// to live; the writer treats it like a file with no layers and no image data.
    /// </summary>
    public static PsdFile Empty(Document doc) => new()
    {
        Header = new PsdHeader(1, doc.ColorMode.ColorChannelCount(), doc.Width, doc.Height, doc.BitDepth, doc.ColorMode),
        Resources = [new ImageResource("8BIM", PsdResolution.ResourceId, "", PsdResolution.Write(doc.Resolution, null))],
    };
}
