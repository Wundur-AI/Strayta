using Avalonia.Controls;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

/// <summary>The unsaved-documents review shown when several edited documents close at once.</summary>
public partial class UnsavedDocumentsWindow : Window
{
    public UnsavedDocumentsWindow() => InitializeComponent();

    public UnsavedDocumentsWindow(UnsavedDocumentsViewModel review) : this()
    {
        DataContext = review;
        review.Decided += Close;
    }
}
