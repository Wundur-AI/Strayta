using System.Diagnostics;
using System.Globalization;
using Strayta.Core;
using Strayta.Core.Text;
using Strayta.Psd;
using Strayta.Psd.Text;
using Strayta.Text;

namespace Strayta.Inspect;

/// <summary>Type layer commands: dump the text model, and render type layers to compare with Photoshop's pixels.</summary>
internal static class TextCommands
{
    public static int Dump(string path)
    {
        var file = PsdFile.Open(path, new PsdReadOptions { SkipLayerPixels = true, SkipComposite = true, MaxRawBlockBytes = long.MaxValue });
        foreach (var record in file.Layers.Where(PsdTypeLayer.IsTypeLayer))
        {
            var data = PsdTypeLayer.Read(record);
            Console.WriteLine($"\"{record.Name}\" {record.Rect}");
            if (data is null) { Console.WriteLine("  unreadable"); continue; }
            var t = data.Transform;
            Console.WriteLine($"  {data.Kind} {data.Orientation} aa={data.AntiAlias} warp={data.Warp ?? "none"} transform=[{t.XX:0.###} {t.XY:0.###} {t.YX:0.###} {t.YY:0.###} {t.TX:0.###} {t.TY:0.###}]"
                + (data.Kind == TextKind.Paragraph ? $" box={data.Box}" : ""));
            Console.WriteLine($"  text: \"{Escape(data.Text)}\"");
            int at = 0;
            foreach (var run in data.StyleRuns)
            {
                var s = run.Style;
                Console.WriteLine($"  style [{at},{at + run.Length}) {s.FontPostScriptName} {s.FontSize:0.###} lead={(s.AutoLeading ? "auto" : s.Leading.ToString("0.##"))} track={s.Tracking} kern={s.Kerning}/{s.ManualKerning} color=({s.FillColor.R:0.###},{s.FillColor.G:0.###},{s.FillColor.B:0.###})"
                    + (s.FauxBold ? " fauxbold" : "") + (s.FauxItalic ? " fauxitalic" : "") + (s.Caps != TextCaps.Normal ? $" {s.Caps}" : "")
                    + (s.Underline ? " underline" : "") + (s.Strikethrough ? " strike" : "") + (s.HorizontalScale != 1 ? $" hscale={s.HorizontalScale}" : "")
                    + (s.VerticalScale != 1 ? $" vscale={s.VerticalScale}" : "") + (s.BaselineShift != 0 ? $" shift={s.BaselineShift}" : ""));
                at += run.Length;
            }
            at = 0;
            foreach (var run in data.ParagraphRuns)
            {
                var p = run.Style;
                Console.WriteLine($"  paragraph [{at},{at + run.Length}) {p.Justification} indent={p.StartIndent}/{p.FirstLineIndent}/{p.EndIndent} space={p.SpaceBefore}/{p.SpaceAfter} autolead={p.AutoLeadingFactor}");
                at += run.Length;
            }
        }
        return 0;
    }

    private static string Escape(string s) => s.Replace("\n", "\\n").Replace("\u0003", "\\x03");

