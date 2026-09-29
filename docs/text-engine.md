# Text engine

Type layers are read, edited, written and drawn by three pieces:

| Where | What |
|---|---|
| `Strayta.Core.Text` | The format-agnostic model: `TextLayerData`, `TextStyle`, `ParagraphStyle`, runs, transform, bounds. |
| `Strayta.Psd.Text` | `PsdTypeLayer` (read/write a layer's `TySh` block) and `EngineData` (Photoshop's text engine syntax). |
| `Strayta.Text` | `FontCatalog`, `TextLayout` (shaping with HarfBuzz, line layout, hit testing), `TextRenderer`, `TextFidelity`. |

`Strayta.Text` depends on Core, SkiaSharp 3.119.4 and HarfBuzzSharp 8.3.1.5 (the pair SkiaSharp.HarfBuzz 3.119.4
uses; both MIT). It does not know about PSD; the editor (`apps/Strayta.Editor/Editing/TypeLayers.cs`) joins the two.

Unedited type layers keep showing the pixels Photoshop stored; only layers whose text changes are drawn by the engine.

## Model

```csharp
var data = PsdTypeLayer.Read(record);                 // TextLayerData, or null for other layers
data.Text;                                            // paragraphs separated by '\n'; U+0003 = forced line break
data.StyleRuns / data.ParagraphRuns;                  // TextRun<TextStyle> / TextRun<ParagraphStyle>, covering Text exactly
data.StyleAt(i); data.ParagraphStyleAt(i); data.FontsUsed;
data.Kind;                                            // Point or Paragraph (box text; data.Box in text space)
data.Transform;                                       // text space -> document (xx, xy, yx, yy, tx, ty)
data.AntiAlias; data.Orientation; data.Warp;          // Warp is kept, not drawn
```

`TextLayerData` is immutable. Editing returns a new instance:

```csharp
data.ReplaceText(start, length, "new", style: null);  // typed text takes the style before it
data.WithText("Whole new text");
data.ApplyStyle(start, length, s => s with { FontSize = 24, FillColor = new TextColor(1, 0, 0) });
data.ApplyParagraphStyle(start, length, p => p with { Justification = TextJustification.Center });
data.WithTransform(t); data.WithLayout(kind: TextKind.Paragraph, box: new TextRect(0, 0, 300, 200));
TextLayerData.CreatePoint("Hello", style, x, y);      // new layers
TextLayerData.CreateParagraph("Hello", style, new TextRect(left, top, right, bottom));
data.SameContent(other);                              // equality ignoring where styles came from
```

`TextStyle`: PostScript font name, size, auto leading / leading, tracking (1/1000 em), kerning (`Metrics`, `Optical`,
`None`) plus manual kerning, baseline shift, fill color, faux bold/italic, caps (normal, small, all), superscript /
subscript, underline, strikethrough, horizontal/vertical scale, standard and discretionary ligatures.
`ParagraphStyle`: justification (left, right, center, the three justify-last variants, justify all), first-line /
start / end indents, space before/after, hyphenation, auto-leading factor (120%).

Sizes are in text-space units (document pixels before the transform). Photoshop shows points:
`points = size × transform scale × 72 / resolution`.

