using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Core.Text;
using Strayta.Editor.Editing;
using Strayta.Text;

namespace Strayta.Editor.ViewModels;

/// <summary>
/// The Type tool's edits (see <see cref="TypeSession"/>): a click starts point text, a drag paragraph text, a click on
/// existing type edits it. While typing the layer is redrawn per keystroke without history; committing (✓, ⌘Return,
/// Enter on the keypad, another tool, another layer, any other edit, saving) records one step, "Type" for new text and
/// "Edit Type Layer" for an existing layer, and cancelling (⊘, Esc) puts everything back. Edit › Undo inside the edit
/// steps back through the typing, as in Photoshop.
/// </summary>
public sealed partial class DocumentViewModel
{
    /// <summary>The type being edited on the canvas, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingType))]
    public partial TypeSession? TypeSession { get; private set; }

    public bool IsEditingType => TypeSession is not null;

    private bool _typeCommitting;

    /// <summary>Milliseconds from the last keystroke's change to its frame on screen (the preview lane).</summary>
    public double LastTypeFrameMs { get; private set; }

    private System.Diagnostics.Stopwatch? _typeFrameClock;

    /// <summary>
    /// New text at a click (<paramref name="box"/> null: point text anchored on its first baseline) or in a dragged box,
    /// in the options' current font, size, color and alignment. The layer is added above the selected one at once.
    /// </summary>
    public TypeSession BeginNewText(double x, double y, TextRect? box = null)
    {
        CommitType();
        var (style, paragraph, antiAlias) = Editor.Type.NewTextSettings(Model);
        var data = box is { } b
            ? TextLayerData.CreateParagraph("", style, b, paragraph)
            : TextLayerData.CreatePoint("", style, x, y, paragraph);
        data = data.WithLayout(antiAlias: antiAlias);

        var (parent, index) = InsertionPoint();
        var layer = new PixelLayer { Name = LayerFactory.NextName(Model, "Layer") };
        layer.Tags.Add("text");
        parent.Insert(index, layer); // outside history until the commit records it
        RebuildLayers();
        Select(layer);
        var session = new TypeSession(Model, layer, data, isNew: true, parent, index);
        StartType(session);
        return session;
    }

    /// <summary>
    /// Starts editing an existing type layer with the caret nearest to a document point (or at the end). A layer using
    /// fonts that are not installed asks first, as Photoshop does: Replace swaps them for a chosen font (part of the
    /// edit, so ⌘Z or Cancel brings them back); Cancel leaves the layer, and its font names, alone.
    /// </summary>
    public async Task<TypeSession?> BeginEditTextAsync(PixelLayer layer, double? x = null, double? y = null)
    {
        if (TypeSession is { } open && ReferenceEquals(open.Layer, layer)) return open;
        CommitType();
        if (TypeLayers.Read(layer) is not { } data) return null;
        var missing = data.FontsUsed.Where(f => f.Length > 0 && !FontCatalog.System.Contains(f)).ToList();
        string? replacement = null;
        if (missing.Count > 0)
        {
            replacement = Editor.AskReplaceMissingFonts is { } ask ? await ask(missing) : null;
            if (replacement is null)
            {
                Notice = $"Editing \"{layer.Name}\" needs {string.Join(", ", missing)}, which {(missing.Count == 1 ? "is" : "are")} not installed. Replace {(missing.Count == 1 ? "it" : "them")} to edit the text.";
                return null;
            }
        }
        if (layer.Parent is not { } parent) return null;
        Select(layer);
        var session = new TypeSession(Model, layer, data, isNew: false, parent, parent.IndexOf(layer));
        if (replacement is not null)
            session.Editor.Replace(data.ApplyStyle(s => missing.Contains(s.FontPostScriptName) ? s with { FontPostScriptName = replacement } : s));
        StartType(session);
        if (x is { } px && y is { } py)
        {
            var (tx, ty) = session.ToText(px, py);
            session.Editor.SetCaret(session.Editor.HitTest(tx, ty));
        }
        return session;
    }

    private void StartType(TypeSession session)
    {
        session.PixelsChanged += OnTypePixelsChanged;
        session.Changed += OnTypeChanged;
        TypeSession = session;
        Notice = "";
        OnTypeChanged();
    }

    private void StopType(TypeSession session)
    {
        session.PixelsChanged -= OnTypePixelsChanged;
        session.Changed -= OnTypeChanged;
        TypeSession = null;
    }

    private void OnTypePixelsChanged()
    {
        _typeFrameClock = System.Diagnostics.Stopwatch.StartNew();
        FrameDisplayed -= OnTypeFrame;
        FrameDisplayed += OnTypeFrame;
        RequestRender();
    }

