using Strayta.Editor.Editing;
using Strayta.Text;

namespace Strayta.Editor.ViewModels;

// Getting missing fonts: the banner's search links, installing font files, and looking again after an install.
public sealed partial class DocumentViewModel
{
    /// <summary>The missing fonts with readable names and search links, for the banner.</summary>
    public IReadOnlyList<MissingFont> MissingFontItems => MissingFonts.Select(FontSources.Describe).ToList();

    /// <summary>
    /// Installs font files for the current user and adds them to the running app, then checks the document again.
    /// Returns how many faces were added.
    /// </summary>
    public async Task<int> InstallFontsAsync(IEnumerable<string> paths)
    {
        var files = paths.Where(FontSources.IsFontFile).ToList();
        int added = 0;
        try
        {
            added = await Task.Run(() => files.Sum(path => FontCatalog.System.AddFile(FontSources.Install(path))));
        }
        catch (Exception ex)
        {
            Notice = $"Could not install the font: {ex.Message}";
            return 0;
        }
        await RecheckFontsAsync(scanFolders: false);
        return added;
    }

    /// <summary>
    /// Looks for fonts again (after installing one some other way, e.g. by opening it in Font Book), so type in newly
    /// installed fonts becomes editable without restarting.
    /// </summary>
    public async Task RecheckFontsAsync(bool scanFolders = true)
    {
        if (scanFolders) await Task.Run(() => { foreach (var folder in FontSources.FontFolders()) FontCatalog.System.AddFolder(folder); });
        var before = MissingFonts.Count;
        await CheckFontsAsync();
        if (MissingFonts.Count == 0)
        {
            HasMissingFontsBanner = false;
            Notice = before > 0 ? "All the fonts this document uses are installed now." : Notice;
        }
        OnPropertyChanged(nameof(MissingFontItems));
    }
}
