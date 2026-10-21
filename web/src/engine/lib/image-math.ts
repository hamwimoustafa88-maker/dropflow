// Pure geometry / search helpers for the images converter. Node-compatible.

export interface Rect {
  x: number;
  y: number;
  width: number;
  height: number;
}

export const MAX_SIDE = 16384;
/** Chromium's practical canvas area limit (16384 x 16384). */
export const MAX_AREA = 268_435_456;

export function parseRatio(s: string): [number, number] | null {
  const m = /^\s*(\d+)\s*:\s*(\d+)\s*$/.exec(s);
  if (!m) return null;
  const a = Number(m[1]);
  const b = Number(m[2]);
  return a > 0 && b > 0 ? [a, b] : null;
}

/** Centred crop of maximum area with aspect ratio rw:rh, in whole pixels. */
export function cropRect(srcW: number, srcH: number, rw: number, rh: number): Rect {
  const r = rw / rh;
  let width: number;
  let height: number;
  if (srcW / srcH > r) {
    height = srcH;
    width = Math.round(srcH * r);
  } else {
    width = srcW;
    height = Math.round(srcW / r);
  }
  width = Math.min(srcW, Math.max(1, width));
  height = Math.min(srcH, Math.max(1, height));
  return { x: Math.floor((srcW - width) / 2), y: Math.floor((srcH - height) / 2), width, height };
}

/**
 * Target size for resize. 0 means "auto". Both 0 -> null. One set -> the other follows the aspect ratio.
 * Both set: keepRatio fits inside the w x h box, otherwise the size is exactly w x h. null when the result is invalid.
 */
export function resizeDims(srcW: number, srcH: number, w: number, h: number, keepRatio: boolean): { width: number; height: number } | null {
  w = Math.floor(w);
  h = Math.floor(h);
  if (!(w >= 0) || !(h >= 0) || (w === 0 && h === 0)) return null;
  let width: number;
  let height: number;
  if (w > 0 && h > 0) {
    if (keepRatio) {
      const k = Math.min(w / srcW, h / srcH);
      width = Math.round(srcW * k);
      height = Math.round(srcH * k);
    } else {
      width = w;
      height = h;
    }
  } else if (w > 0) {
    width = w;
    height = Math.round((srcH * w) / srcW);
  } else {
    height = h;
    width = Math.round((srcW * h) / srcH);
  }
  width = Math.max(1, width);
  height = Math.max(1, height);
  if (width > MAX_SIDE || height > MAX_SIDE || width * height > MAX_AREA) return null;
  return { width, height };
}

/**
 * Intermediate sizes for a high-quality downscale (repeated halving, then the final step). Upscales go straight to the
 * target. The last element is always the target size.
 */
export function resizeSteps(srcW: number, srcH: number, dstW: number, dstH: number): { width: number; height: number }[] {
  const steps: { width: number; height: number }[] = [];
  let cw = srcW;
  let ch = srcH;
  while (cw > dstW * 2 || ch > dstH * 2) {
    const nw = Math.max(dstW, Math.ceil(cw / 2));
    const nh = Math.max(dstH, Math.ceil(ch / 2));
    if (nw === cw && nh === ch) break;
    cw = nw;
    ch = nh;
    steps.push({ width: cw, height: ch });
  }
  if (!steps.length || steps[steps.length - 1].width !== dstW || steps[steps.length - 1].height !== dstH) {
    steps.push({ width: dstW, height: dstH });
  }
  return steps;
}

/** Rectangle that fits srcW x srcH inside a size x size square (centred, aspect kept). */
export function containRect(srcW: number, srcH: number, size: number): Rect {
  const k = Math.min(size / srcW, size / srcH);
  const width = Math.max(1, Math.round(srcW * k));
  const height = Math.max(1, Math.round(srcH * k));
  return { x: Math.floor((size - width) / 2), y: Math.floor((size - height) / 2), width, height };
}

export interface SearchResult<T> {
  quality: number;
  result: T;
}

/**
 * Finds the HIGHEST integer quality in [min, max] whose encoded size is <= targetBytes, assuming size grows with
 * quality (binary search, ~log2(range) encodes). Returns null when even `min` is too big.
 */
