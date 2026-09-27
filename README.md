# Strayta

Open-source, MIT-licensed .NET libraries for reading, writing and rendering
layered image documents, starting with Photoshop PSD/PSB files.

> Early development. Nothing is published yet.

## Packages

| Package | Purpose |
|---|---|
| `Strayta.Core` | Format-agnostic document model: layers, groups, masks, blend modes, color modes, pixel buffers. |
| `Strayta.Psd` | PSD/PSB reader and writer, mapping to and from the Core model; unmodeled data round-trips byte for byte. |
| `Strayta.Imaging` | Opens standard images (PNG, JPEG, WebP, GIF, BMP, ICO; HEIC on macOS) as documents, and writes PNG at the image's own bit depth. Uses SkiaSharp for decoding, keeping it out of the core engine. |
| `Strayta.Rendering` | Compositing engine that renders a Core document to pixels (`IRenderer`; CPU today). |
| `Strayta.Segmentation` | Local AI selection (SAM 2.1, BiRefNet) on ONNX Runtime, producing Core selections. Used by the editor only. |

`Strayta.Core` depends on nothing else in the repo. Format and rendering
packages depend only on Core.

## Building

Requires the .NET 10 SDK.

```sh
dotnet build
dotnet test
```

## Editor

`apps/Strayta.Editor` is an Avalonia desktop editor with tabbed documents and dockable panels
(documents and panels can be dragged out into their own windows), in dark or light appearance.

- New documents, or open several PSD/PSB files and standard images at once (menu, drag and drop, or command
  line). Images open as a single Background layer, upright (camera orientation applied), keeping their color
  profile, bit depth (16-bit PNG) and grayscale. Save writes back to PNG/JPEG while the document is a single
  plain layer; once it has layers, Save asks for a PSD, and saving a layered document as PNG/JPEG writes a
  flattened copy.
- Tools: Move (V), Hand (H), Brush (B), Eraser (E); `[` and `]` resize the brush. Space or the middle
  button pans with any tool; the wheel zooms.
- Selections: Rectangular and Elliptical Marquee (M, Shift+M switches), Lasso (L), with Photoshop's modifiers
  (Shift adds or constrains, Option subtracts or draws from the center, Shift+Option intersects; a click
  deselects). Select > All / Deselect / Reselect / Inverse. Painting, Delete (clear), Fill, Cut, Copy and
  Paste (to a new layer) work on the selected area, and every selection change is undoable.
- Magic Wand and Quick Selection (W, Shift+W switches). The wand selects colors within a tolerance of the clicked
  pixel (per channel, 0–255), contiguous or across the whole image, anti-aliased or hard, from the selected layer
  (its transparency included) or all layers. Quick Selection is a brush (`[`/`]` size) that grows the selection to
  similar, connected pixels and stops at edges, live while you drag; later strokes add, Option subtracts,
  Auto-Enhance softens the edge. Each click or stroke is one undo step.