Each style keeps `SourceData` (for PSD, the run's dictionary in the engine data), so keys the model does not cover
survive an edit; `with` copies it along.

## Reading and writing PSD

```csharp
byte[] tySh = PsdTypeLayer.Encode(data, layout.ToTextBounds()); // the block
PsdLayerRecord r2 = PsdTypeLayer.Apply(record, data, bounds);  // the record with its TySh replaced
PsdLayerRecord r3 = PsdTypeLayer.Create(data, bounds);         // a record for a new type layer
PsdTypeLayer.UseResourcesOf(newData, anyTypeLayerOfTheDocument); // share the document's font list and sheets
```

- `TySh`: version, six-double transform, text version 50, the `TxLr` descriptor (`Txt `, `textGridding`, `Ornt`,
  `AntA`, `bounds`, `boundingBox`, `TextIndex`, `EngineData`), the warp descriptor, 16 bytes, zero padding to 4 bytes.
- Engine data is PostScript-like: `<< /Key value >>`, `[ ... ]`, numbers (`1.0`, `.5`, `-.25`, five decimals at most),
  `true`/`false`, `/Names`, and strings in parentheses holding a UTF-16BE BOM and big-endian text, with the *bytes*
  `(`, `)` and `\` escaped by a backslash. Photoshop's layout is fixed: two newlines, tab indentation, a dictionary on
  the line after its key, arrays of scalars on one line (`[ 1.0 0.0 ]`), arrays of dictionaries with each element and
  the `]` at the key's indentation, no newline at the end. `EngineData.Write(EngineData.Parse(x))` reproduces every
  engine data block in the test corpora byte for byte.
- Text is `EngineDict/Editor/Text` with `\r` after every paragraph (the last included). `StyleRun` and `ParagraphRun`
  hold `RunArray` (dictionaries) and `RunLengthArray` (lengths counting that final `\r`). Run dictionaries list only
  what differs from the normal sheets (`ResourceDict/StyleSheetSet[TheNormalStyleSheet]`, and
  `ParagraphSheetSet[TheNormalParagraphSheet]`); fonts are indices into `ResourceDict/FontSet`. Point vs box text is
  `EngineDict/Rendered/Shapes/Children[0]/ShapeType` (0/1), the box `Cookie/Photoshop/BoxBounds`.
- Kerning between two characters follows the *second* one's `AutoKerning` (Photoshop turns it off on the first
  character of a text); manual `Kerning` sits before its character.
- Writing: data read from a layer and not changed gives back the original bytes (`Encode` returns the very array).
  An edit rewrites the engine data from the layer's own tree: `Editor/Text`, both run arrays (each run starting from
  its source dictionary, changed keys set in Photoshop's key order), `AntiAlias`, the shape; new fonts are appended
  to both `FontSet`s. `Txt `, `AntA`, `Ornt`, `bounds` and `boundingBox` are updated in the descriptor.
- `PsdWriter` leaves out the document's `Txt2` block (Photoshop's cached copy of all text) when a type layer was
  edited, as it would describe the old text; `PsdWriteOptions.KeepDocumentTextData` keeps it (for checking what
  Photoshop makes of a stale copy).

## Fonts

```csharp
FontCatalog.System                  // installed faces by PostScript name (built once, ~1 s; FontCatalog.WarmUp())
catalog.Find("Helvetica-Bold")      // FontFace(PostScriptName, FamilyName, StyleName, Weight, Width, Italic)
catalog.Families; catalog.FacesOf("Helvetica"); catalog.Faces
FontCatalog.FromFiles(paths); catalog.AddFolder(dir); FontCatalog.Merge(a, b)
```

Missing fonts are drawn with `FallbackPostScriptName` (Myriad Pro, Arial, Helvetica, ... whichever is installed) and
reported in `TextLayout.MissingFonts`; the file keeps the original font name, as Photoshop does. Characters a font
lacks fall back per character through the system font manager.

## Layout, hit testing, rendering

```csharp
var layout = TextLayout.Create(data, catalog);  // catalog optional
layout.Lines;                                   // TextLine(Start, Length, Baseline, Ascent, Descent, Left, Right, Visible)
layout.Bounds; layout.InkBounds; layout.ToTextBounds();
layout.GetCaret(index);                         // TextCaret(Index, X, Top, Bottom, Line), text space
layout.HitTest(x, y); layout.HitTestDocument(x, y);
layout.GetSelectionRects(start, length);        // one TextRect per line
layout.GetCaretDocument(index);

