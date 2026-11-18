# Older project history

Entries moved out of the "Project History" list in `CLAUDE.md` (which keeps the newest 30). Newest first.

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
