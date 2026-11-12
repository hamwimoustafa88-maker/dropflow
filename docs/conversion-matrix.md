# Conversion matrix — every tool on real files

The conversion matrix runs **every item the wheel offers** (Convert and Tools) on a corpus of real, independently produced files and
re-opens every output with a reader that is not DropFlow's own. It exists because unit tests use small, friendly inputs; real
files (an Arabic PDF printed by Edge, a stereo MP3, a HEIC from a phone, a small blur box) are where the defects were.

## Windows desktop (`tests/DropFlow.App.Tests/Matrix`)

```
set DROPFLOW_MATRIX=1
set DROPFLOW_MATRIX_DIR=%TEMP%\dropflow-matrix        (optional; corpus, outputs, report.md, report.json, matrix.log)
set DROPFLOW_MATRIX_ONLY=arabic                         (optional; a sample-name or operation substring)
dotnet test tests/DropFlow.App.Tests --filter "FullyQualifiedName~ConversionMatrix"
```

- **Engines:** the ones the installer ships (`%LOCALAPPDATA%\DropFlow\current\engines`: FFmpeg 9.0.2 essentials, qpdf, Tesseract with
  Arabic + English), then PATH (LibreOffice). Override with `DROPFLOW_MATRIX_ENGINES`. Models come from `%LOCALAPPDATA%\DropFlow Data\models`.
- **Corpus** (`MatrixCorpus.cs`, 67 files, generated on every run, nothing downloaded): Arabic PDFs printed by Edge and exported by
  LibreOffice, an English 3-page PDF, an image-only scanned PDF, an AES-256 protected PDF (qpdf); JPEG with EXIF orientation 6 + GPS,
  WebP, TIFF, AVIF, BMP, 12 MP JPEG, transparent PNG, animated GIF, SVG, ICO, a real HEVC HEIC, pictures of English and Arabic text;
  UTF-8 and Windows-1256 Arabic text, Markdown, HTML, ODT/DOCX/DOC/RTF (LibreOffice), XLSX with an Arabic sheet name and formulas,
  XLS, ODS, CSV with a BOM and a quoted comma, TSV, JSON, PPTX/PPT/ODP, two EPUBs; Arabic SRT, VTT with cue settings, ASS with tags;
  nine audio formats with silence at both ends and an Arabic title tag, Windows TTS speech; eight video formats, a phone video with a
  90° display rotation, a video without sound; ZIP with an Arabic folder, 7z, TAR, TGZ, GZ, RAR (WinRAR).
- **Cells:** each sample × each wheel item, then pairs of one category for the multi-file tools (merge, join, compare, collage,
  rename, ZIP). Options are the defaults; tools that need the user's input get what the visual editor or dialog would send
  (`EditorValues`: a redact box, a name to sign, page list, times, a subtitle file…).
- **Checks** (`OutputChecker.cs`): the original's SHA-256 is unchanged; every output exists, is not empty and opens: Magick.NET for
  pictures, ffprobe for media (streams, duration, a video stream in video files), PdfPig for PDFs (with the password for protected
  ones), the OpenXML validator for DOCX/XLSX/PPTX, ZIP structure for ODF/EPUB, OLE header for DOC/XLS/PPT, SharpCompress/GZip for
  archives, JSON parsing, cue markers for subtitles, no U+FFFD in text. Text formats must contain the sample's words — **Arabic words
  in logical order** (a reversed word or broken letters are reported as such).
- **Statuses:** PASS · WARN (opens, but e.g. a PDF text layer lacks the words; OpenXML validator remarks on LibreOffice's own DOCX) ·
  NOTE (success with nothing to do, e.g. no GPS to strip) · REFUSED (the correct answer: no audio track, no text in a photo, a scan
  without a text layer) · NEEDS-INPUT (asks for what only the user can give) · WINDOW (opens its own window) · FAIL / BAD-OUTPUT /
  THREW / TIMEOUT (defects; the test fails).

### Results, 2026-11-12

| Run | Cells | PASS | Defects |
|---|---|---|---|
| First run | 1290 | 1065 | 143 cells: HEIC output impossible everywhere, every tool on a HEIC photo, Arabic OCR, Arabic PDF text, video blur boxes under ~84 px, plus harness mistakes |
| After the fixes (run 5) | 1290 | 1168 | 6 (HEIC compress, fixed afterwards; 4 harness checks of protected PDFs and one corpus file, fixed in the harness) |

Defects found and fixed (each has a test that failed first):

1. **HEIC could not be written** (`convert:heic` on every wheel, and every tool on a HEIC photo: compress, resize, rotate, edit,
   crop, annotate, redact, metadata). Magick.NET's libheif has no encoder. New `Heic/HeicEncoder` (FFmpeg libx265, one intra frame,
   full range) and `Heic/HeifContainer` (own ISO/IEC 23008-12 writer: hvcC from the stream's VPS/SPS/PPS, ispe, colr nclx + ICC,
   pixi, clap for odd sizes, Exif item). libheif reads the results back. `convert:heic` now declares FFmpeg as its engine.
