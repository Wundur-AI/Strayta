using Avalonia.Controls;

namespace Strayta.Editor.Views;

public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
        // Keep the current state in view as edits are added or the document steps back.
        States.SelectionChanged += (_, _) =>
        {
            if (States.SelectedItem is { } item) States.ScrollIntoView(item);
        };
    }
}
