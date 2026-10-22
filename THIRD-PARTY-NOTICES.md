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
| PDFtoImage | 5.4.0 | MIT | Rendering PDF pages (replaced PDFiumCore, which had Windows x64/x86 natives only); DropFlow also calls five PDFium C functions directly for page count, sizes and permission flags | Engines.Pdf, AI (`pdf.ocr` page renderer on macOS/Linux) |
| bblanchon.PDFium.Win32 / .Linux / .macOS (via PDFtoImage) | 152.0.7961 | Apache-2.0 (packaging; PDFium itself BSD-3-Clause/Apache-2.0) | The PDFium native library for win-x64/x86/arm64, linux-x64/arm64 (glibc and musl) and osx-x64/arm64 | Engines.Pdf, AI |
| SkiaSharp + SkiaSharp.NativeAssets.Win32 / .Linux.NoDependencies / .macOS (via PDFtoImage) | 4.150.1 | MIT (Skia: BSD-3-Clause) | The bitmap PDFtoImage draws into | Engines.Pdf, AI |
| DocumentFormat.OpenXml | 3.5.1 | MIT | Writing/reading DOCX and PPTX | Engines.Pdf |
| ClosedXML | 0.104.2 | MIT | XLSX (its SixLabors.Fonts 1.0.0 is Apache-2.0) | Engines.Pdf |
| Markdig | 1.4.2 | BSD-2-Clause | Markdown to HTML | Engines.Pdf |
| ReverseMarkdown | 6.2.1 | MIT | HTML to Markdown | Engines.Pdf |
| AngleSharp | 1.5.2 | MIT | Cleaning EPUB HTML | Engines.Pdf |
| System.Security.Cryptography.Pkcs | 10.0.13 | MIT | Pinned to a patched version (PDFsharp pulls 8.0.1 with advisories) | Engines.Pdf |
| SharpCompress | 0.50.7 | MIT | Reading ZIP/TAR/7z/RAR | Engines.Archive |
| ZXing.Net | 0.16.11 | Apache-2.0 | Creating and reading QR codes | Engines.Archive |
| Whisper.net, Whisper.net.Runtime | 1.9.1 | MIT (bundles whisper.cpp, MIT) | Speech to text (CPU runtime; Apple-only Metal runtime rides along unused) | AI |
| Microsoft.ML.OnnxRuntime | 1.31.0 | MIT | Background removal, face detection and the English/Arabic translation networks (Opus-MT) | AI |
| Microsoft.ML.Tokenizers | 2.0.1 | MIT | Cuts text into SentencePiece pieces for the translation models (reads their `source.spm`, incl. the NFKC normalisation); added 2026-10-23 with the owner's approval | AI |
| Google.Protobuf (via Microsoft.ML.Tokenizers) | 3.30.2 | BSD-3-Clause | Reads the SentencePiece model file | AI |

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
| **Windows on ARM64 installer only:** FFmpeg + ffprobe (BtbN FFmpeg-Builds, `n9.0.2-31-gc89d6ee1ca` winarm64 GPL build) | https://github.com/BtbN/FFmpeg-Builds (dated autobuild 2026-10-21; BtbN prunes old autobuilds, so the pin in `tools/fetch-engines.ps1` must be refreshed when it disappears) | **GPL-3.0** | Same use and the same separate-program terms as the x64 FFmpeg (LICENSE + SOURCE-OFFER.txt next to it) |
| **Windows on ARM64 installer only:** qpdf `msvc-arm64` | 12.4.2 - https://github.com/qpdf/qpdf | Apache-2.0 | Same as x64. Tesseract has no Windows ARM64 build: the ARM64 installer leaves it out unless the x64 build is bundled on purpose (it then runs under Windows 11's x64 emulation) |

## macOS and Linux app (`src/DropFlow.Desktop`, added 2026-10-22; Avalonia approved by the owner)

