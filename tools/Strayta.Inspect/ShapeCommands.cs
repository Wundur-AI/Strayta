using System.Buffers.Binary;
using System.Diagnostics;
using Strayta.Core;
using Strayta.Core.Paths;
using Strayta.Psd;
using Strayta.Psd.Descriptors;

namespace Strayta.Inspect;

/// <summary>Commands for vector masks, shape layers and paths.</summary>
internal static class ShapeCommands
{
    public static IEnumerable<string> Files(string target) =>
        File.Exists(target) ? [target]
        : Directory.EnumerateFiles(target, "*.ps?", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith("._")).Order();

    /// <summary>Dumps every vector mask, live shape, vector stroke and path resource.</summary>
    public static int Dump(string[] targets, bool verbose)
    {
        int paths = 0, identical = 0;
        foreach (var f in targets.SelectMany(Files))
        {
            PsdFile file;
            try { file = PsdFile.Open(f, new PsdReadOptions { SkipComposite = true, SkipLayerPixels = !verbose }); }
            catch (Exception e) { Console.WriteLine($"FAIL {f}: {e.Message}"); continue; }
            var (w, h) = (file.Header.Width, file.Header.Height);
            bool header = false;
            void Header()
            {
                if (header) return;
                header = true;
                Console.WriteLine($"== {Path.GetFileName(f)} {w}x{h}");
            }
            foreach (var res in file.Resources.Where(r => r.Id is 1025 or (>= 2000 and <= 2997)))
            {
                Header();
                var p = PsdPaths.Decode(res.Data, 0, w, h);
                paths++;
                bool same = PsdPaths.Encode(p, w, h).AsSpan().SequenceEqual(res.Data);
                if (same) identical++;
                Console.WriteLine($"  resource {res.Id} \"{res.Name}\" {res.Data.Length} bytes layout {p.RecordLayout} init {p.InitialFillAll} {(same ? "" : "RE-ENCODE DIFFERS")}");
                if (verbose) PrintPath(p);
            }
            foreach (var r in file.Layers)
            {
                var vm = r.FindBlock("vmsk") ?? r.FindBlock("vsms");
                var keys = r.Blocks.Select(b => b.Key).Where(k => k is "vmsk" or "vsms" or "vogk" or "vscg" or "vstk" or "SoCo" or "GdFl" or "PtFl").ToList();
                if (keys.Count == 0) continue;
                Header();
                string mask = r.Mask is { } m ? $"mask {m.Rect} def {m.DefaultColor} flags {m.Flags:x2}{(m.RealRect is { } rr ? $" real {rr} flags {m.RealFlags:x2}" : "")}" : "no mask";
                Console.WriteLine($"  \"{r.Name}\" {PsdLayerKinds.Classify(r)} rect {r.Rect} {mask} [{string.Join(" ", keys)}]");
                if (vm?.Data is { Length: >= 8 } data)
                {
                    int version = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data);
                    int flags = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4));
                    var p = PsdPaths.Decode(data, 8, w, h);
                    paths++;
                    bool same = PsdPaths.Encode(p, w, h).AsSpan().SequenceEqual(data.AsSpan(8));
                    if (same) identical++;
                    Console.WriteLine($"    {vm.Key} v{version} flags {flags:x} layout {p.RecordLayout} init {p.InitialFillAll} subpaths {p.Subpaths.Count} {(same ? "" : "RE-ENCODE DIFFERS " + FirstDiff(PsdPaths.Encode(p, w, h), data.AsSpan(8).ToArray()))}");
                    if (verbose) PrintPath(p);
                }
                if (verbose)
                    foreach (var key in new[] { "vogk", "vstk", "vscg", "SoCo", "GdFl", "PtFl" })
                        if (r.FindBlock(key)?.Data is { } d)
                        {
                            try
                            {
                                int skip = key is "vogk" or "vscg" ? 4 : 0;
                                if (key == "vscg") Console.WriteLine($"    vscg key {System.Text.Encoding.ASCII.GetString(d, 0, 4)}");
                                Console.WriteLine($"    {key}: {DescriptorReader.ReadVersioned(d, skip).ToString().Replace("\n", "\n      ")}");
                            }
                            catch (Exception e) { Console.WriteLine($"    {key}: unreadable {e.Message} {Convert.ToHexString(d.AsSpan(0, Math.Min(16, d.Length)))}"); }
                        }
            }
        }
        Console.WriteLine($"\n{paths} paths, {identical} re-encoded byte for byte");
        return 0;
    }

    private static string FirstDiff(byte[] a, byte[] b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        return $"at {i} (record {i / 26} byte {i % 26}) lengths {a.Length}/{b.Length}: {Convert.ToHexString(a.AsSpan(i / 26 * 26, Math.Min(26, a.Length - i / 26 * 26)))} vs {Convert.ToHexString(b.AsSpan(i / 26 * 26, Math.Min(26, b.Length - i / 26 * 26)))}";
    }

    private static void PrintPath(Strayta.Core.Paths.VectorPath p)
    {
        foreach (var s in p.Subpaths)
        {
            Console.WriteLine($"      {(s.Closed ? "closed" : "open")} {s.Operation} knots {s.Knots.Count} origin {s.OriginIndex} tail {Convert.ToHexString(s.RecordTail ?? [])}");
            foreach (var k in s.Knots.Take(8))
                Console.WriteLine($"        {(k.Linked ? "L" : "U")} in ({k.In.X:F2},{k.In.Y:F2}) at ({k.Anchor.X:F2},{k.Anchor.Y:F2}) out ({k.Out.X:F2},{k.Out.Y:F2})");
        }
    }

    /// <summary>
    /// Rasterizes every vector mask and compares it with what Photoshop stored: the user mask when it is marked as
    /// rendered from vector data (flag 0x08), or else a fill layer's transparency. Prints per-layer match (share of
    /// pixels within 1/255) and the mean and largest difference. SHAPEFID_SHOW=layer name prints both side by side (small
    /// layers only).
    /// </summary>
    public static int Fidelity(string[] targets, bool verbose)
    {
        var scores = new List<(double Match, long Pixels)>();
        long time = 0;
        foreach (var f in targets.SelectMany(Files))
        {
            PsdFile file;
            try { file = PsdFile.Open(f, new PsdReadOptions { SkipComposite = true }); }
            catch (Exception e) { Console.WriteLine($"FAIL {f}: {e.Message}"); continue; }
            var (w, h) = (file.Header.Width, file.Header.Height);
            foreach (var r in file.Layers)
            {
                if ((r.FindBlock("vmsk") ?? r.FindBlock("vsms"))?.Data is not { Length: >= 8 } data) continue;
                Plane? reference;
                PixelRect rect;
                string against;
                if (r.Mask is { } m && (m.Flags & 0x08) != 0 && r.ChannelData.TryGetValue(PsdChannelId.UserMask, out var um))
                    (reference, rect, against) = (um, m.Rect, "mask");
                else if (r.FindBlock("vstk") is null && PsdLayerKinds.Classify(r) == PsdLayerKind.Fill && r.ChannelData.TryGetValue(PsdChannelId.Transparency, out var ta))
                    (reference, rect, against) = (ta, r.Rect, "alpha");
                else continue;
                if (rect.IsEmpty || reference.BitDepth != 8) continue;
                int flags = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4));
                var path = PsdPaths.Decode(data, 8, w, h);
                var sw = Stopwatch.StartNew();
                var ours = PathRasterizer.Rasterize(path, rect);
                time += sw.ElapsedTicks;
                if ((flags & 1) != 0) for (int i = 0; i < ours.Length; i++) ours[i] = (byte)(255 - ours[i]);
                long match = 0, sum = 0;
                int max = 0;
                for (int i = 0; i < ours.Length; i++)
                {
                    int d = Math.Abs(ours[i] - reference.Data[i]);
                    if (d <= 1) match++;
                    sum += d;
                    max = Math.Max(max, d);
                }
                double pct = 100.0 * match / ours.Length;
                if (Environment.GetEnvironmentVariable("SHAPEFID_SHOW") is { } show && r.Name == show && rect.Width <= 60)
                {
                    PrintPath(path);
                    Console.WriteLine($"rect {rect}");
                    for (int y = 0; y < rect.Height; y++)
                    {
                        Console.WriteLine(string.Join(" ", Enumerable.Range(0, rect.Width).Select(x => ours[y * rect.Width + x].ToString("x2")))
                            + "  |  " + string.Join(" ", Enumerable.Range(0, rect.Width).Select(x => reference.Data[y * rect.Width + x].ToString("x2"))));
                    }
                }
                scores.Add((pct, ours.Length));
                if (verbose || pct < 99.9)
                    Console.WriteLine($"{pct,7:F3}% {Path.GetFileName(f)} \"{r.Name}\" vs {against} {rect.Width}x{rect.Height} subpaths {path.Subpaths.Count}"
                        + $"{(PathRasterizer.IsLegacy(path) ? " legacy" : "")} flags {flags} mean {(double)sum / ours.Length:F3} max {max}");
            }
        }
        if (scores.Count > 0)
            Console.WriteLine($"\n{scores.Count} vector masks; mean match {scores.Average(s => s.Match):F3}%, pixel-weighted "
                + $"{scores.Sum(s => s.Match * s.Pixels) / scores.Sum(s => s.Pixels):F3}%, {scores.Count(s => s.Match >= 99.9)} at >=99.9%, "
                + $"{scores.Count(s => s.Match < 99)} below 99%; rasterizing took {time * 1000.0 / Stopwatch.Frequency:F0} ms");
        return 0;
    }
}
