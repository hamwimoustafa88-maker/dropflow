<p align="center">
  <img src="web/src/app/icon.svg" width="96" height="96" alt="DropFlow logo">
</p>

<h1 align="center">DropFlow</h1>

<p align="center">
  <strong>Drag a file, tap a key, convert it — 100% on your device.</strong><br>
  Windows 10/11 app + in-browser web app · English and Arabic (full RTL)
</p>

<p align="center">
  <a href="https://github.com/hamwimoustafa88-maker/dropflow/actions/workflows/ci.yml"><img src="https://github.com/hamwimoustafa88-maker/dropflow/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-GPL--3.0--or--later-blue" alt="License: GPL-3.0-or-later"></a>
  <a href="https://github.com/hamwimoustafa88-maker/dropflow/releases/latest"><img src="https://img.shields.io/github/v/release/hamwimoustafa88-maker/dropflow?label=release" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%2F11%20%7C%20Web-0078d4" alt="Platform: Windows 10/11 and Web">
</p>

<p align="center">
  <a href="https://dropflow.scouthub.dev">Website</a> ·
  <a href="https://dropflow.scouthub.dev/en/app">Web app</a> ·
  <a href="https://github.com/hamwimoustafa88-maker/dropflow/releases/latest">Download for Windows</a> ·
  <a href="README.ar.md"><strong>اقرأ بالعربية</strong></a>
</p>

---

## What it is

Start dragging any file, tap a key, and a radial wheel appears under the cursor. Drop the file on a slice and DropFlow
converts or processes it **locally** — nothing is uploaded, on the desktop or in the browser.

| Key while dragging | Wheel | Examples |
|---|---|---|
| **Shift** | Convert | PNG → WebP, MKV → MP4, DOCX → PDF, SRT → VTT |
| **Ctrl** | Tools | compress, crop, merge PDFs, trim video, speech to text, OCR |
| **Alt** | Actions | copy, move, share, copy path, open terminal, float, send to phone |

The keys can be changed in Settings.

## Features

From [`catalog/tools.json`](catalog/tools.json), the single source of truth (full list: [docs/feature-catalog.md](docs/feature-catalog.md)):
**428 conversions · 63 tools · 7 file actions.**

- **Images** — convert JPG, PNG, WebP, HEIC, TIFF, AVIF, BMP, GIF, SVG and ICO files; compress, resize, crop, edit,
  annotate, collage, remove location/EXIF, ICO and favicons, QR codes.
- **PDF** — merge, split, organize, rotate, compress, watermark, page numbers, crop, visible signature, true redaction, compare,
  protect/unlock (with a password you know), repair, PDF/A-2b, PDF to Word/PowerPoint/Excel/images/text/EPUB.
- **Documents** — Word, spreadsheets, slides, Markdown, HTML, EPUB, CSV/JSON to PDF and images through the built-in WebView2 browser.
- **Audio and video** — convert, compress (also to a target size), trim, crop, split, join, speed, mute, normalize, bleep,
  visualize, burn in subtitles, and **DropFlow Encoder** (H.264/H.265/AV1, GPU encoders, presets).
- **Local AI** — speech to text with Whisper (Arabic included), OCR (Windows OCR and Tesseract, English + Arabic), background
  removal, automatic face/text redaction. Models download only after you agree.
- **Archives and utilities** — extract ZIP/TAR/GZ/7z/RAR, create ZIP, hashes, Base64, batch rename.
- **Actions** — copy, move, share, copy path, open a terminal, float on a shelf, send to a phone on the same Wi-Fi (QR code).
- **Automation** — recipes, watched folders, clipboard to wheel, Explorer context menu and Send To, and a `dropflow` CLI.

