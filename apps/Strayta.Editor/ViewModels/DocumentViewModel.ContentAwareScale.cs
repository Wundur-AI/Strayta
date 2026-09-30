using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Editor.Editing;
using Strayta.Psd;
using Strayta.Rendering.Transforms;

namespace Strayta.Editor.ViewModels;

// Edit › Content-Aware Scale (⌥⇧⌘C): a transform box that only scales and moves; the layer is resized by seam carving
// (SeamCarver), keeping what stands out, with Protect keeping a saved selection (alpha channel) or the selection whole.
// The canvas previews the carve at screen resolution; Enter applies it at full resolution as one undo step.
public sealed partial class DocumentViewModel
{
    private ContentAwareScalePreview? _casPreview;
    private readonly List<(string Name, Func<PixelRect, float[]?> Mask)> _protectSources = [];

    /// <summary>Protect choices: None, the selection when there is one, and the file's alpha channels.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> ProtectNames { get; private set; } = ["None"];

    /// <summary>The chosen protection (index into <see cref="ProtectNames"/>).</summary>
    [ObservableProperty]
    public partial int ProtectIndex { get; set; }

    public bool IsContentAwareScaling => FreeTransform is { ContentAware: true };

    /// <summary>Time from Enter to the carved pixels being in the document, for the self-test.</summary>
    public double LastContentAwareScaleMs { get; private set; }

    /// <summary>The rasterize prompt before Content-Aware Scale on type, shapes, fills and smart objects; null when none.</summary>
    public string? ContentAwareRasterizePrompt() => SelectedLayer?.Node is PixelLayer n && RasterizeEdit.CanRasterize(n) ? FilterRasterizePrompt() : null;

    /// <summary>Opens Content-Aware Scale on the selected pixel layer; false (with a notice) when it cannot.</summary>
    public bool BeginContentAwareScale()
    {
        if (IsTransforming || IsPuppetWarping)
        {
            Notice = "Apply or cancel the transform first.";
            return false;
        }
        CommitType();
        var node = SelectedLayer?.Node;
        string? problem = node switch
        {
            null => "Select a layer to scale.",
            LayerGroup or AdjustmentLayer => "Content-Aware Scale works on one pixel layer.",
            { Visible: false } => $"\"{node.Name}\" is hidden. Show it to scale it.",
            PixelLayer p when RasterizeEdit.CanRasterize(p) => $"Rasterize \"{node.Name}\" before scaling it by content.",
            PixelLayer p when Resampler.ContentBounds(p).IsEmpty => $"\"{node.Name}\" has no pixels to scale.",
            _ => null,
        };
        if (problem is not null || node is not PixelLayer layer)
        {
            Notice = problem ?? "";
            return false;
        }
        var box = Resampler.ContentBounds(layer);
        _protectSources.Clear();
        _protectSources.Add(("None", _ => null));
        if (Selection is { } selection) _protectSources.Add(("Selection", b => ProtectFromSelection(selection, b)));
        foreach (var (name, plane) in AlphaChannels()) _protectSources.Add((name, b => ProtectFromPlane(plane, b)));
        ProtectNames = _protectSources.Select(p => p.Name).ToList();
        ProtectIndex = 0;

        Notice = "";
        PaintBlock = null;
        _transformTargets = [layer];
        _transformNode = layer;
        _transformDescription = "Content-Aware Scale";
        _transformPreview = new TransformPreview(_transformTargets);
        _deformPreview = new DeformPreview(_transformTargets);
        _casPreview = new ContentAwareScalePreview(layer, box, null);
        var transform = new FreeTransform(box) { ContentAware = true, AllowsPerspective = false };
        transform.Changed += OnTransformChanged;
        FreeTransform = transform;
        PropertyChanged += CommitOnSelectionChange;
        OnPropertyChanged(nameof(IsContentAwareScaling));
        return true;
    }

    partial void OnProtectIndexChanged(int value)
    {
        if (_casPreview is not { } p || FreeTransform is not { ContentAware: true } t) return;
        var mask = value >= 0 && value < _protectSources.Count ? _protectSources[value].Mask(p.Box) : null;
        _casPreview = new ContentAwareScalePreview(p.Layer, p.Box, mask);
        UpdateContentAwareTarget(t);
        RequestRender();
    }

    /// <summary>The box's size and top-left now, for the preview and the commit.</summary>
    private void UpdateContentAwareTarget(FreeTransform t)
    {
        if (_casPreview is null) return;
        var b = Resampler.TransformBounds(t.Original, t.AffineMap);
        _casPreview.Target = (Math.Max(1, (int)Math.Round(Math.Abs(t.WidthPercent) / 100 * t.Original.Width)),
            Math.Max(1, (int)Math.Round(Math.Abs(t.HeightPercent) / 100 * t.Original.Height)), b.Left, b.Top);
    }

