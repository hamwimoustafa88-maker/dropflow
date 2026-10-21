# ADR-0009: The web app's privacy is enforced by code and by the browser

Date: 2026-10-21 · Status: Accepted

## Decision
DropFlow Web (`/ar/app`, `/en/app`) processes files in the browser (Canvas, pdf-lib, pdf.js, fflate and a single-thread ffmpeg.wasm
core in Web Workers) and enforces "nothing is uploaded" twice:
1. **Code:** a static test scans the app source for `fetch`, XHR, `sendBeacon`, WebSocket, EventSource, WebRTC, `importScripts`,
   POST/PUT, `FormData` and analytics SDKs. There is none (the app code has no `fetch` at all).
2. **Browser:** the `/app` pages, their worker chunks and the `/vendor` engines carry a CSP with `connect-src 'self' blob: data:`
   and `form-action 'none'`. The end-to-end suite proves a foreign `fetch` POST, `sendBeacon`, WebSocket and image ping are blocked.

Other choices: the ffmpeg core is the single-thread build, so no COOP/COEP headers are needed and the landing page and embeds are
unaffected; the ffmpeg core and pdf.js worker are copied from `node_modules` into `public/vendor` at install/build time (gitignored),
served from our own origin, never from a CDN; the service worker is registered on `/app` only and caches versioned shell and engine files.

## Consequences
`@ffmpeg/core` is GPL-2.0-or-later and is served with its notice (compatible with GPL-3.0-or-later; recorded in THIRD-PARTY-NOTICES).
Tools that cannot run in a browser (Office, OCR, Whisper, protect/unlock, 7z/RAR) are never offered; a hint tile points to the Windows app.
The service worker's own requests are not covered by the network audit (only its source was scanned).