The **web app** ([/en/app](https://dropflow.scouthub.dev/en/app)) runs 20 of these tools inside the browser (images, PDF, subtitles,
archives, hashes). Audio/video, Office, OCR and speech to text are Windows-only.

## Privacy

Privacy is the product. File contents never leave your device.

- The Windows app works offline. It goes online only for: an update check against GitHub Releases, AI model downloads you
  approve, a file you ask it to download from a link, and the local-network "Send to phone" server while its window is open.
- The web app processes files in Web Workers with WebAssembly. Its Content Security Policy only allows connections to its own
  origin, and a test scans the app code for network calls.
- No accounts, analytics, ads or tracking. Details: [Privacy Policy](https://dropflow.scouthub.dev/en/privacy).

## Status (honest)

**v0.1.0** is the first public release (2026-10-21). What is verified and what is not is tracked in
[docs/status.md](docs/status.md); please read it before relying on a feature.

- The installer is **not code-signed yet**, so Windows SmartScreen warns on first run: click **More info → Run anyway**.
  Free signing through SignPath Foundation is being prepared ([code signing policy](docs/code-signing-policy.md)).
- The drag-and-key flow was reported working by the owner in a manual test on Windows 10. On Windows 11 only an automated smoke
  test ran (the app starts and installs its input hooks); a manual Windows 11 test, multiple monitors and display scaling above
  100 % are still pending.
- About 2,900 automated .NET tests and the web test suites pass on the development machine; engine-dependent tests skip visibly
  when FFmpeg, qpdf, Tesseract or AI models are missing.
- No screenshots yet — the [website](https://dropflow.scouthub.dev) shows the wheel.

## Architecture

| Path | Responsibility |
|---|---|
| `catalog/` | `tools.json` (every tool and conversion) and `build.mjs` (validates it, regenerates docs and web data) |
| `src/DropFlow.Core` | Catalog loader, `IConverter` contract, registry, job queue, wheel model, output naming, settings, history, recipes, CLI logic |
| `src/DropFlow.Hooks` | Drag + key-tap state machine and low-level mouse/keyboard hooks on a dedicated thread |
| `src/DropFlow.Overlay` | The wheel window (non-activating drop target), options form, floating shelf |
| `src/DropFlow.App` | WPF host: tray, settings, history and undo, toasts, Encoder window, updates (Velopack) |
| `src/DropFlow.Engines.*` | Converters per domain: `Images`, `Media`, `Pdf`, `Archive`, `Web` (WebView2), `Common` (process runner, engine locator) |
| `src/DropFlow.AI` | Whisper, OCR, background removal, auto-redaction, model manager (SHA-256 verified) |
| `src/DropFlow.Cli` | The `dropflow` command-line tool |
| `tests/` | xUnit projects, one per source project, plus sample generators |
| `web/` | Next.js landing page and in-browser web app (ar/en) |
| `installer/`, `.github/workflows/` | Velopack packaging, winget templates, CI and release workflows |
| `docs/` | Plan, ADRs, status ledger, deferred work, release and signing docs |

## Build from source

Requirements: Windows 10/11 x64, [.NET 10 SDK](https://dotnet.microsoft.com/download), Node.js 24, PowerShell 7 (`pwsh`), Git.

```powershell
git clone https://github.com/hamwimoustafa88-maker/dropflow.git
cd dropflow

node catalog/build.mjs                  # validate the catalog (must print OK)
dotnet build DropFlow.sln
dotnet test DropFlow.sln                # engine tests skip visibly when a program or model is missing
dotnet run --project src/DropFlow.App   # run the Windows app

# Optional: download the pinned, SHA-256-checked FFmpeg, qpdf and Tesseract that the installer bundles
# into tools/engines (git-ignored). Put them on PATH or next to the app as engines\ to run engine features.
pwsh tools/fetch-engines.ps1

# Web
cd web
npm install
npm run dev                             # http://localhost:3000
```

Packaging and releases: [docs/release.md](docs/release.md).

## Contributing

Contributions are welcome — Arab developers especially. Start with [CONTRIBUTING.md](CONTRIBUTING.md) (setup, project rules,
DCO sign-off) and look for issues labelled
[`good first issue`](https://github.com/hamwimoustafa88-maker/dropflow/labels/good%20first%20issue). Questions and ideas go to
[Discussions](https://github.com/hamwimoustafa88-maker/dropflow/discussions). Please follow the
[Code of Conduct](CODE_OF_CONDUCT.md) and report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

Roadmap: [docs/plan.md](docs/plan.md) (Arabic) · postponed work and how to resume it: [docs/deferred.md](docs/deferred.md) ·
how to add a converter: [docs/writing-a-converter.md](docs/writing-a-converter.md).

## License

DropFlow is free software: you can redistribute it and/or modify it under the terms of the **GNU General Public License,
version 3 or (at your option) any later version** ([LICENSE](LICENSE), [ADR-0013](docs/adr/0013-gpl3-open-source.md)).
Copyright (c) 2026 Moustafa Hamwi and contributors.

- **Name and logo:** the GPL covers the code, not the name "DropFlow" or its logo. Forks must use another name and logo —
  see [TRADEMARK.md](TRADEMARK.md).
- **Third-party components** keep their own licences: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
- **FFmpeg** (GPL-3.0 build) is shipped unmodified as a separate program in the installer's `engines\` folder, with its licence
  and a written source offer; its source is at <https://ffmpeg.org/releases/> and on request through this repository's issues.

## Acknowledgements

DropFlow stands on the work of many projects, including FFmpeg, qpdf, Tesseract OCR, Magick.NET (ImageMagick), PDFsharp, PdfPig,
PDFium/PDFiumCore, Open XML SDK, ClosedXML, Markdig, SharpCompress, ZXing.Net, Whisper.net (whisper.cpp), ONNX Runtime,
Velopack, CommunityToolkit.Mvvm, H.NotifyIcon, Next.js, React, Tailwind CSS, next-intl, pdf-lib, pdf.js and fflate.
The drag-and-wheel idea was inspired by Tangerine for macOS; DropFlow is an independent project and is not affiliated with it.
