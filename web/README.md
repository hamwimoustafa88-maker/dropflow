# DropFlow web

Landing page (`/ar`, `/en`) and **DropFlow Web**, the in-browser app (`/ar/app`, `/en/app`). Next.js 16 + Tailwind 4 +
next-intl. Files are processed **entirely in the browser** (Web Workers + WebAssembly): nothing is ever uploaded. The site sets no cookies and has no analytics.

- Dev: `npm run dev` · Build: `npm run build` · Lint: `npm run lint` · Unit tests: `npm test` (Vitest)
- E2E (headless Edge over DevTools): `npm run build && npx next start --port 3200`, then `node e2e/run.mjs http://localhost:3200`
  (`--only=images,pdf,text,wheel,media,link,extras,offline,quality,privacy,landing,legal,seo`, `--out=<dir>` for screenshots/downloads).
- Content: `messages/ar.json` and `messages/en.json` (all UI text; the web app lives under the `app` namespace and ships only
  with `/app`). Tool/format data comes from `src/data/catalog.json`, **generated** by `node ../catalog/build.mjs`: never edit by hand.
- Design tokens live in `src/app/globals.css` (`:root` + dark mode); the app adds only its status colours in `src/app/[locale]/app/app.css`.
- Deploy: Vercel with root directory `web/`. Fully static (no server code); `headers()` in `next.config.ts` apply on Vercel.
- Next.js 16 differs from older versions: read `node_modules/next/dist/docs/` (see `AGENTS.md`) before changing framework code.

## DropFlow Web architecture (`src/engine`, `src/components/app`)