    private void OnTypeFrame(bool full)
    {
        FrameDisplayed -= OnTypeFrame;
        if (_typeFrameClock is { } clock) LastTypeFrameMs = clock.Elapsed.TotalMilliseconds;
        _typeFrameClock = null;
    }

    private void OnTypeChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(RedoText));
        if (ReferenceEquals(Editor.ActiveDocument, this)) Editor.Type.Refresh();
    }

    /// <summary>
    /// Applies the edit as one history step. New text that stays empty leaves no layer, as in Photoshop; an existing
    /// layer whose text and formatting did not change leaves no step.
    /// </summary>
    public void CommitType()
    {
        if (TypeSession is not { } s || _typeCommitting) return;
        StopType(s);
        var data = s.Editor.Data;
        _typeCommitting = true;
        try
        {
            if (s.IsNew)
            {
                var parent = s.Layer.Parent ?? s.Parent;
                int index = s.Layer.Parent is { } p ? p.IndexOf(s.Layer) : s.Index;
                s.Layer.Parent?.Remove(s.Layer);
                if (data.Text.Length == 0)
                {
                    RebuildLayers();
                    RequestRender();
                    return;
                }
                // The final layer's data, pixels and name, on the same layer object so the panels keep their selection.
                var made = TypeLayers.Create(Model, data, out _);
                s.Layer.SourceData = made.SourceData;
                s.Layer.Pixels = made.Pixels;
                s.Layer.Bounds = made.Bounds;
                s.Layer.Name = made.Name;
                Apply(new InsertEdit(s.Layer, parent, index, "Type"));
                Select(s.Layer);
            }
            else
            {
                s.RestoreOriginal();
                if (!s.Editor.IsModified)
                {
                    RequestRender();
                    return;
                }
                EditText(s.Layer, data);
            }
            var style = data.Text.Length > 0 ? data.StyleAt(Math.Max(0, s.Editor.Caret - 1)) : data.StyleAt(0);
            Editor.Type.RememberFrom(style, data.ParagraphStyleAt(Math.Min(s.Editor.Caret, Math.Max(0, data.Text.Length - 1))), data.AntiAlias,
                TypeOptions.PointsPerUnit(data, Model));
        }
        finally
        {
            _typeCommitting = false;
            OnTypeChanged();
        }
    }

    /// <summary>Esc / ⊘: throws the edit away (a new layer disappears, an existing one keeps its old text and pixels).</summary>
    public void CancelType()
    {
        if (TypeSession is not { } s) return;
        StopType(s);
        if (s.IsNew)
        {
            s.Layer.Parent?.Remove(s.Layer);
            RebuildLayers();
        }
        else s.RestoreOriginal();
        RequestRender();
        OnTypeChanged();
    }

    /// <summary>
    /// The topmost visible type layer under a document point: within its text lines (a little margin around them) or,
    /// for paragraph text, inside its box.
    /// </summary>
    public PixelLayer? TypeLayerAt(double x, double y)
    {
        foreach (var node in Model.Root.Descendants().Reverse())
        {
            if (node is not PixelLayer layer || !layer.Tags.Contains("text") || !IsShown(layer)) continue;
            if (TypeLayers.Read(layer) is not { } data) continue;
            TextTransform inverse;
            try
            {
                inverse = data.Transform.Invert();
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            var (tx, ty) = inverse.Apply(x, y);
            if (data.Kind == TextKind.Paragraph)
            {
                var b = data.Box;
                if (tx >= b.Left && tx <= b.Right && ty >= b.Top && ty <= b.Bottom) return layer;
                continue;
            }
            // Point text: its laid-out lines. The stored pixel bounds rule most layers out without laying them out.
            var pb = layer.Bounds;
            double margin = Math.Max(8, pb.Height);
            if (!pb.IsEmpty && (x < pb.Left - margin || x > pb.Right + margin || y < pb.Top - margin || y > pb.Bottom + margin)) continue;
            var lines = TextLayout.Create(data).Bounds;
            double pad = Math.Max(2, (lines.Bottom - lines.Top) * 0.05);
            if (tx >= lines.Left - pad && tx <= lines.Right + pad && ty >= lines.Top - pad && ty <= lines.Bottom + pad) return layer;
        }
        return null;
    }

    private static bool IsShown(LayerNode node)
    {
        for (LayerNode? n = node; n is not null; n = n.Parent)
            if (!n.Visible) return false;
        return true;
    }

    /// <summary>Selecting another layer (or none) while typing commits the edit, as in Photoshop.</summary>
    private void OnTypeSelectionChanged(LayerItemViewModel? selected)
    {
        if (TypeSession is { } s && !ReferenceEquals(selected?.Node, s.Layer)) CommitType();
        if (ReferenceEquals(Editor.ActiveDocument, this)) Editor.Type.Refresh();
    }
}
