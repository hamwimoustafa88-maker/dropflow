// Photo tools that need no download: edit (light, colour, rotate, flip, blur, sharpen), add a background, redact areas,
// collage, icon + favicon set, read a QR code. LIGHT manifest; the work is in ./photo.impl (canvas + pure maths in
// lib/photo-math.ts; jsQR for QR codes), loaded on first run. Mirrors the desktop tools' options.

import { getImageCaps } from "../caps";
import { canDecode } from "../lib/image-formats";
import { normalizeExt } from "../names";
import type { Converter, Job, ProgressFn, Runtime, ToolOption } from "../types";

export const PHOTO_TOOLS = ["img.edit", "img.background", "img.redact", "img.collage", "img.icons", "img.qr"] as const;

const ASPECTS = ["original", "1:1", "4:3", "3:2", "16:9", "9:16", "4:5"] as const;

const OPTIONS: Record<string, readonly ToolOption[]> = {
  "img.edit": [
    { key: "brightness", kind: "range", default: 0, min: -100, max: 100, step: 1 },
    { key: "contrast", kind: "range", default: 0, min: -100, max: 100, step: 1 },
    { key: "saturation", kind: "range", default: 100, min: 0, max: 200, step: 1 },
    { key: "sharpen", kind: "range", default: 0, min: 0, max: 100, step: 1 },
    { key: "blur", kind: "range", default: 0, min: 0, max: 20, step: 1 },
    { key: "filter", kind: "choice", default: "none", choices: ["none", "grayscale", "sepia", "invert"] },
    { key: "rotate", kind: "choice", default: "0", choices: ["0", "90", "180", "270"] },
    { key: "flipH", kind: "bool", default: false },
    { key: "flipV", kind: "bool", default: false },
  ],
  "img.background": [
    { key: "background", kind: "text", default: "#ffffff", placeholder: "#ffffff" },
    { key: "aspect", kind: "choice", default: "original", choices: ASPECTS },
    { key: "padding", kind: "range", default: 8, min: 0, max: 40, step: 1 },
    { key: "radius", kind: "range", default: 0, min: 0, max: 50, step: 1 },
    { key: "shadow", kind: "bool", default: false },
  ],
  "img.redact": [
    { key: "regions", kind: "text", default: "", required: true },
    { key: "style", kind: "choice", default: "solid", choices: ["solid", "blur", "pixelate"] },
    { key: "color", kind: "text", default: "#000000" },
  ],
  "img.collage": [
    { key: "layout", kind: "choice", default: "grid", choices: ["grid", "row", "column", "featured"] },
    { key: "columns", kind: "number", default: 0, min: 0, max: 12, step: 1 },
    { key: "cell", kind: "number", default: 600, min: 100, max: 2000, step: 10 },
    { key: "spacing", kind: "number", default: 16, min: 0, max: 200, step: 1 },
    { key: "radius", kind: "number", default: 0, min: 0, max: 300, step: 1 },
    { key: "background", kind: "text", default: "#ffffff" },
    { key: "format", kind: "choice", default: "jpg", choices: ["jpg", "png"] },
  ],
  "img.icons": [{ key: "fit", kind: "choice", default: "contain", choices: ["contain", "cover"] }],
  "img.qr": [],
};

const SUFFIX: Record<string, string> = {
  "img.edit": "edited",
  "img.background": "background",
  "img.redact": "redacted",
  "img.collage": "collage",
  "img.icons": "icons",
  "img.qr": "qr",
};

export const photoConverter: Converter = {
  id: "photo",
  operations: PHOTO_TOOLS,
  supports(operation, rawExt) {
    if (!(PHOTO_TOOLS as readonly string[]).includes(operation)) return false;
    return canDecode(normalizeExt(rawExt), getImageCaps());
  },
  options: (operation) => OPTIONS[operation] ?? [],
  runtime(_operation: string, ext: string): Runtime {
    return normalizeExt(ext) === "svg" || typeof OffscreenCanvas === "undefined" ? "main" : "worker";
  },
  suffix: (operation) => SUFFIX[operation] ?? "edited",
  maxBytes: () => 300 * 1024 * 1024,
  async run(job: Job, onProgress: ProgressFn, signal: AbortSignal): Promise<File[]> {
    return (await import("./photo.impl")).runPhoto(job, onProgress, signal);
  },
};