    /// <summary>Enter: carves the layer at full resolution to the box as one step "Content-Aware Scale".</summary>
    private async Task CommitContentAwareScaleAsync(FreeTransform transform)
    {
        if (_casPreview is not { } preview) return;
        transform.Locked = true;
        UpdateContentAwareTarget(transform);
        var target = preview.Target;
        var layer = preview.Layer;
        var box = preview.Box;
        var state = TransformEdit.Read(layer);
        var mask = ProtectIndex >= 0 && ProtectIndex < _protectSources.Count ? _protectSources[ProtectIndex].Mask(box) : null;
        var affine = transform.AffineMap;
        var clock = Stopwatch.StartNew();
        try
        {
            var after = await Task.Run(() =>
            {
                var (part, partBounds) = Resampler.Crop(state.Pixels!, state.Bounds, box);
                var rgba = new float[box.Width * box.Height * 4];
                if (part is not null)
                {
                    var src = SeamCarver.ToRgba(part);
                    for (int y = 0; y < partBounds.Height; y++)
                        Array.Copy(src, y * partBounds.Width * 4, rgba, ((partBounds.Top - box.Top + y) * box.Width + partBounds.Left - box.Left) * 4, partBounds.Width * 4);
                }
                var carved = SeamCarver.Retarget(rgba, box.Width, box.Height, target.Width, target.Height, mask);
                var raster = SeamCarver.FromRgba(carved, target.Width, target.Height, Model.ColorMode, Model.BitDepth);
                var bounds = new PixelRect(target.Left, target.Top, target.Left + target.Width, target.Top + target.Height);
                return new TransformEdit.State(raster, bounds, Resampler.TransformMask(state.Mask, affine, ResampleFilter.Bicubic), state.Source);
            });
            EndTransform();
            Apply(new TransformEdit([(layer, after)], "Content-Aware Scale"));
            LastContentAwareScaleMs = clock.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex)
        {
            transform.Locked = false;
            Notice = $"Could not apply Content-Aware Scale: {ex.Message}";
        }
    }

    /// <summary>The selection's coverage over <paramref name="box"/> (0..1 per pixel).</summary>
    private static float[] ProtectFromSelection(Core.Selection.SelectionMask selection, PixelRect box)
    {
        var o = new float[box.Width * box.Height];
        for (int y = 0; y < box.Height; y++)
            for (int x = 0; x < box.Width; x++) o[y * box.Width + x] = selection.CoverageAt(box.Left + x, box.Top + y) / 255f;
        return o;
    }

    /// <summary>A full-canvas channel's values over <paramref name="box"/> (0..1 per pixel; outside the canvas nothing is protected).</summary>
    private float[] ProtectFromPlane(Plane plane, PixelRect box)
    {
        var o = new float[box.Width * box.Height];
        for (int y = 0; y < box.Height; y++)
            for (int x = 0; x < box.Width; x++)
            {
                int dx = box.Left + x, dy = box.Top + y;
                if (dx < 0 || dy < 0 || dx >= plane.Width || dy >= plane.Height) continue;
                o[y * box.Width + x] = plane.GetNormalized(dy * plane.Width + dx);
            }
        return o;
    }

    /// <summary>
    /// The file's saved selections (alpha channels after the color and transparency channels of the composite) with
    /// their names (resource 1045, Unicode, else 1006), while the canvas has its original size.
    /// </summary>
    private List<(string Name, Plane Plane)> AlphaChannels()
    {
        var list = new List<(string, Plane)>();
        if (Model.SourceData is not PsdFile file || file.Header.Width != Model.Width || file.Header.Height != Model.Height) return list;
        int colors = Model.ColorMode.ColorChannelCount() + (file.CompositeHasTransparency ? 1 : 0);
        var extra = file.CompositeChannels.Skip(colors).ToList();
        var names = AlphaNames(file);
        for (int i = 0; i < extra.Count; i++) list.Add((i < names.Count ? names[i] : $"Alpha {i + 1}", extra[i]));
        return list;
    }

    private static List<string> AlphaNames(PsdFile file)
    {
        var names = new List<string>();
        if (file.FindResource(1045)?.Data is { } unicode)
        {
            int at = 0;
            while (at + 4 <= unicode.Length)
            {
                int n = (unicode[at] << 24) | (unicode[at + 1] << 16) | (unicode[at + 2] << 8) | unicode[at + 3];
                at += 4;
                if (n < 0 || at + 2 * n > unicode.Length) break;
                var chars = new char[n];
                for (int k = 0; k < n; k++) chars[k] = (char)((unicode[at + 2 * k] << 8) | unicode[at + 2 * k + 1]);
                at += 2 * n;
                names.Add(new string(chars).TrimEnd('\0'));
            }
        }
        else if (file.FindResource(1006)?.Data is { } pascal)
        {
            int at = 0;
            while (at < pascal.Length)
            {
                int n = pascal[at++];
                if (at + n > pascal.Length) break;
                names.Add(System.Text.Encoding.Latin1.GetString(pascal, at, n));
                at += n;
            }
        }
        return names;
    }
}
