# Status ledger — what is verified and what is not

Updated by hand whenever a milestone lands. **Verified** means an automated test or a real run on this machine
(Windows 10 Pro 19045, standard user) proved it. Anything else is **built, unverified**. Read this before claiming anything works.

Last updated: 2026-10-21 (third pass: bundled engines, PDF/A, link import, release prep). Everything postponed on purpose, with the reason and how to resume, is in [deferred.md](deferred.md).

## Test totals (`dotnet test DropFlow.sln`, dev PC with FFmpeg, qpdf, LibreOffice, Tesseract and four AI models present)

| Project | Passed | Skipped |
|---|---|---|
| DropFlow.Core.Tests | 326 | 0 |
| DropFlow.Hooks.Tests | 16 | 0 |
| DropFlow.Overlay.Tests | 83 | 0 |
| DropFlow.Engines.Common.Tests | 26 | 0 |
| DropFlow.Engines.Images.Tests | 74 | 0 |
| DropFlow.Engines.Media.Tests | 712 | 0 |
| DropFlow.Engines.Pdf.Tests | 713 | 1 (optional veraPDF check, needs `DROPFLOW_VERAPDF`) |
| DropFlow.Engines.Archive.Tests | 561 | 0 |
| DropFlow.AI.Tests | 293 | 0 |
| DropFlow.Engines.Web.Tests | 18 | 0 |
| DropFlow.App.Tests | 45 | 0 |
| DropFlow.Cli.Tests | 18 | 0 |
| **Total (.NET)** | **2,885** | **1** |
| Web (`web/`) | 489 Vitest + 230 headless-Edge checks | 0 |

The Media, PDF and AI suites were also run with the **bundled** FFmpeg 9.0.2 essentials, qpdf 12.4.2 and Tesseract 5.4 first on PATH (the exact builds the installer ships): 712 + 692 + 293 passed, 0 skipped.

Skips are visible in the test output (`[EngineFact]` / `[ModelFact]` / `[WebViewFact]`); they are never silent passes.

## What the catalog covers

`node catalog/build.mjs`: 428 conversions, 63 tools, 7 actions; every Tangerine tool and format target is covered by the catalog.
`RegistryCoverageTests` computes what has a converter: **all 63 Windows tools and all 419 conversions** (a test pins that there are no gaps):

`pdf -> pdfa` (PDF/A-2b, built in-process, **veraPDF 1.30.3 says PASS**; pages become images, so text is not selectable) and `heic -> pdf`
(real HEVC fixture, BSD-3 licensed) were the last two gaps; both are closed and need no external program.

## Area by area

