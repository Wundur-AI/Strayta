using Strayta.Core;
using Strayta.Imaging;
using Strayta.Psd;
using Strayta.Psd.Descriptors;
using Strayta.Rendering;
using Strayta.Rendering.Transforms;

namespace Strayta.Inspect;

/// <summary>Smart object diagnostics: the placed-layer descriptors and embedded files.</summary>
internal static class SmartCommands
{
    public static int Dump(string target)
    {
        foreach (var f in Files(target))
        {
            PsdFile file;
            try { file = PsdFile.Open(f, new PsdReadOptions { MaxRawBlockBytes = long.MaxValue, SkipComposite = true }); }
            catch (Exception e) { Console.WriteLine($"FAIL {f}: {e.Message}"); continue; }
            var smart = file.Layers.Where(r => r.FindBlock("SoLd") is not null || r.FindBlock("SoLE") is not null).ToList();
            if (smart.Count == 0) continue;
            Console.WriteLine($"== {Path.GetFileName(f)} ({file.Header.Width}x{file.Header.Height})");
            foreach (var b in file.GlobalBlocks.Where(b => b.Key is "lnk2" or "lnk3" or "lnkD" or "lnkE"))
            {
                Console.WriteLine($"  [{b.Key}] {b.Data?.Length:N0} bytes");
                foreach (var e in PsdLinkedFiles.Read(b.Data ?? []))
                {
                    bool same = PsdLinkedFiles.Write([e with { Raw = null }]).AsSpan(8).StartsWith(e.Raw);
                    Console.WriteLine($"    {e.Kind} v{e.Version} {e.UniqueId} \"{e.Name}\" '{e.FileType}' {e.Data?.Length ?? e.ExternalSize:N0} bytes child \"{e.ChildDocumentId}\" mod {e.AssetModTime} lock {e.AssetLocked} tail {Convert.ToHexString(e.Tail)} re-encodes {(same ? "identically" : "DIFFERENTLY")}");
                    if (e.OpenDescriptor is { } od) Console.WriteLine("      open " + od.ToString().Replace("\n", "\n      "));
                    if (e.LinkDescriptor is { } ld) Console.WriteLine("      link " + ld.ToString().Replace("\n", "\n      "));
                }
            }
            foreach (var r in smart)
            {
                var block = r.FindBlock("SoLd") ?? r.FindBlock("SoLE")!;
                Console.WriteLine($"  -- \"{r.Name}\" {r.Rect} blocks: {string.Join(" ", r.Blocks.Select(x => x.Key))}");
                try
                {
                    var d = DescriptorReader.ReadVersioned(block.Data!, 8);
                    Console.WriteLine("     " + d.ToString().Replace("\n", "\n     "));
                }
                catch (Exception e) { Console.WriteLine($"     unreadable: {e.Message}"); }
                Console.WriteLine($"     SoLd head {Convert.ToHexString(block.Data!.AsSpan(0, 12))}");
                try
                {
                    var d = DescriptorReader.ReadVersioned(block.Data!, 8);
                    Console.WriteLine($"     SoLd class '{d.ClassId}' name '{d.Name}' types: " + string.Join(", ", d.Items.Select(kv => $"{kv.Key.TrimEnd()}={kv.Value.GetType().Name}{(kv.Value is ObjectValue ov ? "(" + ov.Value.ClassId + ":" + string.Join("/", ov.Value.Items.Select(i => i.Key.TrimEnd() + "=" + i.Value.GetType().Name)) + ")" : "")}")));
                }
                catch (PsdFormatException) { }
                if (r.FindBlock("PlLd")?.Data is { } pl)
                {
                    int head = 8 + 1 + pl[8] + 16 + 64 + 8;
                    Console.WriteLine($"     PlLd head {Convert.ToHexString(pl.AsSpan(0, Math.Min(head, pl.Length)))}");
                    try
                    {
                        int at = 8 + 1 + pl[8] + 16 + 64;
                        var d = DescriptorReader.ReadVersioned(pl, at + 4);
                        Console.WriteLine($"     PlLd warp ({pl.Length} bytes): " + d.ToString().Replace("\n", "\n     "));
                    }
                    catch (Exception e) { Console.WriteLine($"     PlLd unreadable: {e.Message}"); }
                }
            }
        }
        return 0;
    }

