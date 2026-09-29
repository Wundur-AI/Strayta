using Strayta.Core;
using Strayta.Editor.Editing;
using Strayta.Psd;
using Strayta.Psd.Descriptors;
using Strayta.Rendering;
using Strayta.Rendering.Filters;

namespace Strayta.Editor.ViewModels;

/// <summary>
/// A document opened by Edit Contents: saving it writes its content back into <see cref="Parent"/>'s smart object
/// (every layer showing <see cref="UniqueId"/>) instead of to a file.
/// </summary>
public sealed record SmartContentLink(DocumentViewModel Parent, string UniqueId, string Name, string FileType);

// Smart objects (Layer › Smart Objects): Edit Contents and saving back, Replace Contents, New Smart Object via Copy,
// Convert to Smart Object, Export Contents, Relink, and smart filters. The file data lives in the PSD records
// (Strayta.Psd.PsdSmartObjects); pixels are drawn from content by Editing/SmartObjects.cs.
public sealed partial class DocumentViewModel
{
    /// <summary>Set on a document opened by Edit Contents.</summary>
    public SmartContentLink? SmartContent { get; set; }

    /// <summary>The selected layer when it is a smart object.</summary>
    public PixelLayer? SelectedSmartObject =>
        SelectedLayer?.Node is PixelLayer layer && layer.Tags.Contains("smart-object") && layer.SourceData is PsdLayerRecord ? layer : null;

    /// <summary>The smart object layers showing the file with this unique ID (instances share content).</summary>
    public IEnumerable<PixelLayer> SmartObjectInstances(string uniqueId) =>
        Model.Root.Descendants().OfType<PixelLayer>().Where(l => SmartObjects.Read(l)?.UniqueId == uniqueId);

    /// <summary>The document's file data, created (an empty PSD shell) for documents that did not come from a PSD.</summary>
    private PsdFile FileData() => Model.SourceData as PsdFile ?? new PsdFile
    {
        Header = new PsdHeader(1, Model.ColorMode.ColorChannelCount(), Model.Width, Model.Height, Model.BitDepth, Model.ColorMode),
    };

    // ---- Edit Contents and writing back ----------------------------------------------------------------

    /// <summary>
    /// The selected smart object's content as a document for Edit Contents (title like Photoshop's "Layer.psb"), or
    /// null with a notice. Linked content returns the file's path instead, to open it as itself.
    /// </summary>
    public (Document? Content, string? LinkedPath, SmartContentLink? Link, string Title)? OpenSmartContent()
    {
        if (SelectedSmartObject is not { } layer || Model.SourceData is not PsdFile file || SmartObjects.Read(layer) is not { } so)
        {
            Notice = "Select a smart object to edit its contents.";
            return null;
        }
        if (PsdSmartObjects.FindLinkedFile(file, so.UniqueId) is not { } entry)
        {
            Notice = "The smart object's content is not in this document.";
            return null;
        }
        if (entry.Kind != "liFD")
        {
            if (SmartObjects.ExternalPath(file, entry) is { } path && File.Exists(path)) return (null, path, null, Path.GetFileName(path));
            Notice = $"The linked file \"{entry.Name}\" cannot be found. Use Relink to File to find it.";
            return null;
        }
        Document? content;
        try
        {
            content = SmartObjects.OpenContent(entry.Data!);
        }
        catch (Exception e) when (e is PsdFormatException or IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            content = null;
        }
        if (content is null)
        {
            Notice = $"Strayta cannot open the smart object's content ({entry.FileType.Trim()}).";
            return null;
        }
        string ext = Path.GetExtension(entry.Name) is { Length: > 0 } x ? x : entry.FileType == "8BPB" ? ".psb" : "";
        string title = layer.Name + ext;
        return (content, null, new SmartContentLink(this, so.UniqueId, entry.Name, entry.FileType), title);
    }

