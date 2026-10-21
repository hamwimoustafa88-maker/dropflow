# DropFlow web

Landing page (`/ar`, `/en`) and **DropFlow Web**, the in-browser app (`/ar/app`, `/en/app`). Next.js 16 + Tailwind 4 +
next-intl. Files are processed **entirely in the browser** (Web Workers + WebAssembly): nothing is ever uploaded. The site sets no cookies and has no analytics.

- Dev: `npm run dev` · Build: `npm run build` · Lint: `npm run lint` · Unit tests: `npm test` (Vitest)
- E2E (headless Edge over DevTools): `npm run build && npx next start --port 3200`, then `node e2e/run.mjs http://localhost:3200`
  (`--only=images,pdf,text,wheel,media,offline,quality,privacy,landing,legal`, `--out=<dir>` for screenshots/downloads).
- Content: `messages/ar.json` and `messages/en.json` (all UI text; the web app lives under the `app` namespace and ships only
  with `/app`). Tool/format data comes from `src/data/catalog.json`, **generated** by `node ../catalog/build.mjs`: never edit by hand.
- Design tokens live in `src/app/globals.css` (`:root` + dark mode); the app adds only its status colours in `src/app/[locale]/app/app.css`.
- Deploy: Vercel with root directory `web/`. Fully static (no server code); `headers()` in `next.config.ts` apply on Vercel.
- Next.js 16 differs from older versions: read `node_modules/next/dist/docs/` (see `AGENTS.md`) before changing framework code.

## DropFlow Web architecture (`src/engine`, `src/components/app`)

| Piece | File | Notes |
|---|---|---|
| Converter contract | `engine/types.ts` | `Converter { operations, supports(op, ext), options(op), runtime, suffix, run(job, onProgress, signal) -> File[] }`, mirrors desktop `IConverter`. Expected failures are `ConvertError(code)`; the UI shows `app.err.<code>`. |
| Registry + wheel | `engine/registry.ts`, `engine/wheel.ts` | The wheel is catalog (`platforms` includes `web`) ∩ registry: only operations that really run in a browser appear. |
| Converters | `engine/converters/*` | Light manifests (imported by UI, worker, tests); heavy code is in `*.impl.ts`, loaded on first run. Pure helpers in `engine/lib`. |
| Queue / workers | `engine/queue.ts`, `pool.ts`, `worker.ts`, `session.ts` | Bounded concurrency, cancel (cooperative, then terminate), progress; `main`-runtime converters (canvas, pdf.js) run on the UI thread. |
| Naming | `engine/names.ts` | Results are always NEW files `name (converted).ext`, made unique; inputs are never touched. |
| PWA | `scripts/sw.template.js`, `scripts/build-sw.mjs`, `public/manifest*.webmanifest` | `build` writes `public/sw.js` from the real build output (versioned caches + update flow). Registered on `/app` only. |
| Engines | `scripts/vendor.mjs` | Copies the pdf.js worker/data from `node_modules` to `public/vendor` on install/dev/build. Served from our own origin, never a CDN. |
| Legal pages | `components/LegalPage.tsx`, `app/[locale]/{privacy,terms}`, `messages/*.json` (`privacy`, `terms`, `legal`) | Static, server-rendered, noindex like the rest of the site; linked from the landing footer and the app footer. The long texts are never sent to the browser as JSON. |

Privacy is enforced twice: the code contains no network API (`src/engine/__tests__/no-upload.test.ts` scans for it) and the browser
refuses it (CSP `connect-src 'self'` on `/app`, on the worker chunks and on the vendor files; the e2e proves a foreign POST is blocked).

Not offered in a browser (the app shows a short "Windows app" hint instead): **all audio and video tools** (the only in-browser engine,
ffmpeg.wasm, is GPL-2.0-or-later and was removed because DropFlow is proprietary; ADR-0010), Office/PDF→Word, 7z/RAR create, OCR,
Whisper, HEIC/TIFF (no license-clean decoder justified), password-protect/unlock PDF. Catalog `platforms` for `web` lists exactly the
tools the registry implements (`webTools` in `engine/catalog-web.ts` reads it).

## Gotchas found while building it (Next 16 / Turbopack)

1. (Removed with ffmpeg.wasm.) pdf.js is bundled with its worker copied to `/vendor/pdfjs` instead of a CDN.
2. Turbopack's worker bootstrap reads `?params=` from the worker script URL. A service worker answering from the Cache API returns a
   response with no query, giving "Missing worker bootstrap config"; serve a URL-less copy of the cached response.
3. `<Link href="/app">` on the landing page prefetches `/app` chunks into the landing: keep `prefetch={false}`.
4. The next-intl proxy matcher redirects extensionless static files (for example `LICENSE_FOXIT`), which stalled the service-worker
   install. `/vendor` and `/icons` are excluded in `src/proxy.ts`.
5. `NextIntlClientProvider` ships every message to every page, so the landing layout filters out the `app` namespace.
6. `body { overflow-x: clip }` hides real overflow from `scrollWidth`; measure element edges instead (this caught a real 390 px overflow).
7. `html { scroll-behavior: smooth }` breaks coordinate-based clicks in tests unless `scrollIntoView` uses `instant`.
8. The service worker has no media-core cache any more; an old install drops its old `dropflow-vendor-*` cache (and the 31 MB core in it) on the first update.
9. `next dev` and `next build` use separate output directories, so building next to a dev server on port 3000 is safe.
10. `public/sw.js` is generated *after* `next build` (`build-sw.mjs`). It works under `next start`; that Vercel collects `public/` after the
    build command is assumed, not verified: check the first deployment.
