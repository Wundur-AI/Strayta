using System.Diagnostics;
using System.Numerics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;

namespace Strayta.Editor.ViewModels;

/// <summary>How Select and Mask shows the refined selection (Photoshop's View Mode, in its order).</summary>
public enum RefineView
{
    /// <summary>The unselected part of the image made transparent (by the Transparency amount) over the checkerboard.</summary>
    OnionSkin,
    /// <summary>The image as it is, with the refined selection's outline as marching ants.</summary>
    MarchingAnts,
    /// <summary>The unselected area tinted red, like Quick Mask.</summary>
    Overlay,
    OnBlack,
    OnWhite,
    /// <summary>The selection itself: white selected, black not.</summary>
    BlackAndWhite,
    /// <summary>The image masked by the refined selection over the layers below the selected one.</summary>
    OnLayers,
}

/// <summary>The Select and Mask workspace's tools (Photoshop's, less Object Selection).</summary>
public enum RefineTool
{
    QuickSelection,
    RefineEdge,
    Brush,
    Lasso,
    Hand,
    Zoom,
}

/// <summary>Select and Mask settings kept between sessions when Remember Settings is on.</summary>
public sealed record SelectAndMaskSettings(
    RefineView View, double Radius, bool SmartRadius, double Smooth, double Feather, double Contrast, double ShiftEdge,
    bool Decontaminate, double DecontaminateAmount, RefineOutput Output, IReadOnlyDictionary<RefineView, double> ViewAmounts);