- Object Selection (tool strip, below Lasso): drag a box around an object or click it, and a local AI model
  selects it; Shift adds, Option subtracts. Sample All Layers (on by default) looks at the whole image, off at the
  selected layer. Select > Subject (also a button in the tool's options bar) selects the main subject. See
  [AI selection models](#ai-selection-models) below.
- Select > Modify > Border, Smooth, Expand, Contract and Feather (Shift+F6), with Photoshop's pixel amounts and
  "Apply effect at canvas bounds"; soft edges stay soft when expanding or contracting. Each is one undo step.
- Select > Select and Mask (Option+Cmd+R): a workspace that previews the selection as a red overlay, on black, on
  white or as black and white, with Radius (edge detection that re-decides the edge from the image's colors, for
  hair and fur), Smooth, Feather, Contrast and Shift Edge, and a Refine Edge brush (Option erases) for areas the
  radius misses. The preview follows every slider step at screen resolution and settles at full resolution when
  you pause. Output to the selection, a layer mask, or a new layer with a layer mask. Simpler than Photoshop's: no
  Smart Radius, Decontaminate Colors, other brushes or view modes, and it looks at the whole image (Sample All
  Layers).
- Layers panel: blend mode, opacity and fill for the selected layer; visibility, thumbnails, rename
  (double-click), drag-and-drop reordering into and out of groups, new layer/group, duplicate, delete.
- Layer masks on layers, groups and adjustment layers (Layer > Layer Mask, or the panel's mask button): click the
  mask thumbnail to paint in it (black hides, white reveals), Shift-click to disable it; Apply, Delete. Reveal
  Selection / Hide Selection (and the panel's button while something is selected) make the mask from the selection.
- Adjustment layers (Layer > New Adjustment Layer): Levels, Curves, Hue/Saturation, Brightness/Contrast, Invert,
  Threshold, Posterize, edited live in the Properties panel.
- Full undo/redo. Save / Save As write PSD, keeping everything Strayta does not edit (text, smart objects,
  effects, ...) exactly as it was.
- Edit > Free Transform (⌘T) on a layer or group: drag corners to scale (proportional; Shift frees it, Option
  scales from the center), sides to stretch, outside to rotate (Shift snaps to 15°), inside to move; or type
  X/Y/W/H/angle in the options bar. Enter or double-click applies (bicubic, area-filtered when shrinking,
  masks follow), Esc cancels.
- File > Export As writes PNG (with transparency) or JPEG (quality, flattened on a matte) with Strayta's own
  encoders.
- Interaction renders a screen-resolution preview; full resolution follows when you pause.
- View > Photoshop Composite / Difference compare Strayta's render with the image stored in the file.

```sh
dotnet run -c Release --project apps/Strayta.Editor -- [file.psd ...]
```

Diagnostics (environment variables): `STRAYTA_SELFTEST=1` runs a scripted editing session and reports
each step; `STRAYTA_DRAGBENCH=1` / `STRAYTA_PAINTBENCH=1` / `STRAYTA_TRANSFORMBENCH=1` / `STRAYTA_ADJUSTBENCH=1`
measure frame rates (layer drag, brush stroke, free transform, Properties slider drag) on the first opened file
(`STRAYTA_TRANSFORMBENCH=new` builds a 4000×3000 document and runs the drag and transform benchmarks on it);
`STRAYTA_WANDBENCH=1` times Magic Wand clicks and a Quick Selection drag (`=new` on a generated 4000×3000
document).
`STRAYTA_SEGBENCH=1` times Object Selection and Select Subject (encoder, per-prompt latency, memory) on the
first opened file; `STRAYTA_THEME=Light|Dark` sets the starting appearance.

### AI selection models

Object Selection and Select > Subject run neural networks on your computer with ONNX Runtime (CPU); images never
leave the machine. `src/Strayta.Segmentation` holds the inference code and only the editor references it; the
PSD engine and renderer do not depend on it.

The models (about 335 MB) are not in the repository. Fetch them once:

```sh
dotnet build tools/FetchModels.proj
```

This downloads pinned files from Hugging Face into `models/`, verifies their SHA-256, and building the editor
copies them to `models/` next to the app (`dotnet build apps/Strayta.Editor -p:FetchModels=true` does both).
Set `STRAYTA_MODELS` to use another folder. Without the models the editor runs normally and the tool explains
how to fetch them; model tests are skipped.

| Feature | Model | License |
|---|---|---|
| Object Selection | SAM 2.1 Hiera-S (Meta), ONNX export by Viet-Anh Nguyen | Apache-2.0 |
| Select Subject | BiRefNet-lite (Zheng Peng et al.), 512×512 ONNX export | MIT |

Sources, exact revisions, checksums, attributions and license texts are in [models/MODELS.md](models/MODELS.md).

## Inspect tool

`tools/Strayta.Inspect` is a development CLI for poking at real files:

```sh
dotnet run --project tools/Strayta.Inspect -- info file.psd --export out/   # layer tree + PNGs
dotnet run --project tools/Strayta.Inspect -- scan ~/psd-corpus             # parse everything, summarize
dotnet run --project tools/Strayta.Inspect -- fidelity ~/psd-corpus         # render and score vs composite
```

## Test corpus

Fidelity tests compare our rendering against the composite image Photoshop
embeds in each file. Licensed third-party PSDs cannot be committed, so they live
outside the repo. Point the tests at them with:

```sh
export STRAYTA_CORPUS=/path/to/psd-corpus
```

Tests that need the corpus are skipped when it is not set.

## Clean-room policy

Strayta is implemented from Adobe's published file format specification, direct
observation of real files, and standard compositing math. Do not copy or port
code from GPL-licensed implementations (e.g. GIMP, Krita). MIT/BSD-licensed
projects may be consulted for behavior and must be credited.

## License

MIT. See [LICENSE](LICENSE).