2. **OCR of an Arabic picture gave "pJl.9Jl.J"**: "auto" ran Windows OCR with the profile language (English). "auto" now uses the
   shipped Tesseract with Arabic + English (reads both in one run; measured far better on Arabic) and falls back to Windows OCR when
   Tesseract reads nothing (small text on a busy background).
3. **Arabic PDFs from Edge/Chrome**: shared heh/yeh glyphs came out as Persian ھ/ی ("ھذه", "الكمیة"), so search, copy and translation
   failed (`ArabicGlyphSharing`, never applied to Persian/Urdu lines); an English sentence on an Arabic page had its full stop at
   the front; LibreOffice's tanween came out as "$" (zero-width ASCII glyphs are now dropped); a table's tight header row was left
   out of the table (Excel, Markdown, Word).
4. **Video blur failed for every box under about 84 px** (faces, number plates): FFmpeg needs the chroma radius strictly below
   half the half-size plane (`ToolFilters.BlurRadii`).

Follow-up the same day (owner: "solve these too"):

5. **Scanned PDFs** are no longer refused by PDF to TXT/Markdown/HTML/Word/Excel/PowerPoint/EPUB, extract text and translate: the
   tools read the scan through OCR themselves (`ScannedPdf` hook in the PDF engine, `ScannedPdfReader` in DropFlow.AI = the shipped
   Tesseract, Arabic + English) and say so in a note. Matrix: the scanned sample went from 9 refusals to 28 PASS.
6. **TIFF Artist / Copyright / ImageDescription / Make / Model / Software / DateTime** are written as TIFF's own IFD0 tags
   (`TiffTags`; the picture data is not touched, a new directory is appended) and read back by DropFlow and by Magick.NET. Other
   EXIF fields in a TIFF are still refused with the list of what works.
7. **Web OCR on pictures without text** no longer invents letters: only lines Tesseract scores at least 60 (real text measured
   90-96, invented lines 0-55) with two or more letters are kept, so a photo or QR code says "no text found".

Known limits seen in the matrix (by design or documented): PDF/A pages are pictures (no text layer); redacting words in a scanned
PDF needs regions (no OCR boxes yet); PDF to Excel keeps tables only when the PDF has tables; LibreOffice-made DOCX files carry `w:bidiVisual w:val="true"`, which the strict OpenXML validator
flags and Word accepts. One full run (of five) ended with the test host crashing while two transcriptions waited on Whisper; it did
not happen again with per-cell logging (`matrix.log` records start/end and memory of every cell for the next time).

## Web (`web/e2e/sections/matrix.mjs`)

See `web/README.md`. `node e2e/run.mjs http://localhost:3200 --only=matrix --out=<dir>` (about 35 minutes; `DROPFLOW_MATRIX_LOCALE=ar`
for `/ar/app`). 2026-11-12: 938 cells, 863 PASS, 75 REFUSED, 0 failures after four fixes (WebP/AVIF/SVG compress handing back bigger
or flattened files, Arabic text from Chromium PDFs, stereo audio to Opus crashing ffmpeg.wasm, plus harness fixes).

## Android (`android/app/src/androidTest/.../matrix/`)

Instrumented tests on an emulator (`C:\Android\emulator\emulator.exe -avd dropflow_api35 -no-window -no-audio -no-snapshot -gpu swiftshader_indirect`,
then `gradlew.bat connectedDebugAndroidTest`; one `DFMATRIX` logcat line per row). 2026-11-12, Android 15: 258 of 258 rows pass after
13 fixes (every job crashed on Android 15+, reversed Arabic words from PDFs, rotate without an orientation tag, CP437 zip names,
bigger "compressed" files, target size above the target, HEVC shrinking the video, GPS left in XMP, inputs without an extension,
`../` zip entries reported as damaged, a missing ZIP MIME type, the trim timeline running right to left). Details, the share-flow
screenshots script and what is still unverified: [android.md](android.md).