/// <summary>
/// One Select and Mask session: the settings, the workspace's tools (Quick Selection, Refine Edge brush, Brush, Lasso),
/// its own undo history, and a live preview of the refined selection over the image. Nothing touches the document until
/// the session is applied.
/// </summary>
/// <remarks>
/// Like the main canvas, the preview runs two lanes. The live lane refines a copy of the image scaled to the view (one
/// image pixel per device pixel or so), so every slider step, brush dab or tool stroke answers within a frame or two,
/// and it never queues: changes that arrive while it works are folded into one next run. The full lane refines at full
/// resolution once changes pause for a moment, is cancelled by the next change, and replaces the preview only if nothing
/// changed meanwhile. Zoomed in to 100% or more the live lane itself works at full resolution. Both lanes draw the
/// display image into its bitmap in the background; the UI thread only swaps it in.
/// <para>The selection being refined (the "base") changes with the tools. Each level rebuilds its refiner from the new
/// base in the background the next time it runs; a Brush stroke in progress is merged into the base per frame at the
/// level's resolution, so it shows live without touching the full-resolution selection until release.</para>
/// </remarks>
public sealed partial class SelectAndMaskViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan FullDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan MergeWindow = TimeSpan.FromMilliseconds(800);

    private readonly byte[] _render;          // the document, straight RGBA, full resolution
    private byte[]? _below;                   // the layers below the selected one, straight RGBA (On Layers), once rendered
    private readonly SampleImage _sample;     // the document, premultiplied, for edge detection and Quick Selection
    private readonly RefineBrushMask _brush;
    private readonly List<(Vector2 From, Vector2 To, float Diameter, bool Erase)> _strokes = [];
    private readonly Task<Level> _full;
    private readonly Dictionary<RefineView, double> _viewAmounts = new()
    {
        [RefineView.OnionSkin] = 50, [RefineView.Overlay] = 50, [RefineView.OnBlack] = 100, [RefineView.OnWhite] = 100,
    };
    private Task<Level>? _preview;
    private int _previewFactor = 1;
    private int _version, _shownVersion = -1;
    private bool _previewRunning, _previewPending, _disposed, _restoring;
    private CancellationTokenSource? _fullCancel;
    private Vector2 _lastBrush;
    private bool _brushErase;
    private int _strokeStart;
    private readonly Stopwatch _sinceChange = new();

    // The selection being refined, and a version that tells levels to rebuild their refiners.
    private SelectionMask? _base;
    private int _baseVersion;

    /// <summary>Everything needed to refine and display at one scale (1 = full resolution).</summary>
    private sealed class Level
    {
        public required int Factor { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required byte[] Rgba { get; init; }
        public byte[]? Below;
        public required SampleImage Sample { get; init; }
        public required RefineBrushMask Brush { get; init; }

        /// <summary>The refiner for <see cref="BaseVersion"/>'s selection; replaced (never changed) when the base changes.</summary>
        public SelectionRefiner? Refiner;
        public int BaseVersion = -1;

        /// <summary>How many of the session's recorded strokes this level's brush mask holds.</summary>
        public int Strokes { get; set; }
    }

    public SelectAndMaskViewModel(DocumentViewModel document, byte[] render, SampleImage sample, SelectionMask? selection,
        LayerNode? refinesMaskOf = null, Func<Task<byte[]?>>? below = null, SelectAndMaskSettings? remembered = null)
    {
        Document = document;
        _render = render;
        _sample = sample;
        _base = selection;
        RefinesMaskOf = refinesMaskOf;
        _brush = new RefineBrushMask(sample.Width, sample.Height);
        DocumentSize = new Avalonia.PixelSize(sample.Width, sample.Height);
        _restoring = true;
        if (refinesMaskOf is not null) Output = RefineOutput.LayerMask; // refining a mask puts the result back into it
        if (remembered is not null)
        {
            RememberSettings = true;
            Apply(remembered with { Output = refinesMaskOf is not null ? RefineOutput.LayerMask : remembered.Output });
        }
        _restoring = false;
        _full = Task.Run(() => new Level
        {
            Factor = 1, Width = sample.Width, Height = sample.Height, Rgba = render, Sample = sample, Brush = _brush,
        });
        Update();
        _belowSource = below;
        if (View == RefineView.OnLayers) RequestBelow();
    }

    private Func<Task<byte[]?>>? _belowSource;
    private bool _belowRequested;

    /// <summary>The On Layers view's backdrop is rendered the first time the view is chosen, not for every session.</summary>
    private void RequestBelow()
    {
        if (_belowSource is null || _belowRequested || _full is null) return;
        _belowRequested = true;
        _ = ReceiveBelowAsync(_belowSource());
    }

    /// <summary>The On Layers view's backdrop arrives after the view is chosen; the levels pick it up then.</summary>
    private async Task ReceiveBelowAsync(Task<byte[]?> below)
    {
        var pixels = await below;
        if (pixels is null || _disposed) return;
        _below = pixels;
        (await _full).Below = pixels;
        _preview = null; // rebuilt with it
        if (View == RefineView.OnLayers) Update();
    }

    public DocumentViewModel Document { get; }
    public Avalonia.PixelSize DocumentSize { get; }

    /// <summary>The layer whose mask is being refined (Select and Mask with a layer mask targeted), or null.</summary>
    public LayerNode? RefinesMaskOf { get; }

    public string Title => RefinesMaskOf is { } n ? $"Select and Mask — refining the mask of \"{n.Name}\"" : "Select and Mask";

    [ObservableProperty] public partial RefineView View { get; set; } = RefineView.Overlay;
    [ObservableProperty] public partial double Radius { get; set; }
    [ObservableProperty] public partial bool SmartRadius { get; set; }
    [ObservableProperty] public partial double Smooth { get; set; }
    [ObservableProperty] public partial double Feather { get; set; }
    [ObservableProperty] public partial double Contrast { get; set; }
    [ObservableProperty] public partial double ShiftEdge { get; set; }

    /// <summary>Decontaminate Colors: fringe colors replaced by nearby foreground; forces output to a new layer, as in Photoshop.</summary>
    [ObservableProperty] public partial bool Decontaminate { get; set; }

    /// <summary>Decontaminate Colors' Amount, 0–100%.</summary>
    [ObservableProperty] public partial double DecontaminateAmount { get; set; } = 50;

    [ObservableProperty] public partial RefineOutput Output { get; set; } = RefineOutput.Selection;

    /// <summary>Keep these settings for the next session (Photoshop's Remember Settings).</summary>
    [ObservableProperty] public partial bool RememberSettings { get; set; }

    /// <summary>Refine Edge brush diameter in image pixels.</summary>
    [ObservableProperty] public partial double BrushSize { get; set; } = 60;

    /// <summary>Quick Selection and Brush diameter in image pixels.</summary>
    [ObservableProperty] public partial double SelectBrushSize { get; set; } = 40;

    /// <summary>Quick Selection, Brush and Lasso add to the selection (true) or subtract from it (Option always subtracts).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSubtractMode), nameof(DefaultMode))]
    public partial bool IsAddMode { get; set; } = true;

    public bool IsSubtractMode { get => !IsAddMode; set => IsAddMode = !value; }

    public SelectionMode DefaultMode => IsAddMode ? SelectionMode.Add : SelectionMode.Subtract;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanvasTool), nameof(ToolBrushSize), nameof(ToolName), nameof(UsesSize), nameof(UsesMode), nameof(ToolHint))]
    [NotifyPropertyChangedFor(nameof(IsQuickSelectionTool), nameof(IsRefineEdgeTool), nameof(IsBrushTool), nameof(IsLassoTool), nameof(IsHandTool), nameof(IsZoomTool))]
    public partial RefineTool Tool { get; set; } = RefineTool.RefineEdge;

    public bool IsQuickSelectionTool { get => Tool == RefineTool.QuickSelection; set { if (value) Tool = RefineTool.QuickSelection; } }
    public bool IsRefineEdgeTool { get => Tool == RefineTool.RefineEdge; set { if (value) Tool = RefineTool.RefineEdge; } }
    public bool IsBrushTool { get => Tool == RefineTool.Brush; set { if (value) Tool = RefineTool.Brush; } }
    public bool IsLassoTool { get => Tool == RefineTool.Lasso; set { if (value) Tool = RefineTool.Lasso; } }
    public bool IsHandTool { get => Tool == RefineTool.Hand; set { if (value) Tool = RefineTool.Hand; } }
    public bool IsZoomTool { get => Tool == RefineTool.Zoom; set { if (value) Tool = RefineTool.Zoom; } }

    public CanvasTool CanvasTool => Tool switch
    {
        RefineTool.QuickSelection => CanvasTool.QuickSelect,
        RefineTool.RefineEdge or RefineTool.Brush => CanvasTool.Brush,
        RefineTool.Lasso => CanvasTool.Lasso,
        RefineTool.Zoom => CanvasTool.Zoom,
        _ => CanvasTool.Hand,
    };

    public double ToolBrushSize
    {
        get => Tool == RefineTool.RefineEdge ? BrushSize : SelectBrushSize;
        set
        {
            if (Tool == RefineTool.RefineEdge) BrushSize = Math.Clamp(value, 1, 1000);
            else SelectBrushSize = Math.Clamp(value, 1, 1000);
        }
    }

    public bool UsesSize => Tool is RefineTool.QuickSelection or RefineTool.RefineEdge or RefineTool.Brush;
    public bool UsesMode => Tool is RefineTool.QuickSelection or RefineTool.Brush or RefineTool.Lasso;

    public string ToolName => Tool switch
    {
        RefineTool.QuickSelection => "Quick Selection",
        RefineTool.RefineEdge => "Refine Edge Brush",
        RefineTool.Brush => "Brush",
        RefineTool.Lasso => "Lasso",
        RefineTool.Zoom => "Zoom",
        _ => "Hand",
    };

    public string ToolHint => Tool switch
    {
        RefineTool.QuickSelection => "Paint over an area to add it · Option subtracts",
        RefineTool.RefineEdge => "Paint over hair or fur to let edge detection decide there · Option erases",
        RefineTool.Brush => "Paint to add to the selection · Option subtracts",
        RefineTool.Lasso => "Drag around an area to add it · Option subtracts",
        RefineTool.Zoom => "Click zooms in · Option-click zooms out",
        _ => "Drag to pan · wheel zooms",
    } + " · ⌘Z undoes · Enter OK · Esc cancels";

    partial void OnToolChanged(RefineTool value) => OnPropertyChanged(nameof(ToolBrushSize));
    partial void OnBrushSizeChanged(double value) => OnPropertyChanged(nameof(ToolBrushSize));
    partial void OnSelectBrushSizeChanged(double value) => OnPropertyChanged(nameof(ToolBrushSize));

    /// <summary>What the canvas shows: the image with the refined selection, at preview or full resolution.</summary>
    [ObservableProperty] public partial Bitmap? Preview { get; private set; }

    /// <summary>Resolution and timing of the image on screen, for the panel's footer.</summary>
    [ObservableProperty] public partial string Info { get; private set; } = "";

    /// <summary>The refined selection's outline in the Marching Ants view (image coordinates), else null.</summary>
    [ObservableProperty] public partial IReadOnlyList<Vector2[]>? AntsOutline { get; private set; }

    /// <summary>The live outline of a Quick Selection stroke in progress.</summary>
    [ObservableProperty] public partial IReadOnlyList<Vector2[]>? QuickOutline { get; private set; }

    /// <summary>The selection being refined (the canvas uses it to pick the tools' modes; its outline is not drawn).</summary>
    public SelectionMask? BaseSelection => _base;

    // The panel's drop-downs pick by position, in the enums' order.
    public int ViewIndex { get => (int)View; set => View = (RefineView)Math.Clamp(value, 0, 6); }
    public int OutputIndex { get => (int)Output; set => Output = (RefineOutput)Math.Clamp(value, 0, 3); }

    /// <summary>The view's Transparency (Onion Skin) or Opacity (Overlay, On Black, On White), 0–100.</summary>
    public double ViewAmount
    {
        get => _viewAmounts.GetValueOrDefault(View, 100);
        set
        {
            if (!HasViewAmount || _viewAmounts.GetValueOrDefault(View) == value) return;
            double old = _viewAmounts[View];
            var view = View;
            _viewAmounts[view] = Math.Clamp(value, 0, 100);
            OnPropertyChanged();
            RecordSetting("View " + view, () => SetViewAmount(view, old), () => SetViewAmount(view, value));
            Update();
        }
    }

    private void SetViewAmount(RefineView view, double value)
    {
        _viewAmounts[view] = value;
        OnPropertyChanged(nameof(ViewAmount));
        Update();
    }

    public bool HasViewAmount => View is RefineView.OnionSkin or RefineView.Overlay or RefineView.OnBlack or RefineView.OnWhite;
    public string ViewAmountLabel => View == RefineView.OnionSkin ? "Transparency (%)" : "Opacity (%)";

    /// <summary>Decontaminate Colors only makes new pixels, so Photoshop limits the output to new layers.</summary>
    public RefineOutput EffectiveOutput => Decontaminate && Output is RefineOutput.Selection or RefineOutput.LayerMask ? RefineOutput.NewLayerWithLayerMask : Output;

    public string OutputNote => Decontaminate && Output != EffectiveOutput ? "Decontaminate Colors outputs to a new layer with a layer mask." : "";

    public RefineSettings Settings => new((float)Radius, (float)Smooth, (float)Feather, (float)Contrast, (float)ShiftEdge, SmartRadius);

    /// <summary>`[` and `]`: the current tool's brush one step smaller or larger, like the painting tools.</summary>
    public void ResizeBrush(int direction)
    {
        double size = ToolBrushSize;
        double step = size < 10 ? 1 : size < 50 ? 5 : size < 100 ? 10 : 25;
        size = Math.Clamp(size + direction * step, 1, 1000);
        if (Tool == RefineTool.RefineEdge) BrushSize = size;
        else SelectBrushSize = size;
    }

    partial void OnViewChanged(RefineView oldValue, RefineView newValue)
    {
        OnPropertyChanged(nameof(ViewIndex));
        OnPropertyChanged(nameof(ViewAmount));
        OnPropertyChanged(nameof(HasViewAmount));
        OnPropertyChanged(nameof(ViewAmountLabel));
        if (newValue != RefineView.MarchingAnts) AntsOutline = null;
        if (newValue == RefineView.OnLayers) RequestBelow();
        RecordSetting(nameof(View), () => View = oldValue, () => View = newValue);
        Update();
    }

    partial void OnOutputChanged(RefineOutput value)
    {
        OnPropertyChanged(nameof(OutputIndex));
        OnPropertyChanged(nameof(EffectiveOutput));
        OnPropertyChanged(nameof(OutputNote));
    }

    partial void OnRadiusChanged(double oldValue, double newValue) => SettingChanged(nameof(Radius), () => Radius = oldValue, () => Radius = newValue);
    partial void OnSmartRadiusChanged(bool oldValue, bool newValue) => SettingChanged(nameof(SmartRadius), () => SmartRadius = oldValue, () => SmartRadius = newValue);
    partial void OnSmoothChanged(double oldValue, double newValue) => SettingChanged(nameof(Smooth), () => Smooth = oldValue, () => Smooth = newValue);
    partial void OnFeatherChanged(double oldValue, double newValue) => SettingChanged(nameof(Feather), () => Feather = oldValue, () => Feather = newValue);
    partial void OnContrastChanged(double oldValue, double newValue) => SettingChanged(nameof(Contrast), () => Contrast = oldValue, () => Contrast = newValue);
    partial void OnShiftEdgeChanged(double oldValue, double newValue) => SettingChanged(nameof(ShiftEdge), () => ShiftEdge = oldValue, () => ShiftEdge = newValue);

    partial void OnDecontaminateChanged(bool oldValue, bool newValue)
    {
        OnPropertyChanged(nameof(EffectiveOutput));
        OnPropertyChanged(nameof(OutputNote));
        SettingChanged(nameof(Decontaminate), () => Decontaminate = oldValue, () => Decontaminate = newValue);
    }

    partial void OnDecontaminateAmountChanged(double oldValue, double newValue) =>
        SettingChanged(nameof(DecontaminateAmount), () => DecontaminateAmount = oldValue, () => DecontaminateAmount = newValue);

    private void SettingChanged(string name, Action undo, Action redo)
    {
        RecordSetting(name, undo, redo);
        Update();
    }

    /// <summary>The settings to remember for the next session.</summary>
    public SelectAndMaskSettings CurrentSettings => new(View, Radius, SmartRadius, Smooth, Feather, Contrast, ShiftEdge,
        Decontaminate, DecontaminateAmount, Output, new Dictionary<RefineView, double>(_viewAmounts));

    private void Apply(SelectAndMaskSettings s)
    {
        View = s.View;
        Radius = s.Radius;
        SmartRadius = s.SmartRadius;
        Smooth = s.Smooth;
        Feather = s.Feather;
        Contrast = s.Contrast;
        ShiftEdge = s.ShiftEdge;
        Decontaminate = s.Decontaminate;
        DecontaminateAmount = s.DecontaminateAmount;
        Output = s.Output;
        foreach (var (view, amount) in s.ViewAmounts) _viewAmounts[view] = amount;
    }

    // ---- Undo inside the workspace -----------------------------------------------------------------------

    private sealed record Step(string Name, Action Undo, Action Redo, string? MergeKey)
    {
        public Action Redo { get; set; } = Redo;
        public DateTime At { get; set; } = DateTime.UtcNow;
    }

    private readonly List<Step> _undo = [];
    private readonly List<Step> _redo = [];

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string UndoText => _undo.Count > 0 ? $"Undo {_undo[^1].Name}" : "Undo";

    private void Record(string name, Action undo, Action redo, string? mergeKey = null)
    {
        if (_restoring) return;
        var now = DateTime.UtcNow;
        if (mergeKey is not null && _undo.Count > 0 && _undo[^1].MergeKey == mergeKey && now - _undo[^1].At < MergeWindow)
        {
            // A slider drag: one step from where it started to where it ended.
            _undo[^1].Redo = redo;
            _undo[^1].At = now;
        }
        else _undo.Add(new Step(name, undo, redo, mergeKey));
        _redo.Clear();
        NotifyUndo();
    }

    private void RecordSetting(string name, Action undo, Action redo) => Record(name, undo, redo, mergeKey: name);

    private void NotifyUndo()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoText));
    }

    /// <summary>⌘Z inside the workspace: undoes the last setting change, brush stroke or selection change.</summary>
    public void Undo() => Replay(_undo, _redo, s => s.Undo);

    /// <summary>⇧⌘Z inside the workspace.</summary>
    public void Redo() => Replay(_redo, _undo, s => s.Redo);

    private void Replay(List<Step> from, List<Step> to, Func<Step, Action> action)
    {
        if (from.Count == 0) return;
        var step = from[^1];
        from.RemoveAt(from.Count - 1);
        _restoring = true;
        try
        {
            action(step)();
        }
        finally
        {
            _restoring = false;
        }
        step.At = default; // never merge into a step that was undone or redone
        to.Add(step);
        NotifyUndo();
        Update();
    }

    // ---- Timings (self-test and benchmark) ------------------------------------------------------------

    /// <summary>Milliseconds from the last change to its preview on screen, and to the full-resolution image.</summary>
    public double LastPreviewLatencyMs { get; private set; }
    public double LastFullLatencyMs { get; private set; }

    /// <summary>The longest the UI thread spent handing a finished image to the canvas.</summary>
    public double MaxUiMs { get; private set; }

    /// <summary>True when the image on screen is the full-resolution result of the current settings.</summary>
    public bool IsFullShown { get; private set; }

    /// <summary>Raised on the UI thread whenever a new image is shown.</summary>
    public event Action? PreviewShown;

    // ---- View scale -----------------------------------------------------------------------------------

    /// <summary>
    /// The canvas zoom in device pixels per image pixel. Picks the preview lane's scale: the largest power of two
    /// (up to 8) that still gives at least one preview pixel per device pixel.
    /// </summary>
    public void SetViewScale(double devicePixelsPerImagePixel)
    {
        int factor = 1;
        while (factor < 8 && devicePixelsPerImagePixel * factor * 2 <= 1.0001) factor *= 2;
        if (factor == _previewFactor) return;
        _previewFactor = factor;
        _preview = null;
        Update();
    }

    private Task<Level> PreviewLevel()
    {
        if (_previewFactor == 1) return _full;
        if (_preview is { } p) return p;
        int f = _previewFactor;
        var strokes = _strokes.ToList();
        return _preview = Task.Run(() => BuildLevel(f, strokes));
    }

    /// <summary>A reduced copy of the image and brush strokes: block averages of <paramref name="f"/>×<paramref name="f"/> pixels.</summary>
    private Level BuildLevel(int f, List<(Vector2 From, Vector2 To, float Diameter, bool Erase)> strokes)
    {
        int w = _sample.Width, h = _sample.Height, lw = (w + f - 1) / f, lh = (h + f - 1) / f;
        var straight = Reduce(_render, w, h, f, 4);
        var premultiplied = Reduce(_sample.Rgba, w, h, f, 4);
        var below = _below is null ? null : Reduce(_below, w, h, f, 4);
        var brush = new RefineBrushMask(lw, lh);
        foreach (var (from, to, d, erase) in strokes) brush.Paint(from / f, to / f, d / f, erase);
        return new Level
        {
            Factor = f, Width = lw, Height = lh, Rgba = straight, Below = below, Sample = new SampleImage(lw, lh, premultiplied),
            Brush = brush, Strokes = strokes.Count,
        };
    }

    private static byte[] Reduce(byte[] src, int w, int h, int f, int channels)
    {
        int lw = (w + f - 1) / f, lh = (h + f - 1) / f;
        var dst = new byte[(long)lw * lh * channels];
        Parallel.For(0, lh, ly =>
        {
            Span<int> sum = stackalloc int[channels];
            int y0 = ly * f, y1 = Math.Min(y0 + f, h);
            for (int lx = 0; lx < lw; lx++)
            {
                int x0 = lx * f, x1 = Math.Min(x0 + f, w);
                sum.Clear();
                for (int y = y0; y < y1; y++)
                    for (int x = x0, i = (y * w + x0) * channels; x < x1; x++)
                        for (int c = 0; c < channels; c++, i++) sum[c] += src[i];
                int n = (y1 - y0) * (x1 - x0), o = (ly * lw + lx) * channels;
                for (int c = 0; c < channels; c++) dst[o + c] = (byte)((sum[c] + n / 2) / n);
            }
        });
        return dst;
    }

    /// <summary>The level's refiner for the base selection <paramref name="version"/>, rebuilt (in the background) when the base changed.</summary>
    private SelectionRefiner RefinerFor(Level level, SelectionMask? selection, int version)
    {
        lock (level)
        {
            if (level.Refiner is { } r && level.BaseVersion == version) return r;
            SelectionRefiner refiner;
            if (level.Factor == 1) refiner = new SelectionRefiner(level.Sample, selection);
            else
            {
                int w = _sample.Width, h = _sample.Height;
                var coverage = SelectionLayerMask.Coverage(selection, _sample.Bounds);
                refiner = new SelectionRefiner(level.Sample, Reduce(coverage, w, h, level.Factor, 1));
            }
            // A newer base may already be in place (lanes run concurrently); only move forward.
            if (version >= level.BaseVersion)
            {
                level.Refiner = refiner;
                level.BaseVersion = version;
            }
            return refiner;
        }
    }

    // ---- Refine Edge brush and Brush ---------------------------------------------------------------------

    private PaintStroke? _paint;
    private bool _paintAdd;

    /// <summary>
    /// Starts a stroke of the current tool at an image position: the Refine Edge brush (Option erases refinements) or
    /// the Brush (adds to the selection; Option or Subtract mode takes away).
    /// </summary>
    public bool BeginStroke(float x, float y, bool option)
    {
        if (Tool == RefineTool.Brush)
        {
            _paintAdd = IsAddMode != option;
            _paint = new PaintStroke(new PixelLayer(), new BrushSettings((float)SelectBrushSize, 0.9f, 1f), default, erase: false, _sample.Bounds);
            _paint.StrokeTo(x, y);
            Update();
            return true;
        }
        return BeginBrush(x, y, option);
    }

    public void ContinueStroke(float x, float y)
    {
        if (_paint is { } paint)
        {
            paint.StrokeTo(x, y);
            Update();
        }
        else ContinueBrush(x, y);
    }

    public async Task EndStrokeAsync()
    {
        if (_paint is not { } paint)
        {
            EndBrush();
            return;
        }
        bool add = _paintAdd;
        var before = _base;
        var canvas = _sample.Bounds;
        var next = await Task.Run(() =>
        {
            var b = paint.Bounds.Intersect(canvas);
            if (b.IsEmpty) return before;
            var coverage = new byte[b.Width * b.Height];
            for (int y = b.Top; y < b.Bottom; y++)
                for (int x = b.Left; x < b.Right; x++)
                    coverage[(y - b.Top) * b.Width + (x - b.Left)] = (byte)MathF.Round(paint.CoverageAt(x, y) * 255f);
            return SelectionMask.Combine(before, SelectionMask.FromCoverage(b, coverage, canvas), add ? SelectionMode.Add : SelectionMode.Subtract);
        });
        _paint = null;
        if (!ReferenceEquals(before, _base)) return; // changed meanwhile (undo)
        SetBase(next, "Brush");
    }

    /// <summary>Starts a Refine Edge stroke at an image position; Option (<paramref name="erase"/>) erases refinements.</summary>
    public bool BeginBrush(float x, float y, bool erase)
    {
        _brushErase = erase;
        _lastBrush = new Vector2(x, y);
        _strokeStart = _strokes.Count;
        PaintBrush(_lastBrush, _lastBrush);
        return true;
    }

    public void ContinueBrush(float x, float y)
    {
        var p = new Vector2(x, y);
        PaintBrush(_lastBrush, p);
        _lastBrush = p;
    }

    /// <summary>Ends a Refine Edge stroke: one step in the workspace's history.</summary>
    public void EndBrush()
    {
        int start = _strokeStart, end = _strokes.Count;
        if (end <= start) return;
        var segments = _strokes.GetRange(start, end - start);
        Record("Refine Edge Brush", () => RemoveStrokes(start), () => AddStrokes(segments));
    }

    /// <summary>
    /// Stamps the segment into the full-resolution mask and records it; the preview's reduced mask catches up when
    /// the preview lane next runs. Masks are only ever changed on the UI thread; the lanes refine from snapshots.
    /// </summary>
    private void PaintBrush(Vector2 from, Vector2 to)
    {
        float d = (float)BrushSize;
        _strokes.Add((from, to, d, _brushErase));
        _brush.Paint(from, to, d, _brushErase);
        Update();
    }

    private void RemoveStrokes(int count)
    {
        _strokes.RemoveRange(count, _strokes.Count - count);
        RepaintBrushes();
    }

    private void AddStrokes(List<(Vector2 From, Vector2 To, float Diameter, bool Erase)> segments)
    {
        _strokes.AddRange(segments);
        RepaintBrushes();
    }

    /// <summary>After undo or redo of a stroke, the masks are painted again from the recorded strokes.</summary>
    private void RepaintBrushes()
    {
        _brush.Clear();
        foreach (var (from, to, d, erase) in _strokes) _brush.Paint(from, to, d, erase);
        if (_preview is { IsCompletedSuccessfully: true } p)
        {
            p.Result.Brush.Clear();
            p.Result.Strokes = 0;
        }
        else _preview = null;
    }

    /// <summary>Paints the strokes recorded since a reduced level last caught up (or was built) into its mask.</summary>
    private void CatchUp(Level level)
    {
        if (level.Factor == 1) return; // the full-resolution mask is painted directly
        for (; level.Strokes < _strokes.Count; level.Strokes++)
        {
            var (from, to, d, erase) = _strokes[level.Strokes];
            level.Brush.Paint(from / level.Factor, to / level.Factor, d / level.Factor, erase);
        }
    }

    // ---- Quick Selection and Lasso ------------------------------------------------------------------------

    private Task<QuickSelectionImage>? _quickImage;
    private QuickSelectionStroke? _quick;
    private Task? _quickPump;
    private readonly List<Vector2> _quickPending = [];
    private Vector2 _quickLast;
    private Task<QuickSelectionStroke>? _quickStart;

    /// <summary>
    /// Starts a Quick Selection stroke. The canvas reports Photoshop's modifiers (Option subtracts, Shift adds); without
    /// them the workspace's Add / Subtract mode applies.
    /// </summary>
    public bool BeginQuickSelection(float x, float y, SelectionMode mode)
    {
        if (_quickStart is not null) return false;
        var m = mode == SelectionMode.Subtract ? SelectionMode.Subtract : mode == SelectionMode.Add ? SelectionMode.Add : DefaultMode;
        if (_base is null && m == SelectionMode.Subtract) return false;
        var before = _base;
        float size = (float)SelectBrushSize;
        _quickImage ??= Task.Run(() => QuickSelectionImage.Build(_sample));
        var image = _quickImage;
        _quickStart = Task.Run(async () => new QuickSelectionStroke(await image, size, before, m == SelectionMode.Subtract ? SelectionMode.Subtract : SelectionMode.Add));
        _quickLast = new Vector2(x, y);
        _quickPending.Clear();
        _quickPending.Add(_quickLast);
        _quickPump = PumpQuickAsync();
        return true;
    }

    public void ContinueQuickSelection(float x, float y)
    {
        if (_quickStart is null) return;
        _quickPending.Add(new Vector2(x, y));
        if (_quickPump is null || _quickPump.IsCompleted) _quickPump = PumpQuickAsync();
    }

    private async Task PumpQuickAsync()
    {
        if (_quickStart is not { } start) return;
        var stroke = _quick = await start;
        while (_quickPending.Count > 0)
        {
            var points = _quickPending.ToArray();
            _quickPending.Clear();
            var from = _quickLast;
            _quickLast = points[^1];
            var loops = await Task.Run(() =>
            {
                bool grew = false;
                var a = from;
                foreach (var b in points)
                {
                    grew |= stroke.AddSegment(a, b);
                    a = b;
                }
                return grew && !stroke.IsEmpty ? stroke.PreviewOutline() : null;
            });
            if (loops is not null && ReferenceEquals(stroke, _quick)) QuickOutline = loops;
        }
    }

    /// <summary>Release: the stroke's selection becomes the base, as one step.</summary>
    public async Task EndQuickSelectionAsync()
    {
        if (_quickStart is not { } start) return;
        var before = _base;
        while (_quickPump is { IsCompleted: false } pump) await pump;
        var stroke = await start;
        if (_quickPending.Count > 0) await PumpQuickAsync();
        _quickStart = null;
        _quick = null;
        var next = stroke.IsEmpty ? before : await Task.Run(() => stroke.Finish(autoEnhance: false));
        QuickOutline = null;
        if (ReferenceEquals(before, _base)) SetBase(next, "Quick Selection");
    }

    /// <summary>A finished Lasso drag: adds or subtracts the drawn area (a click does nothing).</summary>
    public async Task ApplyLassoAsync(SelectionGesture gesture)
    {
        if (gesture.Points.Count < 3) return;
        var mode = gesture.Mode == SelectionMode.Subtract ? SelectionMode.Subtract
            : gesture.Mode is SelectionMode.Add or SelectionMode.Intersect ? gesture.Mode : DefaultMode;
        var before = _base;
        var canvas = _sample.Bounds;
        var next = await Task.Run(() => SelectionMask.Combine(before, SelectionMask.Polygon(gesture.Points, canvas), mode));
        if (ReferenceEquals(before, _base)) SetBase(next, "Lasso");
    }

    /// <summary>Replaces the selection being refined, as one step in the workspace's history.</summary>
    internal void SetBase(SelectionMask? next, string name)
    {
        var before = _base;
        if (ReferenceEquals(before, next)) return;
        ApplyBase(next);
        Record(name, () => ApplyBase(before), () => ApplyBase(next));
        Update();
    }

    private void ApplyBase(SelectionMask? selection)
    {
        _base = selection;
        _baseVersion++;
        OnPropertyChanged(nameof(BaseSelection));
    }

    // ---- Lanes -----------------------------------------------------------------------------------------

    /// <summary>
    /// Something changed: refresh the preview now, and the full-resolution image after a pause. When the view is
    /// zoomed in far enough that the preview is the full resolution, the live lane refines at full resolution
    /// directly (never cancelled, so a continuous drag still shows every result it can finish).
    /// </summary>
    private void Update()
    {
        if (_disposed || _restoring && _full is null) return;
        _version++;
        _sinceChange.Restart();
        IsFullShown = false;
        _fullCancel?.Cancel();
        _fullCancel = null;
        if (_previewFactor > 1)
        {
            _fullCancel = new CancellationTokenSource();
            _ = RunFullAfterPauseAsync(_version, _fullCancel.Token);
        }
        _ = RunLiveLaneAsync();
    }

    /// <summary>The live lane: at most one refine at a time; changes meanwhile fold into one more run with the latest state.</summary>
    private async Task RunLiveLaneAsync()
    {
        if (_previewRunning)
        {
            _previewPending = true;
            return;
        }
        _previewRunning = true;
        try
        {
            do
            {
                _previewPending = false;
                int version = _version;
                var level = await PreviewLevel();
                if (_disposed) return;
                CatchUp(level);
                var job = Job(level);
                var image = await Task.Run(() => Compose(level, job, CancellationToken.None));
                bool full = level.Factor == 1 && version == _version && job.Live is null;
                if (!_disposed && (version > _shownVersion || full)) Show(image, level, version, full);
                else image.Bitmap.Dispose();
            } while (_previewPending);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Info = $"Preview failed: {ex.Message}";
        }
        finally
        {
            _previewRunning = false;
        }
    }

    /// <summary>The full-resolution lane behind a reduced preview: starts once changes pause, cancelled by the next change.</summary>
    private async Task RunFullAfterPauseAsync(int version, CancellationToken cancel)
    {
        try
        {
            await Task.Delay(FullDelay, cancel);
            if (_paint is not null || _quickStart is not null) return; // mid-stroke: the preview follows it, the full image waits
            var level = await _full;
            if (cancel.IsCancellationRequested) return;
            var job = Job(level);
            var image = await Task.Run(() => Compose(level, job, cancel), cancel);
            if (_disposed || cancel.IsCancellationRequested || version != _version)
            {
                image.Bitmap.Dispose();
                return;
            }
            Show(image, level, version, full: true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A Brush stroke in progress, sampled at a level's pixels: where it paints and whether it adds.</summary>
    private sealed record LiveBrush(PixelRect Area, byte[] Coverage, bool Add);

    /// <summary>What a lane refines, captured on the UI thread: settings scaled to the level, snapshots, the view.</summary>
    private sealed record RefineJob(RefineSettings Settings, RefineBrushSnapshot? Brush, RefineView View, double ViewAmount,
        SelectionMask? Base, int BaseVersion, LiveBrush? Live, float Decontaminate);

    private RefineJob Job(Level level) => new(
        Settings.Scaled(1f / level.Factor), level.Brush.Snapshot(), View, _viewAmounts.GetValueOrDefault(View, 100) / 100.0,
        _base, _baseVersion, _paint is { } p ? SampleStroke(p, _paintAdd, level) : null, Decontaminate ? (float)(DecontaminateAmount / 100) : 0f);

    /// <summary>The Brush stroke's coverage at the level's pixel centers (UI thread, where the stroke is painted).</summary>
    private LiveBrush? SampleStroke(PaintStroke stroke, bool add, Level level)
    {
        int f = level.Factor;
        var b = stroke.Bounds;
        if (b.IsEmpty) return null;
        var area = new PixelRect(b.Left / f, b.Top / f, (b.Right + f - 1) / f, (b.Bottom + f - 1) / f).Intersect(PixelRect.FromSize(level.Width, level.Height));
        if (area.IsEmpty) return null;
        var coverage = new byte[area.Width * area.Height];
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                coverage[(y - area.Top) * area.Width + (x - area.Left)] = (byte)MathF.Round(stroke.CoverageAt(x * f + f / 2, y * f + f / 2) * 255f);
        return new LiveBrush(area, coverage, add);
    }

    private sealed record Composed(WriteableBitmap Bitmap, double ComputeMs, IReadOnlyList<Vector2[]>? Ants);

    /// <summary>Refines and draws the result straight into a new bitmap, all off the UI thread.</summary>
    private Composed Compose(Level level, RefineJob job, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        var refiner = RefinerFor(level, job.Base, job.BaseVersion);
        var matte = refiner.Refine(job.Settings, job.Brush, cancel);
        if (job.Live is { } live)
        {
            // A Brush stroke in progress is drawn over the refined result (the refinement's edge detection is cached,
            // so every frame costs only this); on release the stroke joins the selection and is refined with it.
            matte = (byte[])matte.Clone(); // the refiner may hand back its cached edge matte
            for (int y = live.Area.Top; y < live.Area.Bottom; y++)
                for (int x = live.Area.Left; x < live.Area.Right; x++)
                {
                    int c = live.Coverage[(y - live.Area.Top) * live.Area.Width + (x - live.Area.Left)], i = y * level.Width + x;
                    matte[i] = live.Add ? (byte)Math.Max(matte[i], c) : (byte)Math.Min(matte[i], 255 - c);
                }
        }
        cancel.ThrowIfCancellationRequested();
        var rgba = job.Decontaminate > 0 && job.View != RefineView.BlackAndWhite
            ? ColorDecontamination.Apply(level.Rgba, level.Width, level.Height, matte, job.Decontaminate, cancel)
            : level.Rgba;
        var bitmap = new WriteableBitmap(new Avalonia.PixelSize(level.Width, level.Height), new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Unpremul);
        using (var fb = bitmap.Lock())
            ComposeView(rgba, level.Below, matte, level.Width, level.Height, job.View, (float)job.ViewAmount, fb.Address, fb.RowBytes);
        IReadOnlyList<Vector2[]>? ants = null;
        if (job.View == RefineView.MarchingAnts)
        {
            var loops = SelectionOutline.Trace(SelectionMask.FromCoverage(PixelRect.FromSize(level.Width, level.Height), matte));
            int f = level.Factor;
            ants = f == 1 ? loops : loops.Select(l => l.Select(v => v * f).ToArray()).ToList();
        }
        return new Composed(bitmap, clock.Elapsed.TotalMilliseconds, ants);
    }

    /// <summary>
    /// Draws the display image for a view mode (straight RGBA rows at <paramref name="address"/>) from the document's
    /// pixels and the refined coverage. <paramref name="amount"/> is the view's Opacity (Transparency for Onion Skin), 0..1.
    /// </summary>
    private static unsafe void ComposeView(byte[] rgba, byte[]? below, byte[] matte, int w, int h, RefineView view, float amount, IntPtr address, int rowBytes)
    {
        Parallel.For(0, h, y =>
        {
            var dst = new Span<byte>((byte*)address + (long)y * rowBytes, w * 4);
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x, p = i * 4, d = x * 4;
                int m = matte[i], ia = rgba[p + 3];
                switch (view)
                {
                    case RefineView.OnionSkin:
                    {
                        // The unselected part fades toward the checkerboard by the Transparency amount.
                        float keep = (m + (255 - m) * (1 - amount)) / 255f;
                        rgba.AsSpan(p, 3).CopyTo(dst.Slice(d, 3));
                        dst[d + 3] = MaskByte(ia * keep);
                        break;
                    }
                    case RefineView.MarchingAnts:
                        rgba.AsSpan(p, 4).CopyTo(dst.Slice(d, 4));
                        break;
                    case RefineView.Overlay:
                    {
                        // Red at the Opacity over the unselected part, composited over the image (which may be transparent).
                        float o = (255 - m) / 255f * amount, under = ia / 255f * (1 - o), a = o + under;
                        if (a <= 0)
                        {
                            dst.Slice(d, 4).Clear();
                            break;
                        }
                        dst[d] = MaskByte((255 * o + rgba[p] * under) / a);
                        dst[d + 1] = MaskByte(rgba[p + 1] * under / a);
                        dst[d + 2] = MaskByte(rgba[p + 2] * under / a);
                        dst[d + 3] = MaskByte(a * 255);
                        break;
                    }
                    case RefineView.OnBlack:
                    case RefineView.OnWhite:
                    {
                        float k = ia * m / (255f * 255f), wBack = (1 - k) * amount;
                        int back = view == RefineView.OnWhite ? 255 : 0;
                        for (int c = 0; c < 3; c++) dst[d + c] = MaskByte(rgba[p + c] * (1 - wBack) + back * wBack);
                        dst[d + 3] = 255;
                        break;
                    }
                    case RefineView.OnLayers:
                    {
                        // The masked image over the layers below (transparent without any).
                        float sa = ia * m / (255f * 255f), ba = below is null ? 0 : below[p + 3] / 255f, a = sa + ba * (1 - sa);
                        if (a <= 0)
                        {
                            dst.Slice(d, 4).Clear();
                            break;
                        }
                        for (int c = 0; c < 3; c++)
                            dst[d + c] = MaskByte((rgba[p + c] * sa + (below is null ? 0 : below[p + c]) * ba * (1 - sa)) / a);
                        dst[d + 3] = MaskByte(a * 255);
                        break;
                    }
                    default:
                        dst[d] = dst[d + 1] = dst[d + 2] = (byte)m;
                        dst[d + 3] = 255;
                        break;
                }
            }
        });
    }

    private static byte MaskByte(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);

    private void Show(Composed image, Level level, int version, bool full)
    {
        var clock = Stopwatch.StartNew();
        var old = Preview;
        Preview = image.Bitmap;
        old?.Dispose();
        AntsOutline = View == RefineView.MarchingAnts ? image.Ants : null;
        MaxUiMs = Math.Max(MaxUiMs, clock.Elapsed.TotalMilliseconds);
        _shownVersion = version;
        IsFullShown = full;
        double latency = _sinceChange.Elapsed.TotalMilliseconds;
        if (full) LastFullLatencyMs = latency;
        else LastPreviewLatencyMs = latency;
        Info = full
            ? $"{level.Width}×{level.Height} · {image.ComputeMs:0} ms"
            : $"Preview 1/{level.Factor} · {image.ComputeMs:0} ms";
        PreviewShown?.Invoke();
    }

    // ---- Result -----------------------------------------------------------------------------------------

    /// <summary>The refined selection at full resolution with the current settings, tools and brush strokes.</summary>
    public async Task<SelectionMask?> ResultAsync()
    {
        var level = await _full;
        var settings = Settings;
        var brush = _brush.Snapshot();
        var (selection, version) = (_base, _baseVersion);
        return await Task.Run(() => RefinerFor(level, selection, version).RefineSelection(settings, brush));
    }

    /// <summary>Waits until the full-resolution image of the current settings is on screen (for tests and benchmarks).</summary>
    public async Task WaitForFullAsync(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!IsFullShown && clock.Elapsed < timeout)
            await Task.Delay(10);
    }

    public void Dispose()
    {
        _disposed = true;
        _fullCancel?.Cancel();
        Dispatcher.UIThread.Post(() =>
        {
            Preview?.Dispose();
            Preview = null;
        });
    }
}
