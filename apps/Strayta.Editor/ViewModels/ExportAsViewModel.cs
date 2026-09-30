using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Psd;
using Strayta.Rendering.Export;

namespace Strayta.Editor.ViewModels;

/// <summary>One thing Export As writes: the document, an artboard or a layer, with its pixels once rendered.</summary>
public sealed partial class ExportItem(string name, ExportTarget target) : ObservableObject
{
    public string Name { get; } = name;
    public ExportTarget Target { get; } = target;
    public string Kind => Target.Node is null ? "Document" : Target.IsArtboard ? "Artboard" : Target.Node is LayerGroup ? "Group" : "Layer";

    /// <summary>The rendered pixels (full resolution), or null until rendered or when the item shows nothing.</summary>
    public RgbaImage? Image { get; set; }

    public bool Rendered { get; set; }

    /// <summary>The render in progress or done, shared by everyone who needs the pixels.</summary>
    internal Task? Rendering { get; set; }

    [ObservableProperty] public partial Bitmap? Thumbnail { get; set; }
    [ObservableProperty] public partial bool Include { get; set; } = true;
}

/// <summary>One line of Export As's scale list: a size multiplier and the suffix its files get ("@2x").</summary>
public sealed partial class ExportScaleRow : ObservableObject
{
    public static IReadOnlyList<double> Choices { get; } = [0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4];
    public static IReadOnlyList<string> ChoiceNames { get; } = Choices.Select(c => $"{c:0.##}x").ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Scale))]
    public partial int ScaleIndex { get; set; } = 3;

    [ObservableProperty] public partial string Suffix { get; set; } = "";

    public double Scale => Choices[Math.Clamp(ScaleIndex, 0, Choices.Count - 1)];

    public static ExportScaleRow Of(double scale, string suffix) => new()
    {
        ScaleIndex = Math.Max(0, Choices.ToList().FindIndex(c => Math.Abs(c - scale) < 1e-9)),
        Suffix = suffix,
    };
}

/// <summary>
/// File › Export › Export As (⌥⇧⌘W), modeled on Photoshop's dialog: the items to export (the document, its artboards
/// or the selected layers) on the left with a live preview of the selected one, and on the right the file settings
/// (format, quality, transparency, matte, 8-bit PNG, lossless WebP), image size (scale, width, height), canvas size,
/// metadata, sRGB conversion, plus a list of extra scales with suffixes (1x, @2x, @3x). The preview shows the encoded
/// file decoded again (JPEG artifacts, GIF palette) with its size in bytes.
/// </summary>
public sealed partial class ExportAsViewModel : ObservableObject
{
    public static IReadOnlyList<string> FormatNames { get; } = ["PNG", "JPEG", "GIF", "WebP"];
    public static IReadOnlyList<string> MatteNames { get; } = ["None", "White", "Black", "Background Color"];
    public static IReadOnlyList<string> MetadataNames { get; } = ["None", "Copyright and Contact Info"];

    private readonly Document _doc;
    private readonly (byte R, byte G, byte B) _backgroundColor;
    private CancellationTokenSource? _previewCancel;
    private bool _syncingSize;

    public ExportAsViewModel(Document doc, IEnumerable<ExportItem> items, ExportOptions settings, IEnumerable<ExportScaleRow> scales,
        (byte R, byte G, byte B) backgroundColor)
    {
        _doc = doc;
        _backgroundColor = backgroundColor;
        foreach (var item in items) Items.Add(item);
        foreach (var row in scales) Scales.Add(row);
        if (Scales.Count == 0) Scales.Add(ExportScaleRow.Of(1, ""));
        foreach (var row in Scales) row.PropertyChanged += (_, _) => SettingsChanged();
        Scales.CollectionChanged += (_, e) =>
        {
            foreach (ExportScaleRow row in e.NewItems ?? Array.Empty<ExportScaleRow>()) row.PropertyChanged += (_, _) => SettingsChanged();
            SettingsChanged();
        };

        FormatIndex = (int)settings.Format;
        Quality = settings.Quality;
        Transparency = settings.Transparency;
        SmallerFile = settings.SmallerFile;
        Lossless = settings.Lossless;
        MatteIndex = settings.Matte == (255, 255, 255) ? 1 : settings.Matte == (0, 0, 0) ? 2 : settings.Matte == backgroundColor ? 3 : 1;
        if (settings.Transparency && settings.Format != ExportFormat.Jpeg) MatteIndex = 0;
        ScalePercent = settings.Scale * 100;
        ConvertToSrgb = settings.ConvertToSrgb;
        MetadataIndex = (int)settings.Metadata;
        (Copyright, Author) = ReadFileInfo(doc);
        SelectedItem = Items.FirstOrDefault();
    }

