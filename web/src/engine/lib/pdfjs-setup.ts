// pdf.js bootstrap. pdf.js is loaded lazily (dynamic import) so it never lands in the landing or app-shell bundle, and
// every runtime asset (worker, cmaps, standard fonts, wasm, ICC profiles) comes from the site's own origin under
// /vendor/pdfjs (copied from node_modules by scripts/vendor.mjs; precached by the service worker). No CDN.

import type * as Pdfjs from "pdfjs-dist";

export type PdfjsModule = typeof Pdfjs;

export const PDFJS_BASE = "/vendor/pdfjs/";
export const PDFJS_WORKER_SRC = `${PDFJS_BASE}pdf.worker.min.mjs`;

let loading: Promise<PdfjsModule> | null = null;

/** Loads pdf.js once and points it at the same-origin worker. Main thread only. */
export function loadPdfjs(): Promise<PdfjsModule> {
  if (!loading) {
    loading = import("pdfjs-dist").then((m) => {
      m.GlobalWorkerOptions.workerSrc = PDFJS_WORKER_SRC;
      return m;
    });
    loading.catch(() => {
      loading = null; // allow a retry (e.g. the first attempt was offline before the service worker had cached it)
    });
  }
  return loading;
}

/** getDocument() parameters. pdf.js transfers the buffer to its worker, so a COPY of the bytes is passed. */
export function pdfjsDocParams(data: Uint8Array) {
  return {
    data: new Uint8Array(data),
    cMapUrl: `${PDFJS_BASE}cmaps/`,
    cMapPacked: true,
    standardFontDataUrl: `${PDFJS_BASE}standard_fonts/`,
    wasmUrl: `${PDFJS_BASE}wasm/`,
    iccUrl: `${PDFJS_BASE}iccs/`,
    enableXfa: false,
    // Never let a hostile PDF reach a network address from the renderer.
    disableAutoFetch: true,
    disableStream: true,
  };
}
