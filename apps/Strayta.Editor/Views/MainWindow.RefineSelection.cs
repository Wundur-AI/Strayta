using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

// Select › Select and Mask and Select › Modify, Layer › Layer Mask › Reveal/Hide Selection, and their dialogs.
public partial class MainWindow : ISelectionDialogs
{
    /// <summary>
    /// Adds Select and Mask… (⌥⌘R) and the Modify submenu after Subject, in Photoshop's order, and Reveal Selection /
    /// Hide Selection to Layer › Layer Mask after Hide All.
    /// </summary>
    private void AddRefineSelectionMenus(NativeMenu menu, Func<string, ICommand, KeyGesture?, object?, NativeMenuItem> item)
    {
        var cmd = Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        var top = menu.Items.OfType<NativeMenuItem>().ToList();

        var select = top.First(i => i.Header == "Select").Menu!;
        select.Items.Add(new NativeMenuItemSeparator());
        select.Items.Add(item("Select and Mask…", Editor.SelectAndMaskCommand, new KeyGesture(Key.R, cmd | KeyModifiers.Alt), null));
        var modify = new NativeMenu
        {
            item("Border…", Editor.ModifySelectionCommand, null, nameof(SelectionModification.Border)),
            item("Smooth…", Editor.ModifySelectionCommand, null, nameof(SelectionModification.Smooth)),
            item("Expand…", Editor.ModifySelectionCommand, null, nameof(SelectionModification.Expand)),
            item("Contract…", Editor.ModifySelectionCommand, null, nameof(SelectionModification.Contract)),
            item("Feather…", Editor.ModifySelectionCommand, new KeyGesture(Key.F6, KeyModifiers.Shift), nameof(SelectionModification.Feather)),
        };
        select.Items.Add(new NativeMenuItem("Modify") { Menu = modify });

        var layerMask = top.First(i => i.Header == "Layer").Menu!.Items.OfType<NativeMenuItem>().First(i => i.Header == "Layer Mask").Menu!;
        int hideAll = layerMask.Items.IndexOf(layerMask.Items.OfType<NativeMenuItem>().First(i => i.Header == "Hide All"));
        layerMask.Items.Insert(hideAll + 1, item("Reveal Selection", Editor.AddMaskFromSelectionCommand, null, "reveal"));
        layerMask.Items.Insert(hideAll + 2, item("Hide Selection", Editor.AddMaskFromSelectionCommand, null, "hide"));
    }

    // ---- ISelectionDialogs ---------------------------------------------------------------------------------

    public async Task<(double Amount, bool ApplyAtCanvasBounds)?> AskModifySelectionAsync(SelectionModification kind, double amount, bool applyAtCanvasBounds)
    {
        // Photoshop's titles, labels and ranges.
        var (title, label, min, max) = kind switch
        {
            SelectionModification.Expand => ("Expand Selection", "Expand By", 1.0, (double)Core.Selection.SelectionModify.MaxRadius),
            SelectionModification.Contract => ("Contract Selection", "Contract By", 1.0, (double)Core.Selection.SelectionModify.MaxRadius),
            SelectionModification.Feather => ("Feather Selection", "Feather Radius", 0.1, (double)Core.Selection.SelectionModify.MaxFeather),
            SelectionModification.Smooth => ("Smooth Selection", "Sample Radius", 1.0, (double)Core.Selection.SelectionModify.MaxRadius),
            _ => ("Border Selection", "Width", 1.0, (double)Core.Selection.SelectionModify.MaxBorder),
        };
        bool fractional = kind == SelectionModification.Feather;
        var value = new NumericUpDown
        {
            Value = (decimal)Math.Clamp(amount, min, max), Minimum = (decimal)min, Maximum = (decimal)max,
            Increment = fractional ? 0.1m : 1, FormatString = fractional ? "0.0" : "0", Width = 110,
        };
        // Expanding never reads beyond the canvas, so Photoshop's option does nothing there.
        var bounds = new CheckBox { Content = "Apply effect at canvas bounds", IsChecked = applyAtCanvasBounds, IsVisible = kind != SelectionModification.Expand };
        var dialog = new Window
        {
            Title = title,
            Width = 320,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        bool ok = false;
        var okButton = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        okButton.Click += (_, _) => { ok = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = label + ":", Width = 100, VerticalAlignment = VerticalAlignment.Center },
                        value,
                        new TextBlock { Text = "pixels", VerticalAlignment = VerticalAlignment.Center },
                    },
                },
                bounds,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), Children = { cancel, okButton } },
            },
        };
        dialog.Opened += (_, _) => value.Focus();
        await dialog.ShowDialog(this);
        if (!ok) return null;
        double chosen = (double)(value.Value ?? (decimal)amount);
        return (fractional ? Math.Round(chosen, 1) : Math.Round(chosen), bounds.IsChecked == true);
    }

    public Task<bool> RunSelectAndMaskAsync(SelectAndMaskViewModel session) =>
        ShowSelectAndMaskWorkspaceAsync(session); // MainWindow.SelectAndMask.cs
}
