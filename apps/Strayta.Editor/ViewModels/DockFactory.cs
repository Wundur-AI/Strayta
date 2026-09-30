using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace Strayta.Editor.ViewModels;

/// <summary>Builds the window layout: documents as tabs in the center, the Layers panel docked on the right.</summary>
public sealed class DockFactory(EditorViewModel editor) : Factory
{
    private IDocumentDock? _documents;

    public override IRootDock CreateLayout()
    {
        var layers = new LayersToolViewModel(editor) { Id = "Layers", Title = "Layers", CanClose = false };

        _documents = new DocumentDock
        {
            Id = "Documents",
            Title = "Documents",
            IsCollapsable = false,
            CanCreateDocument = false,
            EmptyContent = "",
            Proportion = 0.78,
            VisibleDockables = CreateList<IDockable>(),
        };

        var color = new ColorToolViewModel(editor) { Id = "Color", Title = "Color", CanClose = false };
        var swatches = new SwatchesToolViewModel(editor) { Id = "Swatches", Title = "Swatches", CanClose = false };
        var properties = new PropertiesToolViewModel(editor) { Id = "Properties", Title = "Properties", CanClose = false };
        var history = new HistoryToolViewModel(editor) { Id = "History", Title = "History", CanClose = false };
        var cloneSource = new CloneSourceToolViewModel(editor) { Id = "CloneSource", Title = "Clone Source", CanClose = false }; // CloneSources.cs
        var character = new CharacterToolViewModel(editor) { Id = "Character", Title = "Character", CanClose = false }; // TypePanels.cs
        var paragraph = new ParagraphToolViewModel(editor) { Id = "Paragraph", Title = "Paragraph", CanClose = false };
        var info = new InfoToolViewModel(editor) { Id = "Info", Title = "Info", CanClose = false }; // InfoPanel.cs
        var brushes = new BrushesToolViewModel(editor) { Id = "Brushes", Title = "Brushes", CanClose = false }; // BrushPresetsPanel.cs
        var paths = new PathsToolViewModel(editor) { Id = "Paths", Title = "Paths", CanClose = false }; // PathsPanel.cs

        // Photoshop's default right column: Color/Swatches on top, Properties in the middle, Layers below.
        var tools = new ProportionalDock
        {
            Id = "RightColumn",
            Orientation = Orientation.Vertical,
            Proportion = 0.22,
            VisibleDockables = CreateList<IDockable>(
                new ToolDock { Id = "ColorDock", Alignment = Alignment.Right, Proportion = 0.25, ActiveDockable = color, VisibleDockables = CreateList<IDockable>(color, swatches, info, brushes) },
                new ProportionalDockSplitter(),
                new ToolDock { Id = "PropertiesDock", Alignment = Alignment.Right, Proportion = 0.3, ActiveDockable = properties, VisibleDockables = CreateList<IDockable>(properties, history, cloneSource, character, paragraph) },
                new ProportionalDockSplitter(),
                new ToolDock { Id = "LayersDock", Alignment = Alignment.Right, Proportion = 0.45, ActiveDockable = layers, VisibleDockables = CreateList<IDockable>(layers, paths) }),
        };

        var main = new ProportionalDock
        {
            Id = "Main",
            Orientation = Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(_documents, new ProportionalDockSplitter(), tools),
        };

        var root = CreateRootDock();
        root.Id = "Root";
        root.IsCollapsable = false;
        root.VisibleDockables = CreateList<IDockable>(main);
        root.ActiveDockable = main;
        root.DefaultDockable = main;
        return root;
    }

    /// <summary>
    /// Tells Dock how to create the window a torn-off document or panel lives in. Without this, dropping a
    /// panel outside the dock removes it from the layout with nowhere to go.
    /// </summary>
    public override void InitLayout(IDockable layout)
    {
        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => new HostWindow(),
        };
        DefaultHostWindowLocator = () => new HostWindow();
        base.InitLayout(layout);
    }

    public void AddDocument(DocumentViewModel document)
    {
        if (_documents is null) return;
        AddDockable(_documents, document);
        SetActiveDockable(document);
        SetFocusedDockable(_documents, document);
    }

    /// <summary>Documents still open anywhere in the layout, including floating windows.</summary>
    public IEnumerable<DocumentViewModel> OpenDocuments() =>
        Find(d => d is DocumentViewModel).OfType<DocumentViewModel>();
}