var result = TextRenderer.Render(data, new TextRenderOptions { Fonts = catalog, ColorMode = ..., BitDepth = ... });
result.Pixels; result.Bounds;                   // straight-alpha raster in document coordinates, tight
```

Rules (measured against Photoshop's stored pixels): point text's first baseline is the anchor; box text's is the
line's ascent (OS/2 typographic ascender) below the box top; further lines one leading apart (auto = 120% of the
largest size on the line) plus paragraph spacing; tracking adds tracking/1000 em after each character; box text
wraps after spaces and hyphens (anywhere in CJK), hides lines that do not fit, and justifies by widening spaces.
Glyph outlines are filled unhinted through the transform. Anti-aliasing: Smooth = exact coverage; Strong = outlines
grown 0.13 px across / 0.15 px up and down, 4×4 point samples ×16 (Photoshop's Strong pixels are multiples of 16);
Sharp and Crisp in between; None aliased.

## Editor

`TypeLayers.Read(layer)`, `TypeLayers.Edit(doc, layer, data, out render)` (an undoable `TextEdit`: new `TySh`,
pixels, bounds, and the name while it still equals the old text), `TypeLayers.Create(doc, data, out render)`,
`TypeLayers.MissingFonts(doc)`. `DocumentViewModel.EditText` / `AddTextLayer` apply them, and `CheckFontsAsync`
(run when a document opens) lists missing fonts in `MissingFonts` and the status-bar notice.
`STRAYTA_SELFTEST=text` runs only the text and Type tool steps of the self-test (`STRAYTA_TYPE_SAMPLES=dir` also writes
sample files, `STRAYTA_CORPUS` edits corpus type layers).

### Type tool

- `Strayta.Text.TextEditor` is the editing logic without UI: caret and anchor on grapheme-cluster boundaries, typing
  over the selection (the style before the caret, or a style picked at the caret for what comes next), Backspace /
  Delete by character, word or line, `Move(CaretMove, extend)` by character, word, laid-out line (keeping the
  horizontal position for ↑ / ↓), paragraph and text, word / line / paragraph selection, `ApplyStyle` /
  `ApplyParagraphStyle` over the selection, `Replace` (box, transform, anti-aliasing), and an undo history for the edit
  (a run of typing is one step). Tests: `tests/Strayta.Text.Tests/TextEditorTests.cs`.
- `Editing/TypeSession.cs` holds one edit on the canvas: it draws each change straight into the layer (no history),
  and handles the box's handles and ⌘-drag. `DocumentViewModel.TypeTool.cs` starts, commits (one `InsertEdit` "Type"
  or `TextEdit` "Edit Type Layer") and cancels sessions; any other edit, another tool, layer or document, Free Transform
  and saving commit first. While a session is open Edit › Undo / Redo step through the typing.
- `Controls/ImageCanvas.Type.cs` turns pointer and keys into edits, takes text from `TextInput` and an input-method
  client (composition drawn at the caret), and draws the caret, selection and box. `MainWindow.IsTyping` keeps the
  single-key shortcuts (tools, spring-loaded keys, Delete, X / D, crop and transform keys) from firing while typing.
- `ViewModels/TypeOptions.cs` backs the options bar, the Character and Paragraph panels and the Properties panel: it
  reads the selection's styles (mixed values: NaN / null / -1) and writes to the session, the selected layer or the
  new-text defaults. Points = text units × the layer's vertical scale × 72 / resolution.
- `LiveContent` redraws type through its new transform after Free Transform, turned crops and Image Size when its
  fonts are installed.

Keystroke to screen, measured by the self-test (`TYPEBENCH`, an M-series Mac under load): the text engine takes
0.2 ms per keystroke for 40 px text and 3.5 ms for a 90 px line on a 1920×1080 document (keystroke to frame ~5–9 ms
median), 10 ms for 250 px text on 4000×3000 (~12–18 ms median).

## Measuring

```sh
dotnet run --project tools/Strayta.Inspect -- text file.psd                       # the text model of each type layer
dotnet run --project tools/Strayta.Inspect -- textfid dir... [--fonts dir] [--all] [--export dir]
dotnet run --project tools/Strayta.Inspect -- textsamples out/ <Main dir> <corpus dir> [--fonts dir]
```

`textfid` renders every type layer whose fonts are available and compares its coverage with Photoshop's pixels
(`TextFidelity`: share of drawn pixels within 16/255, similarity 1 − Σ|Δ|/Σmax, best whole-pixel offset).

## Not done yet

- Type tool: no vertical type tool or type masks, no warp dialog, no ⌘-drag transform handles on point text (Free
  Transform works), no Option-drag / Alt-click box dialog; the input-method composition is drawn as an overlay rather
  than laid out inline; Optical kerning is stored as metrics.

- Hinting: Photoshop hints small Sharp/Smooth type (TrueType instructions move stems and dots by up to a pixel at
  12 px); glyphs here are unhinted.
- Vertical text, warped text and text on a path are kept but drawn unwarped/horizontally (a warning says so).
- Optical kerning is read and written as metrics kerning; its engine-data encoding has not been observed.
- No hyphenation, no Adobe composer (greedy line breaking), no right-to-left reordering, no OpenType features beyond
  kerning and ligatures (small caps are synthesized at 70%, as Photoshop does without an `smcp` font).
- Stroke (`StrokeColor`/`StrokeFlag`) and other unmodeled keys are preserved but not drawn.
