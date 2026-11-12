# Older project history

Entries moved out of the "Project History" list in `CLAUDE.md` (which keeps the newest 30). Newest first.

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
