# Strayta

Open-source, MIT-licensed .NET libraries for reading, writing and rendering
layered image documents, starting with Photoshop PSD/PSB files.

> Early development. Nothing is published yet.

## Packages

| Package | Purpose |
|---|---|
| `Strayta.Core` | Format-agnostic document model: layers, groups, masks, blend modes, color modes, pixel buffers. |
| `Strayta.Psd` | PSD/PSB reader and writer, mapping to and from the Core model; unmodeled data round-trips byte for byte. |
| `Strayta.Rendering` | Compositing engine that renders a Core document to pixels (`IRenderer`; CPU today). |

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

- New documents, or open several PSD/PSB files at once (menu, drag and drop, or command line).
- Tools: Move (V), Hand (H), Brush (B), Eraser (E); `[` and `]` resize the brush. Space or the middle
  button pans with any tool; the wheel zooms.
- Layers panel: blend mode, opacity and fill for the selected layer; visibility, thumbnails, rename
  (double-click), drag-and-drop reordering into and out of groups, new layer/group, duplicate, delete.
- Layer masks on layers, groups and adjustment layers (Layer > Layer Mask, or the panel's mask button): click the
  mask thumbnail to paint in it (black hides, white reveals), Shift-click to disable it; Apply, Delete.
- Adjustment layers (Layer > New Adjustment Layer): Levels, Curves, Hue/Saturation, Brightness/Contrast, Invert,
  Threshold, Posterize, edited live in the Properties panel.
- Full undo/redo. Save / Save As write PSD, keeping everything Strayta does not edit (text, smart objects,
  effects, ...) exactly as it was.
- Interaction renders a screen-resolution preview; full resolution follows when you pause.
- View > Photoshop Composite / Difference compare Strayta's render with the image stored in the file.

```sh
dotnet run -c Release --project apps/Strayta.Editor -- [file.psd ...]
```

Diagnostics (environment variables): `STRAYTA_SELFTEST=1` runs a scripted editing session and reports
each step; `STRAYTA_DRAGBENCH=1` / `STRAYTA_PAINTBENCH=1` / `STRAYTA_ADJUSTBENCH=1` measure frame rates (layer drag, brush
stroke, Properties slider drag) on the first opened file;
`STRAYTA_THEME=Light|Dark` sets the starting appearance.

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