    /// <summary>
    /// Writes edited content back (Edit Contents, then Save): the embedded file is replaced (size and, for newer entries,
    /// content ID updated) and every instance is redrawn through its corners, warp and smart filters, as one undo step
    /// "Update Smart Object". A content size change keeps each instance's scale, centered where it was.
    /// </summary>
    public async Task<bool> UpdateSmartObjectAsync(string uniqueId, Document content, string name, string fileType)
    {
        if (Model.SourceData is not PsdFile file || PsdSmartObjects.FindLinkedFile(file, uniqueId) is not { } entry)
        {
            Notice = "The smart object this content came from is gone.";
            return false;
        }
        IsBusy = true;
        try
        {
            var host = Model;
            var (data, type, newName) = await Task.Run(() => SmartObjects.Encode(content, name, fileType));
            var image = await Task.Run(() =>
            {
                // The edited layers, not the composite the content was opened with.
                using var renderer = new CpuRenderer();
                return renderer.Render(content).ToRaster(content.ColorMode, content.BitDepth);
            });
            var updated = PsdSmartObjects.WithLinkedFile(file, entry.WithData(data, newName, type));
            await ApplyContentAsync(uniqueId, updated, SmartObjects.Convert(image, host), content.Width, content.Height, "Update Smart Object");
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Redraws every instance of <paramref name="uniqueId"/> from <paramref name="image"/> and records one edit.</summary>
    private async Task ApplyContentAsync(string uniqueId, PsdFile file, Raster? image, int width, int height, string description)
    {
        var host = Model;
        var canvas = host.Bounds;
        var clip = new PixelRect(canvas.Left - canvas.Width, canvas.Top - canvas.Height, canvas.Right + canvas.Width, canvas.Bottom + canvas.Height);
        var changes = new List<(PixelLayer, SmartObjectEdit.LayerState)>();
        var stale = new List<string>();
        foreach (var layer in SmartObjectInstances(uniqueId).ToList())
        {
            var record = (PsdLayerRecord)layer.SourceData!;
            var resized = PsdSmartObjects.WithContentSize(record, width, height);
            var drawn = image is null ? null : await Task.Run(() => SmartObjects.Draw(resized, file, host, clip, image));
            if (drawn is { } d) changes.Add((layer, new(d.Pixels, d.Pixels is null ? PixelRect.Empty : d.Bounds, resized, [.. layer.Tags])));
            else
            {
                changes.Add((layer, new(layer.Pixels, layer.Bounds, resized, [.. layer.Tags])));
                stale.Add(layer.Name);
            }
        }
        Apply(new SmartObjectEdit(Model, description, file, changes));
        Notice = stale.Count > 0
            ? $"\"{string.Join("\", \"", stale)}\" keeps its previous pixels: {SmartObjects.WhyNotDrawable((PsdLayerRecord)changes[0].Item2.Source!, file, host, image) ?? "it cannot be drawn"}."
            : "";
    }

    // ---- Replace Contents, Relink, Export Contents -----------------------------------------------------

    /// <summary>Layer › Smart Objects › Replace Contents…: the chosen file becomes the content, keeping transform, warp and filters.</summary>
    public async Task<bool> ReplaceContentsAsync(string path)
    {
        if (SelectedSmartObject is not { } layer || Model.SourceData is not PsdFile file || SmartObjects.Read(layer) is not { } so
            || PsdSmartObjects.FindLinkedFile(file, so.UniqueId) is not { } entry) return false;
        byte[] data = await File.ReadAllBytesAsync(path);
        var host = Model;
        var image = await Task.Run(() => SmartObjects.Decode(data, host));
        if (image is null)
        {
            Notice = $"Strayta cannot read {Path.GetFileName(path)} as smart object content.";
            return false;
        }
        string name = Path.GetFileName(path);
        var updated = PsdSmartObjects.WithLinkedFile(file, entry.WithData(data, name, PsdSmartObjects.FileTypeFor(name)));
        await ApplyContentAsync(so.UniqueId, updated, image, image.Width, image.Height, "Replace Contents");
        return true;
    }

