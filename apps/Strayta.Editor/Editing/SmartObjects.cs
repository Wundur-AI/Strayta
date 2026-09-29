using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Strayta.Core;
using Strayta.Imaging;
using Strayta.Psd;
using Strayta.Psd.Descriptors;
using Strayta.Rendering;
using Strayta.Rendering.Export;
using Strayta.Rendering.Filters;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.Editing;

/// <summary>
/// Drawing smart objects from their content: the embedded (or linked) file decoded to an image in the document's color
/// mode and depth, placed through the corners and warp (<see cref="SmartObjectPlacement"/>), then its smart filters
/// applied in order (<see cref="SmartFilterStack"/>). When something cannot be drawn faithfully (content Strayta cannot
/// read or render, an enabled filter it does not have) the answer is null and the layer keeps Photoshop's pixels.
/// </summary>
internal static class SmartObjects
{
    private static readonly ConditionalWeakTable<PsdFile, ConcurrentDictionary<string, Raster?>> Cache = new();

    /// <summary>Where relative links are resolved from (the parent document's folder), per file.</summary>
    private static readonly ConditionalWeakTable<PsdFile, string> BaseDirs = new();

    /// <summary>Remembers the folder of the document <paramref name="file"/> belongs to, for its relatively linked files.</summary>
    public static void SetBaseDirectory(PsdFile file, string? dir)
    {
        if (dir is not null) BaseDirs.AddOrUpdate(file, dir);
    }

    /// <summary>The smart object layer's placed-layer data, or null.</summary>
    public static PsdSmartObject? Read(LayerNode node) =>
        node.Tags.Contains("smart-object") && node.SourceData is PsdLayerRecord r ? PsdLiveContent.ReadSmartObject(r) : null;

    /// <summary>The file entry a smart object shows (embedded or linked), or null.</summary>
    public static PsdLinkedFile? Entry(Document doc, LayerNode node) =>
        doc.SourceData is PsdFile file && Read(node) is { } so ? PsdSmartObjects.FindLinkedFile(file, so.UniqueId) : null;

    /// <summary>A linked entry's file on disk (absolute, relative to the document, or by name next to it), or null.</summary>
    public static string? ExternalPath(PsdFile file, PsdLinkedFile entry)
    {
        if (entry.Kind == "liFD") return null;
        var candidates = new List<string>();
        if (entry.ExternalPath is { Length: > 0 } p) candidates.Add(p);
        if (BaseDirs.TryGetValue(file, out var dir))
        {
            if (entry.LinkDescriptor?.Text("relPath") is { Length: > 0 } rel) candidates.Add(Path.GetFullPath(Path.Combine(dir, rel)));
            candidates.Add(Path.Combine(dir, entry.Name));
        }
        return candidates.FirstOrDefault(File.Exists) ?? candidates.FirstOrDefault();
    }

    /// <summary>The content image of a smart object (cached per file and ID), or null when it cannot be shown.</summary>
    public static Raster? Content(PsdFile file, string uniqueId, Document host)
    {
        var cache = Cache.GetOrCreateValue(file);
        if (cache.TryGetValue(uniqueId, out var hit)) return hit;
        Raster? image = null;
        if (PsdSmartObjects.FindLinkedFile(file, uniqueId) is { } entry)
        {
            if (entry.Data is { } data) image = Decode(data, host);
            else if (ExternalPath(file, entry) is { } path && File.Exists(path))
            {
                try { image = Decode(File.ReadAllBytes(path), host); }
                catch (IOException) { image = null; }
            }
        }
        // Linked content can change on disk, so only embedded content is kept.
        if (PsdSmartObjects.FindLinkedFile(file, uniqueId) is { Kind: "liFD" }) cache[uniqueId] = image;
        return image;
    }

    /// <summary>Forgets cached content, e.g. after a linked file changed.</summary>
    public static void Forget(PsdFile file) => Cache.Remove(file);

