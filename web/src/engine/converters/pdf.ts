// PDF converter manifest (LIGHT: no pdf-lib / pdf.js import here; the implementations are loaded on demand).
// Operations that really work in a browser: merge, split, rotate, organize, metadata, watermark, page numbers
// (pdf-lib, Web Worker) and pages->images, text/markdown extraction, compress-by-rendering (pdf.js, main thread).
// Not offered: protect/unlock/OCR/redact/sign/Office conversions (the Windows app does those).

import type { Converter, Job, ProgressFn, Runtime, ToolOption } from "../types";
import { PAGE_NUMBER_POSITIONS } from "../lib/pdf-layout";

const MB = 1024 * 1024;

const WORKER_OPS = ["pdf.merge", "pdf.split", "pdf.rotate", "pdf.organize", "pdf.metadata", "pdf.watermark", "pdf.page-numbers"] as const;
const RENDER_OPS = ["convert:png", "convert:jpg", "convert:txt", "convert:md", "pdf.to-images", "pdf.extract-text", "pdf.compress"] as const;

const OPERATIONS: readonly string[] = [
  "convert:png",
  "convert:jpg",
  "convert:txt",
  "convert:md",
  "pdf.merge",
  "pdf.split",
  "pdf.rotate",
  "pdf.organize",
  "pdf.metadata",
  "pdf.watermark",
  "pdf.page-numbers",
  "pdf.to-images",
  "pdf.extract-text",
  "pdf.compress",
];

const OPTIONS: Record<string, readonly ToolOption[]> = {
  "pdf.split": [{ key: "ranges", kind: "text", default: "", placeholder: "1-3, 5, 7-9" }],
  "pdf.rotate": [
    { key: "angle", kind: "choice", default: "90", choices: ["90", "180", "270"] },
    { key: "pages", kind: "text", default: "", placeholder: "1-3, 5" },
  ],
  "pdf.organize": [{ key: "order", kind: "text", default: "", required: true, placeholder: "3, 1, 2, 5-7" }],
  "pdf.metadata": [
    { key: "title", kind: "text", default: "" },
    { key: "author", kind: "text", default: "" },
    { key: "subject", kind: "text", default: "" },
    { key: "keywords", kind: "text", default: "" },
    { key: "clear", kind: "bool", default: false },
  ],
  "pdf.watermark": [
    { key: "text", kind: "text", default: "", required: true },
    { key: "opacity", kind: "range", default: 25, min: 5, max: 100, step: 5 },
  ],
  "pdf.page-numbers": [
    { key: "position", kind: "choice", default: "bottom-center", choices: PAGE_NUMBER_POSITIONS },
    { key: "start", kind: "number", default: 1, min: 1, step: 1 },
    { key: "format", kind: "choice", default: "n", choices: ["n", "n-of-total"] },
  ],
  "pdf.to-images": [
    { key: "format", kind: "choice", default: "png", choices: ["png", "jpg"] },
    { key: "scale", kind: "choice", default: "2", choices: ["1", "1.5", "2", "3"] },
    { key: "pages", kind: "text", default: "", placeholder: "1-3, 5" },
  ],
  "pdf.extract-text": [{ key: "format", kind: "choice", default: "txt", choices: ["txt", "md"] }],
  "pdf.compress": [
    { key: "quality", kind: "range", default: 60, min: 30, max: 90, step: 5 },
    { key: "scale", kind: "choice", default: "1.5", choices: ["1", "1.5", "2"] },
  ],
};

const SUFFIX: Record<string, string> = {
  "convert:png": "pages",
  "convert:jpg": "pages",
  "pdf.to-images": "pages",
  "convert:txt": "text",
  "convert:md": "text",
  "pdf.extract-text": "text",
  "pdf.merge": "merged",
  "pdf.split": "split",
  "pdf.rotate": "rotated",
  "pdf.organize": "organized",
  "pdf.metadata": "metadata",
  "pdf.watermark": "watermarked",
  "pdf.page-numbers": "numbered",
  "pdf.compress": "compressed",
};

/** Text watermarks are drawn with a canvas; without OffscreenCanvas (older Safari) they have to run on the main thread. */
export function workerCanRenderText(): boolean {
  return typeof OffscreenCanvas !== "undefined";
}

function isRenderOp(op: string): boolean {
  return (RENDER_OPS as readonly string[]).includes(op);
}

export const pdfConverter: Converter = {
  id: "pdf",
  operations: OPERATIONS,

  supports(operation: string, ext: string): boolean {
    return ext === "pdf" && OPERATIONS.includes(operation);
  },

  options(operation: string): readonly ToolOption[] {
    return OPTIONS[operation] ?? [];
  },

  runtime(operation: string): Runtime {
    if (isRenderOp(operation)) return "main";
    if (operation === "pdf.watermark" && !workerCanRenderText()) return "main";
    return "worker";
  },

  suffix(operation: string): string {
    return SUFFIX[operation] ?? "converted";
  },

  warning(operation: string): string | undefined {
    return operation === "pdf.compress" ? "textToImages" : undefined;
  },

  maxBytes(operation: string): number | undefined {
    if (isRenderOp(operation)) return 250 * MB;
    return (WORKER_OPS as readonly string[]).includes(operation) ? 400 * MB : undefined;
  },

  async run(job: Job, onProgress: ProgressFn, signal: AbortSignal): Promise<File[]> {
    if (isRenderOp(job.operation)) {
      const impl = await import("./pdf.render.impl");
      return impl.runPdfRenderOp(job, onProgress, signal);
    }
    const impl = await import("./pdf.impl");
    return impl.runPdfWorkerOp(job, onProgress, signal);
  },
};
