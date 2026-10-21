// Heavy half of image -> text (OCR). Main thread: the picture is decoded with its orientation applied (HEIC/TIFF/SVG
// included), flattened onto white, scaled so small text gets enough pixels, and handed as a PNG to Tesseract's own worker.

import { canvasToBlob, decodeImage, drawScaled } from "../lib/canvas";
import { extForMime } from "../lib/image-formats";
import { hasRealText, recognize } from "../lib/ocr-engine";
import { extensionOf, normalizeExt, stemOf } from "../names";
import { ConvertError, throwIfAborted, type Job, type ProgressFn } from "../types";

/**
 * Small pictures are enlarged (at most 2x: measured in Edge, a smooth 3x upscale made Tesseract misread Arabic words that it
 * reads fine at 1x-2.5x), huge ones reduced so recognition stays quick.
 */
export function ocrScale(width: number, height: number): number {
  const long = Math.max(width, height);
  if (long < 1000) return Math.min(2, 2000 / long);
  if (long > 5000) return 5000 / long;
  return 1;
}

export async function runOcr(job: Job, onProgress: ProgressFn, signal: AbortSignal): Promise<File[]> {
  const lang = typeof job.options.lang === "string" ? job.options.lang : "auto";
  const out: File[] = [];
  const n = job.files.length;
  for (let i = 0; i < n; i++) {
    throwIfAborted(signal);
    const file = job.files[i];
    const ext = extensionOf(file.name) || normalizeExt(extForMime(file.type) ?? "");
    const dec = await decodeImage(file, ext);
    let png: Blob;
    try {
      const k = ocrScale(dec.width, dec.height);
      const canvas = drawScaled(dec, Math.max(1, Math.round(dec.width * k)), Math.max(1, Math.round(dec.height * k)), { flatten: true });
      png = await canvasToBlob(canvas, "image/png");
    } finally {
      dec.close();
    }
    onProgress((i + 0.1) / n);
    const text = await recognize(png, lang, (p) => onProgress((i + 0.1 + 0.9 * p) / n), signal);
    if (!hasRealText(text)) throw new ConvertError("noTextFound");
    out.push(new File([text], `${stemOf(file.name)}.txt`, { type: "text/plain;charset=utf-8" }));
    onProgress((i + 1) / n);
  }
  return out;
}
