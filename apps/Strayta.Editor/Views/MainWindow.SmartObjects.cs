using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;

namespace Strayta.Editor.Views;

// Layer › Smart Objects, in Photoshop's place after Rasterize.
public partial class MainWindow
{
    private void AddSmartObjectMenus(NativeMenu menu, Func<string, ICommand, KeyGesture?, object?, NativeMenuItem> item)
    {
        var smart = new NativeMenu
        {
            item("Convert to Smart Object", Editor.ConvertToSmartObjectCommand, null, null),
            new NativeMenuItemSeparator(),
            item("New Smart Object via Copy", Editor.NewSmartObjectViaCopyCommand, null, null),
            item("Edit Contents", Editor.EditContentsCommand, null, null),
            item("Export Contents…", Editor.ExportContentsCommand, null, null),
            item("Replace Contents…", Editor.ReplaceContentsCommand, null, null),
            new NativeMenuItemSeparator(),
            item("Update Modified Content", Editor.UpdateModifiedContentCommand, null, null),
            item("Relink to File…", Editor.RelinkToFileCommand, null, null),
            new NativeMenuItemSeparator(),
            item("Rasterize", Editor.RasterizeLayerCommand, null, null),
        };
        var layer = menu.Items.OfType<NativeMenuItem>().First(i => i.Header == "Layer").Menu!;
        var rasterize = layer.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "Rasterize");
        int at = rasterize is null ? layer.Items.Count : layer.Items.IndexOf(rasterize) + 1;
        layer.Items.Insert(at, new NativeMenuItem("Smart Objects") { Menu = smart });
    }
}
