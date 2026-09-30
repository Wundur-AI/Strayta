using System.Globalization;
using System.Text.RegularExpressions;
using Strayta.Core;

namespace Strayta.Rendering.Export;

/// <summary>A width or height with its unit ("px", "in", "cm" or "mm"), or unspecified ("?": keep proportions).</summary>
public readonly record struct AssetLength(double Value, string Unit)
{
    public double ToPixels(double resolution) => Unit switch
    {
        "in" => Value * resolution,
        "cm" => Value * resolution / 2.54,
        "mm" => Value * resolution / 25.4,
        _ => Value,
    };
}

/// <summary>One file an asset layer asks for.</summary>
/// <param name="FileName">Relative path inside the assets folder: folders, name and extension ("icons/home.png").</param>
/// <param name="Format">Null for SVG, which is recognised but not written.</param>
/// <param name="Quality">JPEG / WebP quality 1–100, or PNG bit depth (8, 24 or 32), when the name gives one.</param>
/// <param name="Scale">From a "200%" prefix.</param>
/// <param name="Width">From a "300x200" prefix; null where it is "?" or missing.</param>
public sealed record AssetSpec(string FileName, ExportFormat? Format, int? Quality, double? Scale, AssetLength? Width, AssetLength? Height)
{
    public bool HasSize => Width is not null || Height is not null;
}

/// <summary>A variant from a "default" layer: every asset is also written with this scale or size, folder and suffix.</summary>
public sealed record AssetDefault(double? Scale, AssetLength? Width, AssetLength? Height, string Folder, string Suffix);

/// <summary>
/// Parses layer names that ask for image assets, following the naming convention Adobe publishes for Photoshop's
/// Generate › Image Assets (written from that description, no code taken):
/// <list type="bullet">
/// <item>a layer or group whose name is a file name is an asset: <c>icon.png</c>, <c>photo.jpg</c>, <c>anim.gif</c>, <c>hero.webp</c>;</item>
/// <item>several files are separated by commas (or "+"): <c>hero.png, hero@2x.png</c>;</item>
/// <item>a scale or size goes before the name: <c>200% button@2x.png</c>, <c>300x200 thumb.jpg</c>, <c>?x100 logo.png</c>,
/// <c>2in x 1in print.png</c> (units px, in, cm, mm);</item>
/// <item>quality follows the extension: <c>logo.jpg80%</c> or <c>logo.jpg8</c> (1–10 means ×10%), <c>icon.png8</c> /
/// <c>png24</c> / <c>png32</c> for bit depth, <c>hero.webp70%</c>;</item>
/// <item>folders go in the name: <c>assets/icons/home.png</c>;</item>
/// <item>a layer named <c>default …</c> adds variants to every asset: <c>default 200% @2x, 300% @3x</c> or
/// <c>default 50% small/</c> (scale or size, then a folder ending in "/" and/or a suffix).</item>
/// </list>
/// Anything else in a name (words without an extension) makes that part not an asset. SVG is recognised but not written.
/// </summary>
public static partial class ImageAssetNames
{
    // name.ext with an optional quality right after the extension: jpg80%, jpg8, png8, png24, webp90%.
    [GeneratedRegex(@"^(?<name>.+)\.(?<ext>png|jpe?g|gif|webp|svg)(?<q>\d{1,3})?(?<pct>%)?$", RegexOptions.IgnoreCase)]
    private static partial Regex FileRegex();

    [GeneratedRegex(@"^(?<v>\d+(?:\.\d+)?)%$")]
    private static partial Regex ScaleRegex();

