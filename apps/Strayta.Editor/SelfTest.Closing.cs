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
        // About Strayta carries the trademark notice (the UI itself names the stored image, not Photoshop).
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } appWindow })
        {
            var about = new AboutWindow();
            about.Show(appWindow);
            await Task.Delay(300);
            check(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(about).OfType<Avalonia.Controls.TextBlock>()
                    .Any(t => t.Text?.Contains("not affiliated with") == true),
                "About Strayta shows the trademark notice");
            if (Environment.GetEnvironmentVariable("STRAYTA_SELFTEST_SHOTS") is { Length: > 0 } shots)
            {
                foreach (var (w, name) in new (Avalonia.Controls.Window, string)[] { (about, "about.png"), (appWindow, "main-window.png") })
                {
                    using var shot = new RenderTargetBitmap(new Avalonia.PixelSize((int)w.Bounds.Width, (int)w.Bounds.Height));
                    shot.Render(w);
                    shot.Save(Path.Combine(shots, name), new PngBitmapEncoderOptions());
                }
            }
            about.Close();

            // A file still in iCloud: a card over the canvas says it is downloading, then goes away when it arrives.
            var cloudFile = Path.Combine(Path.GetTempPath(), "Strayta cloud test.png");
            await File.WriteAllBytesAsync(cloudFile, new byte[3 << 20]);
            var downloading = editor.ShowCloudDownload(cloudFile, TimeSpan.Zero);
            await Task.Delay(1300);
            var card = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(appWindow).OfType<Avalonia.Controls.ItemsControl>()
                .FirstOrDefault(c => c.Name == "CloudDownloadsList");
            check(card is { IsEffectivelyVisible: true } && editor.CloudDownloads.Count == 1
                  && editor.CloudDownloads[0].Detail.StartsWith("3.0 MB · ") && !editor.CloudDownloads[0].Detail.EndsWith(" 0 s"),
                $"a file downloading from iCloud shows a card with its size and the time waited ({editor.CloudDownloads.FirstOrDefault()?.Detail})");
            if (Environment.GetEnvironmentVariable("STRAYTA_SELFTEST_SHOTS") is { Length: > 0 } cloudShots)
            {
                using var shot = new RenderTargetBitmap(new Avalonia.PixelSize((int)appWindow.Bounds.Width, (int)appWindow.Bounds.Height));
                shot.Render(appWindow);
                shot.Save(Path.Combine(cloudShots, "cloud-download.png"), new PngBitmapEncoderOptions());
            }
            downloading.Dispose();
            check(!editor.HasCloudDownloads && card?.IsEffectivelyVisible != true, "the download card goes away when the file arrives");
            using (editor.ShowCloudDownload(cloudFile)) await Task.Delay(50);
            await Task.Delay(500);
            check(!editor.HasCloudDownloads, "a quick download never shows the card");
            File.Delete(cloudFile);
            check(!CloudFiles.NeedsDownload(Path.GetTempFileName()), "a local file doesn't need downloading");
        }

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