    /// <summary>Layer › Smart Objects › Relink to File…: a linked smart object shows another file.</summary>
    public async Task<bool> RelinkAsync(string path)
    {
        if (SelectedSmartObject is not { } layer || Model.SourceData is not PsdFile file || SmartObjects.Read(layer) is not { } so
            || PsdSmartObjects.FindLinkedFile(file, so.UniqueId) is not { Kind: not "liFD" } entry)
        {
            Notice = "Relink applies to linked smart objects.";
            return false;
        }
        var host = Model;
        byte[] data = await File.ReadAllBytesAsync(path);
        var image = await Task.Run(() => SmartObjects.Decode(data, host));
        if (image is null)
        {
            Notice = $"Strayta cannot read {Path.GetFileName(path)} as smart object content.";
            return false;
        }
        string name = Path.GetFileName(path);
        var link = (entry.LinkDescriptor ?? new Descriptor { ClassId = "ExternalFileLink" })
            .With("descVersion", new IntegerValue(2))
            .With("Nm  ", new TextValue(name))
            .With("fullPath", new TextValue(new Uri(path).AbsoluteUri))
            .With("originalPath", new TextValue(path));
        if (FilePath is { } own) link = link.With("relPath", new TextValue(Path.GetRelativePath(Path.GetDirectoryName(own)!, path)));
        var info = new FileInfo(path);
        var when = info.LastWriteTimeUtc;
        var relinked = entry with
        {
            Name = name, FileType = PsdSmartObjects.FileTypeFor(name), LinkDescriptor = link, ExternalSize = info.Length,
            FileDate = entry.Version > 3 ? (when.Year, when.Month, when.Day, when.Hour, when.Minute, when.Second + when.Millisecond / 1000.0) : null,
            Raw = null,
        };
        var updated = PsdSmartObjects.WithLinkedFile(file, relinked);
        SmartObjects.SetBaseDirectory(updated, FilePath is null ? null : Path.GetDirectoryName(FilePath));
        await ApplyContentAsync(so.UniqueId, updated, image, image.Width, image.Height, "Relink to File");
        return true;
    }

