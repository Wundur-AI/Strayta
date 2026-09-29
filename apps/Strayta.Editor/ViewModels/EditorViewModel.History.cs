using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Controls;
using Dock.Model.Core;
using Strayta.Editor.Controls;

namespace Strayta.Editor.ViewModels;

// History: the History States preference, the History Brush tool (Y), and cleaning up panels on Window › Reset Layout.
public sealed partial class EditorViewModel
{
    public const int DefaultHistoryStates = 50;

    /// <summary>
    /// Photoshop's History States preference: how many steps each document keeps (1–1000, 50 by default); the oldest
    /// step is dropped when a new one would exceed it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryStatesField))]
    public partial int HistoryStates { get; set; } = DefaultHistoryStates;

    /// <summary>The History panel's number field for <see cref="HistoryStates"/>.</summary>
    public double HistoryStatesField { get => HistoryStates; set => HistoryStates = (int)Math.Round(value); }

    partial void OnHistoryStatesChanged(int value)
    {
        int clamped = Math.Clamp(value, 1, 1000);
        if (clamped != value)
        {
            HistoryStates = clamped;
            return;
        }
        foreach (var doc in Factory.OpenDocuments()) doc.SetHistoryLimit(clamped);
    }

    public bool IsHistoryBrushTool { get => Tool == CanvasTool.HistoryBrush; set { if (value) Tool = CanvasTool.HistoryBrush; } }

    /// <summary>
    /// Window › Reset Layout replaces every panel: the old ones must stop following the editor and its documents, or
    /// they keep updating (and stay alive) behind the new layout.
    /// </summary>
    private static void DetachPanels(IDockable? root)
    {
        foreach (var dockable in AllDockables(root))
            if (dockable is Dock.Model.Mvvm.Controls.Tool and IDisposable panel) panel.Dispose(); // panels only, never documents
    }

    private static IEnumerable<IDockable> AllDockables(IDockable? node)
    {
        if (node is null) yield break;
        yield return node;
        if (node is IDock dock)
            foreach (var child in dock.VisibleDockables?.ToList() ?? [])
                foreach (var d in AllDockables(child)) yield return d;
        if (node is IRootDock root)
        {
            foreach (var hidden in root.HiddenDockables?.ToList() ?? [])
                foreach (var d in AllDockables(hidden)) yield return d;
            foreach (var window in root.Windows?.ToList() ?? [])
                foreach (var d in AllDockables(window.Layout)) yield return d;
        }
    }
}