    /// <summary>
    /// Redraws each smart object from its embedded content through its placement (warp included) and compares the
    /// result with the pixels Photoshop stored for the layer: the share of pixels (where either is not transparent)
    /// within 3/255 premultiplied, with the warp ignored for comparison.
    /// </summary>
    public static int Fidelity(string target, string? exportDir)
    {
        var scores = new List<(double Plain, double Warped)>();
        foreach (var f in Files(target))
        {
            PsdFile file;
            try { file = PsdFile.Open(f, new PsdReadOptions { MaxRawBlockBytes = long.MaxValue, SkipComposite = true }); }
            catch (Exception e) { Console.WriteLine($"FAIL {f}: {e.Message}"); continue; }
            int index = 0;
            foreach (var r in file.Layers)
            {
                if (PsdLiveContent.ReadSmartObject(r) is not { } so) continue;
                index++;
                string label = $"{Path.GetFileName(f)} #{index} \"{r.Name}\"";
                var content = Decode(file, so.UniqueId, out string why);
                if (content is null) { Console.WriteLine($"  --   {label}: {why}"); continue; }
                var stored = Stored(r, file.Header.ColorMode);
                if (stored is null) { Console.WriteLine($"  --   {label}: no stored pixels"); continue; }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var warped = SmartObjectPlacement.Render(content, so.Width, so.Height, so.Corners, so.Warp, ResampleFilter.Bicubic);
                long ms = sw.ElapsedMilliseconds;
                var plain = SmartObjectPlacement.Render(content, so.Width, so.Height, so.Corners, null, ResampleFilter.Bicubic);
                double a = Score(plain, (stored, r.Rect), out _), b = Score(warped, (stored, r.Rect), out var diff);
                scores.Add((a, b));
                if (so.Warp?.Values is { } wv) Console.WriteLine("        warpValues " + string.Join(" ", wv.Select(v => v.ToString("R"))) + $" Sz {so.Width}x{so.Height}");
                Console.WriteLine($"{b,7:F2}% (unwarped {a,6:F2}%) {label} {so.Warp?.Style ?? "none"}{(so.HasFilters ? " +filters" : "")} stored {r.Rect} drawn {warped.Bounds} {ms} ms");
                if (exportDir is not null)
                {
                    Directory.CreateDirectory(exportDir);
                    string stem = Path.Combine(exportDir, $"smart-{Path.GetFileNameWithoutExtension(f)}-{index}");
                    Export(stem + "-stored.png", stored);
                    Export(stem + "-content.png", content);
                    if (warped.Pixels is { } wp) Export(stem + "-drawn.png", wp);
                    if (diff is not null) PngWriter.Write(stem + "-diff.png", diff.Value.W, diff.Value.H, diff.Value.Rgba);
                }
            }
        }
        if (scores.Count > 0)
            Console.WriteLine($"\n{scores.Count} smart objects; mean match {scores.Average(s => s.Warped):F2}% (warp ignored: {scores.Average(s => s.Plain):F2}%)");
        return 0;
    }

    private static void Export(string path, Raster r) => PngWriter.Write(path, r.Width, r.Height, RgbaConverter.ToRgba8(r));

    private static Raster? Stored(PsdLayerRecord r, ColorMode mode)
    {
        if (r.Rect.IsEmpty) return null;
        var planes = new List<Plane>();
        for (short c = 0; c < mode.ColorChannelCount(); c++)
            if (r.ChannelData.TryGetValue(c, out var p)) planes.Add(p);
            else return null;
        r.ChannelData.TryGetValue(PsdChannelId.Transparency, out var alpha);
        return new Raster(mode, planes, alpha);
    }