| Piece | File | Notes |
|---|---|---|
| Converter contract | `engine/types.ts` | `Converter { operations, supports(op, ext), options(op), runtime, suffix, run(job, onProgress, signal) -> File[] }`, mirrors desktop `IConverter`. Expected failures are `ConvertError(code)`; the UI shows `app.err.<code>`. |
| Registry + wheel | `engine/registry.ts`, `engine/wheel.ts` | The wheel is catalog (`platforms` includes `web`) ∩ registry: only operations that really run in a browser appear. `WEB_EXTRA_OUTPUTS` adds web-only Convert targets (image → TXT through OCR). |
| + Add (pins) | `engine/wheel.ts` (`withPins`), `engine/pins-store.ts`, `components/app/AddList.tsx` | Mirrors the desktop: every non-empty wheel ends with "+ Add", a list (Convert to / Tools / Actions) of what these files can do that is not on this wheel. Picking runs it now and pins it to that wheel for that file category; pins live in `localStorage` (`dropflow.pins`, in-memory fallback when blocked) and are removed with × in the list. |
| Converters | `engine/converters/*` | Light manifests (imported by UI, worker, tests); heavy code is in `*.impl.ts`, loaded on first run. Pure helpers in `engine/lib`. |
| Queue / workers | `engine/queue.ts`, `pool.ts`, `worker.ts`, `session.ts` | Bounded concurrency, cancel (cooperative, then terminate), progress; `main`-runtime converters (canvas, pdf.js) run on the UI thread. |
| Naming | `engine/names.ts` | Results are always NEW files `name (converted).ext`, made unique; inputs are never touched. |
| PWA | `scripts/sw.template.js`, `scripts/build-sw.mjs`, `public/manifest*.webmanifest` | `build` writes `public/sw.js` from the real build output (versioned caches + update flow). Registered on `/app` only. |
| Engines | `scripts/vendor.mjs` | Copies the single-thread ffmpeg.wasm core + worker and the pdf.js worker/data from `node_modules` to `public/vendor` on install/dev/build. Served from our own origin, never a CDN. The 31 MB core is cached only on first use or "Save media offline". Also Tesseract.js (+ LSTM cores, Arabic/English models), libheif's ES module (HEIC) and ONNX Runtime Web, plus the U²-Net-P model, downloaded once with a pinned SHA-256 (`DROPFLOW_SKIP_MODELS=1` skips it). These are imported at runtime through `engine/lib/vendor-import.ts` (never bundled, so their wasm stays out of the app shell) and cached by the service worker on first use. |
| Drop a link | `engine/lib/fetch-link.ts`, `components/app/AppShell.tsx` | A typed, pasted or dropped http(s) address is downloaded by the browser (GET, `credentials: "omit"`, no referrer, 1 GB cap, progress + cancel) and then handled like a dropped file. Sites without CORS get a bilingual "download it and drop it here, or use the Windows app" message. The ONLY module allowed to make a network request. |
| Headers | `next.config.ts` | Every route: CSP (strict same-origin; `/app` stricter), nosniff, `X-Frame-Options: DENY`, HSTS preload, Permissions-Policy, COOP/CORP same-origin, own-origin ACAO (replaces Vercel's `*`, to verify after deploy). `/` is a 308 to `/ar`. `public/.well-known/security.txt` (RFC 9116). |
| Share images | `app/[locale]/opengraph-image.tsx`, `scripts/og-font.mjs` | Per-locale 1200x630 PNG; the Arabic one uses IBM Plex Sans Arabic Bold as TTF, fetched at prebuild into `.og-cache/` (gitignored) because next/og cannot read WOFF2. Without it the image falls back to Latin. |
| SEO | `lib/site.ts`, `lib/seo.ts`, `app/robots.ts`, `app/sitemap.ts` | One base URL (`NEXT_PUBLIC_SITE_URL`, default `https://dropflow.scouthub.dev`); `pageMetadata()` gives every page canonical + hreflang (ar, en, x-default → ar), Open Graph and Twitter tags (a page that sets `openGraph` replaces the parent's, so each page calls it); JSON-LD (`SoftwareApplication` + `SoftwareSourceCode` + `FAQPage`, author = the ScoutHub organisation) on the landing page. All public links (GitHub, GPL) live in `lib/site.ts`. |
| Legal pages | `components/LegalPage.tsx`, `app/[locale]/{privacy,terms}`, `messages/*.json` (`privacy`, `terms`, `legal`) | Static, server-rendered, indexable like the rest of the site; section `links` are keys of `LEGAL_LINKS` in `lib/site.ts`; linked from the landing footer and the app footer. The long texts are never sent to the browser as JSON. |

Privacy is enforced twice: the code contains no network API except one GET in `engine/lib/fetch-link.ts` (`src/engine/__tests__/no-upload.test.ts`
scans for it and pins that call to GET without credentials, referrer or body) and the browser refuses the rest (CSP on `/app`, the worker chunks and the
vendor files: `connect-src 'self' blob: data: https:` - `https:` exists only for link downloads; forms, `http:`/`ws:` beacons and image pings stay blocked, and
the e2e proves it). Note: since `https:` is allowed, the browser no longer blocks an https request by itself; the static test is the guard there.

Audio and video run in the browser with the single-thread ffmpeg.wasm core (GPL-2.0-or-later, compatible with DropFlow's GPL-3.0;
restored after ADR-0013). Pictures: HEIC/TIFF input, GIF/TIFF output, OCR (image → TXT, "Text (OCR)", scanned PDF → TXT),
background removal, edit, background, redact, collage, icons + favicons, read QR, and batch rename (renamed copies) all run on the
device. Still Windows-only (the app's tile says so and links to the latest release): Office/PDF → Word, 7z/RAR create, Whisper,
HEIC/AVIF output (no licence-clean small encoder: HEVC needs x265, the AVIF wasm encoder is 3.5 MB), annotate (needs the visual
editor), auto-redact (face model), password-protect/unlock PDF and the other desktop-only tools. Catalog `platforms` for `web` lists
exactly the tools the registry implements (`webTools` in `engine/catalog-web.ts` reads it).

## Gotchas found while building it (Next 16 / Turbopack)

1. ffmpeg.wasm and pdf.js are bundled with its worker copied to `/vendor/pdfjs` instead of a CDN.
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
11. onnxruntime-web's bundle contains `new URL("ort-wasm-simd-threaded.wasm", import.meta.url)`, which Turbopack would copy (14 MB) into
    `_next/static` and so into the service-worker shell. Tesseract.js, ONNX Runtime and libheif are therefore imported at runtime from
    `/vendor` (`import(/* webpackIgnore: true */ url)` in `engine/lib/vendor-import.ts`); the build has no `.wasm` in `_next/static`.
12. Tesseract misread Arabic after a smooth 3x canvas upscale (fine at 1x-2.5x, measured in Edge): `ocrScale` never enlarges more than 2x.
