# Test samples

Fixture files the tests read from disk: binary files that cannot be generated in code, and real-world-shaped text files (subtitles in
legacy encodings and formats) that both the desktop and the web tests read. Everything here must be freely redistributable; record the
source and licence below.

| File | What | Source | Licence |
|---|---|---|---|
| `heic/RGB_8__128x128.heic` | 128 x 128 px, 8-bit RGB, HEVC-coded HEIC (6.5 KB). Magick.NET can read HEIC but not write it, so this file cannot be produced by the tests. The file is unchanged from the source; only the extension was renamed from `.heif` to `.heic`. SHA-256 `b1f3c0578b52525e95d1db617286879aee76779eb2e507fc57d3ce2a3ab527f9` | https://github.com/bigcat88/pillow_heif, file `tests/images/heif/RGB_8__128x128.heif`, master at commit `5a6eb1e29f9ae3cbdcfa99d352f15c925e512a4c` | BSD-3-Clause, (c) 2021-2023 Pillow-Heif contributors. Full text in `heic/LICENSE-pillow_heif.txt`. |

| `subtitles/arabic-cp1256.srt` | Three Arabic SRT cues stored in the legacy Windows-1256 code page (not UTF-8), as old Arabic subtitle files are. | Written for DropFlow's tests. | GPL-3.0-or-later (part of DropFlow). |
| `subtitles/sample.smi` | SAMI with two languages (English and Arabic classes), `<br>`, `&nbsp;` clears and a `<font>` tag. | Written for DropFlow's tests. | GPL-3.0-or-later (part of DropFlow). |
| `subtitles/sample.lrc` | LRC lyrics with metadata tags, a line with two time tags, an empty clearing line and enhanced word times. | Written for DropFlow's tests. | GPL-3.0-or-later (part of DropFlow). |
| `subtitles/sample.ttml` | TTML with clock, tick (`ttp:tickRate`) and frame (`ttp:frameRate`) times, a bold span and a style from the head. | Written for DropFlow's tests. | GPL-3.0-or-later (part of DropFlow). |
| `subtitles/sample.dfxp` | DFXP (the 2006 TTML namespace) whose times are relative to a `<div begin="10s">`. | Written for DropFlow's tests. | GPL-3.0-or-later (part of DropFlow). |
| `subtitles/sample.sub` | MicroDVD at a declared 25 fps (`{1}{1}25`) with `{y:i}`, `{Y:b}` and `/` styles. | Written for DropFlow's tests. | GPL-3.0-or-later (part of DropFlow). |
| `subtitles/sample.sbv` | YouTube SBV with Arabic text. | Written for DropFlow's tests. | GPL-3.0-or-later (part of DropFlow). |

The HEIC fixture is used only by tests (`tests/DropFlow.Engines.Pdf.Tests/ImageToPdfTests.cs`); the subtitle samples are read by
`tests/DropFlow.Engines.Media.Tests/Subtitles/SubtitleFormatsTests.cs` and `web/src/engine/__tests__/subtitle-formats.test.ts`. None of
them is shipped with the product.
