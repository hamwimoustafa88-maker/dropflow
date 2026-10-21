# DropFlow web

Landing page (`/ar`, `/en`) and **DropFlow Web**, the in-browser app (`/ar/app`, `/en/app`). Next.js 16 + Tailwind 4 +
next-intl. Files are processed **entirely in the browser** (Web Workers + WebAssembly): nothing is ever uploaded.

- Dev: `npm run dev` · Build: `npm run build` · Lint: `npm run lint` · Unit tests: `npm test` (Vitest)
- E2E (headless Edge over DevTools): `npm run build && npx next start --port 3200`, then `node e2e/run.mjs http://localhost:3200`
  (`--only=images,pdf,text,wheel,media,offline,quality,privacy,landing`, `--out=<dir>` for screenshots/downloads).
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
| Queue / workers | `engine/queue.ts`, `pool.ts`, `worker.ts`, `session.ts` | Bounded concurrency, cancel (cooperative, then terminate), progress; `main`-runtime converters (canvas, pdf.js, ffmpeg.wasm) run on the UI thread. |
| Naming | `engine/names.ts` | Results are always NEW files `name (converted).ext`, made unique; inputs are never touched. |
| PWA | `scripts/sw.template.js`, `scripts/build-sw.mjs`, `public/manifest*.webmanifest` | `build` writes `public/sw.js` from the real build output (versioned caches + update flow). Registered on `/app` only. |
| Engines | `scripts/vendor.mjs` | Copies the ffmpeg.wasm core (single-thread, so no COOP/COEP) and pdf.js worker/data from `node_modules` to `public/vendor` on install/dev/build. Served from our own origin, never a CDN. |

Privacy is enforced twice: the code contains no network API (`src/engine/__tests__/no-upload.test.ts` scans for it) and the browser
refuses it (CSP `connect-src 'self'` on `/app`, on the worker chunks and on the vendor engines; the e2e proves a foreign POST is blocked).

Not possible in a browser, so never offered (the app shows a short "Windows app" hint instead): Office/PDF→Word, 7z/RAR create,
OCR, Whisper, HEIC/TIFF (no license-clean decoder justified), password-protect/unlock PDF.
