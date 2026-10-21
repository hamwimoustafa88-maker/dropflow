# Third-party notices

Every dependency is recorded here when it is added (see CLAUDE.md, licensing policy). This file lists what is **actually used** in
the repository; planned components are in `docs/plan.md` §3. DropFlow itself is free software under the GNU GPL v3 or later (see `LICENSE`); the licences below apply to each third-party component only, and all of them are compatible with GPL-3.0.

## Libraries in the Windows app and engines (NuGet)

| Component | Version | License | Used for | Project |
|---|---|---|---|---|
| CommunityToolkit.Mvvm | 8.4.2 | MIT | MVVM helpers | App |
| H.NotifyIcon.Wpf | 2.4.1 | MIT | Tray icon | App |
| Velopack | 1.2.161 | MIT | Installer and updates (GitHub Releases) | App |
| Microsoft.Web.WebView2 | 1.0.4258.31 | BSD-style (Microsoft, file `LICENSE.txt` read) | HTML to PDF through the Edge WebView2 runtime that ships with Windows | Engines.Web |
| Magick.NET-Q8-AnyCPU (+ Magick.NET.Core) | 15.0.0 | Apache-2.0 | Image decode/encode and drawing | Engines.Images, Engines.Pdf, Engines.Archive, AI |
| PDFsharp | **6.2.4** | MIT | PDF page work, stamping, image embedding. **Do not upgrade:** 6.5.0 and later are AGPL-3.0-only | Engines.Pdf |
| PdfPig | 0.1.16 | Apache-2.0 | PDF text and geometry extraction | Engines.Pdf (tests too) |
| PDFiumCore | 4688.0.0 | Apache-2.0 (wraps PDFium, BSD-3/Apache-2.0) | Rendering PDF pages (win-x64/x86 natives only) | Engines.Pdf |
| DocumentFormat.OpenXml | 3.5.1 | MIT | Writing/reading DOCX and PPTX | Engines.Pdf |
| ClosedXML | 0.104.2 | MIT | XLSX (its SixLabors.Fonts 1.0.0 is Apache-2.0) | Engines.Pdf |
| Markdig | 1.4.2 | BSD-2-Clause | Markdown to HTML | Engines.Pdf |
| ReverseMarkdown | 6.2.1 | MIT | HTML to Markdown | Engines.Pdf |
| AngleSharp | 1.5.2 | MIT | Cleaning EPUB HTML | Engines.Pdf |
| System.Security.Cryptography.Pkcs | 10.0.13 | MIT | Pinned to a patched version (PDFsharp pulls 8.0.1 with advisories) | Engines.Pdf |
| SharpCompress | 0.50.7 | MIT | Reading ZIP/TAR/7z/RAR | Engines.Archive |
| ZXing.Net | 0.16.11 | Apache-2.0 | Creating and reading QR codes | Engines.Archive |
| Whisper.net, Whisper.net.Runtime | 1.9.1 | MIT (bundles whisper.cpp, MIT) | Speech to text (CPU runtime; Apple-only Metal runtime rides along unused) | AI |
| Microsoft.ML.OnnxRuntime | 1.31.0 | MIT | Background removal and face detection | AI |

Transitive packages that ride along are MIT or Apache-2.0 (for example Microsoft.Extensions.AI.Abstractions 10.2.0,
System.Numerics.Tensors 9.0.0, Magick.NET.Core 15.0.0).

