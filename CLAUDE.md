# DropFlow

An enhanced, open-source Windows 10/11 + web counterpart of Tangerine (tangerineformac.com): drag a file, tap a key,
and a radial wheel appears to convert, process, or act on files 100% locally — no uploads, on desktop or in the browser.
License: GPL-3.0-or-later. Audience: Arabic + English users.

**Status (2026-10-21):** The owner told us to build all remaining phases without waiting. The Phase 0 manual gate
(`docs/phase0-checklist.md`) was our own rule and has **not been run** (needs a human dragging files; Windows 11 and
multi-monitor/DPI untested) — the input-capture risk stays open and is tracked in `docs/status.md`, which is the honest
verified-vs-unverified ledger: read it before claiming anything works. Landing page (`web/`, ar/en, static, noindex) is done; CTAs
are inert "Soon" badges until a release exists. GitHub: private repo `hamwimoustafa88-maker/dropflow` (remote `origin`, branch
`main`). Vercel: NOT linked — the connector gets 403 on the owner's personal scope (`moustafa-hamwis-projects`); the owner must grant
access or import the repo in the dashboard with Root Directory `web`. No Vercel CLI login on this machine. .NET 10 SDK 10.0.402 is
installed (new terminals may need a restart to see it on PATH). The dev machine has no modern FFmpeg, qpdf or LibreOffice (only 7-Zip,
WinRAR's Rar.exe, Tesseract English, and an old FFmpeg 4.0 inside another vendor's app that one test run used); we do not download
binaries or models without the owner's say-so; engine-dependent tests skip visibly via `[EngineFact]` / `[ModelFact]`.
**Read before any task:** `docs/plan.md` (roadmap, Arabic), `docs/feature-catalog.md` (generated tool list),
`docs/reference/tangerine-features.md` (parity baseline — DropFlow must never ship less than Tangerine in a category) and
`docs/writing-a-converter.md` (the contract every converter follows).

## Stack
- Desktop: C# / .NET 10, WPF with DropFlow's own theme (WPF-UI was not adopted), CommunityToolkit.Mvvm, H.NotifyIcon (tray), Velopack (updates)
- Engines: FFmpeg (GPL), Magick.NET, qpdf, PDFiumCore, PDFsharp, PdfPig, LibreOffice headless (Ghostscript dropped: not in winget, AGPL),
  DocumentFormat.OpenXml, ClosedXML, WebView2, SharpCompress/7-Zip
- Local AI: Whisper.net, Windows.Media.Ocr + Tesseract, ONNX Runtime
- Web: Next.js + TypeScript + Tailwind + next-intl on Vercel; in-browser WASM (ffmpeg.wasm, pdf-lib, pdf.js,
  qpdf/Ghostscript wasm, Tesseract.js, transformers.js)
- Tests: xUnit with plain `Assert` (desktop — FluentAssertions 8+ is commercially licensed, do not add it), Vitest + Playwright (web) · Installer: Velopack

## Repository map
| Path | Responsibility |
|---|---|
| catalog/tools.json | Single source of truth for all tools/conversions (read by desktop, web, landing) |
| catalog/build.mjs | Validates the catalog, checks Tangerine parity, regenerates docs/feature-catalog.md |
| DropFlow.sln | Desktop solution |
| src/DropFlow.Core | Catalog loader, `IConverter`/`ConversionJob`, `ToolRegistry`, `JobQueue`, `WheelBuilder`, `OutputPathResolver`, settings, history (no UI, no engines) |
| src/DropFlow.Engines.Common | `ProcessRunner`, `EngineLocator`, winget-based `EngineInstaller` |
| src/DropFlow.Engines.{Images,Media,Pdf,Archive} | Converters per domain; each exposes `XModule.Create(IEngineProvider)` (split from the planned single Engines project for isolation). Media also has `MediaEncoder` + `EncoderPlanner`/presets for the Encoder window; Archive also has `PhoneShareServer`; Pdf pins PDFsharp to 6.2.4 (6.5+ is AGPL) |
| src/DropFlow.Engines.Web | `HtmlToPdfTool`: HTML → PDF in one reused hidden WebView2 window (network blocked unless the job allows it); needs the UI dispatcher |
| src/DropFlow.AI | Whisper speech to text, OCR, background removal, auto-redaction, `ModelManager` (resumable, SHA-256 verified; downloads only after the app asks the user) (net10.0-windows10.0.19041.0) |
| src/DropFlow.Hooks | `DragDetector` (pure drag + modifier-tap state machine) and `LowLevelInputHook` (WH_MOUSE_LL/WH_KEYBOARD_LL on a dedicated thread) |
| src/DropFlow.Overlay | `WheelWindow` (non-activating topmost OLE drop target), `WheelControl`/`WheelGeometry`, option form, floating shelf |
| src/DropFlow.App | WPF host (`AppHost`): tray, settings, history, toasts, hooks → wheel → `OperationRunner`, watched folders, global shortcuts, update check; `app.manifest` = per-monitor DPI v2, asInvoker |
| src/DropFlow.Cli | `dropflow` CLI (assembly name `dropflow`): list / convert / recipe commands over the same Core |
| src/DropFlow.Spike | Phase 0 throwaway WPF app: log window + transparent drop-target wheel |
| installer/ · .github/workflows/ | Velopack packaging (`installer/build.ps1`), winget manifest templates, CI + release workflows (docs/release.md) |
| src/DropFlow.* (planned) | Encoder window, Shell — created as their phases start |
| tests/DropFlow.TestSupport | Sample-file generators (`Samples`), `TempDir`, `[EngineFact]` (skips when an engine is missing) |
| tests/*.Tests | xUnit tests, one project per src project. `DropFlow.App.Tests` is cross-module: catalog conformance of every converter, no overlapping converters, option-label coverage in ar/en, the known-gaps ratchet, Encoder and phone-share windows |
| web/ | Next.js 16: landing (`/ar`, `/en`), web app (`/app`, Phase 6), PWA. Copy in `web/messages/`, tokens in `src/app/globals.css`, data in generated `src/data/catalog.json` |
| tests/ | Desktop tests; tests/samples holds one sample per format |
| tools/ | Scripts that download engine binaries (never commit binaries) |
| docs/ | plan.md, feature-catalog.md (generated), adr/, reference/ (Tangerine baseline + screenshots), archive/ |

## Commands
- Catalog: `node catalog/build.mjs` (run after any edit to catalog/tools.json; must print OK)
- Desktop: `dotnet build DropFlow.sln` · `dotnet test` · `dotnet run --project src/DropFlow.Spike` (single test: `dotnet test --filter "FullyQualifiedName~Name"`)
- Web: `cd web && npm install && npm run dev` · `npm run lint` · `npm run build` (Next 16 differs from older versions: read `web/node_modules/next/dist/docs/` first)

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

## Licensing policy (GPL-3.0-or-later, ADR-0001)
- Allowed: MIT, BSD, Apache-2.0, LGPL, MPL-2.0, GPL-2.0-or-later, GPL-3.0; AGPL-3.0 as an external process/WASM module.
- Forbidden: GPL-2.0-only code in-process, non-commercial/research-only models (e.g. RMBG-1.4), non-redistributable SDKs.
- Code adapted from GPL projects (File Converter, ShareX) keeps its copyright header.
- Ask the user before adding any dependency; record it in THIRD-PARTY-NOTICES.md.

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
