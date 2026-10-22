# Status ledger — what is verified and what is not

Updated by hand whenever a milestone lands. **Verified** means an automated test or a real run on this machine
(Windows 10 Pro 19045, standard user) proved it. Anything else is **built, unverified**. Read this before claiming anything works.

Last updated: 2026-10-23 (English/Arabic translation: text, Markdown, HTML, subtitles, PDF text and pictures; earlier on 2026-10-22: lossless rotation, trim silence, multi-box redaction, strict target size, drag robustness, `dropflow doctor`; bundled engines, PDF/A, link import, release prep). Everything postponed on purpose, with the reason and how to resume, is in [deferred.md](deferred.md).

**Android app (`android/`, added 2026-10-22):** builds (debug + signed release), 46 JVM unit tests pass, lint 0 errors;
it has **not run on any device or emulator yet** (none available). Details and the full unverified list: [android.md](android.md).

## Test totals (`dotnet test DropFlow.sln`, dev PC with FFmpeg, qpdf, LibreOffice, Tesseract and four AI models present)

| Project | Passed | Skipped |
|---|---|---|
| DropFlow.Core.Tests | 361 | 0 |
| DropFlow.Hooks.Tests | 28 | 0 |
| DropFlow.Overlay.Tests | 98 | 0 |
| DropFlow.Engines.Common.Tests | 39 | 0 |
| DropFlow.Engines.Images.Tests | 206 | 0 |
| DropFlow.Engines.Media.Tests | 833 | 0 |
| DropFlow.Engines.Pdf.Tests | 733 | 1 (optional veraPDF check, needs `DROPFLOW_VERAPDF`) |
| DropFlow.Engines.Archive.Tests | 702 | 0 |
| DropFlow.AI.Tests | 406 | 3 (the u2netp, birefnet-lite and ultraface models are no longer in this PC's data folder since it moved to `DropFlow Data`) |
| DropFlow.Engines.Web.Tests | 18 | 0 |
| DropFlow.App.Tests | 162 | 0 |
| DropFlow.Cli.Tests | 37 | 0 |
| **Total (.NET, 2026-10-23)** | **3,623** | **4** |
| Web (`web/`) | 910 Vitest + 230 headless-Edge checks | 0 |

2026-10-23 (translation round): every .NET project in the table was re-run on this PC (AI with both Opus-MT models installed, so the real-model tests ran), none failed; the browser checks (230 headless-Edge) were not re-run. `dotnet build DropFlow.sln`: 0 errors. Vitest re-run after the catalog grew to 69 tools: 910 passed.

The Media, PDF and AI suites were also run with the **bundled** FFmpeg 9.0.2 essentials, qpdf 12.4.2 and Tesseract 5.4 first on PATH (the exact builds the installer ships): 712 + 692 + 293 passed, 0 skipped.
On 2026-10-22 the Media suite was run with the bundled FFmpeg essentials first on PATH: 772 of 774 passed, the 2 failures were wrong assumptions in the new tests themselves (fixed, then those integration tests passed with the bundled build); the full solution above ran with the full build.

Skips are visible in the test output (`[EngineFact]` / `[ModelFact]` / `[WebViewFact]`); they are never silent passes.

## Cross-platform libraries (2026-10-22, platform layer)

Core, Engines.Common/Images/Media/Pdf/Archive and AI build for Windows x64, Windows ARM64, macOS and Linux. **Verified:** Windows 10 x64 (this PC)
and Linux x64 (`mcr.microsoft.com/dotnet/sdk:10.0`, Ubuntu 24.04, FFmpeg 6.1.1, qpdf 11.9.0, Tesseract 5.3.4 + `ara`, Whisper tiny):

| Project | Windows x64 passed / skipped | Linux x64 passed / skipped |
|---|---|---|
| Core | 349 / 0 | 349 / 0 |
| Engines.Common | 37 / 0 | 37 / 0 |
| Engines.Images | 74 / 0 | 74 / 0 |
| Engines.Media | 712 / 0 | 712 / 0 |
| Engines.Pdf | 733 / 1 (veraPDF) | 728 / 6 (veraPDF, 3 LibreOffice, 2 headless-browser; none installed in the container) |
| Engines.Archive | 561 / 0 | 543 / 18 (7-Zip/WinRAR programs, 1 rename test that needs a non-root user) |
| AI | 308 / 3 (3 models not in the default folder) | 289 / 14 (Windows OCR/speech APIs, models) |

**First `build-cross.yml` run (2026-10-22):** everything built on Linux x64/arm64, macOS arm64 and Windows ARM64; the failures were test assumptions and are
fixed (portable shell/7z/`ps` probes, a deterministic rename-cancel test, short pipe names for the macOS socket-path limit, `[FfmpegFact]` skips for FFmpeg
builds without `subtitles`/`libvorbis`, and a clear "no 'subtitles' filter (built without libass)" message from the product). Re-run in Docker (Ubuntu 24.04,
7-Zip installed): Common 39/0, Core 361/0, Archive 697 passed / 4 skipped (RAR, non-root rename), Media 833/0; Windows x64: Common 39, Core 361, Archive 701, Media 833, all passed.
**macOS phone share is unverified:** `Stop_cuts_off_a_download_in_progress` and `A_cut_off_single_download_can_be_retried_but_a_finished_one_cannot` failed only on the
CI Mac (the client never saw the connection close; the server never noticed an abandoned download), pass on Windows and Linux, and could not be reproduced without a Mac.
They are `[MacOsUnverifiedFact]` (visible skip on macOS; `DROPFLOW_RUN_MAC_UNVERIFIED=1` runs them). The listening port closing on Stop is still tested on macOS.

**Unverified:** macOS (arm64/x64: the rest of the suite passes in CI except the two above), Linux arm64, Windows ARM64 (re-run `.github/workflows/build-cross.yml` to confirm the fixes); a real headless Chrome print (the
logic is tested with a fake browser; the real-browser tests skip when none is installed); the win-arm64 installer (`build-win-arm64.yml` was
never run; its engine pins were downloaded and hash-checked here); Apple Vision / WKWebView (the interfaces `IWindowsOcr` and `IHtmlPdfRenderer` are the plug points).
## What the catalog covers

`node catalog/build.mjs`: 428 conversions, 69 tools, 7 actions; every Tangerine tool and format target is covered by the catalog.
`RegistryCoverageTests` computes what has a converter: **all 69 Windows tools and all 419 conversions** (a test pins that there are no gaps).
New on 2026-10-23: `txt.translate` (subtitles + text, Markdown, HTML; `"alsoIn": ["document"]`), `img.translate`, `pdf.translate` (Windows only, not Tangerine parity).
New on 2026-10-22: `vid.rotate`, `img.rotate` and `aud.trim-silence` (the first tool with `"alsoIn": ["video"]`: it is also offered on the video wheel).

`pdf -> pdfa` (PDF/A-2b, built in-process, **veraPDF 1.30.3 says PASS**; pages become images, so text is not selectable) and `heic -> pdf`
(real HEVC fixture, BSD-3 licensed) were the last two gaps; both are closed and need no external program.

## Area by area

| Area | State | Evidence / gap |
|---|---|---|
| Catalog + Tangerine parity | Verified | `node catalog/build.mjs` |
| Landing page (ar/en, dark/light, mobile) | Verified | headless-Edge checks, screenshots; CTAs are inert "Soon" badges until a release exists |
| Core (catalog, registry, queue, wheel model, runner, settings, history, recipes, watch, CLI logic) | Verified | Core tests |
| Drag + key-tap state machine | Verified | Hooks tests (28), each rule mutation-checked |
| Drag robustness (2026-10-22): only real file drags open the wheel (a visible `SysDragImage` window, an OLE drag holding the mouse with a hidden window, or the wheel already got the files over OLE); the real button state ends a drag whose button-up the hook missed; `GetCursorPos` polling (20 ms, only while the button is down) counts movement that bypasses the hook; only a modifier pressed during the drag taps | **Rules verified as pure logic; the Win32 side is unverified** | `DragDetector` tests cover every rule. Not run with real input (this shell cannot inject it): whether `SysDragImage` is visible over every target app, the hidden-capture heuristic for OLE drags, remote desktop and pens. Escape hatch: `"RequireFileDrag": false` in settings.json (no Settings checkbox yet; label `settings.dragCheck` exists); every rejected tap is logged with what the checks saw |
| Wheel as a drop target | Hit-testing fixed | the wheel control answers for its whole square, so petals, gaps, hub and corners never alternate with the window background as the drag target (Overlay test) |
| Real mouse/keyboard hooks installing | Verified (installs, logs) | app log "input hooks installed" |
| Drag in Explorer / browser / other apps raising the wheel | **Reported working by the owner (2026-10-21, manual drag test on their PC)** | not witnessed by this tooling and no per-app/per-monitor detail recorded; Windows 11, multi-monitor and DPI above 100 % remain untested |
| Windows 11, multi-monitor, per-monitor DPI above 100 % | **Unverified** | needs hardware |
| Wheel window as OLE drop target, non-activating | Built; exercised by scripted mouse automation on this PC only | |
| Tray app, settings, history + undo, toasts, single instance, Explorer verbs, Send To | Verified by launching the real app and driving it | screenshots; context-menu verbs batching tested in Core |
| Settings: shortcuts, watched folders, engines list | Verified (window renders, values validate) | duplicate shortcuts rejected |
| Watched folders + recipes + clipboard shortcut | Core verified; end-to-end in the running app only partly exercised | |
| Images engine (Magick.NET) | Verified | 74 tests on generated images; HEIC can be read but not written |
| Media engine (FFmpeg) | **Verified against real FFmpeg 9.0.2 (Gyan full build) with its ffprobe**: all 712 tests, none skipped | found and fixed two bugs only a modern FFmpeg shows: progress jumping back to 0 on `out_time=N/A`, and "no audio track" not recognised because the cause is not FFmpeg's last error line. AV1/QSV/AMF encoders depend on this PC's hardware and are mostly unit-tested |
| Lossless video rotation (`vid.rotate`) | **Verified with the bundled FFmpeg 9.0.2 essentials and the full build** | `-display_rotation` input option + stream copy; ffprobe reads the new rotation back for MP4, MKV, MOV (and WebM, M4V by hand); the video packets hash identically before and after; AVI/WMV/FLV are not offered (they cannot store a rotation). Players that ignore the display matrix (some old ones) show the picture unturned |
| Trim silence (`aud.trim-silence`, audio and the sound of videos) | Verified against real FFmpeg | audio: `silenceremove` + `areverse` (start, end or both, 0.1 s kept), files over 10 min are measured and cut with `atrim` instead (memory); video: measured with `silencedetect`, then cut by stream copy so picture and sound stay in sync (the start snaps to a keyframe, so a little leading silence can remain); nothing to trim = no file and a translated note |
| Video redact: many boxes, each with its own time range | Verified against real FFmpeg | `boxes` option (compact text or the editor's JSON, documented in writing-a-converter.md); blur now pixelates (1/16, nearest-neighbour) before blurring, measured: the box loses over 75 % of its pixel-to-pixel detail; tags such as title and location are not carried over. The visual editor that fills `boxes` does not exist yet |
| Target size never ends above the target | Verified (fake FFmpeg for the loop, real FFmpeg for a reachable target) | up to 3 corrections at (target / actual) x 0.95; still too big = a clear failure and no file (before: delivered slightly over the limit with a note). Also fixed: a second correction scaled from the wrong base bitrate |
| Images: lossless JPEG rotation (`img.rotate`, `img.edit` when only turning/mirroring) | Verified | only the EXIF orientation changes (patched in place, or a minimal EXIF block is added); all 8 starting orientations x 7 operations match a real pixel rotation, and the compressed picture data is byte-identical. A JPEG whose EXIF block lacks the tag is re-encoded instead. Viewers that ignore EXIF orientation show it unturned |
| Images: compress never keeps a bigger file; target search | Verified | applies to BMP to PNG too; translated note `note.img.alreadySmall`; target size = 7-step quality search, then shrink by 0.8, at most 8 rounds |
| `dropflow doctor` | Verified on this PC | version, Windows and architecture, every helper program with path and version (LibreOffice read from the file, never started), the 13 FFmpeg encoders that matter, models, WebView2 Runtime version (registry). A listed GPU encoder may still fail without the hardware |
| Media editors (trim, split, speed, crop, save frames, redact, bleep, normalize, channels, join/merge order) | **Built; verified by STA tests with the real FFmpeg**: each editor opens on FFmpeg-made sample media, its strip (thumbnails or waveform) loads, it is rendered to PNG in en and ar and was looked at, and the option values it produces were run through the real converters (trim, split remove+join, speed, crop, frames, one-box redact, bleep, normalize, channel gain; join/merge return the new order) | **Unverified:** a person using them with mouse and keyboard on a real desktop (dragging handles, boxes and lane ranges, Space/arrow keys, A/B and solo listening through speakers), playback through the Windows player (the screenshots only show FFmpeg stills, because offscreen rendering does not capture the player), MKV/WebM falling back to stills on a PC without those codecs, long files (hour-long thumbnails/waveform timing), and 150 % DPI. Multi-box redact needs the `boxes` option in `vid.redact` (being added separately); until then the editor refuses a second box instead of dropping it |
| Media preview services (frames, thumbnail strip, waveform peaks, loudness reading, bleep/solo/playable renders) | Verified | unit tests of the argument builders and the peak reducer, and integration tests against the real FFmpeg (PNG size, waveform shape of silence/tone/silence, LUFS reading, previews written only to the scratch folder, cancellation) |
| `vid.split` remove clips / join the rest, `aud.channels` side gains | Verified | fake-runner tests (keyframe drift mapped by the segment CSV, numbering kept, impossible removals refused) and real-FFmpeg tests (6 s video minus the middle clip is 4 s, a -12 dB left gain measures 11-13 dB below the right) |
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
| Translation engine (Opus-MT English to Arabic and Arabic to English, ONNX Runtime on the CPU; `src/DropFlow.AI/Translation`) | **Verified with the real models** (`opus-mt-en-ar`, `opus-mt-ar-en`, int8 encoder + merged decoder, 108 MB each) | Real downloads from the pinned Hugging Face revisions through `dropflow models download <id> --yes` (SHA-256 of all 4 files per model checked); 20 real-model tests (loose keyword assertions on 5 sentences per direction, greedy/beam repeatability, batch = single, links/times/amounts kept, cancel, idle release, tokenizer incl. Arabic with diacritics). Speed on this PC: about 0.15 s (fast), 0.25 s (balanced), 0.37 s (best) per 25-word sentence, plus a 1.5 s model load. **Quality is that of Helsinki-NLP/Tatoeba models** (card BLEU: Arabic to English 49, English to Arabic 14): everyday sentences are good, names, idioms and long sentences are not; English to Arabic sometimes drops hamza ("ان"), see the samples below. int8 was compared with fp32 and uint8 on 10 sentences: int8 equals fp32, uint8 made visible mistakes. Tokenizer: Microsoft.ML.Tokenizers 2.0.1 `SentencePieceTokenizer` reads `source.spm` correctly (NFKC, ligatures, full-width, Arabic with diacritics; checked on real sentences) |
| Translation tools (`txt.translate`, `img.translate`, `pdf.translate`) | **Verified on real files on this PC with the real `dropflow` CLI** | SRT/VTT (numbers, timings, tags, `{\an8}`, speaker dashes and line counts kept; identity round trip is byte-exact), Markdown (code, links, front matter, tables kept), HTML (scripts, code, attributes kept; `lang`/`dir`/charset fixed), plain text (hard-wrapped paragraphs re-wrapped), PDF with a text layer (txt and md), PNG with English and with Arabic text (OCR then translation, optional overlay). 77 tests with a fake translator and fake OCR (plus 14 wheel-placement tests in App.Tests). Original files are never touched; outputs never overwrite (`(translated 2)`) |
| Translated text drawn on pictures (`overlay` option) | Verified by looking at the rendered PNGs (English poster to Arabic and Arabic sign to English) | The ImageMagick inside Magick.NET 15 lays out Arabic itself (Raqm is in its delegates), so letters connect and the line runs right to left; `ArabicShaper` (own shaping + bidi) is the fallback for a build without Raqm and is unit-tested. Unverified: photos with busy backgrounds, curved or rotated text, other fonts than Arial, macOS/Linux fonts |
| Model packs (a model of several files) | Verified | `ModelManager` handles `ModelInfo.Files`: one id/consent, per-file size + SHA-256, resume per file, delete removes the folder (10 tests against a local server) |
| Translation: unverified | | the model-consent window with a real click (it is the existing dialog; only its text and the CLI were exercised), the Settings model list showing the packs, Windows 11, scanned/photographed documents, files over 50 MB (refused), mixed-language files beyond skipping paragraphs already in the target language |
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
| External audit (Kashef, kashef.scouthub.dev) | **Security 91 → 92 (A-), site readiness 77 → 94** after the fixes (2026-10-21) | left: CSP `script-src` still allows `unsafe-inline` (static Next.js pages need nonces/hashes for that), mobile LCP 4.2 s, about 56 KiB unused framework JS; DNSSEC and DMARC belong to the scouthub.dev domain, not to this repo |
| Accounts / payments | Deferred on purpose | ADR-0005, Phase 9 |

### Translation samples (2026-10-23, real models, quality "balanced"; copied from real runs, not edited)

| Direction | Source | Result |
|---|---|---|
| en to ar | Hello, how are you today? | مرحباً، كيف حالك اليوم؟ |
| en to ar | Where is the nearest hospital? | أين أقرب مستشفى؟ |
| en to ar | Click the Save button to store your changes before closing the window. | انقر على زر الحفظ لتخزين التغييرات قبل إغلاق النافذة. |
| en to ar | This application converts files without sending them to the internet. | هذا التطبيق يحوّل الملفات دون إرسالها إلى الإنترنت. |
| en to ar | Please restart your computer to finish installing the update. | الرجاء إعادة تشغيل حاسوبك لإنهاء تثبيت التحديث. |
| en to ar (weak) | We offer 50% off all items. | نعرض 50% من جميع البنود. (the word "discount" is lost) |
| en to ar (weak) | Change the format of a file | غيّر تنسيق a ملفّ (an English "a" left in) |
| ar to en | مرحباً، كيف حالك اليوم؟ | Hey, how are you today? |
| ar to en | أين أقرب مستشفى؟ | Where's the nearest hospital? |
| ar to en | اضغط على زر الحفظ لتخزين التغييرات قبل إغلاق النافذة. | Press the save button to store the changes before closing the window. |
| ar to en | يقوم هذا التطبيق بتحويل الملفات دون إرسالها إلى الإنترنت. | This application converts files without sending them online. |
| ar to en | يرجى إعادة تشغيل الحاسوب لإكمال تثبيت التحديث. | Please reboot the computer to complete the installation of the upgrade. |

Real files: an English subtitle file (`Hello, how are you today?`, a two-line cue, an `<i>` cue, a two-speaker cue, `{\an8}`, a link) and an Arabic one were translated with the CLI;
structure came back identical (see `tests/DropFlow.AI.Tests/RealTranslationTests.cs`). A 300-cue English subtitle file took 44 s (fast), 76 s (balanced), 110 s (best).

## macOS and Linux app (`src/DropFlow.Desktop`, 2026-10-22) — details in [desktop-mac-linux.md](desktop-mac-linux.md)

| Piece | Verified | Not verified |
|---|---|---|
| Pure logic (drag decisions, GNOME line protocol, session detection, Unix-socket single instance, XDG/macOS folders, engine search, install hints, login items, file-manager entries, trash, runner, strings ar/en) | **169 tests pass on Windows and on Linux** (Docker, .NET 10 SDK image) | on macOS (the CI workflow runs them there) |
| Builds | `dotnet publish` for linux-x64, osx-arm64 from Windows; linux-x64 + linux-arm64 in Docker | osx-x64 and the .app/DMG (needs a Mac: `build-macos.yml`) |
| Linux packages | built in Docker with nfpm: `.deb` amd64/arm64, `.rpm` x86_64/aarch64, AppImages; metadata and contents inspected, `.deb` installed with `dpkg`, app started from `/usr/bin`, AppImage run with extract-and-run, bundled FFmpeg 9.0.2 runs | install on a real Ubuntu/Fedora desktop with dependency resolution; arm64 binaries never executed |
| Windows (wheel, options, settings, welcome, toast, "+ Add") | rendered under Xvfb in English and Arabic (RTL) and looked at | on a real desktop with a compositor |
| Start-up | under Xvfb: app starts, X11 watcher starts, a second launch forwards `--wheel FILE` over the socket, `--quit` stops it | tray icon (needs a StatusNotifier host) |
| Drag detection | — | **everything**: no real drag on X11, GNOME Wayland (extension) or macOS (drag pasteboard); the wheel as a drop target from Finder/Nautilus/Dolphin |
| macOS specifics | — | **nothing ran on a Mac**: NSWindow level/collection behaviour, `orderFrontRegardless`, Cocoa placement, SMAppService/LaunchAgent, `~/.Trash` undo, Gatekeeper first-launch steps, bundled FFmpeg under quarantine |
| Missing on macOS/Linux | | AI tools, HTML → PDF, the Encoder window, share and send-to-phone actions; PDF page rendering on macOS (PDFiumCore has no macOS natives) |

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

- Run `docs/phase0-checklist.md` by hand (the drag-detection risk is still open). Since 2026-10-22 the wheel only opens for real file drags:
  also try a drag from Explorer, the desktop and a browser download, plus Shift+drag text selection in an editor (must NOT open the wheel). If
  the wheel never opens, set `"RequireFileDrag": false` in `%LOCALAPPDATA%\DropFlow Data\settings.json` and send the log lines "tap ignored".
- Say yes or no to downloading: a modern FFmpeg, qpdf, LibreOffice (via the in-app installer, which asks first) and the AI models.
- Decide when the repository becomes public (the release workflow and the landing page buttons depend on it).
- (Done) Vercel is linked; the owner may still want the connector granted so deployments can be inspected from here (it returned 403).