The PDF/A output (PDF to PDF/A-2b) embeds the standard "sRGB IEC61966-2.1" ICC profile (3,144 bytes, Copyright (c) 1998
Hewlett-Packard Company, freely redistributable under HP's permissive terms; the same file is shipped by ImageMagick and
Debian's colord). DropFlow takes it from Magick.NET at run time (`ColorProfiles.SRGB`); it is not stored in the repository.

## Programs shipped inside the installer (`engines\` next to DropFlow.exe; separate processes, never linked — ADR-0012)

| Program | Version / source | License | How DropFlow uses it |
|---|---|---|---|
| FFmpeg + ffprobe (Gyan.dev "essentials" build) | 9.0.2 - https://www.gyan.dev/ffmpeg/builds/ , source https://ffmpeg.org/releases/ | **GPL-3.0** (the build includes x264, x265, libvpx, SVT-AV1, libaom, lame, opus, vorbis ...) | Audio/video conversion, the Encoder, subtitles. Shipped unmodified as a separate executable with its `LICENSE` and `SOURCE-OFFER.txt`; source also available on request through https://github.com/hamwimoustafa88-maker/dropflow/issues |
| qpdf | 12.4.2 - https://github.com/qpdf/qpdf | Apache-2.0 | Protect, unlock, repair and compress PDFs |
| Tesseract OCR | 5.4.0 (UB Mannheim Windows build) - https://github.com/UB-Mannheim/tesseract | Apache-2.0 (its bundled libraries: see `engines\Tesseract\doc`) | OCR fallback and searchable PDFs |
| Tesseract language data `eng`, `ara`, `osd` (tessdata_fast) | https://github.com/tesseract-ocr/tessdata_fast | Apache-2.0 | English and Arabic text recognition |

## Optional external programs (not shipped; used only if the user installs them)

| Program | License | How DropFlow uses it |
|---|---|---|
| LibreOffice | MPL-2.0 | Legacy binary Office formats and a few exotic document pairs — separate process (headless) |
| 7-Zip | LGPL-2.1+ (unRAR restriction on the RAR code) | Writing 7z archives — separate process |

## AI models (downloaded only after the user agrees; SHA-256 pinned and verified)

| Model | License | Source |
|---|---|---|
| Whisper ggml models (tiny … large-v3-turbo q5) | MIT | `ggerganov/whisper.cpp` on Hugging Face |
| u2netp (background removal) | Apache-2.0 | `BritishWerewolf/U-2-Netp` |
| BiRefNet lite (background removal) | MIT | `onnx-community/BiRefNet_lite-ONNX` |
| UltraFace RFB-320 (face detection) | MIT | `Arm/ultraface-rfb-320-onnx-raspberry` |

RMBG-1.4 and other non-commercial models are deliberately **not** used; a test enforces it.

## Windows components used through the OS (not redistributed)

Windows.Media.Ocr (OCR), Windows.Data.Pdf (page rendering for OCR), the Microsoft Edge WebView2 Evergreen runtime.

## Web (`web/`)

| Component | Version | License | Used for |
|---|---|---|---|
| Next.js | 16.4.2 | MIT | Landing page / web app framework |
| React, React DOM | 19.3.0 | MIT | UI |
| Tailwind CSS (+ @tailwindcss/turbopack) | 4.x | MIT | Styling |
| next-intl | 4.14.9 | MIT | Arabic/English routing and messages |
| TypeScript, ESLint, eslint-config-next, @types/* | 5.x / 9.x | Apache-2.0 / MIT | Dev tooling |
| IBM Plex Sans Arabic, IBM Plex Mono (fonts) | — | SIL OFL 1.1 | Typography, self-hosted at build via `next/font/google` |
| pdf-lib | 1.17.1 | MIT (dependencies MIT, one MIT AND Zlib, one 0BSD) | In-browser PDF merge/split/rotate/watermark/metadata |
| pdfjs-dist (pdf.js) | 6.4.299 | Apache-2.0 | In-browser PDF rendering and text extraction |
| fflate | 0.8.3 | MIT | ZIP/GZIP in the browser |
| @ffmpeg/core (ffmpeg.wasm core, single-thread) | 0.12.10 | **GPL-2.0-or-later** (built with x264 and others; source: github.com/ffmpegwasm/ffmpeg.wasm) | In-browser audio/video conversion, served from `/vendor/ffmpeg` with its notice in `public/vendor/NOTICE.txt`. GPL-2.0-or-later is compatible with DropFlow's GPL-3.0-or-later (restored 2026-10-21 after the GPL switch, ADR-0013) |
| @ffmpeg/ffmpeg | 0.12.15 | MIT | Only its vendored worker files are used (a small own client speaks its protocol) |
| vitest | 5.0.5 | MIT | Web unit tests (dev only; `@types/node` bumped to 24.19.2 because it requires it) |

The in-browser app (`/app`) loads these only on that page (lazy chunks), never on the landing page. Its `connect-src` CSP allows
`self`, `blob:`, `data:` and `https:` (the last one only so the user can paste a link to a file); a static test asserts that exactly one
module (`src/engine/lib/fetch-link.ts`) may call `fetch`, with GET, no credentials and no body, and that nothing else uses XHR/WebSocket/beacon.

## Tests

| Component | License |
|---|---|
| xunit, xunit.runner.visualstudio (2.x) | Apache-2.0 |
| Microsoft.NET.Test.Sdk (17.x) | MIT |
| System.Drawing.Common 10.0.13 (renders OCR test images) | MIT |
| `tests/samples/heic/RGB_8__128x128.heic` (HEIC fixture from bigcat88/pillow_heif, not shipped) | BSD-3-Clause, see `tests/samples/README.md` |
| veraPDF (optional PDF/A validator, run only when `DROPFLOW_VERAPDF` is set; never bundled or committed) | GPL-3.0 / MPL-2.0 |

## Notes
- Fonts are not code; OFL 1.1 permits redistribution with the software.
- `npm audit` (2026-10-20): 0 vulnerabilities in production dependencies; 5 high-severity advisories in the dev-only ESLint chain
  (fast-glob/micromatch) — not shipped, fix when upstream releases a non-breaking update.
- A WinRAR `Rar.exe` and an FFmpeg inside another vendor's application were present on the development machine and were used to
  run integration tests only; DropFlow neither bundles nor depends on them.