| Area | State | Evidence / gap |
|---|---|---|
| Catalog + Tangerine parity | Verified | `node catalog/build.mjs` |
| Landing page (ar/en, dark/light, mobile) | Verified | headless-Edge checks, screenshots; CTAs are inert "Soon" badges until a release exists |
| Core (catalog, registry, queue, wheel model, runner, settings, history, recipes, watch, CLI logic) | Verified | Core tests |
| Drag + key-tap state machine | Verified | Hooks tests |
| Real mouse/keyboard hooks installing | Verified (installs, logs) | app log "input hooks installed" |
| Drag in Explorer / browser / other apps raising the wheel | **Reported working by the owner (2026-10-21, manual drag test on their PC)** | not witnessed by this tooling and no per-app/per-monitor detail recorded; Windows 11, multi-monitor and DPI above 100 % remain untested |
| Windows 11, multi-monitor, per-monitor DPI above 100 % | **Unverified** | needs hardware |
| Wheel window as OLE drop target, non-activating | Built; exercised by scripted mouse automation on this PC only | |
| Tray app, settings, history + undo, toasts, single instance, Explorer verbs, Send To | Verified by launching the real app and driving it | screenshots; context-menu verbs batching tested in Core |
| Settings: shortcuts, watched folders, engines list | Verified (window renders, values validate) | duplicate shortcuts rejected |
| Watched folders + recipes + clipboard shortcut | Core verified; end-to-end in the running app only partly exercised | |
| Images engine (Magick.NET) | Verified | 74 tests on generated images; HEIC can be read but not written |
| Media engine (FFmpeg) | **Verified against real FFmpeg 9.0.2 (Gyan full build) with its ffprobe**: all 712 tests, none skipped | found and fixed two bugs only a modern FFmpeg shows: progress jumping back to 0 on `out_time=N/A`, and "no audio track" not recognised because the cause is not FFmpeg's last error line. AV1/QSV/AMF encoders depend on this PC's hardware and are mostly unit-tested |
| DropFlow Encoder window | Settings, presets, plan preview verified (every preset round-trips through the controls; renders in ar/en) | no real encode was run through the window (no ffmpeg) |
| Subtitles (SRT/VTT/ASS/TXT) | Verified | pure C#, real file tests incl. Arabic and windows-1256 |
| PDF engine, managed tools | Verified | PDFsharp/PdfPig/PDFium round-trip tests incl. Arabic |
| PDF crop (CropBox only), visible signature, true redaction, compare | Verified | 223 tests: PDFium renders prove ink/black boxes at the right place for all four page rotations; redact is checked three ways (PdfPig finds none of the word, pixels are black, raw bytes incl. inflated streams do not contain it); mutations caught |
| PDF sign | Visible stamp only, **not** a cryptographic signature (the result says so) | |
| PDF redact limits | Terms use the text layer only (scans need regions; no OCR); rasterized pages lose selectable text, links and form fields; peak memory on huge pages not measured | |
| PDF crop | Non-destructive: cropped-away content is still in the file (use redact for privacy) | |
| PDF via qpdf 12.4.2 (protect/unlock/repair/compress) | Verified against the real tool | the 3 qpdf integration tests pass; the locator now finds the versioned `Program Files\qpdf 12.4.2` folder |
| Office conversions via LibreOffice (113 pairs) | **Partly verified** against the real program | 3 real tests pass (Arabic txt to PDF, DOCX to PDF/ODT, CSV to PDF and MD to EPUB); the other pairs rely on the same filters but were not each run |
| Documents to PDF/PNG/JPG without LibreOffice | Verified | HTML, text, Markdown, Word, spreadsheets, CSV/JSON and EPUB print through the built-in WebView2 browser (ADR-0011); tests run with no engine installed. Fidelity is that of a web page, not of Word |
| Helper-program install prompts | Off by default | `AppSettings.OfferEngineInstalls`; a missing program is explained and the Install buttons stay in Settings |
| Word/PowerPoint/Excel opening the produced DOCX/PPTX/XLSX | **Unverified** | files pass OpenXml validation, never opened in Office |
| HTML -> PDF (built-in WebView2) | Verified | 9 tests: A4/Letter/landscape sizes read back with PdfPig, Arabic path, no overwrite, network blocked unless allowed |
| Archives (zip/tar/gz/7z/rar-read), hash, Base64, batch rename, QR, ICO/favicons | Verified | 526 tests incl. zip-slip and zip-bomb cases, real 7-Zip and WinRAR files |
| Send to phone (QR) | Window verified; server (Kestrel, no administrator rights needed) verified: bind, serve and download through the PC's own LAN address, token/traversal/limits/stop | **Unverified:** a real phone, the first-time Windows Firewall prompt (cancelling it silently blocks phones; this PC has inbound Block rules for `testhost.exe` created that way), Wi-Fi AP isolation, VPNs, IPv6-only networks |
| Speech to text (Whisper) | **Arabic measured** on 20 real clips of Google FLEURS `ar_eg` test (233 s of speech, CC-BY-4.0; https://huggingface.co/datasets/google/fleurs), normalised word/character error: `ggml-tiny` 61.6 % / 21.0 %, `ggml-small` 23.4 % / 7.3 %, **`ggml-large-v3-turbo-q5_0` 8.8 % / 2.5 %** (CPU on the dev PC: small about 0.65x real time, turbo about 3x real time) | Egyptian-accent read speech only; noisy audio, dialects and long files not measured. `auto` already prefers the largest installed model the RAM allows; recommend turbo for Arabic |
| OCR | Windows OCR (en-US, ar-SA) and Tesseract (English) verified for real | Arabic via Tesseract needs `ara.traineddata` (not installed) |
| Background removal, face redaction | **Real models run**: `u2netp`, `birefnet-lite`, `ultraface-rfb-320` (all 293 AI tests, none skipped) | cut-out quality and face recall on real photos are not measured |
| Model downloads | Verified: against a local test server (resume, hash mismatch, cancel) **and** from the real pinned Hugging Face URLs through `dropflow models download <id> --yes` (SHA-256 checked) | |
| `dropflow models` (CLI) | Verified | list / download / delete; nothing downloads without `--yes` (6 tests). The CLI now targets Windows and also carries the AI tools |
| Helper programs bundled in the installer | **Built and measured**: FFmpeg 9.0.2, qpdf 12.4.2, Tesseract 5.4 + English/Arabic data run from `engines\` next to the app | `tools/fetch-engines.ps1` pins versions and SHA-256; `EngineLocator` checks `engines\` first; the published CLI found all three with PATH cleaned. Installer 264 MB. FFmpeg is GPL-3.0 as a separate program: needs a lawyer's review (ADR-0012) |
| OCR for Arabic | **Measured** on 3 synthetic Arabic images (Arial, Tahoma, Segoe UI; 137 characters each): bundled Tesseract `ara` character error 0.0-0.7 %, Windows OCR (ar-SA) 2.2-7.3 % | clean computer-rendered text only; scans, photos and handwriting are not measured. `auto` still prefers Windows OCR when its pack exists |
| Open from link (Windows app) | Core verified (local test server: names, redirects, size cap, cancel, no overwrite); window verified by STA tests | the tray item and the Ctrl+Alt+V flow were never run with real input. Not available in the browser (CORS + strict CSP) |
| Engine install window | **Real bug found in the owner's run log and fixed** (UI-thread crash while winget output arrived), test fails without the fix | |
| Privacy Policy and Terms (web, ar/en) | Built and checked (200, lang/dir, mobile, footer links, no cookies: the locale cookie was turned off) | drafts: a lawyer should review liability and governing-law wording |
| Installer / updates (Velopack), CI, release workflow | **Release v0.1.0 is live**; CI green; installer built by `release.yml` with bundled engines | Setup 263 MB, unsigned (SmartScreen warns). The in-app updater, winget manifests and a clean install on a fresh PC were not exercised. `windows-smoke.yml` passed on windows-2022 and windows-2025 (build 26100): the app starts and the hooks install; no real drag, no 150 % scaling |
pm ci, which rejects a Windows-made lockfile on Linux, so CI uses 
pm install) was **green on all three jobs**. One WebView2 test (Never_overwrites_an_earlier_output) failed once on the GitHub runner with no message and passed on the next run; it now reports which run failed, so treat it as a possible flake and look at that message if it recurs. `vpk pack` was never run and no release exists |
| Web app (in-browser, `/app`, `web/`) | Verified in headless Edge | 206 browser checks run by its author (real file inputs, synthesized drags with Shift/Ctrl/Alt, real downloads re-parsed, offline reload), 834 Vitest tests (169 run the real ffmpeg core in Node); lint, production build, tests and `npm audit --omit=dev` (0) re-run here. No-upload proof: static scan finds no fetch/XHR/beacon/WebSocket in app code, and the `/app` CSP only allows self/blob/data |
| Web app gaps | **Unverified** | a real OS drag from Explorer with Alt/Shift/Ctrl; Firefox, Safari, phones; folder drop and AVIF/SVG/GIF/ICO inputs in e2e; files over ~500 MB; PWA install prompt; the service worker's own requests are not network-audited; `public/sw.js` is generated after `next build` and is assumed to be collected by Vercel. HEIC/TIFF input, GIF output, Office, OCR, Whisper, protect/unlock/sign/redact are not offered in the browser (a hint tile points to the Windows app) |
| Vercel deployment | **Live and checked from outside** (https://dropflow.scouthub.dev): landing with working Windows (GitHub release) and Web buttons, `/en|ar/privacy`, `/en|ar/terms`, `/en|ar/app` all 200 | the Vercel MCP connector still gets 403 for the team, so build logs are unread; `noindex` is still set |
| Accounts / payments | Deferred on purpose | ADR-0005, Phase 9 |

## End-to-end acceptance run (2026-10-21, real `dropflow` CLI, real files, no engines installed)

A script generated a PNG, an Arabic SRT, a hand-written one-page text PDF and a folder, then ran 26 commands through the real registry:
25 succeeded and wrote the expected files, including png to jpg/webp/ico/bmp/pdf, compress/resize/hash/Base64, srt to vtt/txt (Arabic kept),
zip, PDF watermark/rotate/page numbers/metadata/crop/sign/redact/merge, and pdf to txt/jpg/docx/epub. Re-running a conversion wrote
`(converted 2)` and the originals were unchanged. Visual checks of the rendered output: the redacted page shows black boxes over
"secret code" and "482913" with everything else intact, and the bytes of the redacted file contain none of the redacted words (the
original does); the visible signature sits bottom-right at about 45 mm in a script font with the date under it and no white box.

- The one failure: `pdf.compress` at level `extreme` was refused with "Qpdf is required but not installed" although that level does not need
  it. **Fixed afterwards:** `IConverter` has an option-aware `EngineFor(operation, options)`, the operation runner now asks for options
  before checking the engine, the CLI checks with the given options, and PDF specs can declare that a choice needs no engine (tests in
  Core, PDF and CLI). "Compare PDFs" is now offered for exactly two files (catalog `maxFiles`).
- `img.strip-gps` on a file without location data reports that and writes nothing (by design).
- Not covered by this run: anything needing FFmpeg, qpdf, LibreOffice, models or the UI; the drag flow.

## Input-injection limit of this environment

The shell this work runs in ignores injected mouse and keyboard input (`SendInput`/`mouse_event` return success but change nothing; the
cursor only moves with `SetCursorPos`), so the Phase 0 drag test could not be scripted here. The owner chose to postpone running it
outside the sandbox; it remains an open risk until `docs/phase0-checklist.md` is run on a real desktop.

## Things that need the owner

- Run `docs/phase0-checklist.md` by hand (the drag-detection risk is still open).
- Say yes or no to downloading: a modern FFmpeg, qpdf, LibreOffice (via the in-app installer, which asks first) and the AI models.
- Decide when the repository becomes public (the release workflow and the landing page buttons depend on it).
- (Done) Vercel is linked; the owner may still want the connector granted so deployments can be inspected from here (it returned 403).


