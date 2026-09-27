using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;

namespace Strayta.Editor.Views;

// Edit and Select menu items for selections, and their keyboard shortcuts.
public partial class MainWindow
{
    /// <summary>Adds Cut/Copy/Paste/Clear/Fill to the Edit menu and a Select menu after Layer.</summary>
    private void AddSelectionMenus(NativeMenu menu, Func<string, ICommand, KeyGesture?, object?, NativeMenuItem> item)
    {
        var cmd = Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        var top = menu.Items.OfType<NativeMenuItem>().ToList();

        // Delete and Backspace combinations are handled in OnSelectionKey rather than as menu gestures, so they work
        // the same with or without a global menu bar.
        var edit = top.First(i => i.Header == "Edit").Menu!;
        edit.Items.Add(new NativeMenuItemSeparator());
        edit.Items.Add(item("Cut", TextAware(Editor.CutCommand, t => t.Cut()), new KeyGesture(Key.X, cmd), null));
        edit.Items.Add(item("Copy", TextAware(Editor.CopyCommand, t => t.Copy()), new KeyGesture(Key.C, cmd), null));
        edit.Items.Add(item("Paste", TextAware(Editor.PasteCommand, t => t.Paste()), new KeyGesture(Key.V, cmd), null));
        edit.Items.Add(item("Clear", Editor.ClearCommand, null, null));
        edit.Items.Add(new NativeMenuItemSeparator());
        edit.Items.Add(item("Fill with Foreground Color", Editor.FillCommand, null, "foreground"));
        edit.Items.Add(item("Fill with Background Color", Editor.FillCommand, null, "background"));

        var select = new NativeMenu
        {
            item("All", TextAware(Editor.SelectAllCommand, t => t.SelectAll()), new KeyGesture(Key.A, cmd), null),
            item("Deselect", Editor.DeselectCommand, new KeyGesture(Key.D, cmd), null),
            item("Reselect", Editor.ReselectCommand, new KeyGesture(Key.D, cmd | KeyModifiers.Shift), null),
            item("Inverse", Editor.InvertSelectionCommand, new KeyGesture(Key.I, cmd | KeyModifiers.Shift), null),
        };
        int layer = menu.Items.IndexOf(top.First(i => i.Header == "Layer"));
        menu.Items.Insert(layer + 1, new NativeMenuItem("Select") { Menu = select });

        AddHandler(KeyDownEvent, OnSelectionKey, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    /// <summary>
    /// The global menu bar owns ⌘C/⌘V/⌘X/⌘A even while a text field has focus, so those go to the text field
    /// when one is focused (e.g. renaming a layer).
    /// </summary>
    private ICommand TextAware(ICommand command, Action<TextBox> text) => new RelayCommand<object?>(parameter =>
    {
        if (FocusManager?.GetFocusedElement() is TextBox box) text(box);
        else command.Execute(parameter);
    });

    /// <summary>
    /// Photoshop's keys: M marquee (Shift+M switches rectangle/ellipse), L lasso, Delete/Backspace clears the
    /// selection, Shift or Option+Backspace fills it with the foreground color, ⌘Backspace with the background.
    /// </summary>
    private void OnSelectionKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || FocusManager?.GetFocusedElement() is TextBox) return;
        var cmd = Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        var mods = e.KeyModifiers;
        bool delete = e.Key is Key.Back or Key.Delete;
        (ICommand Command, string? Parameter)? action =
            e.Key == Key.M && mods == KeyModifiers.None ? (Editor.SelectMarqueeCommand, null)
            : e.Key == Key.M && mods == KeyModifiers.Shift ? (Editor.SelectMarqueeCommand, "cycle")
            : e.Key == Key.L && mods == KeyModifiers.None ? (Editor.SetToolCommand, "Lasso")
            : delete && mods == KeyModifiers.None ? (Editor.ClearCommand, null)
            : delete && mods is KeyModifiers.Shift or KeyModifiers.Alt ? (Editor.FillCommand, "foreground")
            : delete && mods == cmd ? (Editor.FillCommand, "background")
            : null;
        if (action is { } a)
        {
            a.Command.Execute(a.Parameter);
            e.Handled = true;
        }
    }
}