    public ObservableCollection<ExportItem> Items { get; } = [];
    public ObservableCollection<ExportScaleRow> Scales { get; } = [];

    [ObservableProperty] public partial ExportItem? SelectedItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Format), nameof(HasQuality), nameof(HasTransparency), nameof(IsPng), nameof(IsWebP), nameof(HasMatte))]
    public partial int FormatIndex { get; set; }

    public ExportFormat Format => (ExportFormat)Math.Clamp(FormatIndex, 0, 3);
    public bool HasQuality => Format is ExportFormat.Jpeg || Format == ExportFormat.WebP && !Lossless;
    public bool HasTransparency => Format != ExportFormat.Jpeg;
    public bool IsPng => Format == ExportFormat.Png;
    public bool IsWebP => Format == ExportFormat.WebP;
    public bool HasMatte => Format == ExportFormat.Jpeg || !Transparency || Format == ExportFormat.Gif;

    [ObservableProperty] public partial double Quality { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMatte))]
    public partial bool Transparency { get; set; }

    [ObservableProperty] public partial bool SmallerFile { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuality))]
    public partial bool Lossless { get; set; }

    [ObservableProperty] public partial int MatteIndex { get; set; }

    /// <summary>Image size: percent of the selected item's own size; width and height follow it.</summary>
    [ObservableProperty] public partial double ScalePercent { get; set; } = 100;

    [ObservableProperty] public partial double ImageWidth { get; set; }
    [ObservableProperty] public partial double ImageHeight { get; set; }

    /// <summary>Canvas size (0 = the image's own size).</summary>
    [ObservableProperty] public partial double CanvasWidth { get; set; }
    [ObservableProperty] public partial double CanvasHeight { get; set; }

    [ObservableProperty] public partial bool ConvertToSrgb { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCopyright))]
    public partial int MetadataIndex { get; set; }

    public bool HasCopyright => MetadataIndex == 1;

    [ObservableProperty] public partial string Copyright { get; set; } = "";
    [ObservableProperty] public partial string Author { get; set; } = "";

    [ObservableProperty] public partial Bitmap? Preview { get; set; }
    [ObservableProperty] public partial string PreviewInfo { get; set; } = "";
    [ObservableProperty] public partial string FileSizeText { get; set; } = "";

    /// <summary>The size of the selected item before scaling.</summary>
    [ObservableProperty] public partial string SourceSizeText { get; set; } = "";

    /// <summary>Settings as they stand, at the base (1x) scale.</summary>
    public ExportOptions Options => new()
    {
        Format = Format,
        Quality = (int)Math.Round(Math.Clamp(Quality, 1, 100)),
        Transparency = Format != ExportFormat.Jpeg && Transparency,
        SmallerFile = Format == ExportFormat.Png && SmallerFile,
        Lossless = Format == ExportFormat.WebP && Lossless,
        Matte = MatteIndex switch { 2 => ((byte)0, (byte)0, (byte)0), 3 => _backgroundColor, _ => ((byte)255, (byte)255, (byte)255) },
        Scale = Math.Clamp(ScalePercent, 1, 10000) / 100,
        CanvasWidth = CanvasWidth >= 1 && CanvasHeight >= 1 ? (int)CanvasWidth : null,
        CanvasHeight = CanvasWidth >= 1 && CanvasHeight >= 1 ? (int)CanvasHeight : null,
        ConvertToSrgb = ConvertToSrgb,
        Metadata = (ExportMetadata)MetadataIndex,
        Copyright = Copyright,
        Author = Author,
    };

    /// <summary>The settings for one scale row: its multiplier applied to the image and canvas sizes.</summary>
    public static ExportOptions ForScale(ExportOptions o, double scale) => scale == 1 ? o : o with
    {
        Scale = o.Scale * scale,
        Width = o.Width is { } w ? (int)Math.Round(w * scale) : null,
        Height = o.Height is { } h ? (int)Math.Round(h * scale) : null,
        CanvasWidth = o.CanvasWidth is { } cw ? (int)Math.Round(cw * scale) : null,
        CanvasHeight = o.CanvasHeight is { } ch ? (int)Math.Round(ch * scale) : null,
    };

    public void AddScale()
    {
        var used = Scales.Select(s => s.Scale).ToHashSet();
        double next = new[] { 2.0, 3, 1.5, 4, 0.5, 0.75, 0.25 }.FirstOrDefault(s => !used.Contains(s), 2);
        Scales.Add(ExportScaleRow.Of(next, $"@{next:0.##}x"));
    }

    public void RemoveScale(ExportScaleRow row)
    {
        if (Scales.Count > 1) Scales.Remove(row);
    }

    partial void OnSelectedItemChanged(ExportItem? value)
    {
        if (_opened) _ = RenderSelectedAsync();
    }

    private bool _opened;

    partial void OnFormatIndexChanged(int value)
    {
        if (Format == ExportFormat.Jpeg && MatteIndex == 0) MatteIndex = 1;
        SettingsChanged();
    }

    partial void OnQualityChanged(double value) => SettingsChanged();
    partial void OnTransparencyChanged(bool value) => SettingsChanged();
    partial void OnSmallerFileChanged(bool value) => SettingsChanged();
    partial void OnLosslessChanged(bool value) => SettingsChanged();
    partial void OnMatteIndexChanged(int value) => SettingsChanged();
    partial void OnCanvasWidthChanged(double value) => SettingsChanged();
    partial void OnCanvasHeightChanged(double value) => SettingsChanged();
    partial void OnConvertToSrgbChanged(bool value) => SettingsChanged();
    partial void OnMetadataIndexChanged(int value) => SettingsChanged();

    partial void OnScalePercentChanged(double value)
    {
        if (_syncingSize) return;
        SyncSizeFromScale();
        SettingsChanged();
    }

    partial void OnImageWidthChanged(double value)
    {
        if (_syncingSize || SelectedItem?.Image is not { } image || value < 1) return;
        _syncingSize = true;
        ScalePercent = value * 100 / image.Width;
        ImageHeight = Math.Max(1, Math.Round(image.Height * ScalePercent / 100));
        _syncingSize = false;
        SettingsChanged();
    }

    partial void OnImageHeightChanged(double value)
    {
        if (_syncingSize || SelectedItem?.Image is not { } image || value < 1) return;
        _syncingSize = true;
        ScalePercent = value * 100 / image.Height;
        ImageWidth = Math.Max(1, Math.Round(image.Width * ScalePercent / 100));
        _syncingSize = false;
        SettingsChanged();
    }

    private void SyncSizeFromScale()
    {
        if (SelectedItem?.Image is not { } image) return;
        _syncingSize = true;
        var (w, h) = Options.OutputSize(image.Width, image.Height);
        ImageWidth = w;
        ImageHeight = h;
        _syncingSize = false;
    }

    /// <summary>Renders the selected item (once) and refreshes the preview.</summary>
    public async Task RenderSelectedAsync()
    {
        _opened = true;
        if (SelectedItem is not { } item) return;
        await EnsureRenderedAsync(item);
        if (!ReferenceEquals(item, SelectedItem)) return;
        SourceSizeText = item.Image is { } image ? $"{image.Width} × {image.Height} px" : "Nothing to export";
        SyncSizeFromScale();
        await UpdatePreviewAsync();
    }

    public Task EnsureRenderedAsync(ExportItem item, CancellationToken cancel = default) =>
        item.Rendering ??= RenderAsync(item, cancel);

    private async Task RenderAsync(ExportItem item, CancellationToken cancel)
    {
        var doc = _doc;
        item.Image = await Task.Run(() => ExportRenderer.TryRender(doc, item.Target, cancel), cancel);
        item.Rendered = true;
        if (item.Image is { } image)
        {
            // A small thumbnail for the list.
            double s = Math.Min(1, 64.0 / Math.Max(image.Width, image.Height));
            var thumb = await Task.Run(() => s < 1
                ? new RgbaImage(RgbaResize.Resize(image.Pixels, image.Width, image.Height, Math.Max(1, (int)(image.Width * s)), Math.Max(1, (int)(image.Height * s))),
                    Math.Max(1, (int)(image.Width * s)), Math.Max(1, (int)(image.Height * s)))
                : image, cancel);
            item.Thumbnail = Controls.BitmapFactory.FromRgba(thumb.Pixels, thumb.Width, thumb.Height);
        }
    }

    private void SettingsChanged() => _ = UpdatePreviewAsync();

    /// <summary>Encodes the selected item with the current settings (after a short pause) and shows the decoded result and its size.</summary>
    public async Task UpdatePreviewAsync()
    {
        _previewCancel?.Cancel();
        var cancel = (_previewCancel = new CancellationTokenSource()).Token;
        try
        {
            await Task.Delay(120, cancel);
            if (SelectedItem?.Image is not { } image)
            {
                Preview = null;
                FileSizeText = "";
                PreviewInfo = SelectedItem is null ? "" : "Nothing to export";
                return;
            }
            var options = ForScale(Options, Scales.FirstOrDefault()?.Scale ?? 1);
            var profile = Profile(_doc);
            var (bytes, w, h) = await Task.Run(() =>
            {
                using var ms = new MemoryStream();
                ImageExporter.Encode(ms, image, options, profile, cancel);
                var prepared = options.OutputSize(image.Width, image.Height);
                return (ms.ToArray(), options.CanvasWidth ?? prepared.Width, options.CanvasHeight ?? prepared.Height);
            }, cancel);
            cancel.ThrowIfCancellationRequested();
            Bitmap? decoded = null;
            try
            {
                decoded = new Bitmap(new MemoryStream(bytes));
            }
            catch (Exception)
            {
                // A format the display cannot decode still shows its size.
            }
            Preview = decoded;
            FileSizeText = FormatSize(bytes.LongLength);
            PreviewInfo = $"{FormatNames[(int)options.Format]} · {w} × {h} px · {FileSizeText}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PreviewInfo = $"Preview failed: {ex.Message}";
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.##} MB",
    };

    /// <summary>The ICC profile the rendered pixels are in (null: treat as sRGB).</summary>
    public static byte[]? Profile(Document doc) => doc.ColorMode == ColorMode.Rgb && doc.BitDepth != 32 ? doc.IccProfile : null;

    /// <summary>The files an export writes for one item: one per scale row, named item + suffix + extension.</summary>
    public IEnumerable<(string FileName, ExportOptions Options)> FilesFor(ExportItem item)
    {
        var options = Options;
        string ext = ExportOptions.ExtensionOf(options.Format);
        foreach (var row in Scales)
            yield return (ImageAssetNames.Sanitize(item.Name + row.Suffix) is { Length: > 0 } n ? n + ext : "Untitled" + row.Suffix + ext, ForScale(options, row.Scale));
    }

    /// <summary>Writes every included item at every scale into <paramref name="folder"/>; returns the files written.</summary>
    public async Task<IReadOnlyList<string>> ExportAllAsync(string folder, string? singleFileName = null, CancellationToken cancel = default)
    {
        var written = new List<string>();
        var profile = Profile(_doc);
        var items = Items.Where(i => i.Include).ToList();
        foreach (var item in items)
        {
            await EnsureRenderedAsync(item, cancel);
            if (item.Image is not { } image) continue;
            foreach (var (file, options) in FilesFor(item))
            {
                string name = file;
                // A single file keeps the name picked in the save dialog; its scale suffixes are added before the extension.
                if (singleFileName is not null && items.Count == 1)
                {
                    string row = Path.GetFileNameWithoutExtension(file)[ImageAssetNames.Sanitize(item.Name).Length..];
                    name = Path.GetFileNameWithoutExtension(singleFileName) + row + ExportOptions.ExtensionOf(options.Format);
                }
                string path = Path.Combine(folder, name);
                await Task.Run(() => ImageExporter.WriteFile(path, image, options, profile, cancel), cancel);
                written.Add(path);
            }
        }
        return written;
    }

    /// <summary>The document's copyright notice and author from its XMP metadata (PSD resource 1060), if any.</summary>
    public static (string Copyright, string Author) ReadFileInfo(Document doc)
    {
        if (doc.SourceData is not PsdFile file || file.FindResource(1060)?.Data is not { } xmpBytes) return ("", "");
        string xmp = System.Text.Encoding.UTF8.GetString(xmpBytes);
        static string First(string xml, string element)
        {
            var m = Regex.Match(xml, $@"<{element}>.*?<rdf:li[^>]*>(?<v>.*?)</rdf:li>", RegexOptions.Singleline);
            return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups["v"].Value.Trim()) : "";
        }
        return (First(xmp, "dc:rights"), First(xmp, "dc:creator"));
    }
}
