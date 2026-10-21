// Images converter: LIGHT manifest. Everything heavy (canvas work, pdf-lib) lives in ./images.impl, loaded on first run.
// Only operations that really work in this browser are claimed (webp/avif encode and avif decode are detected at runtime).

import { getImageCaps, setImageCaps, type ImageCaps } from "../caps";
import { canDecode, canEncode } from "../lib/image-formats";
import { normalizeExt } from "../names";
import type { Converter, Job, ProgressFn, Runtime, ToolOption } from "../types";

export const CONVERT_TARGETS = ["png", "jpg", "webp", "avif", "bmp", "ico", "pdf"] as const;
export const IMAGE_TOOLS = ["img.compress", "img.resize", "img.crop", "img.strip-gps", "img.metadata", "img.to-pdf"] as const;

export const MAX_IMAGE_BYTES = 300 * 1024 * 1024;
export const CROP_RATIOS = ["1:1", "4:3", "3:2", "16:9", "3:4", "2:3", "9:16"] as const;

const OPERATIONS: readonly string[] = [...CONVERT_TARGETS.map((t) => `convert:${t}`), ...IMAGE_TOOLS];

const SUFFIX: Record<string, string> = {
  "img.compress": "compressed",
  "img.resize": "resized",
  "img.crop": "cropped",
  "img.strip-gps": "stripped",
  "img.metadata": "metadata",
  "img.to-pdf": "pdf",
};

function supportsOp(operation: string, rawExt: string): boolean {
  const ext = normalizeExt(rawExt);
  const caps = getImageCaps();
  if (operation.startsWith("convert:")) {
    const target = normalizeExt(operation.slice("convert:".length));
    if (!(CONVERT_TARGETS as readonly string[]).includes(target)) return false;
    if (target === ext) return false; // never format -> itself (jpg/jpeg alias included)
    if (!canDecode(ext, caps)) return false;
    return target === "pdf" || target === "bmp" || target === "ico" || canEncode(target, caps);
  }
  switch (operation) {
    case "img.compress":
      return ext !== "ico" && canDecode(ext, caps);
    case "img.resize":
    case "img.crop":
    case "img.to-pdf":
      return canDecode(ext, caps);
    case "img.strip-gps":
    case "img.metadata":
      return ext === "jpg" || ext === "png" || ext === "webp";
    default:
      return false;
  }
}

function optionsFor(operation: string): readonly ToolOption[] {
  switch (operation) {
    case "img.compress":
      return [
        { key: "quality", kind: "range", default: 75, min: 10, max: 100, step: 1 },
        { key: "targetKb", kind: "number", default: 0, min: 0, max: 1_000_000, step: 10 },
        { key: "format", kind: "choice", default: "same", choices: getImageCaps().encode.webp ? ["same", "jpg", "webp"] : ["same", "jpg"] },
      ];
    case "img.resize":
      return [
        { key: "width", kind: "number", default: 0, min: 0, max: 16384, step: 1 },
        { key: "height", kind: "number", default: 0, min: 0, max: 16384, step: 1 },
        { key: "keepRatio", kind: "bool", default: true },
      ];
    case "img.crop":
      return [{ key: "ratio", kind: "choice", default: "1:1", choices: CROP_RATIOS }];
    case "img.metadata":
      return [{ key: "action", kind: "choice", default: "view", choices: ["view", "remove"] }];
    case "img.to-pdf":
      return [{ key: "page", kind: "choice", default: "fit", choices: ["fit", "a4"] }];
    default:
      return [];
  }
}

export const imagesConverter: Converter = {
  id: "images",
  operations: OPERATIONS,
  supports: supportsOp,
  options: optionsFor,
  runtime(_operation: string, ext: string): Runtime {
    // SVG needs <img>; engines without OffscreenCanvas (Safari < 16.4) need a DOM canvas.
    return normalizeExt(ext) === "svg" || typeof OffscreenCanvas === "undefined" ? "main" : "worker";
  },
  suffix(operation: string): string {
    return operation.startsWith("convert:") ? "converted" : (SUFFIX[operation] ?? "converted");
  },
  maxBytes: () => MAX_IMAGE_BYTES,
  async run(job: Job, onProgress: ProgressFn, signal: AbortSignal): Promise<File[]> {
    const impl = await import("./images.impl");
    return impl.runImages(job, onProgress, signal);
  },
};

let capsPromise: Promise<ImageCaps> | null = null;

/** Detects (once) which image formats this browser can encode/decode and publishes them to the capability store. */
export function ensureImageCaps(): Promise<ImageCaps> {
  capsPromise ??= (async () => {
    try {
      const { detectImageCaps } = await import("../lib/image-caps");
      const caps = await detectImageCaps();
      setImageCaps(caps);
      return caps;
    } catch {
      return getImageCaps(); // keep the conservative defaults
    }
  })();
  return capsPromise;
}