export async function searchQuality<T extends { size: number }>(
  encode: (quality: number) => Promise<T>,
  targetBytes: number,
  range: { min?: number; max?: number } = {},
): Promise<SearchResult<T> | null> {
  const min = Math.max(1, Math.floor(range.min ?? 10));
  const max = Math.max(min, Math.floor(range.max ?? 100));
  const top = await encode(max);
  if (top.size <= targetBytes) return { quality: max, result: top };
  const bottom = await encode(min);
  if (bottom.size > targetBytes) return null;
  let best: SearchResult<T> = { quality: min, result: bottom };
  let lo = min; // known to fit
  let hi = max; // known not to fit
  while (hi - lo > 1) {
    const mid = (lo + hi) >> 1;
    const r = await encode(mid);
    if (r.size <= targetBytes) {
      lo = mid;
      best = { quality: mid, result: r };
    } else {
      hi = mid;
    }
  }
  return best;
}

export type Matrix = [number, number, number, number, number, number];

/**
 * Canvas transform that draws an un-oriented w x h image upright for EXIF orientation 1..8, plus the canvas size needed.
 * Use as ctx.setTransform(...matrix) then ctx.drawImage(img, 0, 0).
 */
export function orientationMatrix(orientation: number, w: number, h: number): { matrix: Matrix; width: number; height: number } {
  switch (orientation) {
    case 2:
      return { matrix: [-1, 0, 0, 1, w, 0], width: w, height: h };
    case 3:
      return { matrix: [-1, 0, 0, -1, w, h], width: w, height: h };
    case 4:
      return { matrix: [1, 0, 0, -1, 0, h], width: w, height: h };
    case 5:
      return { matrix: [0, 1, 1, 0, 0, 0], width: h, height: w };
    case 6:
      return { matrix: [0, 1, -1, 0, h, 0], width: h, height: w };
    case 7:
      return { matrix: [0, -1, -1, 0, h, w], width: h, height: w };
    case 8:
      return { matrix: [0, -1, 1, 0, 0, w], width: h, height: w };
    default:
      return { matrix: [1, 0, 0, 1, 0, 0], width: w, height: h };
  }
}

/** Applies a canvas matrix to a point (for tests and reasoning). */
export function applyMatrix(m: Matrix, x: number, y: number): [number, number] {
  return [m[0] * x + m[2] * y + m[4], m[1] * x + m[3] * y + m[5]];
}

/**
 * True when a browser decoded a JPEG WITHOUT applying a transposing EXIF orientation (5..8): the bitmap still has the raw
 * SOF dimensions although the dimensions should be swapped. (Squares and orientations 2..4 cannot be detected this way.)
 */
export function needsManualOrientation(orientation: number, rawW: number, rawH: number, bitmapW: number, bitmapH: number): boolean {
  return orientation >= 5 && orientation <= 8 && rawW !== rawH && bitmapW === rawW && bitmapH === rawH;
}

export const A4 = { width: 595.28, height: 841.89 } as const;

export interface PdfPlacement {
  pageWidth: number;
  pageHeight: number;
  x: number;
  y: number;
  width: number;
  height: number;
}

/**
 * "fit": the page is exactly the image (px -> pt at 72 dpi). "a4": A4 portrait or landscape by the image's aspect, image
 * scaled to fit inside a 20 pt margin and centred.
 */
export function pdfPlacement(imgW: number, imgH: number, mode: "fit" | "a4"): PdfPlacement {
  if (mode === "fit") return { pageWidth: imgW, pageHeight: imgH, x: 0, y: 0, width: imgW, height: imgH };
  const landscape = imgW > imgH;
  const pageWidth = landscape ? A4.height : A4.width;
  const pageHeight = landscape ? A4.width : A4.height;
  const margin = 20;
  const k = Math.min((pageWidth - 2 * margin) / imgW, (pageHeight - 2 * margin) / imgH);
  const width = imgW * k;
  const height = imgH * k;
  return { pageWidth, pageHeight, x: (pageWidth - width) / 2, y: (pageHeight - height) / 2, width, height };
}
