# Status ledger — what is verified and what is not

Updated by hand whenever a milestone lands. **Verified** means an automated test or a real run on this machine
(Windows 10 Pro 19045, standard user) proved it. Anything else is **built, unverified**. Read this before claiming anything works.

Last updated: 2026-10-21.

## Test totals (`dotnet test DropFlow.sln`)

| Project | Passed | Skipped | Skipped because |
|---|---|---|---|
| DropFlow.Core.Tests | 230 | 0 | |
| DropFlow.Hooks.Tests | 16 | 0 | |
| DropFlow.Overlay.Tests | 76 | 0 | |
| DropFlow.Engines.Common.Tests | 23 | 0 | |
| DropFlow.Engines.Images.Tests | 74 | 0 | |
| DropFlow.Engines.Media.Tests | 659 | 52 | `[EngineFact(Ffmpeg)]` — no ffmpeg on PATH |
| DropFlow.Engines.Pdf.Tests | 461 | 6 | qpdf (3) and LibreOffice (3) not installed |
| DropFlow.Engines.Archive.Tests | 561 | 0 | real `7z.exe` and `Rar.exe` exist on this machine; LAN tests need a LAN address and a non-elevated session |
| DropFlow.AI.Tests | 282 | 7 | Whisper/ONNX models and ffmpeg not present |
| DropFlow.Engines.Web.Tests | 9 | 0 | real WebView2 runtime 155 |
| DropFlow.App.Tests | 14 | 0 | |
| DropFlow.Cli.Tests | 10 | 0 | |
| **Total** | **2,380** | **65** | |

Skips are visible in the test output (`[EngineFact]` / `[ModelFact]` / `[WebViewFact]`); they are never silent passes.

## What the catalog covers

`node catalog/build.mjs`: 428 conversions, 63 tools, 7 actions; every Tangerine tool and format target is covered by the catalog.
`RegistryCoverageTests` computes what has a converter: **59 of 63 Windows tools and 417 of 419 conversions** (a test pins the exact gaps):

| Gap | Why |
|---|---|
| `pdf.crop`, `pdf.sign`, `pdf.redact`, `pdf.compare` | Phase 4 PDF tools, being added by an agent now |
| `heic -> pdf` | no way to create a HEIC test file here, so it is not claimed |
| `pdf -> pdfa` | no trustworthy engine chosen (Ghostscript was dropped: not in winget, AGPL) |

## Area by area

| Area | State | Evidence / gap |
|---|---|---|
| Catalog + Tangerine parity | Verified | `node catalog/build.mjs` |
| Landing page (ar/en, dark/light, mobile) | Verified | headless-Edge checks, screenshots; CTAs are inert "Soon" badges until a release exists |
| Core (catalog, registry, queue, wheel model, runner, settings, history, recipes, watch, CLI logic) | Verified | Core tests |
| Drag + key-tap state machine | Verified | Hooks tests |
| Real mouse/keyboard hooks installing | Verified (installs, logs) | app log "input hooks installed" |
| Drag in Explorer / browser / other apps raising the wheel | **Unverified** | Phase 0 manual checklist (`docs/phase0-checklist.md`) never run; needs a human dragging files |
| Windows 11, multi-monitor, per-monitor DPI above 100 % | **Unverified** | needs hardware |
| Wheel window as OLE drop target, non-activating | Built; exercised by scripted mouse automation on this PC only | |
| Tray app, settings, history + undo, toasts, single instance, Explorer verbs, Send To | Verified by launching the real app and driving it | screenshots; context-menu verbs batching tested in Core |
| Settings: shortcuts, watched folders, engines list | Verified (window renders, values validate) | duplicate shortcuts rejected |
| Watched folders + recipes + clipboard shortcut | Core verified; end-to-end in the running app only partly exercised | |
| Images engine (Magick.NET) | Verified | 74 tests on generated images; HEIC can be read but not written |
| Media engine (FFmpeg) | Planner/arguments verified; real runs only on an **old FFmpeg 4.0 build** found inside another vendor's app | the Gyan FFmpeg 7+/8 build and a real ffprobe were never run; AV1/QSV/AMF unit-tested only |
| DropFlow Encoder window | Settings, presets, plan preview verified (every preset round-trips through the controls; renders in ar/en) | no real encode was run through the window (no ffmpeg) |
| Subtitles (SRT/VTT/ASS/TXT) | Verified | pure C#, real file tests incl. Arabic and windows-1256 |
| PDF engine, managed tools | Verified | PDFsharp/PdfPig/PDFium round-trip tests incl. Arabic |
| PDF via qpdf (protect/unlock/repair/compress) | **Unverified against the real tool** | argument builders + fake runner only |
| Office conversions via LibreOffice (113 pairs) | **Unverified against the real tool** | filters and flags written from knowledge, fake runner only |
| Word/PowerPoint/Excel opening the produced DOCX/PPTX/XLSX | **Unverified** | files pass OpenXml validation, never opened in Office |
| HTML -> PDF (built-in WebView2) | Verified | 9 tests: A4/Letter/landscape sizes read back with PdfPig, Arabic path, no overwrite, network blocked unless allowed |
| Archives (zip/tar/gz/7z/rar-read), hash, Base64, batch rename, QR, ICO/favicons | Verified | 526 tests incl. zip-slip and zip-bomb cases, real 7-Zip and WinRAR files |
| Send to phone (QR) | Window verified; server (Kestrel, no administrator rights needed) verified: bind, serve and download through the PC's own LAN address, token/traversal/limits/stop | **Unverified:** a real phone, the first-time Windows Firewall prompt (cancelling it silently blocks phones; this PC has inbound Block rules for `testhost.exe` created that way), Wi-Fi AP isolation, VPNs, IPv6-only networks |
| Speech to text (Whisper) | Plumbing verified; **real inference never run** | no model downloaded (needs the owner's consent) |
| OCR | Windows OCR (en-US, ar-SA) and Tesseract (English) verified for real | Arabic via Tesseract needs `ara.traineddata` (not installed) |
| Background removal, face redaction | Pipeline verified with hand-written ONNX models | the real models were never run; quality and recall unmeasured |
| Model downloads | Verified against a local test server (resume, hash mismatch, cancel) | the real Hugging Face URLs answered a 1-byte Range request but no model was downloaded |
| Installer / updates (Velopack), CI, release workflow, winget manifests | Written | `vpk pack` and the GitHub workflows were never run; no release exists |
| Web app (in-browser, `web/`) | In progress (agent) | see below when merged |
| Vercel deployment | **Not linked** | connector got 403 on the owner's scope; import the repo with Root Directory `web` |
| Accounts / payments | Deferred on purpose | ADR-0005, Phase 9 |

## Things that need the owner

- Run `docs/phase0-checklist.md` by hand (the drag-detection risk is still open).
- Say yes or no to downloading: a modern FFmpeg, qpdf, LibreOffice (via the in-app installer, which asks first) and the AI models.
- Decide when the repository becomes public (the release workflow and the landing page buttons depend on it).
- Grant the Vercel connector access or import the project in the dashboard.
