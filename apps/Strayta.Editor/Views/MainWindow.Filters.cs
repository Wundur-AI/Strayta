using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

// The Filter menu and its dialogs.
public partial class MainWindow : IFilterDialogs
{
    /// <summary>
    /// Adds the Filter menu in Photoshop's place, before View: the last filter first (⌃⌘F; Ctrl+Alt+F elsewhere), then
    /// Blur, Noise, Other and Sharpen in Photoshop's alphabetical order.
    /// </summary>
    private void AddFilterMenu(NativeMenu menu, Func<string, ICommand, KeyGesture?, object?, NativeMenuItem> item)
    {
        var cmd = Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        var last = item(Editor.LastFilterName, Editor.ApplyLastFilterCommand,
            new KeyGesture(Key.F, cmd | (OperatingSystem.IsMacOS() ? KeyModifiers.Control : KeyModifiers.Alt)), null);
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.LastFilterName)) last.Header = Editor.LastFilterName;
        };
        NativeMenuItem Submenu(string header, params NativeMenuItemBase[] items)
        {
            var sub = new NativeMenu();
            foreach (var i in items) sub.Items.Add(i);
            return new NativeMenuItem(header) { Menu = sub };
        }
        NativeMenuItem Filter(string header, FilterKind kind) => item(header, Editor.FilterCommand, null, kind.ToString());

        var filter = new NativeMenu
        {
            last,
            new NativeMenuItemSeparator(),
            Submenu("Blur",
                Filter("Box Blur…", FilterKind.BoxBlur),
                Filter("Gaussian Blur…", FilterKind.GaussianBlur),
                Filter("Motion Blur…", FilterKind.MotionBlur)),
            Submenu("Noise",
                Filter("Add Noise…", FilterKind.AddNoise)),
            Submenu("Other",
                Filter("High Pass…", FilterKind.HighPass)),
            Submenu("Sharpen",
                Filter("Unsharp Mask…", FilterKind.UnsharpMask)),
        };
        var view = menu.Items.OfType<NativeMenuItem>().First(i => i.Header == "View");
        menu.Items.Insert(menu.Items.IndexOf(view), new NativeMenuItem("Filter") { Menu = filter });

        if (Environment.GetEnvironmentVariable("STRAYTA_FILTERBENCH") == "new")
            Opened += async (_, _) => await SelfTest.RunFilterBenchmarkAsync(Editor); // SelfTest.Filters.cs
    }

    // ---- IFilterDialogs ----------------------------------------------------------------------------------

    public async Task<bool> RunFilterAsync(FilterSessionViewModel session) =>
        await new FilterWindow(session).ShowDialog<bool>(this);

    public async Task<bool> AskRasterizeAsync(string message) =>
        await ShowChoiceAsync("Rasterize", message, ("Cancel", "cancel"), ("OK", "ok")) == "ok";
}
