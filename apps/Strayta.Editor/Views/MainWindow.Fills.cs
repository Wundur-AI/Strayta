using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Strayta.Core.Painting;
using Strayta.Editor.Controls;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

// Edit › Fill…, Stroke… and Define Pattern…, and their dialogs.
public partial class MainWindow : IFillDialogs
{
    /// <summary>Adds Fill… (⇧F5), Stroke… and Define Pattern… to the Edit menu, after the quick fills.</summary>
    private void AddFillMenus(NativeMenu menu, Func<string, ICommand, KeyGesture?, object?, NativeMenuItem> item)
    {
        var edit = menu.Items.OfType<NativeMenuItem>().First(i => i.Header == "Edit").Menu!;
        var quickFill = edit.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "Fill with Foreground Color");
        int at = quickFill is null ? edit.Items.Count : edit.Items.IndexOf(quickFill);
        edit.Items.Insert(at, item("Fill…", Editor.FillDialogCommand, new KeyGesture(Key.F5, KeyModifiers.Shift), null));
        edit.Items.Insert(at + 1, item("Stroke…", Editor.StrokeDialogCommand, null, null));
        int after = edit.Items.IndexOf(edit.Items.OfType<NativeMenuItem>().Last(i => i.Header?.StartsWith("Fill with") == true)) + 1;
        edit.Items.Insert(after, new NativeMenuItemSeparator());
        edit.Items.Insert(after + 1, item("Define Pattern…", Editor.DefinePatternCommand, null, null));
    }

    // ---- IFillDialogs ------------------------------------------------------------------------------------

    public async Task<bool> AskFillAsync(FillDialogViewModel fill) => await new FillWindow(fill).ShowDialog<bool>(this);

    public async Task<bool> AskStrokeAsync(StrokeDialogViewModel stroke) => await new StrokeWindow(stroke).ShowDialog<bool>(this);

    public async Task<string?> AskPatternNameAsync(string suggested, Pattern preview)
    {
        var dialog = new Window
        {
            Title = "Pattern Name",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var name = new TextBox { Text = suggested, MinWidth = 200 };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        string? result = null;
        ok.Click += (_, _) => { result = name.Text ?? suggested; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children =
                    {
                        new Border { BorderThickness = new Thickness(1), BorderBrush = Avalonia.Media.Brushes.Gray, Child = new Image { Width = 56, Height = 56, Source = GradientImages.Thumbnail(preview, 56) } },
                        new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = "Name:" }, name } },
                    },
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
        dialog.Opened += (_, _) =>
        {
            name.Focus();
            name.SelectAll();
        };
        await dialog.ShowDialog(this);
        return result;
    }
}
