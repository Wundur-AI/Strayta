using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Strayta.Text;

namespace Strayta.Editor.Views;

// The Type tool's window side: the Character and Paragraph panels in the Window menu, the system clipboard for text,
// the missing-font prompt, and keeping single-key shortcuts out of the way while typing.
public partial class MainWindow
{
    /// <summary>
    /// True while keys should type rather than act as shortcuts: a text field has focus, or type is being edited on
    /// the canvas.
    /// </summary>
    private bool IsTyping => FocusManager?.GetFocusedElement() is TextBox || Editor.IsEditingType;

    private void AddTypeMenus(NativeMenu menu, Func<string, ICommand, KeyGesture?, object?, NativeMenuItem> item)
    {
        var window = menu.Items.OfType<NativeMenuItem>().First(i => i.Header == "Window").Menu!;
        var history = window.Items.OfType<NativeMenuItem>().First(i => i.Header == "History");
        int at = window.Items.IndexOf(history);
        window.Items.Insert(at, item("Character", Editor.ShowPanelCommand, null, "Character"));
        window.Items.Insert(at + 1, item("Paragraph", Editor.ShowPanelCommand, null, "Paragraph"));

        Editor.SetClipboardText = async text =>
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        };
        Editor.GetClipboardText = async () => Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
        Editor.AskReplaceMissingFonts = AskReplaceMissingFontsAsync;
    }

    /// <summary>
    /// Photoshop's prompt when type with missing fonts is about to be edited: pick an installed font to replace them
    /// with, or cancel (the layer stays as it is, font names included).
    /// </summary>
    private async Task<string?> AskReplaceMissingFontsAsync(IReadOnlyList<string> missing)
    {
        var families = FontCatalog.System.Families;
        string fallback = FontCatalog.System.FallbackPostScriptName is { } f && FontCatalog.System.Find(f) is { } face ? face.FamilyName : families.FirstOrDefault() ?? "";
        var picker = new ComboBox { ItemsSource = families, SelectedItem = fallback, Width = 260, MaxDropDownHeight = 360 };
        var dialog = new Window
        {
            Title = "Missing Fonts",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        string? chosen = null;
        var replace = new Button { Content = "Replace", IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        replace.Click += (_, _) =>
        {
            if (picker.SelectedItem is string family)
            {
                var faces = FontCatalog.System.FacesOf(family);
                chosen = (faces.FirstOrDefault(x => x is { Weight: 400, Italic: false }) ?? faces.FirstOrDefault())?.PostScriptName;
            }
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"This type layer uses font{(missing.Count == 1 ? "" : "s")} that {(missing.Count == 1 ? "is" : "are")} not installed:\n"
                        + string.Join("\n", missing.Select(m => "  " + m)) + "\n\nReplace with an installed font to edit the text, or cancel to leave the layer as it is.",
                },
                new DockPanel { Children = { new TextBlock { Text = "Replace with", Width = 100, VerticalAlignment = VerticalAlignment.Center }, picker } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, replace } },
            },
        };
        await dialog.ShowDialog(this);
        return chosen;
    }
}
