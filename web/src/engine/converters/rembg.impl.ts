// Heavy half of "Remove background": decode (orientation applied), run U²-Net-P at 320 x 320, scale the mask back up
// smoothly and use it as the alpha channel (same pipeline as the desktop BackgroundRemover). Output: PNG.

import { canvasToBlob, decodeImage, drawScaled, get2d, makeCanvas, rgbaOf } from "../lib/canvas";
import { extForMime } from "../lib/image-formats";
import { predictMask } from "../lib/rembg-engine";
import { U2NETP, applyAlpha, flattenOnto } from "../lib/segmentation";
import { extensionOf, normalizeExt, stemOf } from "../names";
import { ConvertError, isAbort, throwIfAborted, type Job, type ProgressFn } from "../types";

export async function runRemoveBackground(job: Job, onProgress: ProgressFn, signal: AbortSignal): Promise<File[]> {
  const fill = typeof job.options.fill === "string" ? job.options.fill : "transparent";
  const out: File[] = [];
  const n = job.files.length;
  for (let i = 0; i < n; i++) {
    throwIfAborted(signal);
    const file = job.files[i];
    const ext = extensionOf(file.name) || normalizeExt(extForMime(file.type) ?? "");
    const dec = await decodeImage(file, ext);
    try {
      const size = U2NETP.size;
      const small = drawScaled(dec, size, size, { flatten: true });
      onProgress((i + 0.1) / n);
      let mask: Uint8ClampedArray;
      try {
        mask = await predictMask(rgbaOf(small));
      } catch (e) {
        if (isAbort(e) || e instanceof ConvertError) throw e;
        throw new ConvertError("engineLoadFailed", {}, e instanceof Error ? e.message : String(e));
      }
      throwIfAborted(signal);
      onProgress((i + 0.8) / n);

      // Mask -> grey picture -> smooth upscale to the photo size.
      const maskCanvas = makeCanvas(size, size);
      const grey = new Uint8ClampedArray(size * size * 4);
      for (let p = 0; p < mask.length; p++) {
        grey[p * 4] = grey[p * 4 + 1] = grey[p * 4 + 2] = mask[p];
        grey[p * 4 + 3] = 255;
      }
      get2d(maskCanvas).putImageData(new ImageData(grey, size, size), 0, 0);
      const bigMask = rgbaOf(drawScaled({ source: maskCanvas, width: size, height: size, close() {} }, dec.width, dec.height));
      const full = drawScaled(dec, dec.width, dec.height);
      const pixels = rgbaOf(full);
      const alpha = new Uint8ClampedArray(dec.width * dec.height);
      for (let p = 0; p < alpha.length; p++) alpha[p] = bigMask[p * 4];
      applyAlpha(pixels, alpha);
      if (fill === "white") flattenOnto(pixels, 255, 255, 255);
      else if (fill === "black") flattenOnto(pixels, 0, 0, 0);
      get2d(full).putImageData(new ImageData(pixels, dec.width, dec.height), 0, 0);
      const blob = await canvasToBlob(full, "image/png");
      out.push(new File([blob], `${stemOf(file.name)}.png`, { type: "image/png" }));
    } finally {
      dec.close();
    }
    onProgress((i + 1) / n);
  }
  return out;
}
