# Vercel costs and limits

DropFlow's website does **no processing on a server**: conversions, OCR and background removal run in the visitor's browser
(WebAssembly). Vercel only serves static files, so a traffic spike costs **bandwidth and requests, never CPU**.

## What a visitor costs

| Item | Size | When |
|---|---|---|
| A page (HTML, CSS, JS) | a few hundred KB | every new visit |
| Video/audio engine (`/vendor/ffmpeg/ffmpeg-core.wasm`) | 31 MB (sent brotli-compressed) | first video/audio job |
| OCR (`/vendor/tesseract`, Arabic + English data) | ~16 MB | first OCR job |
| Background removal (`/vendor/ort` + `u2netp.onnx`) | ~18 MB | first background removal |

After the first use the service worker keeps these files on the device (cache-first, versioned cache), and a revalidation by a
browser without the service worker answers **304 with 0 bytes** (measured on the live site, 2026-11-12). So a returning visitor
costs almost nothing; long-lived `immutable` headers on `/vendor` would save only those small 304 requests and need versioned
paths everywhere, so they were not added.

Plan figures (vercel.com/pricing, 2026-11-12): Hobby 100 GB Fast Data Transfer + 1M CDN requests a month; Pro $20/month with
1 TB + 10M, then $0.15/GB and $2 per 1M requests. Vercel describes Hobby as being for personal, non-commercial projects.

## Done in the repository

- The locale proxy (`src/proxy.ts`) runs only for paths without a language (`/app` → `/ar/app`); `/ar/*` and `/en/*` pages
  never invoke it. The matcher is checked by `src/lib/__tests__/proxy-matcher.test.ts`.

## To do in the Vercel dashboard (not applied from Claude: production changes need the owner)

1. **Spend limit** (Pro): Team → Settings → Billing → *Spend Management* → set an amount (for example $20), turn on
   *Pause production deployments* at 100 %, keep the e-mail/SMS alerts at 50/75/100 %. On Hobby there is no on-demand billing;
   Vercel limits the account instead when the included usage runs out.
2. **Rate limit on the heavy engine files**: Project `dropflow` → Firewall → *Add rule*:
   - If: **Path** matches regex `^/vendor/.+\.(wasm|onnx|gz)$`
   - Then: **Rate limit**, fixed window **600 s**, **200** requests, key **IP**, action **Deny** (429), for 10 minutes.
   - A real visitor needs about 10 of these files once; the rule only stops someone downloading them in a loop.
   - Do **not** use *Challenge* / Attack Challenge Mode here: a challenge page blocks the browser's background engine downloads
     (the site once sat behind a bot checkpoint that showed visitors a dark page).
3. Watch **Usage** (Fast Data Transfer, CDN Requests) for a month before changing plan.

## If traffic grows

- Above ~500 GB a month, or a bill above ~$50: move only `/vendor` (the engines) to Cloudflare R2 behind a subdomain (no egress
  fees). The CSP and the service worker must allow that origin; user files still never leave the device.
- Coolify on a small VPS behind Cloudflare is the fallback for the whole site (DNS switch). It needs its own build/headers setup
  (`scripts/csp-hashes.mjs` expects Vercel's build layout) and a test run before it is relied on.
