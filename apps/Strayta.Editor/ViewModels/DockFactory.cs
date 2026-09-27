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

        // Photoshop's default right column: Color/Swatches on top, Layers below.
        var tools = new ProportionalDock
        {
            Id = "RightColumn",
            Orientation = Orientation.Vertical,
            Proportion = 0.22,
            VisibleDockables = CreateList<IDockable>(
                new ToolDock { Id = "ColorDock", Alignment = Alignment.Right, Proportion = 0.3, ActiveDockable = color, VisibleDockables = CreateList<IDockable>(color, swatches) },
                new ProportionalDockSplitter(),
                new ToolDock { Id = "LayersDock", Alignment = Alignment.Right, Proportion = 0.7, ActiveDockable = layers, VisibleDockables = CreateList<IDockable>(layers) }),
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
