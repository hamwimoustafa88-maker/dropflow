# Tangerine feature baseline (parity reference)

Source: https://tangerineformac.com/ — read live on 2026-10-20 (every tab opened in a browser).
DropFlow must never ship less than this in a category; Windows-specific adaptations are listed in
`docs/feature-catalog.md`. Do **not** copy Tangerine's text, logo, colors or assets.

Headline numbers: **275** conversion options · **26** advanced file tools · **6** file actions.
Requirements: Apple silicon, macOS 13+. Pricing is not shown on the site; file actions "do not use your
conversion allowance" (so conversions are metered). Everything is processed locally.

## Interaction model
- Drag a file → tap **Shift** (conversions), **Command** (file tools) or **Control** (file actions).
- Release the key and keep dragging; the wheel stays open until drop or cancel. Keys are configurable in Settings.
- Or press **Enter** on a selected file to open the format menu; **Command/Control** switch wheels;
  **arrow keys** choose, **Enter** applies, **Escape** cancels.
- Touch screens: drag to move, or hold a file briefly before dragging to convert.
- Output: a new copy beside the original; originals are kept.

## Conversions (Shift wheel) and tools (Command wheel)

| Category | Shift wheel targets | Command wheel tools |
|---|---|---|
| Images | PNG, WEBP, HEIC, TIFF, AVIF, BMP, PDF (and JPG). Input-only: SVG | COMPRESS, METADATA, EDIT, ANNOTATE, ADD BG, CROP, REDACT; plus multi-file Create a PDF, Make a collage |
| Video | MP4, MKV, WEBM, AVI, WMV, GIF, MP3 (and MOV) | COMPRESS, METADATA, MUTE, TRIM, CROP, SPEED, SNAPSHOT, SPLIT, REDACT; plus multi-file Join |
| Audio | M4A, WAV, FLAC, OGG, OPUS, AIFF, WMA (and MP3) | COMPRESS, METADATA, NORMALIZE, VISUALIZE, TRIM, CHANNELS, BLEEP |
| PDF | DOCX, PAGES, PPTX, XLSX, JPG, PNG, EPUB, TXT, MD | COMPRESS, METADATA, SPLIT, ORGANIZE; plus multi-file Merge |
| Documents | PAGES/DOCX, PDF, TXT, MD, HTML, EPUB, PNG, JPG. Input: RTF, RTFD, Finder text clippings | — |
| Slides | PPT/PPTX → PDF, JPG, PNG; PDF → PPTX. Input-only: PPT | — |
| Spreadsheets | XLSX, ODS, CSV, TSV; PDF export; PDF → XLSX. Input-only: XLS | — |
| EPUB | PDF, DOCX, PAGES, TXT, MD, HTML; PDF → EPUB | — |
| Subtitles | SRT ⇄ VTT, TXT export | — |
| Archives | ZIP, TAR, GZIP. Input-only: RAR | EXTRACT |

## Tool details (cards)
**Images** — *Compress images*: target file size, percentage reduction or preset; resize while compressing; one image or a selection.
*Edit metadata*: camera/location tags, or strip metadata. *Edit photos*: exposure, color, detail, effects with live preview.
*Annotate photos*: arrows, shapes, text, numbered callouts. *Add a background*: solid color/gradient/image, aspect ratio, spacing, corner radius.
*Crop images*: aspect ratio or exact dimensions. *Redact photos*: detect faces or text locally, cover with solid color or blur.
*Create a PDF* (multi-file): one page per image, mixed formats. *Make a collage* (multi-file): grid/row/column/featured, order, spacing, corners, background.

**Video** — *Compress videos*. *Join videos* (multi-file): MP4/MOV/MKV, arrange order, save one MP4. *Trim videos*: start/end handles, preview, frame stepping.
*Split videos*: split points on a timeline, each section saved as a clip in a new folder. *Crop videos*: drag crop or aspect ratio, keep audio.
*Change playback speed*: 0.5×/1×/2×, keeps audio pitch. *Remove audio*. *Save video frames*: scrub/step, full-resolution PNG snapshots.
*Redact videos*: solid color or blur over all or part of the timeline. *Edit metadata*: location, device, author tags.

**Audio** — *Compress audio*. *Normalize volume*. *Trim audio*: waveform handles + auto-remove silence at the ends. *Convert audio channels*: mono/stereo (mono→stereo duplicates).
*Visualize audio*: landscape/portrait/square MP4 with an orange waveform on black or a static image. *Bleep audio*: mark ranges in a waveform, replace speech with a bleep. *Edit metadata*: artist/album/comments.

**PDF** — *Compress PDFs*. *Merge PDFs* (selection order). *Split pages* (each page to a PDF in a new folder). *Organize pages* (reorder, rotate, duplicate, remove).
*Save pages as images* (300 DPI JPG/PNG, folder for many pages). *Extract text and Markdown* (headings/lists/tables→MD; scanned pages via local OCR).
*Export to Word* (DOCX paragraphs+tables, OCR for scans, "layout may change"). *Export to Pages*. *Export slides and spreadsheets* (PPTX/XLSX). *Create an EPUB* (adjustable text or original layout). *Edit metadata*.

**Documents** — *Convert Word and Pages* (DOCX ⇄ PAGES: paragraphs, tables, images). *Export documents* (DOCX/Pages/Markdown/text → PDF, HTML, EPUB, JPG, PNG). *Import rich text* (RTF, RTFD, Finder text clippings → editable documents or plain text).

**Slides** — *Export slides* (PPT/PPTX → PDF or JPG/PNG images). *PDF to PowerPoint* (PPTX; legacy PPT importable).

**Spreadsheets** — *Convert spreadsheets* (XLSX/ODS/CSV/TSV; legacy XLS importable). *Export as PDF*. *PDF to Excel* (XLSX workbook).

**EPUB** — *Convert an EPUB* (PDF, DOCX, Pages, TXT, Markdown, HTML). *PDF to EPUB* (adjustable text or original pages).

**Subtitles** — *Convert subtitles* (SRT ⇄ VTT, keeps cue timing). *Extract subtitle text* (TXT).

**Archives** — *Extract archives* (ZIP, TAR, GZIP, RAR into a folder beside the original). *Convert archives* (to ZIP, TAR, GZIP; RAR importable).

## File actions (Control wheel) — 6
AirDrop (share via macOS recipient picker) · Float (floating shelf pinned to a screen edge, drag files into other apps) · Copy (to clipboard) ·
Move (choose destination, move originals) · Copy Path (one path per line) · Terminal (open folder or containing folder in Terminal/iTerm2).
Works with folders, mixed selections and files with no conversion tools.

## Supported-format notes (footer table)
Images: PDF export · SVG input only. Video: MP3 export. PDF: all pages · images at 300 DPI. Documents: RTF, RTFD & text clippings input · HTML export.
Slides: PPT input only · PDF to PPTX. Spreadsheets: PDF export · XLS input only. Subtitles: TXT export. Archives: RAR input only.

## Privacy positioning
"Your files stay on your Mac." Online converters require uploading a copy; Tangerine processes locally and never uploads.
