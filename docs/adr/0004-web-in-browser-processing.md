# ADR-0004: Web app processes files in the browser

Date: 2026-10-20 · Status: Accepted

## Context
DropFlow's promise is "your files never leave your device". A server-side web converter would contradict it.

## Decision
`web/` (Next.js on Vercel) contains the landing page (Phase 1) and the web app at `/app` (Phase 6). Files are processed
client-side in Web Workers with WebAssembly: ffmpeg.wasm (needs COOP/COEP headers), wasm-vips/Canvas, pdf-lib + pdf.js,
qpdf/Ghostscript WASM, Tesseract.js, transformers.js/WebGPU for short speech-to-text. PWA with service worker for offline.
Tools that cannot run in the browser (Office conversions, PDF→Word) are marked "Windows app" in the UI. No request may
carry file content. Per-tool platform support lives in `catalog/tools.json` (`platforms`).

## Consequences
Large files are slower than on desktop, so the web app complements rather than replaces the Windows app and enforces a
size warning. Hosting cost stays near zero (static assets only).
