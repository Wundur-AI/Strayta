using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;
using Document = Strayta.Core.Document;

namespace Strayta.Editor.ViewModels;

/// <summary>What a row of the Channels panel stands for.</summary>
public enum ChannelRowKind
{
    Composite,
    Color,
    Channel,
    QuickMask,
    LayerMask,
}

/// <summary>A row of the Channels panel: its eye, thumbnail, name, shortcut and whether editing targets it.</summary>
public sealed partial class ChannelRow(ChannelRowKind kind, int key, string name, string shortcut) : ObservableObject
{
    public ChannelRowKind Kind { get; } = kind;

    /// <summary>The color channel's index, or the saved selection's ID.</summary>
    public int Key { get; } = key;

    public string Name { get; } = name;
    public string Shortcut { get; } = shortcut;

    [ObservableProperty] public partial bool IsVisible { get; set; }
    [ObservableProperty] public partial bool IsTargeted { get; set; }
    [ObservableProperty] public partial Bitmap? Thumbnail { get; set; }

    public double EyeOpacity => IsVisible ? 1 : 0;

    /// <summary>The quick mask and a layer mask are temporary channels, shown in italics as in Photoshop.</summary>
    public Avalonia.Media.FontStyle Style => Kind is ChannelRowKind.QuickMask or ChannelRowKind.LayerMask
        ? Avalonia.Media.FontStyle.Italic : Avalonia.Media.FontStyle.Normal;

    partial void OnIsVisibleChanged(bool value) => OnPropertyChanged(nameof(EyeOpacity));
}

/// <summary>
/// Window › Channels: the composite and the color channels (red, green, blue; or gray), then the saved selections and
/// spot channels, then the quick mask (in Quick Mask mode) or the targeted layer mask, each with an eye and a
/// thumbnail. Clicking a row targets it (Shift adds a color channel), ⌘-clicking loads it as a selection, the eyes
/// choose what is shown. The footer loads and saves selections, adds and deletes channels.
/// </summary>
public sealed partial class ChannelsToolViewModel : Tool, IDisposable
{
    private const int ThumbSize = 40;
    private DocumentViewModel? _document;
    private bool _refreshQueued, _thumbsQueued;

    public ChannelsToolViewModel(EditorViewModel editor)
    {
        Editor = editor;
        editor.PropertyChanged += OnEditorChanged;
        Follow(editor.ActiveDocument);
    }

    public EditorViewModel Editor { get; }

    public ObservableCollection<ChannelRow> Rows { get; } = [];

