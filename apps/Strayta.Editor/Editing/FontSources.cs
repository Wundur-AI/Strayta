using System.Text.RegularExpressions;

namespace Strayta.Editor.Editing;

/// <summary>A font a document needs that is not installed, with where to look for it.</summary>
/// <param name="PostScriptName">The name stored in the file (e.g. "Neuropol-Regular").</param>
/// <param name="Family">A readable family name for searching (e.g. "Neuropol").</param>
public sealed record MissingFont(string PostScriptName, string Family)
{
    public Uri GoogleFonts => new($"https://fonts.google.com/?query={Uri.EscapeDataString(Family)}");
    public Uri AdobeFonts => new($"https://fonts.adobe.com/search?query={Uri.EscapeDataString(Family)}");
    public Uri WebSearch => new($"https://duckduckgo.com/?q={Uri.EscapeDataString($"\"{Family}\" font")}");
}

/// <summary>
/// Helps get missing fonts: readable names to search for, and installing font files for the current user (the
/// folder the system reads per-user fonts from), without an administrator password.
/// </summary>
public static partial class FontSources
{
    public static MissingFont Describe(string postScriptName) => new(postScriptName, FamilyOf(postScriptName));

    /// <summary>
    /// A family name to search for from a PostScript name: the part before the style ("Neuropol-Regular" → "Neuropol"),
    /// spaced at case changes ("HelloSunshineSansSerif" → "Hello Sunshine Sans Serif"), without the "MT"/"PS" vendor tags.
    /// </summary>
    public static string FamilyOf(string postScriptName)
    {
        string family = postScriptName.Split('-')[0];
        family = VendorSuffix().Replace(family, "");
        family = CaseChange().Replace(family, " ");
        return family.Length > 0 ? family : postScriptName;
    }

    /// <summary>Where fonts installed for this user go.</summary>
    public static string UserFontFolder =>
        OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Fonts")
        : OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "fonts");

    /// <summary>Every folder fonts are installed in on this system, for looking again after an install.</summary>
    public static IEnumerable<string> FontFolders()
    {
        yield return UserFontFolder;
        if (OperatingSystem.IsMacOS())
        {
            yield return "/Library/Fonts";
            yield return "/System/Library/Fonts";
        }
        else if (OperatingSystem.IsWindows()) yield return Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        else
        {
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
        }
    }

    public static bool IsFontFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc";

    /// <summary>Copies a font file into the user's font folder (keeping one already there) and returns where it is.</summary>
    public static string Install(string path)
    {
        Directory.CreateDirectory(UserFontFolder);
        string target = Path.Combine(UserFontFolder, Path.GetFileName(path));
        if (!File.Exists(target)) File.Copy(path, target);
        return target;
    }

    [GeneratedRegex("(MT|PS)$")]
    private static partial Regex VendorSuffix();

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CaseChange();
}
