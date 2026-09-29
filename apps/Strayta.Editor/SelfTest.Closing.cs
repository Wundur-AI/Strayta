using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Strayta.Editor.ViewModels;
using Strayta.Editor.Views;

namespace Strayta.Editor;

internal static partial class SelfTest
{
    /// <summary>Closing several edited documents: one review for all of them, Cancel, save some, discard the rest.</summary>
    private static async Task RunClosingStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        foreach (var open in editor.Factory.OpenDocuments().ToList()) open.CloseWithoutAsking(); // start from nothing open
        var saved = editor.ReviewUnsaved;
        var paths = new List<string>();
        try
        {
            // Three saved documents, each then edited (saved ones, so saving them asks nothing).
            var docs = new List<DocumentViewModel>();
            for (int i = 0; i < 3; i++)
            {
                var doc = new DocumentViewModel(Editing.LayerFactory.NewDocument(120, 80, whiteBackground: true), null, editor);
                editor.Factory.AddDocument(doc);
                editor.ActiveDocument = doc;
                await doc.RenderAsync();
                string path = Path.Combine(Path.GetTempPath(), $"strayta-closing-{i}-{Guid.NewGuid():N}.psd");
                paths.Add(path);
                await doc.SaveAsync(path);
                doc.NewLayer();
                docs.Add(doc);
            }
            check(docs.All(d => d.IsModified), "three documents with unsaved changes");

            // Cancel: nothing is saved or closed.
            UnsavedDocumentsViewModel? shown = null;
            editor.ReviewUnsaved = review =>
            {
                shown = review;
                review.Selected = review.Items[0]; // clicking a row shows that document
                review.CancelCommand.Execute(null);
                return Task.CompletedTask;
            };
            await editor.CloseAllCommand.ExecuteAsync(null);
            check(shown is { Items.Count: 3, SaveText: "Save All" } && editor.ActiveDocument == docs[0]
                  && docs.All(d => d.IsModified) && editor.Factory.OpenDocuments().Count() == 3,
                "Close All shows one review of all three (clicking one shows it); Cancel keeps them open and unsaved");

            // The window itself, for a look (STRAYTA_SELFTEST_SHOTS).
            if (Environment.GetEnvironmentVariable("STRAYTA_SELFTEST_SHOTS") is { Length: > 0 } dir
                && Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            {
                var review = new UnsavedDocumentsViewModel(docs, _ => { });
                review.Items[1].Save = false;
                var window = new UnsavedDocumentsWindow(review);
                window.Show(main);
                await Task.Delay(300);
                var size = new Avalonia.PixelSize((int)window.Bounds.Width * 2, (int)window.Bounds.Height * 2);
                using var bitmap = new RenderTargetBitmap(size, new Avalonia.Vector(192, 192));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(dir, "unsaved-documents.png"), new PngBitmapEncoderOptions());
                window.Close();
            }

            // Save two of three: the checked ones are saved, then everything closes.
            editor.ReviewUnsaved = review =>
            {
                review.Items[1].Save = false;
                shown = review;
                review.SaveCheckedCommand.Execute(null);
                return Task.CompletedTask;
            };
            var stamps = paths.Select(File.GetLastWriteTimeUtc).ToList();
            await Task.Delay(20);
            await editor.CloseAllCommand.ExecuteAsync(null);
            var layers = paths.Select(p => Psd.PsdFile.OpenForEditing(p).Root.Children.Count).ToList();
            check(shown is { SaveText: "Save 2", AllChecked: null } && layers[0] == 2 && layers[1] == 1 && layers[2] == 2
                  && !editor.Factory.OpenDocuments().Any(),
                $"Save 2 of 3 saves the checked documents, discards the other, and closes all (layers now {string.Join(", ", layers)})");
        }
        finally
        {
            editor.ReviewUnsaved = saved;
            foreach (var path in paths) File.Delete(path);
        }
    }
}
