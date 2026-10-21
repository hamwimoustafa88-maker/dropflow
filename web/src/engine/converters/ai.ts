// On-device "AI" tools for pictures: text recognition (OCR, Tesseract.js) and background removal (U²-Net-P on ONNX Runtime
// Web). LIGHT manifest: the engines (8 MB for OCR, 18 MB for the cut-out) load from /vendor only when first used, and a
// one-time note says so before the first run. Image -> TXT is also offered straight on the Convert wheel ("convert:txt").

import { getImageCaps } from "../caps";
import { canDecode } from "../lib/image-formats";
import { normalizeExt } from "../names";
import type { Converter, Job, ProgressFn, Runtime, ToolOption } from "../types";

export const OCR_OPS = ["convert:txt", "img.ocr"] as const;
export const REMBG_OP = "img.remove-bg";

const MAX_BYTES = 300 * 1024 * 1024;
const OCR_LANGS = ["auto", "ara", "eng"] as const;

/** localStorage flags: the first-use note (download size) is shown until the person has run the tool once on this device. */
export const LOADED_KEY = { ocr: "dropflow.loaded.ocr", rembg: "dropflow.loaded.rembg" } as const;

function loadedBefore(key: string): boolean {
  try {
    return typeof localStorage !== "undefined" && localStorage.getItem(key) === "1";
  } catch {
    return false; // storage blocked: keep showing the note
  }
}

export function markLoaded(key: string): void {
  try {
    if (typeof localStorage !== "undefined") localStorage.setItem(key, "1");
  } catch {
    /* storage blocked */
  }
}

function isOcr(operation: string): boolean {
  return operation === "convert:txt" || operation === "img.ocr";
}

export const aiConverter: Converter = {
  id: "ai",
  operations: [...OCR_OPS, REMBG_OP],
  supports(operation, rawExt) {
    const ext = normalizeExt(rawExt);
    if (!isOcr(operation) && operation !== REMBG_OP) return false;
    return ext !== "txt" && canDecode(ext, getImageCaps());
  },
  options(operation): readonly ToolOption[] {
    if (operation === "img.ocr") return [{ key: "lang", kind: "choice", default: "auto", choices: OCR_LANGS }];
    if (operation === REMBG_OP) return [{ key: "fill", kind: "choice", default: "transparent", choices: ["transparent", "white", "black"] }];
    return [];
  },
  // OCR drives Tesseract's own worker from the page; the cut-out runs in the DropFlow worker (OffscreenCanvas).
  runtime(operation: string, ext: string): Runtime {
    if (isOcr(operation)) return "main";
    return normalizeExt(ext) === "svg" || typeof OffscreenCanvas === "undefined" ? "main" : "worker";
  },
  suffix(operation) {
    if (operation === REMBG_OP) return "cutout";
    return operation === "img.ocr" ? "text" : "converted";
  },
  warning(operation) {
    if (isOcr(operation)) return loadedBefore(LOADED_KEY.ocr) ? undefined : "ocrFirstUse";
    if (operation === REMBG_OP) return loadedBefore(LOADED_KEY.rembg) ? undefined : "rembgFirstUse";
    return undefined;
  },
  acknowledgeWarning(operation) {
    if (isOcr(operation)) markLoaded(LOADED_KEY.ocr);
    else if (operation === REMBG_OP) markLoaded(LOADED_KEY.rembg);
  },
  maxBytes: () => MAX_BYTES,
  async run(job: Job, onProgress: ProgressFn, signal: AbortSignal): Promise<File[]> {
    if (isOcr(job.operation)) return (await import("./ocr.impl")).runOcr(job, onProgress, signal);
    return (await import("./rembg.impl")).runRemoveBackground(job, onProgress, signal);
  },
};