    /// <summary>Match percentage over the union of both rectangles, counting pixels where either is visible.</summary>
    private static double Score((Raster? Pixels, PixelRect Bounds) drawn, (Raster Pixels, PixelRect Bounds) stored, out (int W, int H, byte[] Rgba)? diff)
    {
        diff = null;
        var d = drawn.Pixels is null ? PixelRect.Empty : drawn.Bounds;
        var s = stored.Bounds;
        var u = d.IsEmpty ? s : new PixelRect(Math.Min(d.Left, s.Left), Math.Min(d.Top, s.Top), Math.Max(d.Right, s.Right), Math.Max(d.Bottom, s.Bottom));
        byte[]? dr = drawn.Pixels is null ? null : RgbaConverter.ToRgba8(drawn.Pixels);
        var sr = RgbaConverter.ToRgba8(stored.Pixels);
        long count = 0, match = 0;
        var o = new byte[u.Width * u.Height * 4];
        Span<int> a = stackalloc int[4], b = stackalloc int[4];
        for (int y = u.Top; y < u.Bottom; y++)
            for (int x = u.Left; x < u.Right; x++)
            {
                a.Clear(); b.Clear();
                if (dr is not null && x >= d.Left && x < d.Right && y >= d.Top && y < d.Bottom)
                {
                    int i = ((y - d.Top) * d.Width + x - d.Left) * 4;
                    for (int k = 0; k < 4; k++) a[k] = dr[i + k];
                }
                if (x >= s.Left && x < s.Right && y >= s.Top && y < s.Bottom)
                {
                    int i = ((y - s.Top) * s.Width + x - s.Left) * 4;
                    for (int k = 0; k < 4; k++) b[k] = sr[i + k];
                }
                if (a[3] == 0 && b[3] == 0) continue;
                count++;
                int e = Math.Abs(a[3] - b[3]);
                for (int k = 0; k < 3; k++) e = Math.Max(e, Math.Abs(a[k] * a[3] / 255 - b[k] * b[3] / 255));
                if (e <= FidelityReport.Tolerance) match++;
                int oi = ((y - u.Top) * u.Width + x - u.Left) * 4;
                byte g = (byte)(b[3] / 4 + 20);
                o[oi] = e <= FidelityReport.Tolerance ? g : (byte)Math.Min(255, 128 + e * 2);
                o[oi + 1] = e <= FidelityReport.Tolerance ? g : (byte)0;
                o[oi + 2] = e <= FidelityReport.Tolerance ? g : (byte)0;
                o[oi + 3] = 255;
            }
        diff = (u.Width, u.Height, o);
        return count == 0 ? 100 : 100.0 * match / count;
    }

    /// <summary>The embedded file's image: a PSD/PSB's composite (or a render), or a standard image.</summary>
    public static Raster? Decode(PsdFile file, string id, out string why)
    {
        why = "";
        if (PsdLiveContent.FindEmbeddedFile(file, id) is not { } embedded) { why = "content not embedded"; return null; }
        try
        {
            if (embedded.Data.Length >= 4 && embedded.Data.AsSpan(0, 4).SequenceEqual("8BPS"u8))
            {
                using var stream = new MemoryStream(embedded.Data, writable: false);
                var inner = PsdFile.Read(stream);
                var doc = inner.ToDocument();
                if (doc.Composite is { } c && inner.HasRealMergedData != false && c.Width == doc.Width && c.Height == doc.Height) return c;
                return Compositor.Render(doc).ToRaster(doc.ColorMode, doc.BitDepth);
            }
            var image = ImageImporter.Decode(embedded.Data);
            return image.Root.Descendants().OfType<PixelLayer>().FirstOrDefault()?.Pixels;
        }
        catch (Exception e)
        {
            why = $"{e.GetType().Name}: {e.Message}";
            return null;
        }
    }

    /// <summary>Writes every embedded file of <paramref name="path"/> to <paramref name="dir"/>.</summary>
    public static int Extract(string path, string dir)
    {
        var file = PsdFile.Open(path, new PsdReadOptions { MaxRawBlockBytes = long.MaxValue, SkipComposite = true });
        Directory.CreateDirectory(dir);
        foreach (var b in file.GlobalBlocks.Where(b => b.Key is "lnk2" or "lnk3" or "lnkD"))
            foreach (var e in PsdLiveContent.ReadLinkedFiles(b.Data ?? []))
            {
                string name = Path.Combine(dir, $"smart-{e.UniqueId}-{string.Concat(e.Name.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_'))}");
                File.WriteAllBytes(name, e.Data);
                Console.WriteLine($"{name} {e.Data.Length:N0} bytes");
            }
        return 0;
    }

    public static IEnumerable<string> Files(string target) =>
        File.Exists(target) ? [target]
        : Directory.EnumerateFiles(target, "*.ps?", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith("._")).Order();
}