    /// <summary>A file's image (PSD/PSB composite or render, or a standard image) in the host's mode and depth.</summary>
    public static Raster? Decode(byte[] data, Document host)
    {
        try
        {
            if (data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual("8BPS"u8))
            {
                using var stream = new MemoryStream(data, writable: false);
                var inner = PsdFile.Read(stream);
                return Convert(Flatten(inner.ToDocument(), inner), host);
            }
            var doc = ImageImporter.Decode(data);
            return Convert(doc.Root.Descendants().OfType<PixelLayer>().FirstOrDefault()?.Pixels, host);
        }
        catch (Exception e) when (e is PsdFormatException or IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// A content document's image: the composite Photoshop stored when it is a faithful one, else Strayta's render,
    /// unless it has live layers without pixels (shapes, type) that only Photoshop can draw.
    /// </summary>
    public static Raster? Flatten(Document doc, PsdFile? file)
    {
        if (doc.Composite is { } composite && file?.HasRealMergedData != false && composite.Width == doc.Width && composite.Height == doc.Height)
            return composite;
        bool undrawable = doc.Root.Descendants().OfType<PixelLayer>().Any(l => l.Visible && l.Pixels is null
            && (l.Tags.Contains("shape") || l.Tags.Contains("fill") || l.Tags.Contains("text")));
        if (undrawable) return null;
        using var renderer = new CpuRenderer();
        return renderer.Render(doc).ToRaster(doc.ColorMode, doc.BitDepth);
    }

    /// <summary>Brings an image to the host's color mode and bit depth (same mode only; 32-bit is linear, so only to and from itself).</summary>
    public static Raster? Convert(Raster? image, Document host)
    {
        if (image is null) return null;
        if (image.ColorMode != host.ColorMode)
        {
            // Grayscale content in an RGB document: repeat the gray.
            if (image.ColorMode == ColorMode.Grayscale && host.ColorMode == ColorMode.Rgb)
                image = new Raster(ColorMode.Rgb, [image.ColorPlanes[0], image.ColorPlanes[0], image.ColorPlanes[0]], image.Alpha);
            else return null;
        }
        if (image.BitDepth == host.BitDepth) return image;
        if (image.BitDepth == 32 || host.BitDepth == 32) return null;
        Plane To(Plane p)
        {
            var o = Plane.Create(p.Width, p.Height, host.BitDepth);
            int n = p.Width * p.Height;
            for (int i = 0; i < n; i++)
            {
                float v = p.GetNormalized(i);
                if (host.BitDepth == 8) o.Data[i] = RgbaConverter.ToByte(v);
                else o.AsUInt16()[i] = (ushort)Math.Clamp(MathF.Round(v * 65535f), 0f, 65535f);
            }
            return o;
        }
        return new Raster(image.ColorMode, image.ColorPlanes.Select(To).ToArray(), image.Alpha is null ? null : To(image.Alpha));
    }

    /// <summary>
    /// Why a smart object cannot be drawn from its content right now (null when it can): the content is missing or
    /// unreadable, or an enabled smart filter is one Strayta does not have.
    /// </summary>
    public static string? WhyNotDrawable(PsdLayerRecord record, PsdFile file, Document host, Raster? content = null)
    {
        if (PsdLiveContent.ReadSmartObject(record) is not { } so) return "its placement data cannot be read";
        if (UnknownFilters(record).FirstOrDefault() is { } unknown) return $"its smart filter \"{unknown}\" is not one Strayta has";
        if ((content ?? Content(file, so.UniqueId, host)) is null)
            return PsdSmartObjects.FindLinkedFile(file, so.UniqueId) is { Kind: not "liFD" } ? "its linked file is missing" : "its content cannot be drawn by Strayta";
        return null;
    }

    /// <summary>Names of the enabled smart filters Strayta does not draw.</summary>
    public static IEnumerable<string> UnknownFilters(PsdLayerRecord record)
    {
        if (PsdSmartObjects.ReadPlaced(record) is not { } placed || PsdSmartObjects.ReadFilters(placed) is not { Enabled: true } stack) return [];
        return stack.Filters.Where(f => f.Enabled && ToFilter(f) is null).Select(f => f.Name);
    }

    /// <summary>
    /// The smart object drawn from its content through its corners, warp and smart filters, within <paramref name="clip"/>;
    /// null when it cannot be (<see cref="WhyNotDrawable"/>). <paramref name="content"/> overrides the file's content.
    /// </summary>
    public static (Raster? Pixels, PixelRect Bounds)? Draw(PsdLayerRecord record, PsdFile file, Document host, PixelRect clip,
        Raster? content = null, CancellationToken cancel = default)
    {
        if (PsdLiveContent.ReadSmartObject(record) is not { } so || UnknownFilters(record).Any()) return null;
        content ??= Content(file, so.UniqueId, host);
        if (content is null) return null;
        var (pixels, bounds) = SmartObjectPlacement.Render(content, so.Width, so.Height, so.Corners, so.Warp, ResampleFilter.Bicubic, clip, cancel);
        if (pixels is null) return (null, PixelRect.Empty);
        var steps = FilterSteps(record);
        if (steps.Count > 0) (pixels, bounds) = SmartFilterStack.Apply(pixels, bounds, steps, host.Bounds, null, cancel);
        return (pixels, pixels is null ? PixelRect.Empty : bounds);
    }

    /// <summary>The enabled smart filters as drawable steps, bottom first (empty when the stack is off).</summary>
    public static List<SmartFilterStep> FilterSteps(PsdLayerRecord record)
    {
        if (PsdSmartObjects.ReadPlaced(record) is not { } placed || PsdSmartObjects.ReadFilters(placed) is not { Enabled: true } stack) return [];
        return stack.Filters.Where(f => f.Enabled).Select(f => ToFilter(f) is { } filter ? new SmartFilterStep(filter, f.BlendMode, (float)f.Opacity) : null)
            .OfType<SmartFilterStep>().ToList();
    }

    // ---- Filters -----------------------------------------------------------------------------------------

    /// <summary>The Strayta filter for a smart filter's settings, or null for filters Strayta does not have.</summary>
    public static ImageFilter? ToFilter(PsdSmartFilter f)
    {
        if (f.Settings is not { } s) return null;
        double Num(string key, double fallback) => s.Number(key) ?? fallback;
        return f.FilterClass switch
        {
            "GsnB" => new GaussianBlurFilter(Math.Clamp(Num("Rds ", 1), GaussianBlurFilter.MinRadius, GaussianBlurFilter.MaxRadius)),
            "boxblur" => new BoxBlurFilter(Math.Clamp(Num("Rds ", 1), BoxBlurFilter.MinRadius, BoxBlurFilter.MaxRadius)),
            "MtnB" => new MotionBlurFilter(Num("Angl", 0), Math.Clamp(Num("Dstn", 10), MotionBlurFilter.MinDistance, MotionBlurFilter.MaxDistance)),
            "UnsM" => new UnsharpMaskFilter(Math.Clamp(Num("Amnt", 50), UnsharpMaskFilter.MinAmount, UnsharpMaskFilter.MaxAmount), Math.Max(0.1, Num("Rds ", 1)), (int)Num("Thsh", 0)),
            "AdNs" => new AddNoiseFilter(Num("Nose", 10), s.Enum("Dstr") == "Gsn " ? NoiseDistribution.Gaussian : NoiseDistribution.Uniform,
                s.Bool("Mnch") ?? false, (int)Num("FlRs", 0)),
            "HghP" => new HighPassFilter(Math.Max(0.1, Num("Rds ", 10))),
            _ => null,
        };
    }

    /// <summary>A Strayta filter's settings as Photoshop's "Fltr" descriptor (the class ID names the filter).</summary>
    public static Descriptor ToSettings(ImageFilter filter)
    {
        static KeyValuePair<string, DescriptorValue> Px(string key, double v) => new(key, new UnitFloatValue("#Pxl", v));
        static KeyValuePair<string, DescriptorValue> Pct(string key, double v) => new(key, new UnitFloatValue("#Prc", v));
        static KeyValuePair<string, DescriptorValue> Long(string key, int v) => new(key, new IntegerValue(v));
        return filter switch
        {
            GaussianBlurFilter g => new() { Name = "Gaussian Blur", ClassId = "GsnB", Items = [Px("Rds ", g.Radius)] },
            BoxBlurFilter b => new() { Name = "Box Blur", ClassId = "boxblur", Items = [Px("Rds ", b.Radius)] },
            MotionBlurFilter m => new() { Name = "Motion Blur", ClassId = "MtnB", Items = [Long("Angl", (int)Math.Round(m.Angle)), Px("Dstn", m.Distance)] },
            UnsharpMaskFilter u => new() { Name = "Unsharp Mask", ClassId = "UnsM", Items = [Pct("Amnt", u.Amount), Px("Rds ", u.Radius), Long("Thsh", u.Threshold)] },
            AddNoiseFilter n => new()
            {
                Name = "Add Noise",
                ClassId = "AdNs",
                Items =
                [
                    new("Dstr", new EnumValue("Dstr", n.Distribution == NoiseDistribution.Gaussian ? "Gsn " : "Unfr")), Pct("Nose", n.Amount),
                    new("Mnch", new BoolValue(n.Monochromatic)), Long("FlRs", n.Seed),
                ],
            },
            HighPassFilter h => new() { Name = "High Pass", ClassId = "HghP", Items = [Px("Rds ", h.Radius)] },
            _ => throw new NotSupportedException($"{filter.Name} cannot be a smart filter yet."),
        };
    }

    // ---- Encoding content ------------------------------------------------------------------------------

    /// <summary>
    /// The bytes to embed for a content document: PNG or JPEG when the content was one and is still a single plain
    /// layer (as Photoshop keeps placed images), otherwise a PSB. Returns the data, the file type and the name.
    /// </summary>
    public static (byte[] Data, string FileType, string Name) Encode(Document content, string oldName, string oldType)
    {
        bool flat = content.Root.Children is [PixelLayer { Visible: true, Opacity: 1f, BlendMode: BlendMode.Normal, Mask: null, Effects: null } only]
                    && only.Tags.Count == 0 && only.Bounds == content.Bounds;
        using var renderer = new CpuRenderer();
        var composite = renderer.Render(content).ToRaster(content.ColorMode, content.BitDepth);
        if (flat && oldType is "png " or "JPEG" && content.BitDepth == 8)
        {
            var rgba = RgbaConverter.ToRgba8(composite);
            var o = new MemoryStream();
            if (oldType == "png ") PngEncoder.Encode(o, rgba, content.Width, content.Height, keepAlpha: true, content.IccProfile);
            else JpegEncoder.Encode(o, rgba, content.Width, content.Height, 95, content.IccProfile);
            return (o.ToArray(), oldType, oldName);
        }
        var stream = new MemoryStream();
        PsdWriter.Write(content, stream, new PsdWriteOptions { Composite = composite, Psb = true });
        string name = Path.GetExtension(oldName).Equals(".psb", StringComparison.OrdinalIgnoreCase) ? oldName : Path.ChangeExtension(oldName, ".psb");
        return (stream.ToArray(), "8BPB", name);
    }

    /// <summary>Opens content bytes as an editable document (PSD/PSB, or a standard image), or null.</summary>
    public static Document? OpenContent(byte[] data)
    {
        if (data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual("8BPS"u8))
        {
            using var stream = new MemoryStream(data, writable: false);
            var doc = PsdFile.Read(stream, new PsdReadOptions { MaxRawBlockBytes = long.MaxValue }).ToDocument();
            if (doc.Root.Children.Count == 0 && doc.Composite is { } composite)
                doc.Root.Add(new PixelLayer { Name = composite.Alpha is null ? "Background" : "Layer 0", Bounds = doc.Bounds, Pixels = composite });
            return doc;
        }
        var image = ImageImporter.Decode(data);
        image.SourceData = null; // edited contents are written back into the parent, never to a file of their own
        return image;
    }
}
