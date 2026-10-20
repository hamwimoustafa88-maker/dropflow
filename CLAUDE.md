# DropFlow

An enhanced, open-source Windows 10/11 + web counterpart of Tangerine (tangerineformac.com): drag a file, tap a key,
and a radial wheel appears to convert, process, or act on files 100% locally — no uploads, on desktop or in the browser.
License: GPL-3.0-or-later. Audience: Arabic + English users.

**Status:** Planning + repo bootstrap done. Phase 1 landing page built in `web/` (ar/en, static, builds clean) but NOT deployed:
CTAs are disabled "coming soon" because no release/repo URL exists yet. Desktop code not started — Phase 0 spike is next
and needs the .NET 10 SDK, which is NOT installed (`dotnet --list-sdks` is empty).
**Read before any task:** `docs/plan.md` (roadmap, Arabic), `docs/feature-catalog.md` (generated tool list) and
`docs/reference/tangerine-features.md` (parity baseline — DropFlow must never ship less than Tangerine in a category).

## Stack
- Desktop: C# / .NET 10, WPF + WPF-UI, CommunityToolkit.Mvvm, Microsoft.Extensions.Hosting
- Engines: FFmpeg (GPL), Magick.NET, qpdf, Ghostscript, PDFiumCore, PDFsharp, PdfPig, LibreOffice headless,
  DocumentFormat.OpenXml, ClosedXML, WebView2, SharpCompress/7-Zip
- Local AI: Whisper.net, Windows.Media.Ocr + Tesseract, ONNX Runtime
- Web: Next.js + TypeScript + Tailwind + next-intl on Vercel; in-browser WASM (ffmpeg.wasm, pdf-lib, pdf.js,
  qpdf/Ghostscript wasm, Tesseract.js, transformers.js)
- Tests: xUnit + FluentAssertions (desktop), Vitest + Playwright (web) · Installer: Velopack

## Repository map
| Path | Responsibility |
|---|---|
| catalog/tools.json | Single source of truth for all tools/conversions (read by desktop, web, landing) |
| catalog/build.mjs | Validates the catalog, checks Tangerine parity, regenerates docs/feature-catalog.md |
| src/DropFlow.* | Desktop solution (not created yet): App, Hooks, Overlay, Core, Engines, Encoder, AI, Shell, Cli |
| web/ | Next.js 16: landing (`/ar`, `/en`), web app (`/app`, Phase 6), PWA. Copy in `web/messages/`, tokens in `src/app/globals.css`, data in generated `src/data/catalog.json` |
| tests/ | Desktop tests; tests/samples holds one sample per format |
| tools/ | Scripts that download engine binaries (never commit binaries) |
| docs/ | plan.md, feature-catalog.md (generated), adr/, reference/ (Tangerine baseline + screenshots), archive/ |

## Commands
- Catalog: `node catalog/build.mjs` (run after any edit to catalog/tools.json; must print OK)
- Desktop (once created): `dotnet build DropFlow.sln` · `dotnet test` · `dotnet run --project src/DropFlow.App`
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
