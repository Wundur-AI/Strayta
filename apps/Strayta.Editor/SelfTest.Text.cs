using Strayta.Core;
using Strayta.Core.Text;
using Strayta.Editor.Editing;
using Strayta.Editor.ViewModels;
using Strayta.Psd;
using Strayta.Psd.Text;
using Strayta.Text;

namespace Strayta.Editor;

/// <summary>
/// Self-test steps for the text engine: a new type layer, a word changed through the API (re-rendered, renamed,
/// undoable), save and reopen with the text intact, and the missing-font notice.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>STRAYTA_SELFTEST=text: only the text engine steps.</summary>
    public static async Task RunTextOnlyAsync(EditorViewModel editor)
    {
        int failures = 0;
        await RunTextStepsAsync(editor, (ok, what) =>
        {
            Console.WriteLine($"SELFTEST {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) failures++;
        });
        Console.WriteLine(failures == 0 ? "SELFTEST PASSED" : $"SELFTEST FAILED ({failures})");
    }

    private static async Task RunTextStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        try
        {
            var font = await Task.Run(() => FontCatalog.System.FallbackPostScriptName);
            if (font is null)
            {
                check(true, "type: no fonts installed, text steps skipped");
                return;
            }
            var model = LayerFactory.NewDocument(500, 200, whiteBackground: true);
            var doc = new DocumentViewModel(model, null, editor);
            editor.Factory.AddDocument(doc);
            editor.ActiveDocument = doc;
            await doc.RenderAsync();

            var style = new TextStyle { FontPostScriptName = font, FontSize = 48, FillColor = new TextColor(0.8, 0.1, 0.1) };
            var layer = doc.AddTextLayer(TextLayerData.CreatePoint("Hello world", style, 20, 120));
            check(layer.Tags.Contains("text") && layer.Pixels is not null && layer.Name == "Hello world" && layer.Bounds.Left >= 20 && layer.Bounds.Bottom <= 135,
                $"a new type layer is drawn with {font} ({layer.Bounds})");
            int center = (layer.Bounds.Height / 2) * layer.Bounds.Width;
            check(layer.Pixels!.Alpha!.Data.Any(a => a == 255) && layer.Pixels.ColorPlanes[0].Data[center] == 204, "its pixels are the fill color with coverage as transparency");

            var data = TypeLayers.Read(layer)!;
            int word = data.Text.IndexOf("world", StringComparison.Ordinal);
            var bounds = layer.Bounds;
            doc.EditText(layer, data.ReplaceText(word, 5, "Strayta!"));
            var edited = TypeLayers.Read(layer)!;
            check(edited.Text == "Hello Strayta!" && layer.Name == "Hello Strayta!" && layer.Bounds.Width > bounds.Width && doc.UndoText == "Undo Edit Type Layer",
                $"changing a word re-renders and renames the layer ({bounds.Width} -> {layer.Bounds.Width} px wide)");
            doc.Undo();
            check(TypeLayers.Read(layer)!.Text == "Hello world" && layer.Bounds == bounds && layer.Name == "Hello world", "undo restores the text, pixels and name");
            doc.Redo();

            var layout = TextLayout.Create(TypeLayers.Read(layer)!);
            int hit = layout.HitTestDocument(layout.GetCaretDocument(6).Top.X + 0.5, 110);
            check(hit == 6 && layout.Lines.Count == 1, $"hit testing finds the caret between the words (index {hit})");

            string path = Path.Combine(Path.GetTempPath(), $"strayta-text-selftest-{Guid.NewGuid():N}.psd");
            await doc.SaveAsync(path);
            var reopened = PsdFile.OpenForEditing(path);
            var saved = reopened.Root.Descendants().OfType<PixelLayer>().FirstOrDefault(l => l.Tags.Contains("text"));
            var savedText = saved is null ? null : TypeLayers.Read(saved);
            check(savedText?.Text == "Hello Strayta!" && savedText.StyleAt(0).FontPostScriptName == font && saved!.Name == "Hello Strayta!"
                  && saved.Pixels!.Alpha!.Data.SequenceEqual(layer.Pixels!.Alpha!.Data),
                "saved and reopened, the type layer keeps its text, font and pixels");

            // Edit the reopened file's layer again: the file's own data is kept and re-encoded.
            var again = TypeLayers.Edit(reopened, saved!, savedText!.WithText("Reopened"), out _);
            again.Do();
            check(TypeLayers.Read(saved!)!.Text == "Reopened" && PsdTypeLayer.IsRegenerated(((PsdLayerRecord)saved!.SourceData!).FindBlock("TySh")!.Data),
                "a reopened type layer can be edited again");
            File.Delete(path);

            // A font that is not installed: drawn with a substitute, reported, and kept in the file.
            var missing = doc.EditText(layer, TypeLayers.Read(layer)!.ApplyStyle(s => s with { FontPostScriptName = "StraytaMissingFont-Regular" }));
            check(missing.Contains("StraytaMissingFont-Regular") && doc.Notice.Contains("StraytaMissingFont-Regular") && layer.Pixels is not null
                  && TypeLayers.Read(layer)!.StyleAt(0).FontPostScriptName == "StraytaMissingFont-Regular",
                "a missing font is substituted, named in the notice and kept in the layer");
            await doc.CheckFontsAsync();
            check(doc.MissingFonts.SequenceEqual(["StraytaMissingFont-Regular"]), "the document lists its missing fonts");
        }
        catch (Exception ex)
        {
            check(false, $"exception in text steps: {ex}");
        }
    }
}
