// Thin canvas/decoder helpers shared by the images converter. They run in a Web Worker (OffscreenCanvas,
// createImageBitmap) and fall back to a DOM canvas on the main thread. DOM-bound: covered by the browser e2e, not by
// Node unit tests (the maths around it lives in image-math.ts).

import { ConvertError } from "../types";
import { readOrientation } from "./exif";
import { jpegDimensions, svgSize } from "./image-formats";
import { MAX_AREA, MAX_SIDE, needsManualOrientation, orientationMatrix, resizeSteps } from "./image-math";

export type AnyCanvas = OffscreenCanvas | HTMLCanvasElement;
export type Ctx2D = OffscreenCanvasRenderingContext2D | CanvasRenderingContext2D;

export function assertCanvasSize(width: number, height: number): void {
  if (!(width >= 1) || !(height >= 1) || width > MAX_SIDE || height > MAX_SIDE || width * height > MAX_AREA) {
    throw new ConvertError("invalidSize");
  }
}

export function makeCanvas(width: number, height: number): AnyCanvas {
  assertCanvasSize(width, height);
  if (typeof OffscreenCanvas !== "undefined") return new OffscreenCanvas(width, height);
  if (typeof document !== "undefined") {
    const c = document.createElement("canvas");
    c.width = width;
    c.height = height;
    return c;
  }
  throw new ConvertError("noCanvas");
}

export function get2d(canvas: AnyCanvas): Ctx2D {
  const ctx = (canvas as HTMLCanvasElement).getContext("2d") as Ctx2D | null;
  if (!ctx) throw new ConvertError("noCanvas");
  return ctx;
}

/** Encodes a canvas and VERIFIES the browser really produced `mime` (it silently falls back to PNG otherwise). */
export async function canvasToBlob(canvas: AnyCanvas, mime: string, quality?: number): Promise<Blob> {
  let blob: Blob | null = null;
  try {
    if (typeof OffscreenCanvas !== "undefined" && canvas instanceof OffscreenCanvas) {
      blob = await canvas.convertToBlob({ type: mime, quality });
    } else {
      blob = await new Promise<Blob | null>((resolve) => (canvas as HTMLCanvasElement).toBlob(resolve, mime, quality));
    }
  } catch {
    blob = null;
  }
  if (!blob || blob.type !== mime) throw new ConvertError("encodeUnsupported", { type: mime });
  return blob;
}

export interface Decoded {
  source: CanvasImageSource;
  width: number;
  height: number;
  close(): void;
}

function fromCanvas(canvas: AnyCanvas): Decoded {
  return { source: canvas, width: canvas.width, height: canvas.height, close() {} };
}

async function bitmapOf(file: Blob): Promise<ImageBitmap> {
  try {
    return await createImageBitmap(file, { imageOrientation: "from-image" });
  } catch (e) {
    if (!(e instanceof TypeError)) throw new ConvertError("decodeFailed");
  }
  // Older engines reject the "from-image" enum value: retry with defaults.
  try {
    return await createImageBitmap(file);
  } catch {
    throw new ConvertError("decodeFailed");
  }
}

/**
 * Decodes an image with EXIF orientation applied. SVG is rasterised through <img> (main thread only). `ext` is the
 * normalised extension. Caller must call close().
 */
export async function decodeImage(file: Blob, ext: string): Promise<Decoded> {
  if (ext === "svg") return decodeSvg(file);
  const bmp = await bitmapOf(file);
  if (ext === "jpg") {
    // Guard: an engine that ignored the orientation leaves the raw SOF size on a transposing orientation.
    try {
      const head = new Uint8Array(await file.slice(0, 262144).arrayBuffer());
      const o = readOrientation(head);
      const raw = jpegDimensions(head);
      if (o && raw && needsManualOrientation(o, raw.width, raw.height, bmp.width, bmp.height)) {
        const m = orientationMatrix(o, bmp.width, bmp.height);
        const canvas = makeCanvas(m.width, m.height);
        const ctx = get2d(canvas);
        ctx.setTransform(...m.matrix);
        ctx.drawImage(bmp, 0, 0);
        bmp.close();
        return fromCanvas(canvas);
      }
    } catch (e) {
      if (e instanceof ConvertError) throw e;
    }
  }
  return { source: bmp, width: bmp.width, height: bmp.height, close: () => bmp.close() };
}

async function decodeSvg(file: Blob): Promise<Decoded> {
  if (typeof document === "undefined" || typeof Image === "undefined") throw new ConvertError("decodeFailed");
  const text = await file.text();
  const url = URL.createObjectURL(new Blob([text], { type: "image/svg+xml" }));
  try {
    const img = new Image();
    await new Promise<void>((resolve, reject) => {
      img.onload = () => resolve();
      img.onerror = () => reject(new ConvertError("decodeFailed"));
      img.src = url;
    });
    const size = svgSize(text) ?? (img.naturalWidth > 0 && img.naturalHeight > 0
      ? { width: img.naturalWidth, height: img.naturalHeight }
      : { width: 1024, height: 1024 });
    const canvas = makeCanvas(size.width, size.height);
    get2d(canvas).drawImage(img, 0, 0, size.width, size.height);
    return fromCanvas(canvas);
  } catch (e) {
    if (e instanceof ConvertError) throw e;
    throw new ConvertError("decodeFailed");
  } finally {
    URL.revokeObjectURL(url);
  }
}

/** Draws `src` (or a region of it) onto a new width x height canvas, optionally on a white background. */
export function drawScaled(
  src: Decoded,
  width: number,
  height: number,
  opts: { flatten?: boolean; region?: { x: number; y: number; width: number; height: number } } = {},
): AnyCanvas {
  const region = opts.region ?? { x: 0, y: 0, width: src.width, height: src.height };
  let from: CanvasImageSource = src.source;
  let fx = region.x;
  let fy = region.y;
  let fw = region.width;
  let fh = region.height;
  // Step down by halves for a clean result on big reductions (a single drawImage aliases badly).
  const steps = resizeSteps(fw, fh, width, height);
  for (let i = 0; i < steps.length - 1; i++) {
    const s = steps[i];
    const mid = makeCanvas(s.width, s.height);
    const mctx = get2d(mid);
    mctx.imageSmoothingEnabled = true;
    mctx.imageSmoothingQuality = "high";
    mctx.drawImage(from, fx, fy, fw, fh, 0, 0, s.width, s.height);
    from = mid;
    fx = 0;
    fy = 0;
    fw = s.width;
    fh = s.height;
  }
  const out = makeCanvas(width, height);
  const ctx = get2d(out);
  if (opts.flatten) {
    ctx.fillStyle = "#ffffff";
    ctx.fillRect(0, 0, width, height);
  }
  ctx.imageSmoothingEnabled = true;
  ctx.imageSmoothingQuality = "high";
  ctx.drawImage(from, fx, fy, fw, fh, 0, 0, width, height);
  return out;
}

export function rgbaOf(canvas: AnyCanvas): Uint8ClampedArray {
  return get2d(canvas).getImageData(0, 0, canvas.width, canvas.height).data;
}
