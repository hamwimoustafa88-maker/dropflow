# Test samples

Binary fixtures that cannot be generated in code. Everything here must be freely redistributable; record the source and licence below.

| File | What | Source | Licence |
|---|---|---|---|
| `heic/RGB_8__128x128.heic` | 128 x 128 px, 8-bit RGB, HEVC-coded HEIC (6.5 KB). Magick.NET can read HEIC but not write it, so this file cannot be produced by the tests. The file is unchanged from the source; only the extension was renamed from `.heif` to `.heic`. SHA-256 `b1f3c0578b52525e95d1db617286879aee76779eb2e507fc57d3ce2a3ab527f9` | https://github.com/bigcat88/pillow_heif, file `tests/images/heif/RGB_8__128x128.heif`, master at commit `5a6eb1e29f9ae3cbdcfa99d352f15c925e512a4c` | BSD-3-Clause, (c) 2021-2023 Pillow-Heif contributors. Full text in `heic/LICENSE-pillow_heif.txt`. |

The fixture is used only by tests (`tests/DropFlow.Engines.Pdf.Tests/ImageToPdfTests.cs`); it is not shipped with the product.
