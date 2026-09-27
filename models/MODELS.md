# AI selection models

Object Selection and Select › Subject run neural networks locally with ONNX Runtime; nothing is sent anywhere.
The model files are too large for the repository, so they are downloaded into this folder:

```sh
dotnet build tools/FetchModels.proj          # or: dotnet build apps/Strayta.Editor -p:FetchModels=true
```

Every file comes from a pinned revision and is checked against the SHA-256 below; the build then copies
`models/*.onnx` next to the app (`<output>/models/`). The app looks in `$STRAYTA_MODELS`, then `models/` next to
the executable, then `models/` in any parent folder (a source checkout). Without the files the editor still runs;
the tools explain how to fetch them and the tests that need them are skipped.

Only permissively licensed models are used: both the code and the weights of each allow commercial use and
redistribution.

## SAM 2.1 Hiera-S (Object Selection)

| | |
|---|---|
| Model | Segment Anything 2.1, Hiera-Small backbone (Meta FAIR) |
| Upstream | https://github.com/facebookresearch/sam2 (code and checkpoints Apache-2.0), https://huggingface.co/facebook/sam2.1-hiera-small |
| ONNX export | https://huggingface.co/vietanhdev/segment-anything-2.1-onnx-models (Viet-Anh Nguyen, AnyLabeling), Apache-2.0 |
| Download | https://huggingface.co/vietanhdev/segment-anything-2.1-onnx-models/resolve/6a3ac868340a3196a349050a6efae22a5acc0330/sam2.1_hiera_small_20260221.zip |
| Archive SHA-256 | `7c232acd0a95053704aa2ae9f9a3e8f9e93899fa51335b14d38e34bb7267973d` (142,896,844 bytes) |
| `sam2.1_hiera_small.encoder.onnx` | `275013f35a03fcf9d9f32f0490bfb7acd8b8fb86f145ba173123eb3886c8d4b2` (138,018,779 bytes) |
| `sam2.1_hiera_small.decoder.onnx` | `d158eb26a43d39eed23ef677d6826d17eb9eda951921f9a252fa5d36d871b57d` (16,519,569 bytes) |
| License | Apache License 2.0 — [licenses/Apache-2.0.txt](licenses/Apache-2.0.txt) |

The image encoder takes a 1024×1024 image (ImageNet normalization) and returns three feature maps; the decoder
takes those plus point/box prompts in 1024-pixel model coordinates and returns three 256×256 mask logit maps
with predicted IoU scores. The ONNX files are a format conversion of Meta's SAM 2.1 checkpoint; the weights are
unchanged. Neither upstream nor the export ships a NOTICE file.

Attribution: *SAM 2: Segment Anything in Images and Videos*, Ravi et al., 2024, arXiv:2408.00714. Copyright Meta
Platforms, Inc. and affiliates, licensed under the Apache License, Version 2.0.

## BiRefNet-lite at 512×512 (Select Subject)

| | |
|---|---|
| Model | BiRefNet_lite (Swin-T backbone), Bilateral Reference for High-Resolution Dichotomous Image Segmentation |
| Upstream | https://github.com/ZhengPeng7/BiRefNet (MIT), weights https://huggingface.co/ZhengPeng7/BiRefNet_lite (MIT) |
| ONNX export | https://huggingface.co/studioludens/birefnet-lite-512 (a 512×512 re-export of BiRefNet_lite), MIT |
| Download | https://huggingface.co/studioludens/birefnet-lite-512/resolve/4a3c40c36c94093cc1e724d9ea428b8fa4b57dc7/onnx/model.onnx |
| `birefnet-lite-512.onnx` | `1cb0fb360dadd15af77c639085d77a9df67db0c64315560c3de005f676345ac2` (191,877,254 bytes) |
| License | MIT — [licenses/BiRefNet-MIT.txt](licenses/BiRefNet-MIT.txt) (Copyright (c) 2024 ZhengPeng) |

Input 512×512 (ImageNet normalization), output 512×512 foreground logits.

Attribution: *Bilateral Reference for High-Resolution Dichotomous Image Segmentation*, Zheng et al., CAAI AIR 2024.

## Why these

- **SAM 2.1 Hiera-S** gives clearly better masks than SAM 1 ViT-B, MobileSAM or EfficientSAM at a similar or
  smaller size, and its encoder runs in about 1.1–1.5 s on an Apple M3 Pro CPU (Hiera-T is only ~20% faster
  with slightly worse masks). The prompt decoder takes about 20 ms, so prompts are interactive once the image is
  encoded. EdgeSAM (S-Lab license) and SAM 3 (SAM License) were rejected for their licenses.
- **Select Subject** needs "the main thing in the picture" without a prompt, which SAM does not answer well.
  BiRefNet is a strong salient-object / background-removal model under MIT. The 1024×1024 export takes 7–17 s on
  CPU, so the 512×512 re-export (about 1.6–2 s) is used; its coarser mask edge is snapped to the image's own
  edges afterwards. IS-Net (Apache-2.0, 1.2 s) was compared on the same product shot and gave similar masks with
  slightly weaker edges. BRIA RMBG was rejected for its non-commercial license.
- The Core ML execution provider was tried: it runs the SAM encoder in ~0.4 s but spends ~50 s compiling it
  on first load and could not compile BiRefNet, so inference runs on the CPU provider everywhere.

## ONNX Runtime

Inference uses the `Microsoft.ML.OnnxRuntime` NuGet package (MIT, © Microsoft Corporation); its third-party
notices ship inside the package.