| Component | Version | License | Used for |
|---|---|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent | 12.1.3 | MIT | Cross-platform UI: wheel, tray / menu-bar icon, windows, drag and drop |
| Transitive: Avalonia.Native, Avalonia.X11, Avalonia.FreeDesktop (+ .AtSpi), Avalonia.Skia, Avalonia.HarfBuzz, Avalonia.Remote.Protocol, MicroCom.Runtime | 12.1.3 / 0.11.6 | MIT | macOS and X11 backends, rendering, text shaping |
| Transitive: Tmds.DBus.Protocol | 0.94.1 | MIT | The Linux tray icon (StatusNotifierItem over D-Bus), pulled by Avalonia.FreeDesktop |
| Transitive: SkiaSharp (+ native assets) | 3.119.4 | MIT (Skia: BSD-3-Clause) | Drawing |
| Transitive: HarfBuzzSharp (+ native assets) | 8.3.1.3 | MIT (HarfBuzz: Old MIT) | Arabic text shaping |
| Transitive, build time only: Avalonia.BuildServices | 11.3.2 | MIT | Avalonia's build statistics task. **Turned off** in DropFlow (`UsedAvaloniaProducts` cleared, `AVALONIA_TELEMETRY_OPTOUT=1` in CI); never shipped |

PDFiumCore ships Windows and Linux natives but none for macOS, so PDF page rendering is missing in the Mac app until a macOS PDFium
build is added (docs/desktop-mac-linux.md). The AI and Web engine projects target Windows only and are not in the Mac/Linux app yet.

### Programs inside the macOS app (`Contents/Resources/engines`) and the Linux AppImage (`usr/lib/dropflow/engines`)