    /// <summary>
    /// Redraws linked smart objects whose file changed on disk (Layer › Smart Objects › Update Modified Content, and when
    /// a linked file is saved); returns how many layers were redrawn.
    /// </summary>
    public async Task<int> UpdateLinkedAsync(string? changedPath = null)
    {
        if (Model.SourceData is not PsdFile file) return 0;
        SmartObjects.SetBaseDirectory(file, FilePath is null ? null : Path.GetDirectoryName(FilePath));
        var host = Model;
        int count = 0;
        foreach (var id in Model.Root.Descendants().OfType<PixelLayer>().Select(l => SmartObjects.Read(l)?.UniqueId).OfType<string>().Distinct().ToList())
        {
            if (PsdSmartObjects.FindLinkedFile(file, id) is not { Kind: not "liFD" } entry || SmartObjects.ExternalPath(file, entry) is not { } path) continue;
            if (changedPath is not null && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(changedPath), StringComparison.Ordinal)) continue;
            if (!File.Exists(path)) continue;
            byte[] data;
            try { data = await File.ReadAllBytesAsync(path); }
            catch (IOException) { continue; }
            var image = await Task.Run(() => SmartObjects.Decode(data, host));
            if (image is null) continue;
            count += SmartObjectInstances(id).Count();
            await ApplyContentAsync(id, file, image, image.Width, image.Height, "Update Modified Content");
        }
        return count;
    }

    /// <summary>The selected smart object's linked files on disk, for watching.</summary>
    public IEnumerable<string> LinkedFilePaths()
    {
        if (Model.SourceData is not PsdFile file) yield break;
        SmartObjects.SetBaseDirectory(file, FilePath is null ? null : Path.GetDirectoryName(FilePath));
        foreach (var layer in Model.Root.Descendants().OfType<PixelLayer>())
            if (SmartObjects.Entry(Model, layer) is { Kind: not "liFD" } entry && SmartObjects.ExternalPath(file, entry) is { } path)
                yield return path;
    }

    /// <summary>The selected smart object's content bytes and a file name for Export Contents, or null.</summary>
    public (byte[] Data, string Name)? SmartContentForExport()
    {
        if (SelectedSmartObject is not { } layer || Model.SourceData is not PsdFile file || SmartObjects.Entry(Model, layer) is not { } entry) return null;
        if (entry.Data is { } data) return (data, entry.Name.Length > 0 ? entry.Name : layer.Name + ".psb");
        if (SmartObjects.ExternalPath(file, entry) is { } path && File.Exists(path)) return (File.ReadAllBytes(path), Path.GetFileName(path));
        return null;
    }

    // ---- New Smart Object via Copy, Convert to Smart Object ------------------------------------------------

    /// <summary>Layer › Smart Objects › New Smart Object via Copy: a copy above whose content is its own (editing it leaves the original).</summary>
    public bool NewSmartObjectViaCopy()
    {
        if (SelectedSmartObject is not { } layer || Model.SourceData is not PsdFile file || SmartObjects.Read(layer) is not { } so
            || PsdSmartObjects.FindLinkedFile(file, so.UniqueId) is not { } entry || layer.Parent is not { } parent) return false;
        string id = PsdSmartObjects.NewId();
        var copyEntry = entry with
        {
            UniqueId = id, Raw = null,
            ContentDescriptor = entry.Version >= 8 ? PsdLinkedFile.NewContentDescriptor(entry.ContentDescriptor) : entry.ContentDescriptor,
        };
        var updated = PsdSmartObjects.WithLinkedFile(file, copyEntry);
        var record = PsdSmartObjects.WithIds(((PsdLayerRecord)layer.SourceData!).ForDuplicate(), id, PsdSmartObjects.NewId());
        var copy = new PixelLayer
        {
            Name = layer.Name + " copy", Visible = layer.Visible, Opacity = layer.Opacity, FillOpacity = layer.FillOpacity, BlendMode = layer.BlendMode,
            Clipped = layer.Clipped, Effects = layer.Effects, Bounds = layer.Bounds, Pixels = layer.Pixels, Mask = layer.Mask,
            TransparencyLocked = layer.TransparencyLocked, SourceData = record,
        };
        foreach (var t in layer.Tags) copy.Tags.Add(t);
        Apply(new SmartObjectEdit(Model, "New Smart Object via Copy", updated, [],
            new InsertEdit(copy, parent, parent.IndexOf(layer) + 1, "New Smart Object via Copy")));
        Select(copy);
        return true;
    }

    /// <summary>
    /// Layer › Smart Objects › Convert to Smart Object: the selected layer (or group) moves into an embedded PSB the
    /// size of what it draws, and a smart object showing it takes its place, keeping its visibility, opacity, blend mode
    /// and clipping outside (inside it is Normal at 100%).
    /// </summary>
    public async Task<bool> ConvertToSmartObjectAsync()
    {
        if (SelectedLayer?.Node is not { Parent: { } parent } node || node is AdjustmentLayer)
        {
            Notice = "Select a layer or group to convert to a smart object.";
            return false;
        }
        if (!CanSave)
        {
            Notice = $"Smart objects in {Model.ColorMode} documents are not supported yet.";
            return false;
        }
        IsBusy = true;
        try
        {
            var host = Model;
            var built = await Task.Run(() => BuildSmartContent(host, node));
            if (built is null)
            {
                Notice = "The layer is empty, so there is nothing to convert.";
                return false;
            }
            var (content, bbox, composite) = built.Value;
            var stream = new MemoryStream();
            await Task.Run(() => PsdWriter.Write(content, stream, new PsdWriteOptions { Composite = composite, Psb = true }));
            string id = PsdSmartObjects.NewId();
            string name = (node.Name.Length > 0 ? node.Name : "Layer") + ".psb";
            var file = PsdSmartObjects.WithLinkedFile(FileData(), PsdSmartObjects.NewEmbedded(id, name, "8BPB", stream.ToArray()));
            var placed = PsdSmartObjects.NewPlaced(id, PsdSmartObjects.NewId(), bbox.Width, bbox.Height,
                [(bbox.Left, bbox.Top), (bbox.Right, bbox.Top), (bbox.Right, bbox.Bottom), (bbox.Left, bbox.Bottom)], Model.Resolution);
            var record = PsdSmartObjects.WithPlaced(new PsdLayerRecord { Blocks = [new TaggedBlock("8BIM", "PlLd", 0, 0, [])] }, placed);
            var layer = new PixelLayer
            {
                Name = node.Name, Visible = node.Visible, Opacity = node.Opacity, BlendMode = node.BlendMode == BlendMode.PassThrough ? BlendMode.Normal : node.BlendMode,
                Clipped = node.Clipped, Bounds = bbox, Pixels = composite, SourceData = record,
            };
            layer.Tags.Add("smart-object");
            int index = parent.IndexOf(node);
            Apply(new SmartObjectEdit(Model, "Convert to Smart Object", file, [],
                new DeleteEdit(node), new InsertEdit(layer, parent, index, "Convert to Smart Object")));
            Select(layer);
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The content document for Convert to Smart Object: a copy of the node moved so what it draws starts at (0, 0).</summary>
    private static (Document Content, PixelRect Bounds, Raster Composite)? BuildSmartContent(Document host, LayerNode node)
    {
        // What the node draws on a canvas grown around it (effects can reach past its pixels).
        var reach = Reach(node);
        if (reach.IsEmpty) return null;
        int margin = node.Root().Any(n => n.Effects is not null) ? 256 : 0;
        var area = new PixelRect(reach.Left - margin, reach.Top - margin, reach.Right + margin, reach.Bottom + margin);
        var probe = new Document(area.Width, area.Height, host.ColorMode, host.BitDepth) { GlobalLightAngle = host.GlobalLightAngle, GlobalLightAltitude = host.GlobalLightAltitude };
        probe.Patterns.AddRange(host.Patterns);
        probe.Root.Add(Inner(Clone(node), -area.Left, -area.Top, host, area));
        Raster drawn;
        using (var probeRenderer = new CpuRenderer()) drawn = probeRenderer.Render(probe).ToRaster(host.ColorMode, host.BitDepth);
        var used = AlphaBounds(drawn);
        if (used.IsEmpty) return null;
        var bbox = new PixelRect(area.Left + used.Left, area.Top + used.Top, area.Left + used.Right, area.Top + used.Bottom);
        var content = new Document(bbox.Width, bbox.Height, host.ColorMode, host.BitDepth)
        {
            Resolution = host.Resolution, IccProfile = host.IccProfile, GlobalLightAngle = host.GlobalLightAngle, GlobalLightAltitude = host.GlobalLightAltitude,
        };
        content.Patterns.AddRange(host.Patterns);
        content.Root.Add(Inner(Clone(node), -bbox.Left, -bbox.Top, host, bbox));
        using var renderer = new CpuRenderer();
        var composite = renderer.Render(content).ToRaster(host.ColorMode, host.BitDepth);
        return (content, bbox, composite);
    }

    /// <summary>A clone shifted into the content's coordinates, shown, Normal and opaque at the top level.</summary>
    private static LayerNode Inner(LayerNode clone, int dx, int dy, Document host, PixelRect target)
    {
        ShiftDeep(clone, dx, dy, host, target);
        clone.Visible = true;
        clone.Opacity = 1f;
        clone.Clipped = false;
        if (clone is not LayerGroup) clone.BlendMode = BlendMode.Normal;
        else if (clone.BlendMode != BlendMode.PassThrough) clone.BlendMode = BlendMode.Normal;
        return clone;
    }

    private static void ShiftDeep(LayerNode node, int dx, int dy, Document host, PixelRect target)
    {
        MoveEdit.Offset(node, dx, dy);
        foreach (var n in node is LayerGroup g ? g.Descendants().Prepend(node) : [node])
        {
            var map = PsdCanvasMap(dx, dy);
            n.SourceData = n.SourceData switch
            {
                PsdLayerRecord r => PsdCanvas.WithCanvas(r, host.Width, host.Height, target.Width, target.Height, map),
                PsdGroupRecords gr => gr with { Folder = PsdCanvas.WithCanvas(gr.Folder, host.Width, host.Height, target.Width, target.Height, map) },
                var other => other,
            };
        }
    }

    private static CanvasMap PsdCanvasMap(int dx, int dy) => CanvasMap.Translation(dx, dy);

    /// <summary>Union of the pixel bounds of the node's visible layers.</summary>
    private static PixelRect Reach(LayerNode node)
    {
        var r = PixelRect.Empty;
        foreach (var l in (node is LayerGroup g ? g.Descendants().Prepend(node) : [node]).OfType<PixelLayer>())
            if (l.Pixels is not null && !l.Bounds.IsEmpty)
                r = r.IsEmpty ? l.Bounds : new PixelRect(Math.Min(r.Left, l.Bounds.Left), Math.Min(r.Top, l.Bounds.Top), Math.Max(r.Right, l.Bounds.Right), Math.Max(r.Bottom, l.Bounds.Bottom));
        return r;
    }

    private static PixelRect AlphaBounds(Raster r)
    {
        if (r.Alpha is not { } a) return PixelRect.FromSize(r.Width, r.Height);
        int x0 = r.Width, y0 = r.Height, x1 = -1, y1 = -1;
        for (int y = 0; y < r.Height; y++)
            for (int x = 0; x < r.Width; x++)
                if (a.GetNormalized(y * r.Width + x) > 0)
                {
                    x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                    y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                }
        return x1 < 0 ? PixelRect.Empty : new PixelRect(x0, y0, x1 + 1, y1 + 1);
    }

    /// <summary>A deep copy of a layer or group (rasters and records are immutable and shared).</summary>
    internal static LayerNode Clone(LayerNode node)
    {
        LayerNode copy = node switch
        {
            PixelLayer p => new PixelLayer { Bounds = p.Bounds, Pixels = p.Pixels, Mask = p.Mask, TransparencyLocked = p.TransparencyLocked },
            AdjustmentLayer a => new AdjustmentLayer { Kind = a.Kind, Adjustment = a.Adjustment, Mask = a.Mask },
            LayerGroup g => new LayerGroup { Expanded = g.Expanded, Mask = g.Mask },
            _ => throw new NotSupportedException(node.GetType().Name),
        };
        copy.Name = node.Name;
        copy.Visible = node.Visible;
        copy.Opacity = node.Opacity;
        copy.FillOpacity = node.FillOpacity;
        copy.BlendMode = node.BlendMode;
        copy.Clipped = node.Clipped;
        copy.Effects = node.Effects;
        copy.SourceData = node.SourceData;
        foreach (var t in node.Tags) copy.Tags.Add(t);
        if (node is LayerGroup group)
            foreach (var child in group.Children) ((LayerGroup)copy).Add(Clone(child));
        return copy;
    }

    // ---- Smart filters -------------------------------------------------------------------------------

    /// <summary>The smart filters of a layer (null when it has none).</summary>
    public static PsdSmartFilterStack? SmartFiltersOf(LayerNode node) =>
        node.SourceData is PsdLayerRecord r && node.Tags.Contains("smart-object") && PsdSmartObjects.ReadPlaced(r) is { } placed
            ? PsdSmartObjects.ReadFilters(placed) : null;

    /// <summary>
    /// Changes a smart object's smart filters and redraws it from its content (one undo step). When the content cannot
    /// be drawn, adding a filter on top applies it to the current pixels (the same result); other changes are refused.
    /// </summary>
    public async Task<bool> SetSmartFiltersAsync(PixelLayer layer, PsdSmartFilterStack? stack, string description, ImageFilter? added = null)
    {
        if (layer.SourceData is not PsdLayerRecord record || PsdSmartObjects.ReadPlaced(record) is not { } placed) return false;
        var file = Model.SourceData as PsdFile ?? FileData();
        var updated = PsdSmartObjects.WithPlaced(record, PsdSmartObjects.WithFilters(placed, stack));
        var host = Model;
        var canvas = host.Bounds;
        var clip = new PixelRect(canvas.Left - canvas.Width, canvas.Top - canvas.Height, canvas.Right + canvas.Width, canvas.Bottom + canvas.Height);
        IsBusy = true;
        try
        {
            var drawn = await Task.Run(() => SmartObjects.Draw(updated, file, host, clip));
            (Raster? Pixels, PixelRect Bounds) result;
            if (drawn is { } d) result = d;
            else if (added is not null && SmartObjects.UnknownFilters(record).Any() == false)
                result = await Task.Run(() => FilterEngine.ApplyToLayer(layer.Pixels, layer.Bounds, added, new FilterScope(canvas)));
            else
            {
                Notice = $"Strayta cannot redraw \"{layer.Name}\": {SmartObjects.WhyNotDrawable(updated, file, host) ?? "its content cannot be drawn"}.";
                return false;
            }
            Apply(new SmartObjectEdit(Model, description, Model.SourceData,
                [(layer, new SmartObjectEdit.LayerState(result.Pixels, result.Pixels is null ? PixelRect.Empty : result.Bounds, updated, [.. layer.Tags]))]));
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Shows or hides one smart filter (or all of them for <paramref name="index"/> −1).</summary>
    public Task<bool> ToggleSmartFilterAsync(PixelLayer layer, int index)
    {
        if (SmartFiltersOf(layer) is not { } stack) return Task.FromResult(false);
        if (index < 0) return SetSmartFiltersAsync(layer, stack with { Enabled = !stack.Enabled }, stack.Enabled ? "Hide Smart Filters" : "Show Smart Filters");
        var filters = stack.Filters.ToList();
        var f = filters[index];
        filters[index] = f with { Enabled = !f.Enabled };
        return SetSmartFiltersAsync(layer, stack with { Filters = filters }, f.Enabled ? "Hide Smart Filter" : "Show Smart Filter");
    }

    /// <summary>Adds <paramref name="filter"/> as the top smart filter (the Filter menu on a smart object).</summary>
    public Task<bool> AddSmartFilterAsync(PixelLayer layer, ImageFilter filter)
    {
        var stack = SmartFiltersOf(layer) ?? PsdSmartObjects.NewStack();
        var entry = PsdSmartObjects.NewFilter(filter.Name, SmartObjects.ToSettings(filter));
        return SetSmartFiltersAsync(layer, stack with { Filters = [.. stack.Filters, entry] }, filter.Name, filter);
    }

    /// <summary>New settings for smart filter <paramref name="index"/> (double-clicking it in the Layers panel).</summary>
    public Task<bool> EditSmartFilterAsync(PixelLayer layer, int index, ImageFilter filter)
    {
        if (SmartFiltersOf(layer) is not { } stack) return Task.FromResult(false);
        var filters = stack.Filters.ToList();
        filters[index] = filters[index] with { Settings = SmartObjects.ToSettings(filter), Name = filter.Name };
        return SetSmartFiltersAsync(layer, stack with { Filters = filters }, $"Edit {filter.Name}");
    }

    /// <summary>Removes smart filter <paramref name="index"/>.</summary>
    public Task<bool> DeleteSmartFilterAsync(PixelLayer layer, int index)
    {
        if (SmartFiltersOf(layer) is not { } stack) return Task.FromResult(false);
        var filters = stack.Filters.ToList();
        filters.RemoveAt(index);
        return SetSmartFiltersAsync(layer, filters.Count == 0 ? null : stack with { Filters = filters }, "Delete Smart Filter");
    }
}

public sealed partial class DocumentViewModel
{
    /// <summary>
    /// Opens a filter dialog's session on a smart object without rasterizing it: the canvas previews the filter on the
    /// layer's current pixels; OK adds a smart filter instead of changing pixels (EditorViewModel.SmartObjects.cs).
    /// </summary>
    public bool BeginSmartFilterPreview(PixelLayer layer, string name)
    {
        if (_baking || _filterSession is not null) return false;
        if (IsTransforming)
        {
            Notice = "Apply or cancel the transform first.";
            return false;
        }
        if (layer.Pixels is null)
        {
            Notice = $"Could not complete the {name} command because the smart object is empty.";
            return false;
        }
        _filterSession = new FilterSession(new FilterTarget(layer, false, null, false));
        return true;
    }

    /// <summary>After an Edit Contents tab was written back into its parent: nothing left unsaved.</summary>
    internal void MarkContentSaved()
    {
        _undo.MarkSaved();
        IsModified = false;
    }
}

internal static class LayerNodeTreeExtensions
{
    /// <summary>The node and, for groups, everything in it.</summary>
    public static IEnumerable<LayerNode> Root(this LayerNode node) => node is LayerGroup g ? g.Descendants().Prepend(node) : [node];
}
