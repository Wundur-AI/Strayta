using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Strayta.Editor.ViewModels;

namespace Strayta.Editor.Views;

public partial class MainWindow : Window, IEditorDialogs
{
    private bool _quitConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        Editor = new EditorViewModel(this);
        DataContext = Editor;
        Dock.Factory = Editor.Factory;
        Dock.Layout = Editor.Layout;
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.Layout)) Dock.Layout = Editor.Layout;
        };

        AddHandler(DragDrop.DropEvent, OnDrop);
        RegisterShortcuts();
        RegisterSpringLoadedTools(); // MainWindow.Zoom.cs

        // macOS draws the traffic lights on the left; Windows/Linux draw caption buttons on the right.
        if (!OperatingSystem.IsMacOS())
        {
            TitleLeft.Margin = new Thickness(10, 0, 0, 0);
            TitleRight.Margin = new Thickness(0, 0, 150, 0);
        }
        if (Environment.GetEnvironmentVariable("STRAYTA_FLOATTEST") == "1")
            Opened += async (_, _) =>
            {
                await Task.Delay(1500);
                var layers = Editor.Factory.Find(d => d.Id == "Layers").First();
                Editor.Factory.FloatDockable(layers);
                await Task.Delay(1500);
                var windows = (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Windows;
                Console.WriteLine($"FLOATTEST dock windows={Editor.Layout.Windows?.Count ?? 0} app windows={windows?.Count} " +
                                  $"visible={string.Join(",", windows?.Select(w => $"{w.GetType().Name}:{w.IsVisible}:{w.Bounds.Width}x{w.Bounds.Height}") ?? [])} " +
                                  $"layers in layout={Editor.Factory.Find(d => d.Id == "Layers").Any()}");
                Editor.ResetLayoutCommand.Execute(null);
                await Task.Delay(800);
                Console.WriteLine($"FLOATTEST after reset: dock windows={Editor.Layout.Windows?.Count ?? 0} app windows={windows?.Count} layers docked={Editor.Factory.Find(d => d.Id == "Layers").Any()}");
            };
        if (Environment.GetEnvironmentVariable("STRAYTA_SELFTEST") == "selection")
            Opened += async (_, _) => await SelfTest.RunSelectionGapsOnlyAsync(Editor); // SelfTest.SelectionGaps.cs
        if (Environment.GetEnvironmentVariable("STRAYTA_SELFTEST") == "1")
            Opened += async (_, _) => await SelfTest.RunAsync(Editor, AskNewDocumentAsync);
        if (Environment.GetEnvironmentVariable("STRAYTA_TRANSFORMBENCH") == "new")
            Opened += async (_, _) => await SelfTest.RunSyntheticBenchmarksAsync(Editor);
        if (Environment.GetEnvironmentVariable("STRAYTA_WANDBENCH") == "new")
            Opened += async (_, _) => await SelfTest.RunSyntheticWandBenchmarkAsync(Editor);
        if (Environment.GetEnvironmentVariable("STRAYTA_TOOLBENCH") == "new")
            Opened += async (_, _) => await SelfTest.RunSyntheticToolsBenchmarkAsync(Editor); // SelfTest.Everyday.cs
        if (Environment.GetEnvironmentVariable("STRAYTA_RETOUCHBENCH") == "new")
            Opened += async (_, _) => await SelfTest.RunSyntheticRetouchBenchmarkAsync(Editor); // SelfTest.Retouch.cs
    }

    public EditorViewModel Editor { get; }

    /// <summary>
    /// Builds the menu as a native menu: on macOS it appears in the system menu bar and owns the ⌘ shortcuts;
    /// elsewhere <see cref="NativeMenuBar"/> draws it in the window and key bindings provide Ctrl shortcuts.
    /// </summary>
    private void RegisterShortcuts()
    {
        var cmd = Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        bool globalMenu = OperatingSystem.IsMacOS();

        NativeMenuItem Item(string header, System.Windows.Input.ICommand command, KeyGesture? gesture = null, object? parameter = null)
        {
            var item = new NativeMenuItem(header) { Command = command, Gesture = gesture };
            if (parameter is not null) item.CommandParameter = parameter;
            if (gesture is not null && !globalMenu)
            {
                var binding = new KeyBinding { Gesture = gesture, Command = command };
                if (parameter is not null) binding.CommandParameter = parameter;
                KeyBindings.Add(binding);
            }
            return item;
        }

        NativeMenuItem Submenu(string header, params NativeMenuItemBase[] items)
        {
            var menu = new NativeMenu();
            foreach (var i in items) menu.Items.Add(i);
            return new NativeMenuItem(header) { Menu = menu };
        }

        var undo = Item("Undo", Editor.UndoCommand, new KeyGesture(Key.Z, cmd));
        var redo = Item("Redo", Editor.RedoCommand, new KeyGesture(Key.Z, cmd | KeyModifiers.Shift));
        // Keep "Undo Move" / "Redo Change Opacity" labels current.
        Editor.PropertyChanged += (_, _) => RefreshUndoLabels();
        void RefreshUndoLabels()
        {
            undo.Header = Editor.ActiveDocument?.UndoText ?? "Undo";
            redo.Header = Editor.ActiveDocument?.RedoText ?? "Redo";
            undo.IsEnabled = Editor.ActiveDocument?.CanUndo == true;
            redo.IsEnabled = Editor.ActiveDocument?.CanRedo == true;
        }
        Editor.DocumentChanged += RefreshUndoLabels;
        RefreshUndoLabels();

        var menu = new NativeMenu
        {
            Submenu("File",
                Item("New…", Editor.NewDocumentCommand, new KeyGesture(Key.N, cmd)),
                Item("Open…", Editor.OpenCommand, new KeyGesture(Key.O, cmd)),
                new NativeMenuItemSeparator(),
                Item("Save", Editor.SaveCommand, new KeyGesture(Key.S, cmd)),
                Item("Save As…", Editor.SaveAsCommand, new KeyGesture(Key.S, cmd | KeyModifiers.Shift)),
                Item("Export As…", Editor.ExportAsCommand, new KeyGesture(Key.W, cmd | KeyModifiers.Shift | KeyModifiers.Alt)),
                new NativeMenuItemSeparator(),
                Item("Close", Editor.CloseCommand, new KeyGesture(Key.W, cmd))),
            Submenu("Edit", undo, redo,
                new NativeMenuItemSeparator(),
                Item("Free Transform", Editor.FreeTransformCommand, new KeyGesture(Key.T, cmd))),
            Submenu("Layer",
                Item("New Layer", Editor.NewLayerCommand, new KeyGesture(Key.N, cmd | KeyModifiers.Shift)),
                Item("New Group", Editor.NewGroupCommand, new KeyGesture(Key.G, cmd)),
                Item("Duplicate Layer", Editor.DuplicateLayerCommand),
                Item("Layer via Copy", Editor.LayerViaCommand, new KeyGesture(Key.J, cmd), parameter: "copy"),
                Item("Layer via Cut", Editor.LayerViaCommand, new KeyGesture(Key.J, cmd | KeyModifiers.Shift), parameter: "cut"),
                Item("Rasterize", Editor.RasterizeLayerCommand),
                new NativeMenuItemSeparator(),
                Submenu("New Adjustment Layer", Editor.AdjustmentKinds.Select(k => (NativeMenuItemBase)Item(k + "…", Editor.NewAdjustmentCommand, parameter: k)).ToArray()),
                Submenu("Layer Mask",
                    Item("Reveal All", Editor.AddMaskCommand, parameter: "reveal"),
                    Item("Hide All", Editor.AddMaskCommand, parameter: "hide"),
                    new NativeMenuItemSeparator(),
                    Item("Delete", Editor.DeleteMaskCommand),
                    Item("Apply", Editor.ApplyMaskCommand),
                    new NativeMenuItemSeparator(),
                    Item("Disable / Enable", Editor.ToggleMaskCommand)),
                new NativeMenuItemSeparator(),
                Item("Move Up", Editor.LayerUpCommand, new KeyGesture(Key.OemCloseBrackets, cmd)),
                Item("Move Down", Editor.LayerDownCommand, new KeyGesture(Key.OemOpenBrackets, cmd)),
                new NativeMenuItemSeparator(),
                Item("Delete", Editor.DeleteLayerCommand)),
            Submenu("View",
                Item("Strayta Render", Editor.SetViewCommand, parameter: "Strayta"),
                Item("Photoshop Composite", Editor.SetViewCommand, parameter: "Photoshop"),
                Item("Difference", Editor.SetViewCommand, parameter: "Difference"),
                new NativeMenuItemSeparator(),
                Item("Fit on Screen", Editor.FitCommand, new KeyGesture(Key.D0, cmd)),
                Item("Actual Size", Editor.ActualSizeCommand, new KeyGesture(Key.D1, cmd)),
                Item("Zoom In", Editor.ZoomInCommand, new KeyGesture(Key.OemPlus, cmd)),
                Item("Zoom Out", Editor.ZoomOutCommand, new KeyGesture(Key.OemMinus, cmd)),
                new NativeMenuItemSeparator(),
                Submenu("Appearance",
                    Item("Dark", Editor.SetAppearanceCommand, parameter: "Dark"),
                    Item("Light", Editor.SetAppearanceCommand, parameter: "Light"),
                    Item("Match System", Editor.SetAppearanceCommand, parameter: "System"))),
            Submenu("Window",
                Item("Layers", Editor.ShowPanelCommand, parameter: "Layers"),
                Item("Color", Editor.ShowPanelCommand, parameter: "Color"),
                Item("Swatches", Editor.ShowPanelCommand, parameter: "Swatches"),
                Item("Properties", Editor.ShowPanelCommand, parameter: "Properties"),
                Item("History", Editor.ShowPanelCommand, parameter: "History"),
                new NativeMenuItemSeparator(),
                Item("Reset Layout", Editor.ResetLayoutCommand)),
        };
        AddImageMenu(menu, Item); // MainWindow.Crop.cs
        AddLayerStyleMenus(menu, Item); // MainWindow.LayerStyle.cs
        AddSelectionMenus(menu, Item);
        AddObjectSelectionMenus(menu, Item); // MainWindow.ObjectSelection.cs
        AddRefineSelectionMenus(menu, Item); // MainWindow.RefineSelection.cs
        AddFilterMenu(menu, Item); // MainWindow.Filters.cs
        NativeMenu.SetMenu(this, menu);

        // Enter, Esc and arrow keys drive an open Free Transform before any other single-key shortcut.
        AddHandler(KeyDownEvent, OnTransformKey, Avalonia.Interactivity.RoutingStrategies.Bubble);
        // Single-key shortcuts (tools, brush size) must not fire while typing, so they are handled by hand.
        AddHandler(KeyDownEvent, OnSingleKey, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    /// <summary>Empty title bar areas move the window; buttons handle their own clicks first.</summary>
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) is not null) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1) BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) is not null) return;
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnSingleKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || FocusManager?.GetFocusedElement() is TextBox) return;
        // Tool keys (EditorViewModel.ToolGroups): the key picks the slot's tool, Shift plus the key steps through its group.
        if (e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift && e.Key is >= Key.A and <= Key.Z
            && Editor.HandleToolKey(e.Key.ToString(), e.KeyModifiers == KeyModifiers.Shift))
        {
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers != KeyModifiers.None) return;
        (System.Windows.Input.ICommand Command, string Parameter)? action = e.Key switch
        {
            Key.X => (Editor.SwapColorsCommand, ""),
            Key.D => (Editor.DefaultColorsCommand, ""),
            Key.OemCloseBrackets => (Editor.ResizeBrushCommand, "up"),
            Key.OemOpenBrackets => (Editor.ResizeBrushCommand, "down"),
            _ => null,
        };
        if (action is { } a)
        {
            a.Command.Execute(a.Parameter);
            e.Handled = true;
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        foreach (var file in e.DataTransfer.TryGetFiles() ?? [])
            if (file.TryGetLocalPath() is { } path) await Editor.OpenAsync(path);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_quitConfirmed) return;
        e.Cancel = true;
        if (await Editor.ConfirmQuitAsync())
        {
            _quitConfirmed = true;
            Close();
        }
    }

    // ---- IEditorDialogs ----------------------------------------------------------------------------

    private static readonly FilePickerFileType PsdFiles = new("Photoshop documents") { Patterns = ["*.psd", "*.psb"] };

    private static readonly FilePickerFileType OpenableFiles = new("Images and Photoshop documents")
    {
        Patterns = ["*.psd", "*.psb", "*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp", "*.ico", "*.heic", "*.heif"],
    };

    public async Task<string?> PickFileToOpenAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open",
            AllowMultiple = false,
            FileTypeFilter = [OpenableFiles, PsdFiles, FilePickerFileTypes.All],
        });
        return files is [var f] ? f.TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileToSaveAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save As",
            SuggestedFileName = suggestedName,
            DefaultExtension = Path.GetExtension(suggestedName).TrimStart('.') is { Length: > 0 } ext ? ext : "psd",
            // The document's own format first; PNG/JPEG only hold a single flat layer (layered documents get a flattened copy).
            FileTypeChoices = Path.GetExtension(suggestedName).ToLowerInvariant() switch
            {
                ".png" => [PngFiles, JpegFiles, PsdFiles],
                ".jpg" or ".jpeg" or ".jpe" => [JpegFiles, PngFiles, PsdFiles],
                _ => [PsdFiles, PngFiles, JpegFiles],
            },
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string> AskSaveChangesAsync(string documentName) =>
        await ShowChoiceAsync("Unsaved changes", $"Save changes to \"{documentName}\" before closing?",
            ("Save", "save"), ("Don't Save", "discard"), ("Cancel", "cancel")) ?? "cancel";

    public async Task<(int Width, int Height, bool White)?> AskNewDocumentAsync()
    {
        var width = new NumericUpDown { Value = 1920, Minimum = 1, Maximum = 30000, Increment = 1, FormatString = "0", Width = 130 };
        var height = new NumericUpDown { Value = 1080, Minimum = 1, Maximum = 30000, Increment = 1, FormatString = "0", Width = 130 };
        var background = new ComboBox { ItemsSource = new[] { "White", "Transparent" }, SelectedIndex = 0, Width = 130 };
        var dialog = new Window
        {
            Title = "New Document",
            Width = 340,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        bool ok = false;
        var create = new Button { Content = "Create", IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        create.Click += (_, _) => { ok = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();

        Control Row(string label, Control input) => new DockPanel
        {
            Children = { new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center }, input },
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                Row("Width (px)", width),
                Row("Height (px)", height),
                Row("Background", background),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { cancel, create } },
            },
        };
        await dialog.ShowDialog(this);
        return ok ? ((int)(width.Value ?? 1920), (int)(height.Value ?? 1080), background.SelectedIndex == 0) : null;
    }

    public async Task ShowErrorAsync(string title, string message) =>
        await ShowChoiceAsync(title, message, ("OK", "ok"));

    private async Task<string?> ShowChoiceAsync(string title, string message, params (string Label, string Value)[] choices)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, value) in choices)
        {
            var b = new Button { Content = label, MinWidth = 90 };
            b.Click += (_, _) => dialog.Close(value);
            buttons.Children.Add(b);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 20,
            Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, buttons },
        };
        return await dialog.ShowDialog<string?>(this);
    }
}