| Program | Version / source | License | Notes |
|---|---|---|---|
| FFmpeg + ffprobe (Martin Riedl's static release builds) | 9.0.2 - https://ffmpeg.martin-riedl.de (build script https://git.martin-riedl.de/ffmpeg/build-script), source https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz | **GPL-3.0** (`--enable-gpl --enable-version3`, no non-free parts; includes x264, x265, libvpx, libaom, dav1d, SVT-AV1, rav1e, lame, opus, vorbis, OpenSSL 3 …) | Pinned URLs + SHA-256 in `installer/unix/fetch-ffmpeg.sh`; shipped unmodified as separate programs with `LICENSE-GPL-3.0.txt` and `FFMPEG-SOURCE-OFFER.txt`. The .deb/.rpm use the distribution's FFmpeg instead |
| AppImage type2-runtime | 20251108 - https://github.com/AppImage/type2-runtime | MIT | The small loader at the start of every `.AppImage` file (pinned + SHA-256 checked) |

qpdf and Tesseract have no pinned, redistributable macOS build: the Mac app shows the Homebrew command instead. The Linux packages
*recommend* `qpdf`, `tesseract-ocr` + Arabic data from the distribution.

### Build tools for the macOS/Linux packages (not shipped)

| Tool | Version | License |
|---|---|---|
| nfpm | 2.47.0 (pinned + SHA-256) | MIT |
| squashfs-tools (`mksquashfs`), `hdiutil`, `codesign`, `sips`, `iconutil` | system | GPL-2.0 / Apple tools |

## Optional external programs (not shipped; used only if the user installs them)

| Program | License | How DropFlow uses it |
|---|---|---|
| LibreOffice | MPL-2.0 | Legacy binary Office formats and a few exotic document pairs — separate process (headless) |
| 7-Zip | LGPL-2.1+ (unRAR restriction on the RAR code) | Writing 7z archives — separate process |
| Google Chrome, Chromium, Microsoft Edge or Brave (macOS/Linux) | Proprietary / BSD-3-Clause (Chromium) | HTML to PDF on macOS and Linux, started headless as a separate process (`--print-to-pdf`, network blocked unless the job allows it). Found if installed, never downloaded |
| FFmpeg, qpdf, Tesseract from Homebrew or the Linux distribution (macOS/Linux) | as above | Found on PATH, Homebrew (`/opt/homebrew/bin`, `/usr/local/bin`) or `/usr/bin` until the macOS/Linux packages bundle their own |

## AI models (downloaded only after the user agrees; SHA-256 pinned and verified)

| Model | License | Source |
|---|---|---|
| Whisper ggml models (tiny … large-v3-turbo q5) | MIT | `ggerganov/whisper.cpp` on Hugging Face |
| u2netp (background removal) | Apache-2.0 | `BritishWerewolf/U-2-Netp` |
| BiRefNet lite (background removal) | MIT | `onnx-community/BiRefNet_lite-ONNX` |
| UltraFace RFB-320 (face detection) | MIT | `Arm/ultraface-rfb-320-onnx-raspberry` |
| Opus-MT English to Arabic and Arabic to English (translation; `opus-mt-en-ar`, `opus-mt-ar-en`) | Apache-2.0 on the Hugging Face model cards of `Helsinki-NLP/opus-mt-en-ar` and `-ar-en`; the OPUS-MT / Tatoeba project describes its models as CC-BY-4.0 (attribution below) | ONNX export `Xenova/opus-mt-en-ar` (revision `034a6843`) and `Xenova/opus-mt-ar-en` (revision `09c75568`); the int8-quantised encoder and merged decoder plus `source.spm` and `vocab.json`, about 108 MB per direction, 4 files each pinned by SHA-256 |

**Attribution for the translation models.** The English-Arabic and Arabic-English translation models are the OPUS-MT models of the
Language Technology Research Group at the University of Helsinki (Helsinki-NLP), trained on the OPUS corpus by Jorg Tiedemann and
Santhosh Thottingal (OPUS-MT, EAMT 2020; Tatoeba Translation Challenge, 2020). The ONNX conversion is by Joshua Lochner (Xenova,
Transformers.js). DropFlow runs them unmodified with ONNX Runtime and a SentencePiece tokenizer; nothing is sent anywhere. Translations
are machine output and can be wrong: check anything important.

RMBG-1.4 and other non-commercial models are deliberately **not** used; a test enforces it. For translation, NLLB (CC-BY-NC) and every
cloud translation service are excluded for the same reason (licence and privacy); the same test covers it.

## Windows components used through the OS (not redistributed)

Windows.Media.Ocr (OCR), Windows.Data.Pdf (page rendering for OCR), Windows.Media.FaceAnalysis (face detection for automatic redaction, no model download), the Microsoft Edge WebView2 Evergreen runtime. These exist only in the Windows build of the AI engine (`net10.0-windows`); the macOS/Linux build uses Tesseract, PDFium and the UltraFace model instead.

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
| tesseract.js | 7.0.0 | Apache-2.0 (bundles wasm-feature-detect Apache-2.0, bmp-js MIT, zlibjs MIT, idb-keyval Apache-2.0) | On-device OCR (image -> TXT, "Text (OCR)", scanned PDF -> TXT). Its ES module and worker are served from `/vendor/tesseract`, loaded on first use |
| tesseract.js-core | 7.0.0 | Apache-2.0 (Tesseract OCR Apache-2.0, Leptonica BSD-2-Clause) | The LSTM-only wasm cores (one of three is loaded, chosen by the browser's SIMD support), `/vendor/tesseract/core` |
| @tesseract.js-data/eng, @tesseract.js-data/ara | 1.0.0 | MIT (package) / Apache-2.0 (tessdata models) | English + Arabic `best_int` LSTM models, `/vendor/tesseract/lang` (2.8 MB + 1.6 MB, gzip) |
| libheif-js | 1.23.5 | **LGPL-3.0** (libheif LGPL-3.0, libde265 LGPL-3.0) | HEIC/HEIF input. Used unmodified as a separate, replaceable file (`/vendor/heif/libheif-bundle.mjs`, its ES-module build with the wasm inside), imported at runtime, never bundled into DropFlow's code |
| utif2 (UTIF.js) + pako | 4.1.0 + 1.0.11 | MIT; pako MIT AND Zlib | TIFF input and output (bundled in a lazy chunk) |
| gifenc | 1.0.3 | MIT | GIF output for pictures (bundled in a lazy chunk) |
| jsQR | 1.4.0 | Apache-2.0 | "Read QR" (bundled in a lazy chunk) |
| onnxruntime-web | 1.30.0 | MIT | Runs the background-removal model on the device (wasm backend, 1 thread). `ort.wasm.min.mjs` + `ort-wasm-simd-threaded.{mjs,wasm}` served from `/vendor/ort`, imported at runtime (not bundled) |
| U²-Net-P model (`u2netp.onnx`, BritishWerewolf/U-2-Netp ONNX export of xuebinqin/U-2-Net) | commit 7112208 | Apache-2.0 | "Remove background". Downloaded once at install/build time by `scripts/vendor.mjs`, accepted only with SHA-256 `309c8469…ddd8` (same pin as the desktop `ModelCatalog`), served from `/vendor/models` |

The in-browser app (`/app`) loads these only on that page (lazy chunks), never on the landing page. The OCR, HEIC and
background-removal engines are not even in the lazy chunks: they are fetched from the site's own `/vendor` folder the first time
such a tool runs (OCR and the cut-out show a one-time note with the download size first), then kept by the service worker. Its `connect-src` CSP allows
`self`, `blob:`, `data:` and `https:` (the last one only so the user can paste a link to a file); a static test asserts that exactly one
module (`src/engine/lib/fetch-link.ts`) may call `fetch`, with GET, no credentials and no body, and that nothing else uses XHR/WebSocket/beacon.

## Android app (`android/`, Gradle; approved by the owner 2026-10-22)

| Component | Version | License | Used for |
|---|---|---|---|
| AndroidX core-ktx | 1.18.0 | Apache-2.0 | Compat helpers, FileProvider, notifications (1.19 needs compileSdk 37, not installed) |
| AndroidX activity-compose | 1.12.4 | Apache-2.0 | Compose activities, result contracts (pickers, permissions) |
| AndroidX lifecycle-runtime-ktx, lifecycle-service | 2.10.0 | Apache-2.0 | ViewModel/lifecycle scopes, the foreground work service |
| Jetpack Compose BOM (ui, foundation, material3) | 2026.06.01 | Apache-2.0 | All UI, Material 3 |
| AndroidX exifinterface | 1.4.2 | Apache-2.0 | Lossless GPS/EXIF removal, JPEG rotation by orientation tag |
| AndroidX heifwriter | 1.1.0 | Apache-2.0 | HEIC output (only when the phone has an HEVC encoder) |
| AndroidX documentfile | 1.1.0 | Apache-2.0 | "Copy to a folder" through the system folder picker |
| kotlinx-coroutines-android | 1.11.0 | Apache-2.0 | Background work |
| Media3 transformer, effect, common, muxer, exoplayer, ui | 1.11.1 | Apache-2.0 | Video/audio conversion, trimming, compression on the hardware codecs; preview player in the trim editor |
| PdfBox-Android (com.tom-roush) | 2.0.27.0 | Apache-2.0 | Merge, split, rotate, metadata removal, image recompression, text extraction |
| ZXing core | 3.5.4 | Apache-2.0 | Reading and creating QR codes |
| Bouncy Castle bcprov, bcpkix, bcutil (jdk15to18) — transitive, from PdfBox-Android | 1.72 | MIT-style (Bouncy Castle Licence) | Pulled in by PdfBox-Android for PDF encryption support; DropFlow never removes protection |
| Guava, failureaccess, listenablefuture — transitive, from Media3 | (as resolved) | Apache-2.0 | Media3 internals |
| jsr305, error_prone_annotations, j2objc-annotations, checker-qual, jspecify — transitive annotations | (as resolved) | BSD-3-Clause / Apache-2.0 / MIT | Compile-time annotations only |

Build tools (not shipped): Android Gradle Plugin 9.4.1 (Apache-2.0), Kotlin 2.4.21 + Compose compiler plugin (Apache-2.0),
Gradle 9.8.2 through the wrapper (Apache-2.0). The app has no internet permission (removed explicitly in the manifest).

## Tests

| Component | License |
|---|---|
| xunit, xunit.runner.visualstudio (2.x) | Apache-2.0 |
| Microsoft.NET.Test.Sdk (17.x) | MIT |
| System.Drawing.Common 10.0.13 (renders OCR test images) | MIT |
| `tests/samples/heic/RGB_8__128x128.heic` (HEIC fixture from bigcat88/pillow_heif, not shipped) | BSD-3-Clause, see `tests/samples/README.md` |
| veraPDF (optional PDF/A validator, run only when `DROPFLOW_VERAPDF` is set; never bundled or committed) | GPL-3.0 / MPL-2.0 |
| JUnit 4.13.2 (+ Hamcrest 1.3), Android JVM unit tests only, not in the APK | EPL-1.0 (Hamcrest BSD-3-Clause) |

## Notes
- Fonts are not code; OFL 1.1 permits redistribution with the software.
- `npm audit` (2026-10-20): 0 vulnerabilities in production dependencies; 5 high-severity advisories in the dev-only ESLint chain
  (fast-glob/micromatch) — not shipped, fix when upstream releases a non-breaking update.
- A WinRAR `Rar.exe` and an FFmpeg inside another vendor's application were present on the development machine and were used to
  run integration tests only; DropFlow neither bundles nor depends on them.
