# ADR-0012: Helper programs ship inside the installer; users download nothing

Date: 2026-10-21 · Status: Accepted (owner decision; the FFmpeg licence point needs a lawyer's review before wide distribution) · Refines ADR-0010

## Context
The owner: "I do not want any program the user has to download — ever; use a library, or ship it." Until now FFmpeg, qpdf and
Tesseract were found on the PC or installed through winget after a prompt. That makes every first run depend on the user.

## Decision
- **FFmpeg, qpdf and Tesseract (English + Arabic data) are shipped inside the installer** under `engines\<Engine>\` next to
  `DropFlow.exe`. `EngineLocator` looks there first, then the data folder, PATH and the usual install folders, so a user's own newer
  copy still works when the bundled one is removed. `tools/fetch-engines.ps1` downloads pinned versions at **build time**, checks
  SHA-256 (qpdf's equals the official checksum file) and fills `tools/engines/` (git-ignored; binaries are never committed).
  `installer/build.ps1` copies it into the package.
- Licences: qpdf = Apache-2.0, Tesseract + tessdata_fast = Apache-2.0 (fine to bundle). **FFmpeg (Gyan "essentials") is GPL-3.0 because
  it contains x264/x265.** It is shipped as a **separate executable started as a child process** — never linked, never loaded into our
  process — with its `LICENSE` and a `SOURCE-OFFER.txt` next to it, and listed in `THIRD-PARTY-NOTICES.md`. Shipping an unmodified GPL
  program beside a proprietary one is "mere aggregation" in the GPL's terms and is common practice, but it is a licensing risk the owner
  accepts; the clean alternative is an LGPL FFmpeg build (no x264/x265: H.264 would need the Windows Media Foundation encoder or hardware
  encoders), which would change the encoder presets.
- **PDF/A, HEIC to PDF and the other PDF tools use libraries** (PDFsharp 6.2.4, PDFium, PdfPig, Magick.NET); Ghostscript is never used.
- **Still optional, never required:** LibreOffice (350 MB, MPL-2.0) is used only for legacy binary Office formats and a few exotic pairs;
  everyday documents render through the built-in WebView2 browser (ADR-0011). AI models stay download-on-consent (ADR-0006) because of their size.
- **Web:** nothing to install by definition; `@ffmpeg/core` (GPL) is removed from the browser build.

## Consequences
- The installer grows (about 100-150 MB compressed). Measured and recorded in docs/release.md after the first build.
- Updating a bundled engine = change the pin in `tools/fetch-engines.ps1`, rerun the engine test suites against it.
- `OfferEngineInstalls` stays off by default and only matters for LibreOffice now.