    /// <summary>
    /// Renders every type layer whose fonts are available and compares it with the pixels Photoshop stored.
    /// Options: --fonts &lt;dir&gt; (extra font folders, repeatable), --all (also layers with missing fonts, drawn with the
    /// substitute), --export &lt;dir&gt; (PNGs of each comparison).
    /// </summary>
    public static int Fidelity(string[] args)
    {
        var targets = new List<string>();
        var fontDirs = new List<string>();
        string? export = null;
        bool all = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--fonts": fontDirs.Add(args[++i]); break;
                case "--export": export = args[++i]; break;
                case "--all": all = true; break;
                default: targets.Add(args[i]); break;
            }
        }
        var catalog = FontCatalog.System;
        var extra = FontCatalog.FromFiles([]);
        foreach (var dir in fontDirs) extra.AddFolder(dir);
        var merged = FontCatalog.Merge(catalog, extra);

        var files = targets.SelectMany(t => Directory.Exists(t)
            ? Directory.EnumerateFiles(t, "*.ps?", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith("._")).Order()
            : (IEnumerable<string>)[t]).ToList();

        var results = new List<(string Name, TextFidelityResult R, string Fonts, double Ms, bool Missing, TextAntiAlias Aa)>();
        var fontUse = new Dictionary<string, (int Layers, string Where)>();
        int total = 0, skipped = 0, failed = 0, unsupported = 0;
        foreach (var f in files)
        {
            Document doc;
            try
            {
                doc = PsdFile.Open(f).ToDocument();
            }
            catch (Exception e)
            {
                Console.WriteLine($"FAIL {Path.GetFileName(f)}: {e.Message}");
                continue;
            }
            foreach (var layer in doc.Root.Descendants().OfType<PixelLayer>())
            {
                if (layer.SourceData is not PsdLayerRecord record || !PsdTypeLayer.IsTypeLayer(record)) continue;
                total++;
                string name = $"{Path.GetFileNameWithoutExtension(f)} :: {layer.Name}";
                var data = PsdTypeLayer.Read(record);
                if (data is null) { failed++; Console.WriteLine($"  unreadable {name}"); continue; }
                var fonts = data.FontsUsed;
                bool missing = false;
                foreach (var font in fonts)
                {
                    string where = catalog.Contains(font) ? "system" : extra.Contains(font) ? "extra" : "missing";
                    if (where == "missing") missing = true;
                    fontUse[font] = (fontUse.GetValueOrDefault(font).Layers + 1, where);
                }
                if (data.Warp is not null || data.Orientation == TextOrientation.Vertical)
                {
                    unsupported++;
                    Console.WriteLine($"  --     {name}: {(data.Warp is not null ? "warped" : "vertical")}, not compared");
                    continue;
                }
                if (missing && !all) { skipped++; continue; }
                if (layer.Pixels is null) { Console.WriteLine($"  --     {name}: no stored pixels"); continue; }

                var sw = Stopwatch.StartNew();
                var render = TextRenderer.Render(data, new TextRenderOptions { Fonts = merged, ColorMode = doc.ColorMode, BitDepth = doc.BitDepth });
                sw.Stop();
                if (render.Pixels is null) { failed++; Console.WriteLine($"  EMPTY  {name}"); continue; }
                var r = TextFidelity.Compare(render.Pixels, render.Bounds, layer.Pixels, layer.Bounds);
                results.Add((name, r, string.Join(",", fonts), sw.Elapsed.TotalMilliseconds, missing, data.AntiAlias));
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{r.MatchPercent,6:F1}% sim {r.Similarity:F3}  best {r.BestMatchPercent,5:F1}% at ({r.OffsetX},{r.OffsetY})  color {r.ColorError:F1}  {sw.Elapsed.TotalMilliseconds,6:F1} ms  {name} [{string.Join(",", fonts)}{(missing ? " MISSING" : "")}] {data.AntiAlias} {data.Kind}"));
                if (export is not null) Export(export, name, render, layer);
            }
        }

        Console.WriteLine($"\n{total} type layers; {results.Count} compared, {skipped} skipped for missing fonts, {unsupported} warped/vertical, {failed} failed");
        Console.WriteLine("fonts: " + string.Join("  ", fontUse.OrderByDescending(kv => kv.Value.Layers).Select(kv => $"{kv.Key} ({kv.Value.Layers}, {kv.Value.Where})")));
        var installed = results.Where(r => !r.Missing).ToList();
        if (installed.Count > 0)
        {
            var m = installed.Select(r => r.R.MatchPercent).Order().ToList();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"installed fonts: {installed.Count} layers; match mean {m.Average():F1}%, median {m[m.Count / 2]:F1}%, min {m[0]:F1}%; similarity mean {installed.Average(r => r.R.Similarity):F3}; "
                + $"≥99% {m.Count(v => v >= 99)}, ≥95% {m.Count(v => v >= 95)}, ≥90% {m.Count(v => v >= 90)}; aligned (0,0) {installed.Count(r => r.R.OffsetX == 0 && r.R.OffsetY == 0)}; "
                + $"render mean {installed.Average(r => r.Ms):F1} ms, max {installed.Max(r => r.Ms):F1} ms"));
            foreach (var g in installed.GroupBy(r => r.Aa))
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {g.Key}: {g.Count()} layers, match mean {g.Average(r => r.R.MatchPercent):F1}%, similarity {g.Average(r => r.R.Similarity):F3}"));
            var unique = installed.DistinctBy(r => (r.Name.Split(" :: ")[1], r.R.Reference.Width, r.R.Reference.Height, r.Fonts)).ToList();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  distinct layers (same name, size and font counted once): {unique.Count}, match mean {unique.Average(r => r.R.MatchPercent):F1}%, similarity {unique.Average(r => r.R.Similarity):F3}"));
            foreach (var w in installed.OrderBy(r => r.R.MatchPercent).Take(5))
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  worst: {w.R.MatchPercent:F1}% (best {w.R.BestMatchPercent:F1}% at {w.R.OffsetX},{w.R.OffsetY}) {w.Name} [{w.Fonts}] rendered {w.R.Rendered} vs {w.R.Reference}"));
        }
        return 0;
    }

    private static void Export(string dir, string name, TextRenderResult render, PixelLayer layer)
    {
        Directory.CreateDirectory(dir);
        var a = render.Bounds;
        var b = layer.Bounds;
        var u = new PixelRect(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
        int w = u.Width, h = u.Height;
        // Three panels: ours, Photoshop's, difference (red: ours only, green: Photoshop's only).
        var rgba = new byte[w * 3 * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int va = Alpha(render.Pixels!, a, x + u.Left, y + u.Top), vb = Alpha(layer.Pixels!, b, x + u.Left, y + u.Top);
                Put(rgba, w * 3, x, y, 255 - va, 255 - va, 255 - va);
                Put(rgba, w * 3, x + w, y, 255 - vb, 255 - vb, 255 - vb);
                int d = va - vb;
                Put(rgba, w * 3, x + 2 * w, y, d > 0 ? 255 : 255 + d, d < 0 ? 255 : 255 - d, 255 - Math.Abs(d));
            }
        // Small layers are enlarged (nearest neighbour) so single pixels can be seen.
        int k = Math.Clamp(900 / Math.Max(1, w * 3), 1, 8);
        var big = new byte[w * 3 * k * h * k * 4];
        for (int y = 0; y < h * k; y++)
            for (int x = 0; x < w * 3 * k; x++)
                Array.Copy(rgba, ((y / k) * w * 3 + x / k) * 4, big, (y * w * 3 * k + x) * 4, 4);
        string safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        PngWriter.Write(Path.Combine(dir, $"text1-{safe}.png"), w * 3 * k, h * k, big);
    }

    private static int Alpha(Raster r, PixelRect at, int x, int y) =>
        x < at.Left || y < at.Top || x >= at.Right || y >= at.Bottom || r.Alpha is null ? 0
            : (int)Math.Round(r.Alpha.GetNormalized((y - at.Top) * at.Width + x - at.Left) * 255);

    private static void Put(byte[] rgba, int stride, int x, int y, int r, int g, int b)
    {
        int o = (y * stride + x) * 4;
        rgba[o] = (byte)Math.Clamp(r, 0, 255);
        rgba[o + 1] = (byte)Math.Clamp(g, 0, 255);
        rgba[o + 2] = (byte)Math.Clamp(b, 0, 255);
        rgba[o + 3] = 255;
    }
}
