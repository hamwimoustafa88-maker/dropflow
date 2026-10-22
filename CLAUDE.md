# DropFlow

An enhanced Windows 10/11 + web counterpart of Tangerine (tangerineformac.com): drag a file, tap a key,
and a radial wheel appears to convert, process, or act on files 100% locally — no uploads, on desktop or in the browser.
License: **GPL-3.0-or-later — free and open source** (owner's decision 2026-10-21, ADR-0013, supersedes the proprietary ADR-0010).
The repository is public and open to contributors; the DropFlow name/logo are reserved (TRADEMARK.md). Audience includes Arab developers. Audience: Arabic + English users.

**Status (2026-10-21):** The owner told us to build all remaining phases without waiting. The Phase 0 manual gate
(`docs/phase0-checklist.md`) was our own rule; the owner reports the drag test PASSED on their PC (2026-10-21, per-row results not recorded; Windows 11 and
multi-monitor/DPI untested) — remaining input-capture risk is tracked in `docs/status.md`, which is the honest
verified-vs-unverified ledger: read it before claiming anything works. Landing page (`web/`, ar/en, static, noindex) is done; CTAs
are inert "Soon" badges until a release exists. GitHub: public repo `hamwimoustafa88-maker/dropflow` (remote `origin`, branch `main`); release v0.1.0 is live (installer 263 MB, unsigned). Vercel: LIVE at https://dropflow.scouthub.dev (project `moustafa-hamwis-projects/dropflow`, checked from outside 2026-10-21); the MCP connector still gets 403 on the owner's scope, so builds/logs can't be read from here. No Vercel CLI login on this machine. .NET 10 SDK 10.0.402 is
installed (new terminals may need a restart to see it on PATH). The dev machine now has FFmpeg 9.0.2, qpdf 12.4.2, LibreOffice, Tesseract and
four AI models, and the installer ships FFmpeg, qpdf and Tesseract (+ Arabic data) inside (ADR-0012): users never download a program.
`tools/fetch-engines.ps1` fetches them at build time (never commit binaries); engine-dependent tests skip visibly via `[EngineFact]` / `[ModelFact]`
when a program is missing.
**Read before any task:** `docs/plan.md` (roadmap, Arabic), `docs/feature-catalog.md` (generated tool list),
`docs/reference/tangerine-features.md` (parity baseline — DropFlow must never ship less than Tangerine in a category) and
`docs/writing-a-converter.md` (the contract every converter follows).

## Stack
- Desktop: C# / .NET 10, WPF with DropFlow's own theme (WPF-UI was not adopted), CommunityToolkit.Mvvm, H.NotifyIcon (tray), Velopack (updates)
- Engines: FFmpeg (GPL, separate bundled program), Magick.NET, qpdf, PDFiumCore, PDFsharp, PdfPig (PDF/A and HEIC to PDF are in-process; Ghostscript is never used), LibreOffice headless (optional),
  DocumentFormat.OpenXml, ClosedXML, WebView2, SharpCompress/7-Zip
- Local AI: Whisper.net, Windows.Media.Ocr + Tesseract, ONNX Runtime
- Web: Next.js + TypeScript + Tailwind + next-intl on Vercel (https://dropflow.scouthub.dev); in-browser pdf-lib, pdf.js, fflate
  (ffmpeg.wasm was removed: GPL)
- Tests: xUnit with plain `Assert` (desktop — FluentAssertions 8+ is commercially licensed, do not add it), Vitest + Playwright (web) · Installer: Velopack
- Android (`android/`): Kotlin + Jetpack Compose / Material 3, AGP 9.4.1, Gradle 9.8.2 wrapper, compile/target SDK 36, minSdk 29;
  Media3 (Transformer/ExoPlayer), PdfBox-Android, ZXing, AndroidX exifinterface/heifwriter/documentfile; JUnit 4 JVM tests. No internet permission.

## Repository map
| Path | Responsibility |
|---|---|
| catalog/tools.json | Single source of truth for all tools/conversions (read by desktop, web, landing) |
| catalog/build.mjs | Validates the catalog, checks Tangerine parity, regenerates docs/feature-catalog.md |
| DropFlow.sln | Desktop solution |
| src/DropFlow.Core | Catalog loader, `IConverter`/`ConversionJob`, `ToolRegistry`, `JobQueue`, `WheelBuilder`, `OutputPathResolver`, settings, history (no UI, no engines) |
| src/DropFlow.Engines.Common | `ProcessRunner`, `EngineLocator`, winget-based `EngineInstaller` |
| src/DropFlow.Engines.{Images,Media,Pdf,Archive} | Converters per domain; each exposes `XModule.Create(IEngineProvider)` (split from the planned single Engines project for isolation). Media also has `MediaEncoder` + `EncoderPlanner`/presets for the Encoder window and `MediaPreview` (stills, waveform, loudness, listening renders) for the editors; Archive also has `PhoneShareServer`; Pdf pins PDFsharp to 6.2.4 (6.5+ is AGPL) |
| src/DropFlow.Engines.Web | `HtmlToPdfTool`: HTML → PDF in one reused hidden WebView2 window (network blocked unless the job allows it); needs the UI dispatcher |
| src/DropFlow.AI | Whisper speech to text, OCR, background removal, auto-redaction, `ModelManager` (resumable, SHA-256 verified; downloads only after the app asks the user) (net10.0-windows10.0.19041.0) |
| src/DropFlow.Hooks | `DragDetector` (pure drag + modifier-tap state machine), `LowLevelInputHook` (WH_MOUSE_LL/WH_KEYBOARD_LL on a dedicated thread, polls button + cursor while the button is down) and `ShellDragProbe` (is a real file drag in progress?) |
| src/DropFlow.Overlay | `WheelWindow` (non-activating topmost OLE drop target), `WheelControl`/`WheelGeometry`, option form, floating shelf |
| src/DropFlow.App | WPF host (`AppHost`): tray, settings, history, toasts, hooks → wheel → `OperationRunner`, watched folders, global shortcuts, update check; `app.manifest` = per-monitor DPI v2, asInvoker. `Views/Editors/Media`: dedicated media editors (`MediaEditors` = `IOptionsEditor`; `MediaSession` player, `MediaTimeline`) |
| src/DropFlow.Cli | `dropflow` CLI (assembly name `dropflow`): list / convert / recipe / models / doctor commands over the same Core |
| src/DropFlow.Spike | Phase 0 throwaway WPF app: log window + transparent drop-target wheel |
| installer/ · .github/workflows/ | Velopack packaging (`installer/build.ps1`), winget manifest templates, CI + release workflows (docs/release.md) |
| src/DropFlow.* (planned) | Encoder window, Shell — created as their phases start |
| tests/DropFlow.TestSupport | Sample-file generators (`Samples`), `TempDir`, `[EngineFact]` (skips when an engine is missing) |
| tests/*.Tests | xUnit tests, one project per src project. `DropFlow.App.Tests` is cross-module: catalog conformance of every converter, no overlapping converters, option-label coverage in ar/en, the known-gaps ratchet, Encoder and phone-share windows |
| web/ | Next.js 16: landing (`/ar`, `/en`), web app (`/app`, Phase 6), PWA. Copy in `web/messages/`, tokens in `src/app/globals.css`, data in generated `src/data/catalog.json` |
| android/ | Android app: share target `WheelActivity` → wheel (`ui/WheelView`, pure `wheel/WheelGeometry`) → `work/WorkService` (foreground) → `ops/*` → MediaStore (`work/Publisher`). Tool table `catalog/AndroidTools.kt`; labels generated from catalog/tools.json at build time (`generateCatalog`). See docs/android.md |
| tests/ | Desktop tests; tests/samples holds one sample per format |
| tools/ | Scripts that download engine binaries (never commit binaries) |
| docs/ | plan.md, feature-catalog.md (generated), adr/, reference/ (Tangerine baseline + screenshots), archive/ |

## Commands
- Catalog: `node catalog/build.mjs` (run after any edit to catalog/tools.json; must print OK)
- Desktop: `dotnet build DropFlow.sln` · `dotnet test` · `dotnet run --project src/DropFlow.Spike` (single test: `dotnet test --filter "FullyQualifiedName~Name"`)
- Look at a window without clicking: `DropFlow.exe --snapshot-window <edit|annotate|crop|redact|background|qr|metadata|welcome|settings|encoder|history> out.png [--lang ar|en] [--dark] [--page N] [files…]` or `--snapshot-wheel <convert|tools|actions> out.png <files…>` (2x PNG, exits)
- Web: `cd web && npm install && npm run dev` · `npm run lint` · `npm run build` (Next 16 differs from older versions: read `web/node_modules/next/dist/docs/` first)
- Android: `cd android && ./gradlew testDebugUnitTest assembleDebug assembleRelease` (`gradlew.bat` on Windows; release signing from
  `DROPFLOW_KEYSTORE*` env vars or `dropflow.*` Gradle properties, see android/README.md; CI: `.github/workflows/build-android.yml`)

## Architecture rules
- Every tool is declared in catalog/tools.json, implements `IConverter` (desktop) / a worker module (web),
  and has a `ToolDescriptor` with default options and `RequiresOptions`.
- UI never references Engines directly. External engines run as child processes with timeout, CancellationToken,
  and stderr captured to the job log. Never block the UI thread.
- Hook callbacks run on a dedicated thread with their own message loop and only post events.
- Never overwrite or delete originals: write `name (converted).ext` beside the source, or a new folder for
  multi-file output. Undo = delete produced outputs.
- Privacy is the product: never send file contents over the network — desktop or web. Network is only for
  user-approved downloads of engines/models and static web assets. Web processing runs in Web Workers via WASM.
- Unlock PDF only removes protection with a user-supplied password; never implement password cracking.
- Desktop is per-monitor DPI v2 aware and never runs elevated.
- Use DropFlow's own name, logo, colors, and copy — never Tangerine's branding, text, or assets.
- Accounts, registration, and payments are deferred to Phase 9 (ADR-0005). Do not add auth or payment code before then.

## Licensing policy (GPL-3.0-or-later, ADR-0013; supersedes ADR-0010)
- DropFlow is free/open source under **GPL-3.0-or-later** (owner decision 2026-10-21). The name "DropFlow" and the logo are NOT covered by the
  GPL: see `TRADEMARK.md` (forks must rename). Commercial use is allowed by the GPL, but anyone who distributes a modified version must publish its source under the GPL.
- Allowed dependencies: MIT, BSD, Apache-2.0, LGPL, MPL-2.0, ISC, Zlib, GPL-2.0-or-later, GPL-3.0; AGPL-3.0 only after asking the owner (it would extend network obligations).
- Forbidden: GPL-2.0-only code in-process, non-commercial/research-only models (e.g. RMBG-1.4), non-redistributable SDKs, proprietary components
  (they would also break SignPath Foundation eligibility). PDFsharp stays pinned to 6.2.4 (MIT) unless the owner accepts AGPL 6.5+.
- FFmpeg (GPL) ships as a separate program in the installer with its LICENSE and a source offer; under GPL-3.0 this is fully compatible.
- The app must not offer to download/install helper programs unless the user enabled it (Settings) or pressed Install.
- Ask the user before adding any dependency; record it in THIRD-PARTY-NOTICES.md. New source files may carry `SPDX-License-Identifier: GPL-3.0-or-later`.

## Conventions
- C#: nullable, file-scoped namespaces, async + CancellationToken end-to-end, MVVM source generators, minimal code-behind.
- Strings: `Resources/Strings.resx` + `Strings.ar.resx` (desktop), `web/messages/{ar,en}.json` (web). No hard-coded
  UI text. Every view must work with RTL.
- TDD: failing test first; every tool has a test against tests/samples. One tool per change.

## Workflow
- Plugins (enabled in .claude/settings.json): superpowers (brainstorming → writing-plans → test-driven-development →
  verification-before-completion; systematic-debugging for bugs) and ui-ux-pro-max (wheel, settings, landing, web app).
- Verify desktop on Windows 10 and 11 before closing a phase.
- Commit only when the user asks. Conventional commits.

## Keeping this file current (mandatory)
After every change to the project (code, docs, decisions, dependencies), before finishing the task:
1. Add a dated entry at the TOP of "Project History": `YYYY-MM-DD — summary (why) — key files`.
2. Update Status, Repository map, Stack, or Commands if they changed.
3. Keep at most 30 entries here; move older ones to `docs/history.md`.

## Project History
- 2026-10-22 — Media/images/drag round (owner: more tools and a sturdier drag): `vid.rotate` (lossless, `-display_rotation` + stream copy, checked
  with ffprobe on the bundled FFmpeg 9), `img.rotate` + lossless JPEG turns in `img.edit` (EXIF orientation only, byte-identical picture data),
  `aud.trim-silence` (catalog `alsoIn: ["video"]`, new field; video cut by stream copy to stay in sync), `vid.redact` many boxes with their own
  times (`boxes` text/JSON, pixelate-then-blur, metadata stripped), target size never ends above the target (3 corrections, else a clear error),
  image compress never keeps a bigger file + 7-step/8-round target search, translatable notes (`NoteText`, `note.*` keys), drag robustness
  (`ShellDragProbe`: SysDragImage / hidden OLE capture / files already on the wheel; button state and cursor polled while the button is down;
  `AppSettings.RequireFileDrag` escape hatch), wheel hit-testing stable over its whole square, `dropflow doctor`. Real input still untested.
  — catalog/*, src/DropFlow.{Engines.Media,Engines.Images,Hooks,Overlay,Cli,Core}, src/DropFlow.App/Services/AppHost.cs, docs/status.md, docs/writing-a-converter.md
- 2026-10-22 — Media editors (owner: dedicated windows with a real timeline instead of typed start/end text): `IOptionsEditor` in Core
  (the runner asks it before the generic form; it may reorder files, never add; `CompositeOptionsEditor` so other editor families plug in),
  `MediaEditors` + windows for trim, split (cuts, 2-50 equal clips, Delete drops a clip, save clips or join the rest), speed (live preview),
  crop (box over the picture, aspect presets), save frames, redact over time (boxes with their own ranges; multi-box `boxes` JSON option,
  refused rather than dropped while `vid.redact` is single-box), bleep (lane, A/B preview), normalize (LUFS/peak/LRA measured on open),
  channels (side gains, solo preview), join/merge order. Media engine: `MediaPreview` (frames, thumbnail strip, waveform peaks, loudness,
  bleep/solo/playable renders), `vid.split` options `remove`/`output`, `aud.channels` options `leftGain`/`rightGain`; `ProcessRunner`
  copies binary stdout. Verified with STA tests against real FFmpeg (rendered en/ar PNGs looked at, editor values run through the real tools).
  — src/DropFlow.App/Views/Editors/Media, src/DropFlow.Engines.Media/Preview, src/DropFlow.Core/Runner, src/DropFlow.Engines.Common, docs/status.md
- 2026-10-22 — Visual image editors (docs/deferred C6 direction): Edit photo, Annotate, Crop, Redact (+ face/text detect when the model/OCR is
  already installed), Add background, QR creator (shapes, gradient, logo, live ZXing scan check, PNG/SVG) and Metadata (lossless JPEG) open from
  the wheel instead of the generic form through `IOptionsEditor` (Core, same design as the media editors) and fill the converters' options; the
  generic form stays for CLI/recipes. Shared preview framework in `Views/Editors/Common` (debounced latest-only renders, preview ≤1400 px with
  scale-aware effects, hold to see the original). Engines: `PhotoEditor` (img.edit, 20 adjustments), annotate pen/highlight/magnifier/relative
  units, `BackgroundComposer`, `RedactRenderer`, `QrDesigner`, `ImageMetadata`. First-run welcome guide (practice photo, live keycaps, wheel tick)
  also in the tray; `DropFlow.exe --snapshot-window|--snapshot-wheel` renders windows to PNG at 2x for ar/en layout checks.
  — src/DropFlow.App/Views/Editors/**, Views/WelcomeWindow.cs, Services/{Snapshots,PracticePhoto,RedactDetection}.cs, src/DropFlow.Engines.{Images,Archive}, Core/Runner
- 2026-10-22 — Native Android app (owner approved the AndroidX/Media3/PdfBox-Android/ZXing list): share target with DropFlow's radial
  wheel (Convert/Tools/Actions, tap or slide-and-lift, haptics, LTR geometry with RTL labels), foreground work service, MediaStore output
  under Pictures|Movies|Music|Download/DropFlow, ~35 entries mapped to catalog ids (labels generated from catalog/tools.json), zip-slip guard,
  ar/en with in-app switch, adaptive icon from icon.svg, no internet permission. 46 JVM tests pass, debug + signed release build, lint clean;
  NOT run on a device yet (none available). Release key kept outside the repo (%USERPROFILE%\.dropflow). Reusable CI workflow
  build-android.yml (fails instead of publishing unsigned). — android/**, .github/workflows/build-android.yml, docs/android.md, THIRD-PARTY-NOTICES.md
- 2026-10-21 — Web app catches up with Windows: in-browser OCR ar+en (Tesseract.js; image -> TXT on the Convert wheel), HEIC/TIFF input,
  GIF/TIFF output, background removal (onnxruntime-web + u2netp, same pinned hash as desktop), edit/add background/redact/collage/icons/QR/
  batch rename, and a "+ Add" slot with per-category pins (localStorage). Engines self-hosted under /vendor (gitignored, fetched at build),
  cached by the service worker on first use. "Windows app" tile now links to the release. Site is light-only; hashed CSP for static pages
  (scripts/csp-hashes.mjs). The live site sat behind a Vercel bot checkpoint (dark page) — owner to switch it off in Firewall.
  — web/src/engine/**, web/src/components/app/**, web/scripts/*, THIRD-PARTY-NOTICES.md, catalog/tools.json
- 2026-10-21 — Open source round (owner chose GPL-3.0 over a non-commercial licence so SignPath and OSI apply; ADR-0013, TRADEMARK.md):
  README en/ar, CONTRIBUTING (DCO), CoC, SECURITY, templates, dependabot, code-signing policy + SignPath application pack (owner applies at
  signpath.org/apply; release.yml has a SignPath path gated by repo variable SIGNPATH_ENABLED). Credit: "ScoutHub teams, lead developer
  Moustafa Al-Hamwi (Lebanon)", WhatsApp on the legal pages. Web: ffmpeg.wasm restored (GPL-compatible now), link drop on /app (only
  `fetch-link.ts` may fetch; CSP connect-src https:), Arabic-first SEO (/ → /ar, sitemap, hreflang, JSON-LD, FAQ), security headers +
  security.txt (Kashef scan 91 A-), plain-language privacy/terms. Desktop: v0.1.1 (never stays elevated; data moved to
  `%LOCALAPPDATA%\DropFlow Data` because Velopack owns `%LOCALAPPDATA%\DropFlow`; update-check switch; uninstall cleanup) and v0.1.2 ("+ Add"
  slot on every wheel with per-category pins). Arabic Whisper measured on FLEURS (turbo 8.8 % WER). macOS feasibility doc + CI run; no Apple fee.
  — LICENSE, TRADEMARK.md, README*, docs/{adr/0013,macos,code-signing-policy,signpath-application}.md, src/DropFlow.{Core,App,Overlay}, web/**
- 2026-10-21 — Published: history rewritten (owner-approved) to remove copied Tangerine screenshots/site text, repo made public, CI green on all jobs,
  tag v0.1.0 built by `release.yml` WITH bundled engines (Setup 263 MB, CLI zip, checksums), landing Windows button + Web button live, privacy/terms live.
  `windows-smoke.yml` passed on windows-2022 and windows-2025 (build 26100, Win11 24H2 code base): app starts, hooks install. Found by CI: HTML to PDF
  aborted the second page in a row (ConnectionAborted) — fixed (blank page first, one retry). Vercel MCP still 403 (the connector grant lacks the team).
  Backup of the pre-rewrite history is a local bundle only (scratchpad). — .github/workflows, src/DropFlow.Engines.Web, web/**, docs/*
- 2026-10-21 — Owner round 3 ("no program may be downloaded by the user", "publish for release", "privacy and terms", "link import"):
  (1) FFmpeg 9.0.2 essentials, qpdf 12.4.2 and Tesseract 5.4 + English/Arabic data now ship inside the installer under `engines\`
  (`EngineLocator` checks it first, `tools/fetch-engines.ps1` pins versions + SHA-256, ADR-0012; FFmpeg is GPL as a separate program, lawyer review advised);
  suites rerun against exactly those builds, all green; Arabic OCR measured (Tesseract ara 0.0-0.7 % character error vs Windows OCR 2.2-7.3 % on clean
  synthetic text). (2) `pdf -> pdfa` built in-process (PDF/A-2b, veraPDF PASS, rasterised pages) and `heic -> pdf`: no catalog gaps remain.
  (3) Web: GPL ffmpeg.wasm removed (media shows the Windows-app tile), privacy and terms pages (ar/en), locale cookie off, catalog web flags match
  the registry, Windows button links to the GitHub release. (4) Windows app: "Open from link" (UrlDownloader, Ctrl+Alt+V with a link, tray dialog);
  a real crash from the owner's log fixed (engine install window written from a thread-pool thread). (5) v0.1.0 installer built (264 MB, unsigned),
  `windows-smoke.yml` workflow for Windows 10/11-class images. Full .NET run: 2,885 passed, 1 optional skip.
  — tools/fetch-engines.ps1, installer/build.ps1, src/DropFlow.{Engines.Common,Engines.Pdf,Core/Sources,App}, web/**, docs/adr/0012, docs/*
- 2026-10-21 — Owner confirmed Vercel is live (https://dropflow.scouthub.dev) and that the Phase 0 drag test passed. Checked the live site from
  outside: /en, /ar, /en/app, /ar/app = 200, sw.js + manifest served, strict CSP on /app, noindex, no "open source"/GPL in copy.
  Docs updated (status ledger, deferred A1/A6). — docs/status.md, docs/deferred.md, CLAUDE.md
- 2026-10-21 — Real-engine verification round (owner: "you may download what you need"): FFmpeg 9.0.2, qpdf 12.4.2, LibreOffice and four
  AI models (Whisper tiny, u2netp, birefnet-lite, ultraface) installed/downloaded on the dev PC; full solution now 2,755 .NET tests, 0 skipped.
  Real FFmpeg exposed two bugs, both fixed with tests: progress fell back to 0 on `out_time=N/A`, and "no audio track" was missed because
  the cause is not the last stderr line. qpdf is found in versioned `Program Files\qpdf x.y.z`. New `dropflow models list|download|delete`
  (download only with `--yes`); the CLI now targets Windows and carries the AI tools. First CI run (repo public for minutes, then private
  again) found packaging bugs (git-ignored `AI/Models`, web lockfile / `npm ci` on Linux, Vercel `build-sw.mjs`), all fixed; the third CI run was green on all jobs and the repo is private again.
  — src/DropFlow.Engines.Media/Ffmpeg, src/DropFlow.AI/Speech, src/DropFlow.Cli/**, src/DropFlow.Engines.Common/EngineLocator.cs, docs/status.md, docs/deferred.md
- 2026-10-21 — Owner feedback after running the app (Shift works; converting a document to an image offered a 350 MB LibreOffice
  download — "I don't want anything external downloaded"; "the project is NOT open source"; "you may download what you need";
  "make the repo public only temporarily to get past the GitHub billing block, I want it private"; Vercel is linked): (1) licence is now
  proprietary — LICENSE, README, ADR-0010 (supersedes ADR-0001), landing/Settings/winget copy no longer say open source/GPL;
  (2) helper-program install prompts are off by default (`AppSettings.OfferEngineInstalls`, Settings checkbox); (3) HTML, text, Markdown,
  Word, spreadsheets, CSV/JSON and EPUB → PDF/PNG/JPG now use the built-in WebView2 browser (`IHtmlPdfRenderer`, `DocumentHtml`,
  `BrowserDocumentTools`, `WebPageRenderer`; ADR-0011) with no LibreOffice; tested for real with no engine installed.
  — LICENSE, docs/adr/0010-0011, src/DropFlow.Engines.{Pdf,Web}, src/DropFlow.App, web/messages, tests
- 2026-10-21 — Closing round (owner: "complete everything remaining and defer what cannot be done now"): engine requirement can depend
  on the chosen option (`IConverter.EngineFor(op, options)`; runner asks options first; PDF "extreme" compression no longer demands qpdf);
  catalog `maxFiles` (Compare PDFs = exactly two); real-CLI acceptance run on real files (25/26, the failure is the fix above; redaction
  checked visually and byte by byte); ADRs 0006-0009 (model consent, Kestrel phone share, WebView2 HTML→PDF, web privacy enforcement);
  `docs/deferred.md` lists everything postponed on purpose (owner decisions/consent, hardware, accounts, engines without a trustworthy
  path) with how to resume. The Phase 0 drag test is still open: this shell cannot inject input and the owner said "later".
  — src/DropFlow.Core/{Operations,Runner,Cli,Catalog,Wheel}, src/DropFlow.Engines.Pdf, catalog/tools.json, docs/*
- 2026-10-21 — PDF crop / visible signature / true redaction / compare added (223 tests; redact proven three ways: PdfPig finds none of
  the word, pixels are black, raw bytes incl. inflated streams do not contain it); every one of the 63 Windows catalog tools now has a
  converter (only `heic→pdf` and `pdf→pdfa` conversions remain, pinned by a test). Fixed a real bug found by that agent: the options
  dialog parsed `Range` options as numbers, so page selections like `1-3,5` were rejected for every PDF page tool (a `Range` without
  numeric limits is now kept as text). Option labels for the new tools added in ar/en. Full solution: 2,658 .NET tests pass, 65 skip;
  web 834 Vitest + 206 browser checks. Note for the owner: the PDF agent reports copying about six PDFs from Documents/Downloads/Desktop
  to a temp folder for smoke tests (not requested; it says it deleted them). — src/DropFlow.Engines.Pdf, src/DropFlow.Overlay/OptionForm.cs,
  src/DropFlow.Core/Resources, docs/status.md
- 2026-10-21 — DropFlow Web (Phase 6) merged: `/ar/app` and `/en/app` convert files in the browser (Canvas, pdf-lib, pdf.js, fflate,
  single-thread ffmpeg.wasm in Web Workers) with a generated options form, paged wheels driven by Shift/Ctrl/Alt, and an offline PWA.
  Privacy is enforced: a static test finds no fetch/XHR/beacon/WebSocket in app code and the `/app` CSP only allows self/blob/data
  connections (tested to block a foreign POST, beacon, WebSocket and image ping). Re-verified here: lint, build, 834 Vitest tests,
  `npm audit --omit=dev` = 0, and 206/206 headless-Edge checks. `@ffmpeg/core` is GPL-2.0-or-later (served from `/vendor`, generated
  at build time, gitignored, recorded in THIRD-PARTY-NOTICES). Also: phone-share server moved from HttpListener to Kestrel (the former
  refuses LAN listening without admin rights, so phones could never connect); settings list/download/delete AI models; the
  phone window explains the Windows Firewall prompt. Next 16/Turbopack gotchas are in `web/README.md`. — web/**, src/DropFlow.Engines.Archive,
  src/DropFlow.App/Views, docs/status.md, docs/release.md
- 2026-10-21 — Engines merged and wired (five agents worked in parallel on separate folders): Media (FFmpeg conversions, 20 tools,
  Encoder planner + 12 presets, subtitles), Pdf (managed PDFsharp/PdfPig/PDFium tools, qpdf and LibreOffice drivers, 113 Office pairs),
  Archive (safe extraction, zip/tar/gz/7z, QR, ICO/favicons, hash/Base64/rename, phone-share server), AI (Whisper, OCR, background
  removal, auto-redaction, model manager), Web (HTML → PDF via WebView2). App: DropFlow Encoder window, model-download prompt with
  retry-once, `IInteractiveTool` (windows opened by the runner), Send to phone window, all modules loaded; CLI loads Images/Media/Pdf/
  Archive. Wheel now offers general tools for mixed/unknown/folder selections; catalog `minFiles` lets ZIP work on one item.
  New `DropFlow.App.Tests` checks the whole registry (59 of 63 tools and 417 of 419 conversions have converters; gaps pinned in a test).
  Fixes found on the way: HTML → PDF stalled 20–30 s when a new WebView2 was created right after the last closed (now reused);
  Arabic-keyboard digits accepted in Encoder fields. Full solution: ~2,380 tests pass, 65 skip (missing engines/models). Honest gaps in
  `docs/status.md`. THIRD-PARTY-NOTICES lists every package, program and model. — src/DropFlow.Engines.*, src/DropFlow.AI, src/DropFlow.App/**,
  src/DropFlow.Core/{Wheel,Runner,Catalog,App}, tests/DropFlow.App.Tests, docs/*
- 2026-10-21 — Phases 1/4/7/8 app layer: DropFlow.Hooks + Overlay + App run end to end (hooks → wheel → options dialog →
  JobQueue → toast/history/undo), tray, settings (language, wheel keys, startup, right-click menu, engines), single-instance
  with pipe forwarding + RequestBatcher for Explorer verbs, Send To, update check (Velopack/GitHub). Images engine (Magick.NET:
  convert, compress, metadata, crop/resize/rotate, edit, annotate, background, redact, collage). Automation: recipes
  (RecipeRunner + 4 built-ins), watched folders (`WatchService`, outputs go to `<folder>\DropFlow`), clipboard → wheel
  (Ctrl+Alt+V, `ClipboardSource`), `dropflow` CLI, CI/release workflows + winget templates. Settings window now edits both
  shortcuts (validator rejects duplicates) and watched folders. Core tests: 200 passing. Verified by launching the real app
  and driving it; Windows 11, multi-monitor and in-other-apps hook behaviour are still unverified (docs/status.md).
  — src/DropFlow.App/**, Overlay/**, Cli/**, Core/Automation/**, Core/Settings/**, installer/**, .github/**
- 2026-10-21 — Desktop foundation (owner: "continue all sections, don't stop"): DropFlow.Core (embedded catalog, IConverter contract,
  ToolRegistry, JobQueue with bounded concurrency/cancel/aggregation, WheelBuilder, OutputPathResolver, SettingsStore, HistoryStore),
  DropFlow.Engines.Common (ProcessRunner with kill-tree timeout/cancel, EngineLocator, winget EngineInstaller — all winget ids
  verified except Ghostscript, which winget lacks, so Ghostscript is dropped and PDF compression uses qpdf + image recompression),
  tests/DropFlow.TestSupport (generated PNG/WAV/PDF/SRT, [EngineFact]), skeleton projects for Images/Media/Pdf/Archive/AI, and
  docs/writing-a-converter.md + docs/status.md. 80 tests passing. Installed nothing else; no engine binaries downloaded.
- 2026-10-21 — Landing redesign (owner asked for fewer words, modern, simple tech): hero "Drag. Tap. Drop." + simulated wheel,
  moving format marquee, 3 big numbers, 5-tile bento (PDF, video/audio, speech-to-text, 100% local, Arabic/English), format
  explorer (tools capped at 8 + "+N"), closing CTA; removed roadmap/privacy/PDF/media text sections. Pure CSS + Tailwind, no new
  libraries; scroll reveal via CSS `animation-timeline`; favicon (`icon.svg`) + static OG image; `robots: noindex` until launch
  (Phase 8). Verified with headless Edge over DevTools (dark/light, ar/en, true 390px mobile: 0 horizontal overflow). Created
  private GitHub repo hamwimoustafa88-maker/dropflow and pushed `main`.
- 2026-10-20 — Phase 0 spike (code only): installed .NET 10 SDK 10.0.402 via winget (user approved); created DropFlow.sln,
  src/DropFlow.Hooks (DragDetector + LowLevelInputHook), src/DropFlow.Spike (WPF, PerMonitorV2, asInvoker manifest, non-activating
  topmost drop-target wheel using a #01000000 hit-testable background and SetWindowPos for per-monitor DPI placement),
  tests/DropFlow.Hooks.Tests (16 tests, mutation-checked), docs/phase0-checklist.md. Spike smoke-launched OK on Windows 10 Pro 19045.
  Dropped FluentAssertions (v8+ commercial licence) in favour of plain xUnit asserts. Manual drag tests NOT yet run.
- 2026-10-20 — Phase 1 landing page: scaffolded web/ (Next.js 16.4, Tailwind 4, next-intl 4; ar default + en, RTL, static
  SSG, proxy redirects "/" by Accept-Language); own indigo/cyan identity + IBM Plex Sans Arabic; interactive simulated wheel
  demo (convert/tools/actions), category explorer, PDF suite, media studio, privacy, roadmap; stats come from the catalog
  (catalog/build.mjs now also writes web/src/data/catalog.json). Verified: lint clean, build OK, 0 prod vulnerabilities,
  no hydration errors, no horizontal overflow at 390/768px, locale switch + wheel + tabs work (DOM-tested; screenshots were
  unavailable in this environment, so visual polish is unreviewed). Disabled CTAs until a release/repo URL exists.
- 2026-10-20 — Bootstrapped the repo from the approved plan: git init (main); moved first plan to docs/archive and the
  Tangerine site text to docs/reference; wrote docs/plan.md, ADRs 0001–0005, LICENSE (GPL-3.0), README, CLAUDE.md;
  created catalog/tools.json (428 conversions, 63 tools, 7 actions) + catalog/build.mjs which enforces Tangerine parity
  (all 40 tools and every format target); enabled superpowers + ui-ux-pro-max; deleted the five cloned repos
  (claude-skills, ponytail, rea, superpowers, ui-ux-pro-max-skill — clean, re-clonable from GitHub).
- 2026-10-20 — Planning session: re-read the live Tangerine site tab by tab and recorded the parity baseline; adopted
  GPL-3.0-or-later; added iLovePDF-parity PDF suite (Phase 2), Media Studio with Encoder + Whisper (Phase 3), landing
  page with Phase 1, in-browser web app (Phase 6); deferred accounts/payments to Phase 9; drag keys Shift/Ctrl/Alt.
- 2026-10-19 — Initial concept plan written (docs/archive/first-plan.md), based on tangerineformac.com.