    public bool HasDocument => _document is not null;

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.ActiveDocument)) Follow(Editor.ActiveDocument);
    }

    private void Follow(DocumentViewModel? doc)
    {
        if (_document is not null)
        {
            _document.ChannelsChanged -= QueueRefresh;
            _document.PropertyChanged -= OnDocumentChanged;
            _document.FrameDisplayed -= OnFrame;
        }
        _document = doc;
        if (doc is not null)
        {
            doc.ChannelsChanged += QueueRefresh;
            doc.PropertyChanged += OnDocumentChanged;
            doc.FrameDisplayed += OnFrame;
        }
        OnPropertyChanged(nameof(HasDocument));
        Refresh();
    }

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.UndoText) or nameof(DocumentViewModel.SelectedLayer) or nameof(DocumentViewModel.EditMask)
            or nameof(DocumentViewModel.IsQuickMask))
            QueueRefresh();
    }

    // Color thumbnails come from the last full-resolution render.
    private void OnFrame(bool full)
    {
        if (!full || _thumbsQueued) return;
        _thumbsQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _thumbsQueued = false;
            UpdateColorThumbnails();
        }, DispatcherPriority.Background);
    }

    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            Refresh();
        }, DispatcherPriority.Background);
    }

    /// <summary>Rebuilds the rows from the document (rows keep their order: color, saved, then quick or layer mask).</summary>
    public void Refresh()
    {
        var rows = BuildRows();
        // Same rows: update them in place, so a double-click lands on the row it started on.
        if (rows.Count == Rows.Count && rows.Zip(Rows).All(p => (p.First.Kind, p.First.Key, p.First.Name, p.First.Shortcut) == (p.Second.Kind, p.Second.Key, p.Second.Name, p.Second.Shortcut)))
        {
            foreach (var (fresh, row) in rows.Zip(Rows))
            {
                row.IsVisible = fresh.IsVisible;
                row.IsTargeted = fresh.IsTargeted;
                if (fresh.Thumbnail is not null || row.Kind is ChannelRowKind.Channel or ChannelRowKind.QuickMask or ChannelRowKind.LayerMask)
                    row.Thumbnail = fresh.Thumbnail;
            }
        }
        else
        {
            Rows.Clear();
            foreach (var row in rows) Rows.Add(row);
        }
        UpdateColorThumbnails();
    }

    private List<ChannelRow> BuildRows()
    {
        var Rows = new List<ChannelRow>();
        if (_document is not { } doc) return Rows;
        int colors = doc.ColorChannelCount;
        string[] colorNames = colors == 3 ? ["Red", "Green", "Blue"] : colors == 1 ? ["Gray"] : [];
        if (colors != 1)
            Rows.Add(new ChannelRow(ChannelRowKind.Composite, -1, colors == 3 ? "RGB" : doc.Model.ColorMode.ToString(), "⌘2")
            {
                IsVisible = doc.IsCompositeVisible,
                IsTargeted = doc.IsCompositeTargeted,
            });
        for (int k = 0; k < colors; k++)
            Rows.Add(new ChannelRow(ChannelRowKind.Color, k, colorNames[k], doc.ShortcutFor(colorIndex: k))
            {
                IsVisible = doc.IsColorVisible(k),
                IsTargeted = doc.IsColorTargeted(k),
            });
        var channels = doc.Channels;
        for (int i = 0; i < channels.Count; i++)
        {
            var c = channels[i];
            Rows.Add(new ChannelRow(ChannelRowKind.Channel, c.Id, c.Name, doc.ShortcutFor(channelIndex: i))
            {
                IsVisible = doc.IsChannelVisible(c.Id),
                IsTargeted = doc.IsChannelTargeted(c.Id),
                Thumbnail = PlaneThumbnail(doc.Model, c.Pixels),
            });
        }
        if (doc.IsQuickMask && doc.QuickMaskPlane is { } qm)
            Rows.Add(new ChannelRow(ChannelRowKind.QuickMask, 0, "Quick Mask", "")
            {
                IsVisible = doc.IsQuickMaskVisible,
                IsTargeted = doc.ChannelTarget == ChannelTargetKind.QuickMask,
                Thumbnail = PlaneThumbnail(doc.Model, qm),
            });
        else if (doc.SelectedLayer?.Node is { } node && node.GetMask() is { } mask && doc.ChannelTarget == ChannelTargetKind.LayerMask)
            Rows.Add(new ChannelRow(ChannelRowKind.LayerMask, 0, $"{node.Name} Mask", @"\")
            {
                IsVisible = doc.IsLayerMaskVisible,
                IsTargeted = true,
                Thumbnail = MaskThumbnail(doc.Model, mask),
            });
        return Rows;
    }

    private void UpdateColorThumbnails()
    {
        if (_document is not { LastFullRender: { } rgba } doc || rgba.Length != (long)doc.Model.Width * doc.Model.Height * 4) return;
        foreach (var row in Rows)
            if (row.Kind is ChannelRowKind.Composite or ChannelRowKind.Color)
                row.Thumbnail = ColorThumbnail(doc.Model, rgba, row.Kind == ChannelRowKind.Composite ? -1 : row.Key);
    }

    private static (int W, int H) ThumbSizeFor(Document doc)
    {
        double scale = (double)ThumbSize / Math.Max(doc.Width, doc.Height);
        return (Math.Max(1, (int)Math.Round(doc.Width * scale)), Math.Max(1, (int)Math.Round(doc.Height * scale)));
    }

    private static Bitmap Thumbnail(Document doc, Func<int, int, (byte R, byte G, byte B)> sample)
    {
        var (w, h) = ThumbSizeFor(doc);
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int dx = Math.Min(doc.Width - 1, (int)((x + 0.5) * doc.Width / w)), dy = Math.Min(doc.Height - 1, (int)((y + 0.5) * doc.Height / h));
                var (r, g, b) = sample(dx, dy);
                int i = (y * w + x) * 4;
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (r, g, b, 255);
            }
        return BitmapFactory.FromRgba(rgba, w, h);
    }

    private static Bitmap PlaneThumbnail(Document doc, Plane plane) => Thumbnail(doc, (x, y) =>
    {
        byte v = x < plane.Width && y < plane.Height ? (byte)MathF.Round(plane.GetNormalized(y * plane.Width + x) * 255f) : (byte)0;
        return (v, v, v);
    });

    private static Bitmap MaskThumbnail(Document doc, LayerMask mask) => Thumbnail(doc, (x, y) =>
    {
        byte v = (byte)MathF.Round(MaskBaker.Sample(mask, x, y) * 255f);
        return (v, v, v);
    });

    /// <summary>The composite (<paramref name="k"/> -1) or one color channel in gray, over white.</summary>
    private static Bitmap ColorThumbnail(Document doc, byte[] rgba, int k) => Thumbnail(doc, (x, y) =>
    {
        int i = (y * doc.Width + x) * 4;
        float a = rgba[i + 3] / 255f;
        byte Over(int c) => (byte)MathF.Round(rgba[i + c] * a + 255 * (1 - a));
        if (k < 0) return (Over(0), Over(1), Over(2));
        byte v = Over(Math.Min(k, 2));
        return (v, v, v);
    });

    // ---- Interaction -------------------------------------------------------------------------------------------

    /// <summary>A click on a row: targets it (Shift adds a color channel); ⌘-click loads it as a selection (Shift adds, Option subtracts).</summary>
    public void Click(ChannelRow row, bool shift, bool command, bool option)
    {
        if (_document is not { } doc) return;
        if (command)
        {
            var mode = shift && option ? SelectionMode.Intersect : shift ? SelectionMode.Add : option ? SelectionMode.Subtract : SelectionMode.Replace;
            switch (row.Kind)
            {
                case ChannelRowKind.Channel: doc.LoadSelection(row.Key, mode: mode); break;
                case ChannelRowKind.Composite: doc.LoadColorChannelAsSelection(-1, mode); break;
                case ChannelRowKind.Color: doc.LoadColorChannelAsSelection(doc.ColorChannelCount == 1 ? -1 : row.Key, mode); break;
                case ChannelRowKind.LayerMask: doc.LoadLayerMaskAsSelection(mode); break;
            }
            return;
        }
        switch (row.Kind)
        {
            case ChannelRowKind.Composite: doc.TargetComposite(); break;
            case ChannelRowKind.Color when doc.ColorChannelCount == 1: doc.TargetComposite(); break;
            case ChannelRowKind.Color: doc.TargetColor(row.Key, shift); break;
            case ChannelRowKind.Channel: doc.TargetChannel(row.Key, shift); break;
            case ChannelRowKind.QuickMask: doc.TargetQuickMask(); break;
            case ChannelRowKind.LayerMask: doc.TargetLayerMask(); break;
        }
    }

    /// <summary>A click on a row's eye.</summary>
    public void ToggleEye(ChannelRow row)
    {
        if (_document is not { } doc) return;
        bool on = !row.IsVisible;
        switch (row.Kind)
        {
            case ChannelRowKind.Composite: doc.SetCompositeVisible(on); break;
            case ChannelRowKind.Color when doc.ColorChannelCount == 1: doc.SetCompositeVisible(on); break;
            case ChannelRowKind.Color: doc.SetColorVisible(row.Key, on); break;
            case ChannelRowKind.Channel: doc.SetChannelVisible(row.Key, on); break;
            case ChannelRowKind.QuickMask: doc.SetQuickMaskVisible(on); break;
            case ChannelRowKind.LayerMask: doc.SetLayerMaskVisible(on); break;
        }
    }

    /// <summary>Double-clicking a saved selection, spot channel or the quick mask opens its options.</summary>
    public void Open(ChannelRow row)
    {
        if (_document is not { } doc) return;
        if (row.Kind == ChannelRowKind.Channel)
        {
            doc.TargetChannel(row.Key);
            Editor.EditChannelOptionsCommand.Execute(null);
        }
        else if (row.Kind == ChannelRowKind.QuickMask) Editor.EditQuickMaskOptionsCommand.Execute(null);
    }

    public void Dispose()
    {
        Editor.PropertyChanged -= OnEditorChanged;
        Follow(null);
    }
}