    // 300x200, ?x100, 300x?, 3inx2in, 10cm x 5cm (spaces around x are removed before matching).
    [GeneratedRegex(@"^(?<w>\?|\d+(?:\.\d+)?)(?<wu>px|in|cm|mm)?x(?<h>\?|\d+(?:\.\d+)?)(?<hu>px|in|cm|mm)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    /// <summary>True when the name starts a "default" settings layer.</summary>
    public static bool IsDefaultLayer(string name) =>
        name.TrimStart().StartsWith("default ", StringComparison.OrdinalIgnoreCase) || name.Trim().Equals("default", StringComparison.OrdinalIgnoreCase);

    /// <summary>The assets a layer name asks for; empty when it is not an asset name.</summary>
    public static IReadOnlyList<AssetSpec> Parse(string layerName)
    {
        if (IsDefaultLayer(layerName)) return [];
        var specs = new List<AssetSpec>();
        foreach (var part in Split(layerName))
            if (ParseOne(part) is { } spec) specs.Add(spec);
        return specs;
    }

    /// <summary>The variants of a "default" layer; empty for other names.</summary>
    public static IReadOnlyList<AssetDefault> ParseDefaults(string layerName)
    {
        if (!IsDefaultLayer(layerName)) return [];
        var rest = layerName.TrimStart()[7..];
        var list = new List<AssetDefault>();
        foreach (var part in Split(rest))
        {
            var tokens = Tokens(part);
            double? scale = null;
            AssetLength? w = null, h = null;
            string folder = "", suffix = "";
            foreach (var token in tokens)
            {
                if (TryScale(token, out double s)) scale = s;
                else if (TrySize(token, out var sw, out var sh)) (w, h) = (sw, sh);
                else if (token.EndsWith('/')) folder = Sanitize(token.TrimEnd('/'), allowSlash: true);
                else if (token.Contains('/'))
                {
                    int slash = token.LastIndexOf('/');
                    folder = Sanitize(token[..slash], allowSlash: true);
                    suffix = Sanitize(token[(slash + 1)..], allowSlash: false);
                }
                else suffix = Sanitize(token, allowSlash: false);
            }
            if (scale is not null || w is not null || h is not null || folder.Length > 0 || suffix.Length > 0)
                list.Add(new AssetDefault(scale, w, h, folder, suffix));
        }
        return list;
    }

    private static IEnumerable<string> Split(string name) =>
        name.Split([',', '+'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Words of one part, with the spaces of "3in x 2in" and "300 x 200" closed up so a size is one token.</summary>
    private static List<string> Tokens(string part)
    {
        var closed = Regex.Replace(part, @"(?<=[\d?]|px|in|cm|mm)\s*x\s*(?=[\d?])", "x", RegexOptions.IgnoreCase);
        return closed.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static AssetSpec? ParseOne(string part)
    {
        var tokens = Tokens(part);
        if (tokens.Count == 0) return null;
        // The file name is the last token(s): names may contain spaces ("my icon.png") after the modifiers.
        int first = 0;
        double? scale = null;
        AssetLength? width = null, height = null;
        while (first < tokens.Count - 1)
        {
            if (TryScale(tokens[first], out double s)) scale = s;
            else if (TrySize(tokens[first], out var w, out var h)) (width, height) = (w, h);
            else break;
            first++;
        }
        string file = string.Join(' ', tokens.Skip(first));
        var m = FileRegex().Match(file);
        if (!m.Success) return null;
        string ext = m.Groups["ext"].Value.ToLowerInvariant();
        ExportFormat? format = ext switch
        {
            "jpg" or "jpeg" => ExportFormat.Jpeg,
            "gif" => ExportFormat.Gif,
            "webp" => ExportFormat.WebP,
            "svg" => null,
            _ => ExportFormat.Png,
        };
        int? quality = null;
        if (m.Groups["q"].Success && int.TryParse(m.Groups["q"].Value, CultureInfo.InvariantCulture, out int q))
        {
            if (format == ExportFormat.Png) quality = q is 8 or 24 or 32 ? q : null;
            else quality = m.Groups["pct"].Success ? Math.Clamp(q, 1, 100) : q is >= 1 and <= 10 ? q * 10 : Math.Clamp(q, 1, 100);
        }
        string name = Sanitize(m.Groups["name"].Value, allowSlash: true);
        if (name.Length == 0 || name.EndsWith('/')) return null;
        string extension = ext == "jpeg" ? "jpg" : ext;
        return new AssetSpec($"{name}.{extension}", format, quality, scale, width, height);
    }

    private static bool TryScale(string token, out double scale)
    {
        scale = 0;
        var m = ScaleRegex().Match(token);
        if (!m.Success || !double.TryParse(m.Groups["v"].Value, CultureInfo.InvariantCulture, out double v) || v <= 0) return false;
        scale = v / 100;
        return true;
    }

    private static bool TrySize(string token, out AssetLength? width, out AssetLength? height)
    {
        width = height = null;
        var m = SizeRegex().Match(token);
        if (!m.Success) return false;
        width = Length(m.Groups["w"].Value, m.Groups["wu"].Value);
        height = Length(m.Groups["h"].Value, m.Groups["hu"].Value);
        return width is not null || height is not null;

        static AssetLength? Length(string v, string unit) =>
            v == "?" || !double.TryParse(v, CultureInfo.InvariantCulture, out double d) || d <= 0
                ? null
                : new AssetLength(d, unit.Length == 0 ? "px" : unit.ToLowerInvariant());
    }

    /// <summary>
    /// Makes a layer name safe as a file name on every system: no path characters or control characters, no leading
    /// dots or trailing dots and spaces. With <paramref name="allowSlash"/>, "/" separates folders and ".." parts are dropped.
    /// </summary>
    public static string Sanitize(string name, bool allowSlash = false)
    {
        if (allowSlash)
            return string.Join('/', name.Split('/').Select(p => Sanitize(p)).Where(p => p.Length > 0 && p != ".."));
        var chars = name.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim().TrimStart('.').TrimEnd('.', ' ');
    }
}

/// <summary>A file Generate Image Assets wrote, or why it could not.</summary>
public sealed record AssetResult(string Path, string LayerName, string? Error);

/// <summary>
/// File › Generate › Image Assets: every layer or group named as an asset (see <see cref="ImageAssetNames"/>) is
/// rendered on its own, trimmed to its pixels (an artboard to its bounds), scaled and written into the assets folder,
/// normally "&lt;document&gt;-assets" next to the file.
/// </summary>
public static class ImageAssetGenerator
{
    /// <summary>The folder assets go into for a document saved at <paramref name="documentPath"/>.</summary>
    public static string FolderFor(string documentPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(documentPath))!, Path.GetFileNameWithoutExtension(documentPath) + "-assets");

    /// <summary>The layers that name assets, with what each asks for (top to bottom).</summary>
    public static IReadOnlyList<(LayerNode Node, IReadOnlyList<AssetSpec> Specs)> AssetLayers(Document doc) =>
        doc.Root.Descendants().Reverse()
            .Select(n => (Node: n, Specs: ImageAssetNames.Parse(n.Name)))
            .Where(x => x.Specs.Count > 0)
            .ToList();

    /// <summary>The variants of the document's "default" layer, if it has one.</summary>
    public static IReadOnlyList<AssetDefault> Defaults(Document doc) =>
        doc.Root.Descendants().Select(n => ImageAssetNames.ParseDefaults(n.Name)).FirstOrDefault(d => d.Count > 0) ?? [];

    /// <summary>True when any layer asks for an asset.</summary>
    public static bool HasAssets(Document doc) => doc.Root.Descendants().Any(n => ImageAssetNames.Parse(n.Name).Count > 0);

    /// <summary>Writes every asset into <paramref name="folder"/>; returns what was written (and what failed).</summary>
    public static IReadOnlyList<AssetResult> Generate(Document doc, string folder, ExportOptions? baseOptions = null, CancellationToken cancel = default)
    {
        var results = new List<AssetResult>();
        var defaults = Defaults(doc);
        var profile = doc.ColorMode == ColorMode.Rgb && doc.BitDepth != 32 ? doc.IccProfile : null;
        var options = baseOptions ?? new ExportOptions();
        foreach (var (node, specs) in AssetLayers(doc))
        {
            cancel.ThrowIfCancellationRequested();
            RgbaImage? image = null;
            foreach (var spec in specs)
            {
                var outputs = new List<(string File, ExportOptions Options)> { (spec.FileName, OptionsFor(spec, options, doc.Resolution)) };
                foreach (var d in defaults)
                {
                    string file = Variant(spec.FileName, d);
                    var o = OptionsFor(spec, options, doc.Resolution);
                    o = d.Width is not null || d.Height is not null
                        ? o with { Width = Pixels(d.Width, doc.Resolution), Height = Pixels(d.Height, doc.Resolution), Scale = 1 }
                        : o with { Scale = o.Scale * (d.Scale ?? 1) };
                    outputs.Add((file, o));
                }
                foreach (var (file, o) in outputs)
                {
                    string path = Path.Combine(folder, file.Replace('/', Path.DirectorySeparatorChar));
                    if (spec.Format is null)
                    {
                        results.Add(new AssetResult(path, node.Name, "SVG assets are not supported."));
                        continue;
                    }
                    if (!ExportCodecs.Supports(o.Format))
                    {
                        results.Add(new AssetResult(path, node.Name, $"{o.Format} export is not available."));
                        continue;
                    }
                    try
                    {
                        image ??= ExportRenderer.TryRender(doc, ExportTarget.For(node, trimToContent: true), cancel);
                        if (image is null)
                        {
                            results.Add(new AssetResult(path, node.Name, "The layer has no visible pixels."));
                            break;
                        }
                        ImageExporter.WriteFile(path, image, o, profile, cancel);
                        results.Add(new AssetResult(path, node.Name, null));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                    {
                        results.Add(new AssetResult(path, node.Name, e.Message));
                    }
                }
            }
        }
        return results;
    }

    /// <summary>The export settings for one spec: its format and quality, scale or size, on top of <paramref name="baseOptions"/>.</summary>
    public static ExportOptions OptionsFor(AssetSpec spec, ExportOptions baseOptions, double resolution)
    {
        var o = baseOptions with
        {
            Format = spec.Format ?? ExportFormat.Png,
            Scale = spec.Scale ?? 1,
            Width = Pixels(spec.Width, resolution),
            Height = Pixels(spec.Height, resolution),
            CanvasWidth = null,
            CanvasHeight = null,
            Transparency = true,
            SmallerFile = false,
        };
        return spec.Format switch
        {
            ExportFormat.Jpeg or ExportFormat.WebP => o with { Quality = spec.Quality ?? 90 },
            ExportFormat.Png => spec.Quality switch
            {
                8 => o with { SmallerFile = true },
                24 => o with { Transparency = false }, // 24-bit: no alpha channel, flattened onto the matte
                _ => o,
            },
            _ => o,
        };
    }

    private static int? Pixels(AssetLength? length, double resolution) =>
        length is { } l ? Math.Max(1, (int)Math.Round(l.ToPixels(resolution))) : null;

    /// <summary>"icons/home.png" with a default of folder "@2x" and suffix "-hd" → "icons/@2x/home-hd.png".</summary>
    private static string Variant(string file, AssetDefault d)
    {
        string dir = Path.GetDirectoryName(file.Replace('/', Path.DirectorySeparatorChar))?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
        string name = Path.GetFileNameWithoutExtension(file), ext = Path.GetExtension(file);
        string folder = string.Join('/', new[] { dir, d.Folder }.Where(s => s.Length > 0));
        return (folder.Length > 0 ? folder + "/" : "") + name + d.Suffix + ext;
    }
}
