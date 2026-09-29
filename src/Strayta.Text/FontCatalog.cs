using System.Collections.Concurrent;
using SkiaSharp;

namespace Strayta.Text;

/// <summary>One installed font face.</summary>
/// <param name="PostScriptName">The name Photoshop stores in files (e.g. "Helvetica-Bold").</param>
/// <param name="FamilyName">The family as menus show it ("Helvetica").</param>
/// <param name="StyleName">The face within the family ("Bold", "Light Oblique").</param>
/// <param name="Weight">CSS-style weight, 100..900 (400 regular, 700 bold).</param>
/// <param name="Width">Width class 1..9 (5 normal).</param>
public sealed record FontFace(string PostScriptName, string FamilyName, string StyleName, int Weight, int Width, bool Italic);

/// <summary>
/// The fonts available for type: every face the system font manager lists, indexed by PostScript name, since that
/// is how PSD files name fonts. Faces from files (e.g. fonts bundled with a document) can be added.
/// </summary>
public sealed class FontCatalog
{
    private static readonly Lazy<FontCatalog> SystemCatalog = new(() => Build(SKFontManager.Default), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly ConcurrentDictionary<string, SKTypeface> _typefaces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FontFace> _faces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _loose = new(StringComparer.Ordinal);

    private FontCatalog() { }

    /// <summary>
    /// The installed fonts. Built once, on first use (listing a few hundred families takes around a second; call
    /// <see cref="WarmUp"/> from a background thread at startup to hide it).
    /// </summary>
    public static FontCatalog System => SystemCatalog.Value;

    /// <summary>Builds <see cref="System"/> on a thread-pool thread.</summary>
    public static Task WarmUp() => Task.Run(() => _ = System);

    /// <summary>A catalogue of just the given font files (every face in each).</summary>
    public static FontCatalog FromFiles(IEnumerable<string> paths)
    {
        var catalog = new FontCatalog();
        foreach (var path in paths) catalog.AddFile(path);
        return catalog;
    }

    /// <summary>
    /// One catalogue holding the faces of several (the first one listing a PostScript name wins). Typefaces are shared
    /// with the sources.
    /// </summary>
    public static FontCatalog Merge(params FontCatalog[] catalogs)
    {
        var merged = new FontCatalog();
        foreach (var catalog in catalogs)
            foreach (var (name, face) in catalog._faces)
                if (merged._faces.TryAdd(name, face))
                {
                    merged._typefaces[name] = catalog._typefaces[name];
                    merged._loose.TryAdd(Loose(name), name);
                }
        return merged;
    }

    /// <summary>Adds every font file (.ttf, .otf, .ttc) in a folder and its subfolders. Returns how many faces were added.</summary>
    public int AddFolder(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        int added = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            if (Path.GetExtension(path).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc")
                added += AddFile(path);
        return added;
    }

    /// <summary>Adds the faces of a font file (a .ttc adds each of its faces). Returns how many were added.</summary>
    public int AddFile(string path)
    {
        int added = 0;
        for (int index = 0; index < 64; index++)
        {
            var typeface = SKTypeface.FromFile(path, index);
            if (typeface is null) break;
            if (Add(typeface, styleName: null)) added++;
        }
        return added;
    }

    /// <summary>Every face, sorted by family and then weight, width and slant.</summary>
    public IReadOnlyList<FontFace> Faces =>
        _faces.Values.OrderBy(f => f.FamilyName, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Weight).ThenBy(f => f.Width).ThenBy(f => f.Italic).ToList();

    /// <summary>Family names, sorted.</summary>
    public IReadOnlyList<string> Families => _faces.Values.Select(f => f.FamilyName).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The faces of one family.</summary>
    public IReadOnlyList<FontFace> FacesOf(string family) =>
        Faces.Where(f => string.Equals(f.FamilyName, family, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The face with this PostScript name, or null when it is not installed.</summary>
    public FontFace? Find(string postScriptName) =>
        Resolve(postScriptName) is { } key && _faces.TryGetValue(key, out var face) ? face : null;

    public bool Contains(string postScriptName) => Resolve(postScriptName) is not null;

    /// <summary>The typeface for a PostScript name, or null when missing. Typefaces are shared: do not dispose them.</summary>
    public SKTypeface? GetTypeface(string postScriptName) =>
        Resolve(postScriptName) is { } key && _typefaces.TryGetValue(key, out var tf) ? tf : null;

    /// <summary>
    /// The face used for fonts that are not installed: a plain sans serif that is on every system (Myriad Pro, which
    /// Photoshop uses by default, when present), or any face at all.
    /// </summary>
    public string? FallbackPostScriptName =>
        new[] { "MyriadPro-Regular", "ArialMT", "Helvetica", "HelveticaNeue", "DejaVuSans", "LiberationSans", "SegoeUI", "Roboto-Regular" }
            .FirstOrDefault(Contains) ?? _faces.Keys.Order(StringComparer.Ordinal).FirstOrDefault();

    /// <summary>
    /// An exact match first; then ignoring case, spaces and hyphens ("Arial-BoldMT" vs "Arial BoldMT"), which covers
    /// names that differ only in how a platform spells them.
    /// </summary>
    private string? Resolve(string postScriptName)
    {
        if (string.IsNullOrEmpty(postScriptName)) return null;
        if (_faces.ContainsKey(postScriptName)) return postScriptName;
        return _loose.TryGetValue(Loose(postScriptName), out var key) ? key : null;
    }

    private static string Loose(string name) => new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private bool Add(SKTypeface typeface, string? styleName)
    {
        string? ps = typeface.PostScriptName;
        if (string.IsNullOrEmpty(ps) || ps.StartsWith('.')) // names starting with '.' are private system UI faces
        {
            typeface.Dispose();
            return false;
        }
        var style = typeface.FontStyle;
        var face = new FontFace(ps, typeface.FamilyName, styleName ?? StyleNameOf(style), style.Weight, style.Width,
            style.Slant != SKFontStyleSlant.Upright);
        if (!_faces.TryAdd(ps, face))
        {
            typeface.Dispose();
            return false;
        }
        _typefaces[ps] = typeface;
        _loose.TryAdd(Loose(ps), ps);
        return true;
    }

    private static string StyleNameOf(SKFontStyle style)
    {
        string weight = style.Weight switch
        {
            <= 150 => "Thin",
            <= 250 => "ExtraLight",
            <= 350 => "Light",
            <= 450 => "Regular",
            <= 550 => "Medium",
            <= 650 => "SemiBold",
            <= 750 => "Bold",
            <= 850 => "ExtraBold",
            _ => "Black",
        };
        if (style.Slant == SKFontStyleSlant.Upright) return weight;
        return weight == "Regular" ? "Italic" : weight + " Italic";
    }

    private static FontCatalog Build(SKFontManager manager)
    {
        var catalog = new FontCatalog();
        foreach (string family in manager.FontFamilies)
        {
            using var set = manager.GetFontStyles(family);
            for (int i = 0; i < set.Count; i++)
            {
                var typeface = set.CreateTypeface(i);
                if (typeface is null) continue;
                string? name = set.GetStyleName(i);
                catalog.Add(typeface, string.IsNullOrWhiteSpace(name) ? null : name);
            }
        }
        return catalog;
    }
}
