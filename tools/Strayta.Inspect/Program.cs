using System.Diagnostics;
using Strayta.Core;
using Strayta.Inspect;
using Strayta.Rendering;
using Strayta.Psd;

return args switch
{
    ["info", var path] => Info(path, exportDir: null),
    ["info", var path, "--export", var dir] => Info(path, dir),
    ["scan", var target] => Scan(target),
    ["bench", var path] => Bench(path),
    ["effects", var path] => Effects(path),
    ["roundtrip", var target] => RoundTrip(target, keepDir: null),
    ["roundtrip", var target, "--keep", var dir] => RoundTrip(target, dir),
    ["text", var path] => TextCommands.Dump(path),
    ["smart", var target] => SmartCommands.Dump(target),
    ["smartx", var path, var dir] => SmartCommands.Extract(path, dir),
    ["smartfid", var target] => SmartCommands.Fidelity(target, null),
    ["smartfid", var target, "--export", var dir] => SmartCommands.Fidelity(target, dir),
    ["textfid", .. var rest] when rest.Length > 0 => TextCommands.Fidelity(rest),
    ["textsamples", .. var rest] => TextSamples.Run(rest),
    ["shapes", .. var rest] when rest.Length > 0 => ShapeCommands.Dump(rest.Where(a => a != "-v").ToArray(), rest.Contains("-v")),
    ["shapefid", .. var rest] when rest.Length > 0 => ShapeCommands.Fidelity(rest.Where(a => a != "-v").ToArray(), rest.Contains("-v")),
    ["adjustsamples", .. var rest] => AdjustmentSamples.Run(rest),
    ["fidelity", var target] => Fidelity(target, exportDir: null),
    ["fidelity", var target, "--export", var dir] => Fidelity(target, dir),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("""
        usage:
          strayta-inspect info <file.psd> [--export <dir>]   print structure; optionally export PNGs
          strayta-inspect scan <dir | list.txt>               parse many files and summarize
          strayta-inspect fidelity <file | dir> [--export d]  render and score against the embedded composite
          strayta-inspect bench <file>                        time full renders and cached layer toggles
          strayta-inspect roundtrip <file|dir> [--keep dir]   read, save, re-read and compare everything
          strayta-inspect adjustsamples <dir>                 write one PSD per adjustment kind for checking in Photoshop
          strayta-inspect smart <file|dir>                    dump smart objects' placed-layer data and embedded files
          strayta-inspect smartfid <file|dir> [--export d]    redraw smart objects from their content, compare with Photoshop's pixels
          strayta-inspect text <file.psd>                     print the type layers' text model
          strayta-inspect textfid <file|dir>... [--fonts d] [--all] [--export d]
                                                              render type layers, compare with Photoshop's pixels
        """);
    return 2;
}

