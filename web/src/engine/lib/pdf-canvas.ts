// Canvas budget for rendering PDF pages (pdf.js scale 1 = 72 dpi = 1 CSS pixel per PDF point). Pure and Node-compatible.

export const MAX_CANVAS_SIDE = 8192;
/** Total pixel budget per rendered page (about 64 MP = 256 MB of RGBA). */
export const MAX_CANVAS_PIXELS = 64_000_000;

export interface FitInput {
  /** Page size in PDF points (already including the page's /Rotate). */
  widthPt: number;
  heightPt: number;
  /** Requested scale (1 = 72 dpi). */
  scale: number;
  maxSide?: number;
  maxPixels?: number;
}

export interface FitResult {
  /** Scale to pass to getViewport (never above the request). */
  scale: number;
  width: number;
  height: number;
  /** True when the requested scale had to be reduced to fit the budget. */
  reduced: boolean;
}

/** Largest scale <= requested for which the canvas stays within maxSide on the long side and maxPixels in total. */
export function fitRenderScale(input: FitInput): FitResult {
  const maxSide = input.maxSide ?? MAX_CANVAS_SIDE;
  const maxPixels = input.maxPixels ?? MAX_CANVAS_PIXELS;
  const w = Math.max(1, input.widthPt);
  const h = Math.max(1, input.heightPt);
  let scale = Math.max(0.01, input.scale);
  const bySide = maxSide / (Math.max(w, h) * scale);
  if (bySide < 1) scale *= bySide;
  const byPixels = Math.sqrt(maxPixels / (w * scale * h * scale));
  if (byPixels < 1) scale *= byPixels;
  scale = Math.min(scale, input.scale);
  // Shave a little so rounding up never crosses the limit.
  let width = Math.max(1, Math.ceil(w * scale));
  let height = Math.max(1, Math.ceil(h * scale));
  while ((Math.max(width, height) > maxSide || width * height > maxPixels) && scale > 0.01) {
    scale *= 0.995;
    width = Math.max(1, Math.ceil(w * scale));
    height = Math.max(1, Math.ceil(h * scale));
  }
  return { scale, width, height, reduced: scale < input.scale - 1e-9 };
}
