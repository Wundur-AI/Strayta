using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.VisualTree;
using Strayta.Core;
using Strayta.Core.Text;
using Strayta.Editor.Controls;
using Strayta.Editor.Editing;
using Strayta.Editor.ViewModels;
using Strayta.Psd;
using Strayta.Psd.Text;
using Strayta.Text;

namespace Strayta.Editor;

/// <summary>
/// Self-test steps for the Type tool: point and paragraph text typed through the canvas's own key and text input
/// handlers and the view models, caret movement and selection, styling a range from the Character panel, one history
/// step per commit, undo inside the edit, box resizing, the Paragraph panel on a selected layer, editing a corpus type
/// layer (with the missing-font prompt), Free Transform keeping type live, save and reopen, and sample files.
/// </summary>
internal static partial class SelfTest
{
    private static async Task RunTypeToolStepsAsync(EditorViewModel editor, Action<bool, string> check)
    {
        var font = await Task.Run(() => FontCatalog.System.FallbackPostScriptName);
        if (font is null)
        {
            check(true, "type tool: no fonts installed, steps skipped");
            return;
        }
        var savedHooks = (editor.SetClipboardText, editor.GetClipboardText, editor.AskReplaceMissingFonts);
        string? clipboard = null;
        editor.SetClipboardText = t => { clipboard = t; return Task.CompletedTask; };
        editor.GetClipboardText = () => Task.FromResult(clipboard);
        string? samples = Environment.GetEnvironmentVariable("STRAYTA_TYPE_SAMPLES");
        if (samples is not null) Directory.CreateDirectory(samples);
        try
        {
            var model = LayerFactory.NewDocument(640, 360, whiteBackground: true);
            var doc = new DocumentViewModel(model, null, editor);
            editor.Factory.AddDocument(doc);
            editor.ActiveDocument = doc;
            await doc.RenderAsync();

            // ---- The tool --------------------------------------------------------------------------------------
            editor.Tool = CanvasTool.Move;
            check(editor.HandleToolKey("T", shift: false) && editor.IsTypeTool && editor.ToolName == "Horizontal Type"
                  && editor.ToolGroups.Select(g => g.Key).SkipWhile(k => k != "G").Take(3).SequenceEqual(["G", "T", "H"]),
                "T picks the Horizontal Type tool, placed before the Hand tool as in Photoshop's strip");
            editor.Type.FontFamily = FontCatalog.System.Find(font)!.FamilyName;
            editor.Type.FontSize = 40;
            editor.ForegroundColor = Avalonia.Media.Color.FromRgb(200, 30, 30);
            check(editor.Type.Target == TypeTarget.NewText && editor.Type.FontSize == 40 && editor.Type.Color == editor.ForegroundColor,
                "with no type selected the options set up new text (40 pt, in the foreground color)");

            // ---- Point text through the canvas's key and text handlers ---------------------------------------
            var canvas = await FindCanvasAsync(doc);
            int historyBefore = doc.HistoryPosition;
            var session = doc.BeginNewText(60, 120);
            check(doc.IsEditingType && editor.IsEditingType && session.Layer.Tags.Contains("text") && doc.SelectedLayer?.Node == session.Layer,
                "a click starts point text on a new type layer, selected in the Layers panel");
            var renderTimes = new List<double>();
            var frameTimes = new List<double>();
            foreach (char c in "Hello world")
            {
                await Task.Delay(60); // typing pace: the previous frame is on screen before the next key
                var sw = Stopwatch.StartNew();
                if (canvas is not null) TypeText(canvas, c.ToString());
                else session.Editor.Insert(c.ToString());
                renderTimes.Add(session.LastRenderMs);
                await WaitForFrameAsync(doc);
                frameTimes.Add(sw.Elapsed.TotalMilliseconds);
            }
            renderTimes.Sort();
            frameTimes.Sort();
            Console.WriteLine($"TYPEBENCH keystroke: text engine median {renderTimes[renderTimes.Count / 2]:F2} ms (max {renderTimes[^1]:F2}), "
                              + $"to the frame on screen median {frameTimes[frameTimes.Count / 2]:F1} ms (max {frameTimes[^1]:F1}) on a {model.Width}×{model.Height} document");
            check(session.Editor.Data.Text == "Hello world" && session.Layer.Pixels is not null && session.Layer.Bounds.Left >= 55,
                $"typing draws each keystroke into the layer ({session.Layer.Bounds})");
            check(doc.HistoryPosition == historyBefore && doc.UndoText == "Undo Typing" && doc.CanUndo, "while typing, nothing enters the history; Edit › Undo undoes typing");

            if (canvas is not null)
            {
                PressKey(canvas, Avalonia.Input.Key.V);
                TypeText(canvas, "v");
                check(editor.IsTypeTool && session.Editor.Data.Text == "Hello worldv", $"a tool key types instead of switching tools ({editor.Tool}, \"{session.Editor.Data.Text}\")");
                PressKey(canvas, Avalonia.Input.Key.Back);
                PressKey(canvas, Avalonia.Input.Key.Left, KeyModifiers.Alt);
                check(session.Editor.Caret == 6, $"Option+← moves back a word (caret {session.Editor.Caret})");
                PressKey(canvas, Avalonia.Input.Key.Right, KeyModifiers.Alt | KeyModifiers.Shift);
                check(session.Editor.SelectedText == "world", $"Shift+Option+→ selects the word (\"{session.Editor.SelectedText}\")");
                PressKey(canvas, Avalonia.Input.Key.Left, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
                check(session.Editor.Caret == 0 && !session.Editor.HasSelection, "⌘← goes to the line start");
                PressKey(canvas, Avalonia.Input.Key.End);
                PressKey(canvas, Avalonia.Input.Key.Enter, KeyModifiers.Shift);
                TypeText(canvas, "again");
                check(session.Editor.Data.Text == $"Hello world{TextLayerData.LineBreak}again" && session.Layout.Lines.Count == 2,
                    "Shift+Return is a line break within the paragraph");
                PressKey(canvas, Avalonia.Input.Key.Space);
                TypeText(canvas, " ");
                check(session.Editor.Data.Text.EndsWith("again ", StringComparison.Ordinal), "Space types a space instead of panning");
                PressKey(canvas, Avalonia.Input.Key.Back);
                PressKey(canvas, Avalonia.Input.Key.Back, KeyModifiers.Alt);
                PressKey(canvas, Avalonia.Input.Key.Back);
                check(session.Editor.Data.Text == "Hello world", $"Backspace and Option+Backspace delete a character and a word (\"{session.Editor.Data.Text}\")");
            }
            else check(false, "the document's canvas is on screen");

            // Style a range from the Character panel: only the selection changes; mixed values read blank.
            var editor1 = session.Editor;
            editor1.Select(6, 11);
            check(editor.Type.Target == TypeTarget.Editing, "while typing, the Character panel acts on the selection");
            editor.Type.FauxBold = true;
            editor.Type.Color = Avalonia.Media.Color.FromRgb(20, 90, 200);
            editor.Type.FontSize = 60;
            check(editor1.Data.StyleRuns.Count == 2 && editor1.Data.StyleAt(6).FauxBold && !editor1.Data.StyleAt(0).FauxBold
                  && editor1.Data.StyleAt(8).FontSize == 60 && editor1.Data.StyleAt(0).FontSize == 40,
                "faux bold, color and size apply to the selected characters only");
            editor1.Select(0, 11);
            check(double.IsNaN(editor.Type.FontSize) && editor.Type.FauxBold is null && editor.Type.IsColorMixed,
                "across differently styled characters the size, bold and color read as mixed (blank)");
            editor1.SetCaret(11);
            editor.Type.Underline = true;
            TypeText(canvas, "!");
            check(editor1.Data.StyleAt(11).Underline && !editor1.Data.StyleAt(10).Underline, "a style picked at the caret applies to what is typed next");
            doc.Undo();
            check(editor1.Data.Text == "Hello world" && doc.HistoryPosition == historyBefore, "⌘Z inside the edit undoes the last typing only");
            doc.Redo();
            check(editor1.Data.Text == "Hello world!", "⌘⇧Z redoes it");

            // Clipboard: copy a word, paste it at the end.
            editor1.SelectWord(2);
            await editor.CopyCommand.ExecuteAsync(null);
            editor1.Move(CaretMove.TextEnd);
            await editor.PasteCommand.ExecuteAsync(null);
            check(clipboard == "Hello" && editor1.Data.Text == "Hello world!Hello", "⌘C / ⌘V copy and paste plain text");
            editor.SelectAllCommand.Execute(null);
            check(editor1.SelectionLength == editor1.Data.Text.Length && doc.Selection is null, "⌘A selects all the text, not the canvas");
            editor1.SetCaret(editor1.Data.Text.Length);
            editor1.Backspace(CaretMove.WordLeft);

            // Commit: one history step, the layer named after its text.
            editor.Tool = CanvasTool.Move;
            var point = session.Layer;
            var pointData = TypeLayers.Read(point);
            check(!doc.IsEditingType && doc.HistoryPosition == historyBefore + 1 && doc.UndoText == "Undo Type" && point.Name == "Hello world!"
                  && pointData?.Text == "Hello world!" && pointData.StyleRuns.Count >= 2 && model.Root.Children.Contains(point),
                $"choosing another tool commits: one \"Type\" step, the layer named \"{point.Name}\"");
            doc.Undo();
            check(!model.Root.Children.Contains(point), "undo removes the new type layer");
            doc.Redo();
            check(model.Root.Children.Contains(point) && TypeLayers.Read(point)?.Text == "Hello world!", "redo brings it back");
            doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == point);
            check(editor.Type.Target == TypeTarget.Layer && editor.Type.FontFamily == FontCatalog.System.Find(font)!.FamilyName,
                "with the type layer selected the options show its font");

            // ---- Paragraph text --------------------------------------------------------------------------------
            editor.Tool = CanvasTool.Type;
            // Reopening the font menu after a search refills its list, which recycles rows (this used to crash on a null row).
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } fontWindow }
                && fontWindow.GetVisualDescendants().OfType<FontFamilyPicker>().FirstOrDefault(p => p.IsEffectivelyVisible) is { } picker)
            {
                for (int i = 0; i < 3; i++)
                {
                    picker.ShowMenu(true, search: i % 2 == 0 ? "a" : null); // a search, then a reopen that clears it
                    await Task.Delay(150);
                    picker.ShowMenu(false);
                    await Task.Delay(50);
                }
                check(true, "the font menu opens and closes repeatedly");
            }
            else check(false, "the Type tool's options bar has a font picker");
            doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node is PixelLayer { Name: "Background" });
            editor.Type.FontSize = 20;
            var box = doc.BeginNewText(60, 170, new TextRect(60, 170, 300, 340));
            box.Editor.Insert("The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs.");
            int lines = box.Layout.Lines.Count;
            check(box.Editor.Data.Kind == TextKind.Paragraph && lines >= 3, $"a dragged box wraps its text ({lines} lines)");
            var (rx, ry) = box.HandlePosition(BoxHandle.Right);
            check(box.HitHandle(rx + 1, ry, 4) == BoxHandle.Right, "the box's handles hit-test");
            box.BeginDrag(BoxHandle.Right, rx, ry);
            box.DragTo(rx + 150, ry, shift: false);
            box.DragTo(rx + 250, ry, shift: false);
            box.EndDrag();
            check(box.Layout.Lines.Count < lines && Math.Abs(box.Editor.Data.Box.Width - 490) < 1e-6, $"widening the box reflows the text live ({box.Layout.Lines.Count} lines)");
            box.Editor.Undo();
            check(Math.Abs(box.Editor.Data.Box.Width - 240) < 1e-6, "one undo takes back the whole handle drag");
            box.Editor.Redo();
            box.BeginDrag(BoxHandle.Move, 100, 200);
            box.DragTo(110, 203, shift: true);
            box.EndDrag();
            check(box.Editor.Data.Transform.TX == 70 && box.Editor.Data.Transform.TY == 170, "⌘-drag moves the text; Shift keeps it level");
            editor.Type.AlignCenter = true;
            check(box.Editor.Data.ParagraphStyleAt(0).Justification == TextJustification.Center, "the alignment buttons act on the paragraph being typed");
            int beforeBox = doc.HistoryPosition;
            editor.CommitTypeCommand.Execute(null);
            var paragraphLayer = box.Layer;
            check(doc.HistoryPosition == beforeBox + 1 && TypeLayers.Read(paragraphLayer) is { Kind: TextKind.Paragraph } pd && pd.Box.Width == 490,
                "✓ commits the paragraph text as one step");

            // Paragraph and Character panels on a selected (not edited) layer: one undoable step each.
            check(editor.Type.Target == TypeTarget.Layer, "after the commit the new layer is selected");
            editor.Type.JustifyAll = true;
            editor.Type.SpaceAfter = 6;
            editor.Type.Tracking = 50;
            var pdata = TypeLayers.Read(paragraphLayer)!;
            check(pdata.ParagraphStyleAt(0).Justification == TextJustification.JustifyAll && pdata.ParagraphStyleAt(0).SpaceAfter == 6
                  && pdata.StyleRuns.All(r => r.Style.Tracking == 50) && doc.UndoText == "Undo Edit Type Layer",
                "the Paragraph and Character panels change a selected type layer, each an undoable step");
            doc.Undo();
            check(TypeLayers.Read(paragraphLayer)!.StyleRuns.All(r => r.Style.Tracking == 0), "undo takes back a panel change");
            doc.Redo();
            var props = doc.Properties as LayerPanel;
            check(props is { IsType: true } && props.Type.FontSize == 20, "the Properties panel shows the type layer's font and size");

            // ---- Editing an existing layer, cancelling, and an empty new text ---------------------------------------
            int beforeEdit = doc.HistoryPosition;
            var again = await doc.BeginEditTextAsync(point, point.Bounds.Right + 2, point.Bounds.Bottom - 5);
            check(again is not null && again.Editor.Caret == again.Editor.Data.Text.Length, "clicking existing type edits it, the caret where it was clicked");
            again!.Editor.Insert("??");
            if (canvas is not null) PressKey(canvas, Avalonia.Input.Key.Escape);
            else doc.CancelType();
            check(TypeLayers.Read(point)!.Text == "Hello world!" && doc.HistoryPosition == beforeEdit, "Esc cancels: the text and history are as they were");
            check(doc.TypeLayerAt(point.Bounds.Left + 5, point.Bounds.Top + 5) == point && doc.TypeLayerAt(600, 20) is null,
                "the Type tool finds type under the pointer");
            doc.BeginNewText(500, 60);
            doc.CommitType();
            check(doc.HistoryPosition == beforeEdit && !model.Root.Descendants().Any(n => n.Tags.Contains("text") && n.Name.StartsWith("Layer", StringComparison.Ordinal)),
                "committing empty new text leaves no layer and no step");
            again = await doc.BeginEditTextAsync(point);
            again!.Editor.Insert(" Edited");
            editor.CommitTypeCommand.Execute(null);
            check(TypeLayers.Read(point)!.Text == "Hello world! Edited" && point.Name == "Hello world! Edited" && doc.UndoText == "Undo Edit Type Layer"
                  && doc.HistoryPosition == beforeEdit + 1, "editing an existing layer commits as one \"Edit Type Layer\" step and renames it");

            // ---- Free Transform keeps type live and redraws it sharply ------------------------------------------------
            doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == point);
            int widthBefore = point.Bounds.Width;
            check(doc.BeginFreeTransform(), "Free Transform opens on the edited type layer");
            doc.FreeTransform!.WidthPercent = 150;
            doc.FreeTransform.HeightPercent = 150;
            await doc.CommitTransformAsync();
            var scaled = TypeLayers.Read(point);
            check(scaled is { Text: "Hello world! Edited" } && Math.Abs(scaled.Transform.XX - 1.5) < 1e-6 && point.Tags.Contains("text")
                  && Math.Abs(point.Bounds.Width - widthBefore * 1.5) < 6 && CoverageIsCrisp(point),
                $"scaled 150% it stays type and is drawn again by the text engine ({widthBefore} → {point.Bounds.Width} px, crisp edges)");
            check(Math.Abs(TypeOptions.PointsPerUnit(scaled!, model) - 1.5) < 1e-6, "sizes show in points through the layer's scale (40 px text scaled 150% reads 60 pt)");
            again = await doc.BeginEditTextAsync(point);
            again!.Editor.Insert("!");
            doc.CommitType();
            check(TypeLayers.Read(point) is { Text: "Hello world! Edited!" } t2 && Math.Abs(t2.Transform.XX - 1.5) < 1e-6, "the transformed layer can be edited again");

            // ---- Save and reopen ------------------------------------------------------------------------------------
            string path = Path.Combine(Path.GetTempPath(), $"type2-selftest-{Guid.NewGuid():N}.psd");
            await doc.SaveAsync(path);
            var reopened = PsdFile.OpenForEditing(path);
            var texts = reopened.Root.Descendants().OfType<PixelLayer>().Where(l => l.Tags.Contains("text")).Select(TypeLayers.Read).ToList();
            check(texts.Count == 2 && texts.Any(d => d is { Kind: TextKind.Point, Text: "Hello world! Edited!" } && d.StyleRuns.Any(r => r.Style.FauxBold))
                  && texts.Any(d => d is { Kind: TextKind.Paragraph } && d.ParagraphStyleAt(0).Justification == TextJustification.JustifyAll),
                "saved and reopened, both layers are live text with their styles");
            if (samples is not null) File.Copy(path, Path.Combine(samples, "type2-point-and-box.psd"), overwrite: true);
            File.Delete(path);

            await RunTypeLatencyBenchAsync(editor, font);
            await RunCorpusTypeEditAsync(editor, check);
            if (samples is not null) await WriteTypeSamplesAsync(editor, samples, font, check);
            editor.Tool = CanvasTool.Move;
        }
        catch (Exception ex)
        {
            check(false, $"exception in Type tool steps: {ex}");
        }
        finally
        {
            (editor.SetClipboardText, editor.GetClipboardText, editor.AskReplaceMissingFonts) = savedHooks;
        }
    }

    /// <summary>Keystroke to screen on a larger document (TYPEBENCH lines; no assertions, timing depends on the machine's load).</summary>
    private static async Task RunTypeLatencyBenchAsync(EditorViewModel editor, string font)
    {
        foreach (var (w, h) in new[] { (1920, 1080), (4000, 3000) })
        {
            var model = LayerFactory.NewDocument(w, h, whiteBackground: true);
            var doc = new DocumentViewModel(model, null, editor);
            editor.Factory.AddDocument(doc);
            editor.ActiveDocument = doc;
            await doc.RenderAsync();
            await Task.Delay(300);
            var s = doc.BeginNewText(w * 0.1, h * 0.5);
            s.Editor.ApplyStyle(st => st with { FontPostScriptName = font, FontSize = h / 12.0 });
            var engine = new List<double>();
            var frames = new List<double>();
            foreach (char c in "The quick brown fox jumps")
            {
                await Task.Delay(80);
                var sw = Stopwatch.StartNew();
                s.Editor.Insert(c.ToString());
                engine.Add(s.LastRenderMs);
                await WaitForFrameAsync(doc);
                frames.Add(sw.Elapsed.TotalMilliseconds);
            }
            engine.Sort();
            frames.Sort();
            Console.WriteLine($"TYPEBENCH {w}×{h}: text engine median {engine[engine.Count / 2]:F2} ms (max {engine[^1]:F2}), "
                              + $"keystroke to frame median {frames[frames.Count / 2]:F1} ms, p90 {frames[(int)(frames.Count * 0.9)]:F1} ms");
            doc.CancelType();
            doc.MarkSavedForTest();
            editor.Factory.CloseDockable(doc);
        }
    }

    /// <summary>Edits a type layer of a corpus file (STRAYTA_CORPUS): one with installed fonts, and one with missing fonts via the replace prompt.</summary>
    private static async Task RunCorpusTypeEditAsync(EditorViewModel editor, Action<bool, string> check)
    {
        string? corpus = Environment.GetEnvironmentVariable("STRAYTA_CORPUS");
        if (corpus is null || !Directory.Exists(corpus))
        {
            Console.WriteLine("SELFTEST note: STRAYTA_CORPUS not set, corpus type edit skipped");
            return;
        }
        (string Path, bool Missing)? installed = null, missing = null;
        foreach (var file in Directory.EnumerateFiles(corpus, "*.psd").OrderBy(f => new FileInfo(f).Length).Where(f => new FileInfo(f).Length < 40_000_000))
        {
            var fonts = await Task.Run(() =>
            {
                try
                {
                    return PsdFile.OpenForEditing(file).Root.Descendants().OfType<PixelLayer>().Select(TypeLayers.Read).OfType<TextLayerData>()
                        .Select(d => d.FontsUsed.All(FontCatalog.System.Contains)).ToList();
                }
                catch (Exception e) when (e is PsdFormatException or IOException or NotSupportedException)
                {
                    return [];
                }
            });
            if (installed is null && fonts.Contains(true)) installed = (file, false);
            if (missing is null && fonts.Contains(false)) missing = (file, true);
            if (installed is not null && missing is not null) break;
        }
        foreach (var target in new[] { installed, missing })
        {
            if (target is not { } t) continue;
            var model = PsdFile.OpenForEditing(t.Path);
            var doc = new DocumentViewModel(model, t.Path, editor);
            editor.Factory.AddDocument(doc);
            editor.ActiveDocument = doc;
            await doc.RenderAsync();
            var layer = model.Root.Descendants().OfType<PixelLayer>()
                .First(l => TypeLayers.Read(l) is { } d && d.FontsUsed.All(FontCatalog.System.Contains) != t.Missing);
            var original = TypeLayers.Read(layer)!;
            var (pixels, bounds, name) = (layer.Pixels, layer.Bounds, layer.Name);
            string? asked = null;
            editor.AskReplaceMissingFonts = list =>
            {
                asked = string.Join(",", list);
                return Task.FromResult<string?>(FontCatalog.System.FallbackPostScriptName);
            };
            editor.Tool = CanvasTool.Type;
            var session = await doc.BeginEditTextAsync(layer);
            string file = Path.GetFileName(t.Path);
            if (session is null)
            {
                check(false, $"{file}: the type layer \"{name}\" opens for editing");
                continue;
            }
            check(t.Missing == (asked is not null), t.Missing ? $"{file}: editing type in a missing font ({asked}) asks for a replacement" : $"{file}: type in installed fonts opens without asking");
            session.Editor.Move(CaretMove.TextEnd);
            session.Editor.Insert(" 2");
            doc.CommitType();
            var edited = TypeLayers.Read(layer)!;
            check(edited.Text == original.Text + " 2" && doc.UndoText == "Undo Edit Type Layer"
                  && (!t.Missing || edited.FontsUsed.All(FontCatalog.System.Contains)),
                $"{file}: \"{name}\" edited on the canvas, one step{(t.Missing ? ", fonts replaced" : "")}");
            doc.Undo();
            check(TypeLayers.Read(layer)!.SameContent(original) && ReferenceEquals(layer.Pixels, pixels) && layer.Bounds == bounds && layer.Name == name,
                $"{file}: undo restores Photoshop's text, font names and pixels");
            if (t.Missing)
            {
                editor.AskReplaceMissingFonts = _ => Task.FromResult<string?>(null);
                check(await doc.BeginEditTextAsync(layer) is null && !doc.IsEditingType && TypeLayers.Read(layer)!.FontsUsed.SequenceEqual(original.FontsUsed),
                    $"{file}: cancelling the prompt leaves the layer and its font names alone");
            }
            editor.Tool = CanvasTool.Move;
            doc.MarkSavedForTest();
            editor.Factory.CloseDockable(doc);
        }
    }

    /// <summary>A few files made with the tool, for checking in Photoshop (STRAYTA_TYPE_SAMPLES).</summary>
    private static async Task WriteTypeSamplesAsync(EditorViewModel editor, string dir, string font, Action<bool, string> check)
    {
        var model = LayerFactory.NewDocument(800, 600, whiteBackground: true);
        model.Resolution = 144;
        var doc = new DocumentViewModel(model, null, editor);
        editor.Factory.AddDocument(doc);
        editor.ActiveDocument = doc;
        await doc.RenderAsync();
        editor.Tool = CanvasTool.Type;
        doc.SelectedLayer = doc.Layers[0];
        editor.ForegroundColor = Avalonia.Media.Color.FromRgb(20, 20, 20);
        editor.Type.FontFamily = FontCatalog.System.Find(font)!.FamilyName;
        editor.Type.FontSize = 24; // points at 144 ppi: 48 px
        editor.Type.AlignLeft = true; // new text continues in the last settings; start these samples plainly
        editor.Type.Tracking = 0;
        editor.Type.SpaceBefore = 0;

        // Point text with a bold, colored word, a superscript and a second paragraph centered.
        var s = doc.BeginNewText(60, 110);
        s.Editor.Insert("Strayta type tool");
        s.Editor.Select(8, 12);
        editor.Type.FauxBold = true;
        editor.Type.Color = Avalonia.Media.Color.FromRgb(210, 40, 40);
        s.Editor.SetCaret(s.Editor.Data.Text.Length);
        s.Editor.ApplyStyle(st => st with { FauxBold = false, FillColor = TextColor.Black, BaselinePosition = TextBaselinePosition.Superscript });
        s.Editor.Insert("TM");
        doc.CommitType();
        check(TypeLayers.Read(s.Layer)?.StyleRuns.Count >= 3, "sample: styled point text (144 ppi, 24 pt)");

        // Box text, justified, with a larger first word and tracking.
        doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == s.Layer);
        doc.SelectedLayer = null;
        editor.Type.FontSize = 11;
        editor.Type.Color = Avalonia.Media.Color.FromRgb(30, 60, 120);
        var b = doc.BeginNewText(60, 180, new TextRect(60, 180, 520, 420));
        b.Editor.Insert("Paragraph text wraps inside its box. Resize the box and the lines reflow; justify spreads the words across the full width of every line but the last.\nA second paragraph follows, with space before it.");
        b.Editor.SelectAll();
        editor.Type.JustifyLastLeft = true;
        editor.Type.SpaceBefore = 6;
        b.Editor.Select(0, 9);
        editor.Type.FontSize = 16;
        editor.Type.Tracking = 50;
        doc.CommitType();

        // Turned and scaled type.
        doc.SelectedLayer = null;
        editor.Type.FontSize = 18;
        editor.Type.Tracking = 0;
        editor.Type.AlignLeft = true;
        editor.Type.SpaceBefore = 0;
        editor.Type.Color = Avalonia.Media.Color.FromRgb(0, 128, 90);
        var r = doc.BeginNewText(560, 520);
        r.Editor.Insert("Turned 15°");
        doc.CommitType();
        doc.SelectedLayer = doc.Layers.SelectMany(l => l.SelfAndDescendants()).First(i => i.Node == r.Layer);
        doc.BeginFreeTransform();
        doc.FreeTransform!.Angle = -15;
        doc.FreeTransform.WidthPercent = 130;
        doc.FreeTransform.HeightPercent = 130;
        await doc.CommitTransformAsync();

        string path = Path.Combine(dir, "type2-styles-box-transform-144ppi.psd");
        await doc.SaveAsync(path);
        check(File.Exists(path), $"sample written: {path}");
        doc.MarkSavedForTest();
        editor.Factory.CloseDockable(doc);
        editor.Tool = CanvasTool.Move;
    }

    /// <summary>True when the layer's coverage has solid interior pixels and few half-tones (drawn, not blurred by resampling).</summary>
    private static bool CoverageIsCrisp(PixelLayer layer)
    {
        if (layer.Pixels?.Alpha is not { } a) return false;
        int solid = a.Data.Count(v => v == 255), partial = a.Data.Count(v => v is > 0 and < 255);
        return solid > partial;
    }

    private static async Task<ImageCanvas?> FindCanvasAsync(DocumentViewModel doc)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }) return null;
        for (int i = 0; i < 20; i++)
        {
            var canvas = window.GetVisualDescendants().OfType<ImageCanvas>().FirstOrDefault(c => ReferenceEquals(c.DataContext, doc));
            if (canvas is not null) return canvas;
            await Task.Delay(50);
        }
        return null;
    }

    /// <summary>Routes a key press through the canvas as the keyboard would (in-process; bubbling to the window's shortcut handlers).</summary>
    private static void PressKey(ImageCanvas canvas, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        canvas.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = canvas });

    /// <summary>Delivers typed text to the canvas as the platform does after an unhandled key press.</summary>
    private static void TypeText(ImageCanvas? canvas, string text) =>
        canvas?.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text, Source = canvas });

    private static async Task WaitForFrameAsync(DocumentViewModel doc)
    {
        var shown = new TaskCompletionSource();
        void OnFrame(bool _) => shown.TrySetResult();
        doc.FrameDisplayed += OnFrame;
        await Task.WhenAny(shown.Task, Task.Delay(1000));
        doc.FrameDisplayed -= OnFrame;
    }
}