static int Info(string path, string? exportDir)
{
    var sw = Stopwatch.StartNew();
    var file = PsdFile.Open(path);
    var doc = file.ToDocument();
    sw.Stop();

    var h = file.Header;
    Console.WriteLine($"{Path.GetFileName(path)}  ({new FileInfo(path).Length:N0} bytes, parsed in {sw.ElapsedMilliseconds} ms)");
    Console.WriteLine($"  {(h.IsPsb ? "PSB" : "PSD")} {h.Width}x{h.Height} {h.ColorMode} {h.BitDepth}-bit, {h.Channels} channels");
    Console.WriteLine($"  composite: {(doc.Composite is null ? "none" : "yes")}, real merged data: {file.HasRealMergedData?.ToString() ?? "unknown"}, transparency: {file.CompositeHasTransparency}");
    Console.WriteLine($"  resources: {string.Join(" ", file.Resources.Select(r => r.Id).Distinct())}");
    Console.WriteLine($"  global blocks: {string.Join(" ", file.GlobalBlocks.Select(b => b.Key))}");
    Console.WriteLine($"  layers: {file.Layers.Count} records");

    // Print top-down, the way Photoshop's Layers panel shows it.
    int depth = 0;
    foreach (var r in file.Layers.Reverse())
    {
        string kind = PsdLayerKinds.Classify(r).ToString().ToLowerInvariant();
        if (kind == "groupend") { depth = Math.Max(0, depth - 1); continue; }
        var indent = new string(' ', 4 + depth * 2);
        var flags = string.Join(",", new[]
        {
            r.Hidden ? "hidden" : null,
            r.Clipped ? "clipped" : null,
            r.Mask is not null ? "mask" : null,
        }.OfType<string>());
        var keys = string.Join(" ", r.Blocks.Select(b => b.Key).Where(k => k is not ("luni" or "lyid" or "lsct" or "lsdk" or "clbl" or "infx" or "knko" or "lspf" or "lclr" or "fxrp" or "iOpa" or "shmd" or "cust")));
        Console.WriteLine($"{indent}[{kind}] \"{r.Name}\" {r.BlendModeKey.Trim()} {r.Opacity * 100 / 255}% {r.Rect} {flags} {(keys.Length > 0 ? "{" + keys + "}" : "")}");
        if (kind == "group") depth++;
    }

    if (exportDir is not null)
    {
        Directory.CreateDirectory(exportDir);
        if (doc.Composite is { } comp)
            PngWriter.Write(Path.Combine(exportDir, "composite.png"), comp.Width, comp.Height, RgbaConverter.ToRgba8(comp, doc.Palette));

        int i = 0;
        foreach (var layer in doc.Root.Descendants().OfType<PixelLayer>())
        {
            i++;
            if (layer.Pixels is not { } px) continue;
            var safe = string.Concat(layer.Name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            PngWriter.Write(Path.Combine(exportDir, $"{i:D3}-{safe}.png"), px.Width, px.Height, RgbaConverter.ToRgba8(px, doc.Palette));
        }
        Console.WriteLine($"  exported to {exportDir}");
    }
    return 0;
}

static int Scan(string target)
{
    var files = Directory.Exists(target)
        ? Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".psd", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".psb", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).StartsWith("._"))
            .ToList()
        : File.ReadAllLines(target).Where(l => l.Length > 0).ToList();

    int ok = 0, failed = 0;
    var kinds = new Dictionary<string, int>();
    var blends = new Dictionary<string, int>();
    var blockKeys = new Dictionary<string, int>();
    var sw = Stopwatch.StartNew();

    foreach (var f in files)
    {
        try
        {
            var file = PsdFile.Open(f);
            var doc = file.ToDocument();
            ok++;
            foreach (var r in file.Layers)
            {
                Count(kinds, PsdLayerKinds.Classify(r).ToString());
                Count(blends, r.BlendModeKey);
                foreach (var b in r.Blocks) Count(blockKeys, b.Key);
            }
            _ = doc;
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine($"FAIL {f}\n     {ex.GetType().Name}: {ex.Message}");
        }
    }

    Console.WriteLine($"\n{ok} parsed, {failed} failed, {files.Count} total in {sw.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"layer kinds:  {Format(kinds)}");
    Console.WriteLine($"blend modes:  {Format(blends)}");
    Console.WriteLine($"layer blocks: {Format(blockKeys)}");
    return failed == 0 ? 0 : 1;

    static void Count(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;
    static string Format(Dictionary<string, int> d) =>
        string.Join("  ", d.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key.Trim()}:{kv.Value}"));
}

static int Fidelity(string target, string? exportDir)
{
    var files = File.Exists(target) && !target.EndsWith(".txt")
        ? [target]
        : Directory.Exists(target)
            ? Directory.EnumerateFiles(target, "*.ps?", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith("._")).Order().ToList()
            : File.ReadAllLines(target).Where(l => l.Length > 0).ToList();

    var scores = new List<double>();
    foreach (var f in files)
    {
        try
        {
            var file = PsdFile.Open(f);
            var doc = file.ToDocument();
            if (doc.Composite is null || file.HasRealMergedData == false)
            {
                Console.WriteLine($"  --   {Path.GetFileName(f)}: no trustworthy composite to compare against");
                continue;
            }
            if (file.WriterName is { } writer && !file.CompositeIsFromPhotoshop)
                Console.WriteLine($"  note {Path.GetFileName(f)}: composite was written by {writer}, not Photoshop");
            var sw = Stopwatch.StartNew();
            var result = Compositor.Render(doc);
            var rendered = result.ToRgba8();
            sw.Stop();
            var reference = RgbaConverter.ToRgba8(doc.Composite, doc.Palette);
            var report = FidelityReport.Compare(rendered, reference, doc.Width, doc.Height, flattenOverWhite: doc.Composite.Alpha is null);
            scores.Add(report.MatchPercent);
            Console.WriteLine($"{report.MatchPercent,6:F2}% {Path.GetFileName(f)}  (mean err {report.MeanError:F2}, max {report.MaxError}, {sw.ElapsedMilliseconds} ms){(result.Warnings.Count > 0 ? "  ! " + string.Join("; ", result.Warnings.Distinct().Take(3)) : "")}");

            if (exportDir is not null)
            {
                var dir = Path.Combine(exportDir, Path.GetFileNameWithoutExtension(f));
                Directory.CreateDirectory(dir);
                PngWriter.Write(Path.Combine(dir, "rendered.png"), doc.Width, doc.Height, rendered);
                PngWriter.Write(Path.Combine(dir, "reference.png"), doc.Width, doc.Height, reference);
                PngWriter.Write(Path.Combine(dir, "diff.png"), doc.Width, doc.Height, report.DiffRgba);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL   {Path.GetFileName(f)}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    if (scores.Count > 0)
        Console.WriteLine($"\n{scores.Count} rendered; mean match {scores.Average():F2}%, {scores.Count(s => s >= 99.9)} at >=99.9%, {scores.Count(s => s < 95)} below 95%");
    return 0;
}

static int Bench(string path)
{
    var sw = Stopwatch.StartNew();
    var doc = PsdFile.Open(path).ToDocument();
    Console.WriteLine($"parse: {sw.ElapsedMilliseconds} ms ({doc.Width}x{doc.Height})");

    sw.Restart();
    Compositor.Render(doc);
    Console.WriteLine($"full render (cold): {sw.ElapsedMilliseconds} ms");
    sw.Restart();
    Compositor.Render(doc);
    Console.WriteLine($"full render (warm): {sw.ElapsedMilliseconds} ms");

    if (doc.Composite is { } comp)
    {
        sw.Restart();
        var refPixels = RgbaConverter.ToRgba8(comp, doc.Palette);
        Console.WriteLine($"composite to RGBA: {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        var r = Compositor.Render(doc).ToRgba8();
        Console.WriteLine($"render + to RGBA: {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        FidelityReport.Compare(r, refPixels, doc.Width, doc.Height, includeDiff: false);
        Console.WriteLine($"fidelity score: {sw.ElapsedMilliseconds} ms");
    }

    using var renderer = Renderers.CreateDefault();
    renderer.Render(doc);
    var layers = doc.Root.Descendants().OfType<PixelLayer>().Where(l => l.Visible).ToList();
    var picks = new[] { 0, layers.Count / 4, layers.Count / 2, 3 * layers.Count / 4, layers.Count - 1 }.Distinct();
    foreach (int i in picks)
    {
        var hidden = new HashSet<LayerNode> { layers[i] };
        sw.Restart();
        renderer.Render(doc, new RenderOptions { Hidden = hidden });
        long off = sw.ElapsedMilliseconds;
        sw.Restart();
        renderer.Render(doc);
        long on = sw.ElapsedMilliseconds;
        Console.WriteLine($"toggle layer {i + 1}/{layers.Count} \"{layers[i].Name}\": off {off} ms, on {on} ms");
    }
    return 0;
}

static int Effects(string path)
{
    var file = PsdFile.Open(path);
    foreach (var r in file.Layers)
        foreach (var b in r.Blocks.Where(b => b.Key is "lfx2" or "lmfx" && b.Data is not null))
        {
            Console.WriteLine($"== \"{r.Name}\" [{b.Key}]");
            try
            {
                // Object effects version (4 bytes), then a versioned descriptor.
                Console.WriteLine(Strayta.Psd.Descriptors.DescriptorReader.ReadVersioned(b.Data!, 4));
            }
            catch (PsdFormatException ex)
            {
                Console.WriteLine($"   unreadable: {ex.Message}");
            }
        }
    return 0;
}

static int RoundTrip(string target, string? keepDir)
{
    var files = File.Exists(target) ? [target]
        : Directory.EnumerateFiles(target, "*.ps?", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith("._")).Order().ToList();
    int ok = 0, bad = 0;
    foreach (var f in files)
    {
        try
        {
            var original = PsdFile.Open(f, new PsdReadOptions { MaxRawBlockBytes = long.MaxValue });
            var doc = original.ToDocument();
            var ms = new MemoryStream();
            PsdWriter.Write(doc, ms);
            if (keepDir is not null)
            {
                Directory.CreateDirectory(keepDir);
                File.WriteAllBytes(Path.Combine(keepDir, Path.GetFileName(f)), ms.ToArray());
            }
            ms.Position = 0;
            var again = PsdFile.Read(ms, new PsdReadOptions { MaxRawBlockBytes = long.MaxValue });
            var problems = CompareFiles(original, again).Take(5).ToList();
            if (problems.Count == 0) { ok++; Console.WriteLine($"ok    {Path.GetFileName(f)} ({new FileInfo(f).Length:N0} -> {ms.Length:N0} bytes)"); }
            else { bad++; Console.WriteLine($"DIFF  {Path.GetFileName(f)}\n      " + string.Join("\n      ", problems)); }
        }
        catch (Exception ex)
        {
            bad++;
            Console.WriteLine($"FAIL  {Path.GetFileName(f)}: {ex.GetType().Name}: {ex.Message}");
        }
    }
    Console.WriteLine($"\n{ok} identical after round trip, {bad} with differences");
    return bad == 0 ? 0 : 1;
}

static IEnumerable<string> CompareFiles(PsdFile a, PsdFile b)
{
    if (a.Header != b.Header) yield return $"header {a.Header} vs {b.Header}";
    if (a.Layers.Count != b.Layers.Count) { yield return $"layer count {a.Layers.Count} vs {b.Layers.Count}"; yield break; }
    for (int i = 0; i < a.Layers.Count; i++)
    {
        var (x, y) = (a.Layers[i], b.Layers[i]);
        string at = $"layer {i} \"{x.Name}\"";
        if (x.Name != y.Name) yield return $"{at}: name \"{y.Name}\"";
        if (x.BlendModeKey != y.BlendModeKey || x.Opacity != y.Opacity || x.Clipped != y.Clipped || (x.Flags & 0x03) != (y.Flags & 0x03))
            yield return $"{at}: properties differ";
        if (x.SectionType != y.SectionType) yield return $"{at}: section {x.SectionType} vs {y.SectionType}";
        bool hasPixels = x.ChannelData.Count > 0;
        if (hasPixels && x.Rect != y.Rect) yield return $"{at}: rect {x.Rect} vs {y.Rect}";
        foreach (var (id, plane) in x.ChannelData)
            if (!y.ChannelData.TryGetValue(id, out var p2) || !plane.Data.AsSpan().SequenceEqual(p2.Data))
                yield return $"{at}: channel {id} pixels differ";
        var keep = (PsdLayerRecord r) => r.Blocks.Where(k => k.Key is not ("luni" or "lsct" or "lsdk" or "iOpa")).ToList();
        var (bx, by) = (keep(x), keep(y));
        if (bx.Count != by.Count || bx.Zip(by).Any(p => p.First.Key != p.Second.Key || !p.First.Data!.AsSpan().SequenceEqual(p.Second.Data)))
            yield return $"{at}: blocks differ ({string.Join(",", bx.Select(k => k.Key))} vs {string.Join(",", by.Select(k => k.Key))})";
        if (x.Name != y.Name || PsdBlocks.ReadFillOpacity(x.FindBlock("iOpa")) != PsdBlocks.ReadFillOpacity(y.FindBlock("iOpa")) && x.FindBlock("iOpa") is not null)
            yield return $"{at}: fill differs";
    }

    var ga = a.GlobalBlocks.Where(k => k.Key is not ("Lr16" or "Lr32" or "Layr")).ToList();
    var gb = b.GlobalBlocks.Where(k => k.Key is not ("Lr16" or "Lr32" or "Layr")).ToList();
    if (ga.Count != gb.Count || ga.Zip(gb).Any(p => p.First.Key != p.Second.Key || !p.First.Data!.AsSpan().SequenceEqual(p.Second.Data)))
        yield return "global blocks differ";

    // The composite is un-matted on read and re-matted on write, so allow rounding.
    var (ca, cb) = (a.ToDocument().Composite, b.ToDocument().Composite);
    if ((ca is null) != (cb is null)) yield return "composite presence differs";
    else if (ca is not null && cb is not null)
    {
        var (ra, rb) = (RgbaConverter.ToRgba8(ca), RgbaConverter.ToRgba8(cb));
        int worst = 0;
        for (int i = 0; i < ra.Length; i++) worst = Math.Max(worst, Math.Abs(ra[i] - rb[i]) * (ra[i | 3] > 8 ? 1 : 0));
        if (worst > 2) yield return $"composite differs by up to {worst}";
    }
}
