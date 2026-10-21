// Pure pre- and post-processing for the background-removal network (U²-Net-P). A port of the desktop SegmentationMath /
// BackgroundModelSpec.U2NetP (src/DropFlow.AI/Imaging/SegmentationMath.cs) so both apps cut the same way. No model, no DOM.

export const U2NETP = {
  id: "u2netp",
  /** Network input size. */
  size: 320,
  mean: [0.485, 0.456, 0.406] as const,
  std: [0.229, 0.224, 0.225] as const,
} as const;

/**
 * Interleaved RGBA (already resized to size x size, transparent areas already flattened onto white) to a planar float
 * tensor laid out NCHW. Pixels are divided by the LARGEST value in the picture, as the U²-Net reference code does.
 */
export function toTensor(rgba: ArrayLike<number>, size: number = U2NETP.size): Float32Array {
  const pixels = size * size;
  if (rgba.length !== pixels * 4) throw new Error(`expected ${pixels * 4} bytes, got ${rgba.length}`);
  let max = 1;
  for (let i = 0; i < rgba.length; i += 4) {
    if (rgba[i] > max) max = rgba[i];
    if (rgba[i + 1] > max) max = rgba[i + 1];
    if (rgba[i + 2] > max) max = rgba[i + 2];
  }
  const t = new Float32Array(3 * pixels);
  for (let i = 0; i < pixels; i++) {
    for (let c = 0; c < 3; c++) t[c * pixels + i] = (rgba[i * 4 + c] / max - U2NETP.mean[c]) / U2NETP.std[c];
  }
  return t;
}

/** Network output (probabilities) to an 8-bit mask (255 = keep), stretched min..max to 0..1 like the reference code. */
export function toMask(prediction: ArrayLike<number>): Uint8ClampedArray {
  let min = Infinity;
  let max = -Infinity;
  for (let i = 0; i < prediction.length; i++) {
    if (prediction[i] < min) min = prediction[i];
    if (prediction[i] > max) max = prediction[i];
  }
  const range = max - min;
  const mask = new Uint8ClampedArray(prediction.length);
  for (let i = 0; i < prediction.length; i++) {
    const v = range > 1e-6 ? (prediction[i] - min) / range : Math.min(1, Math.max(0, prediction[i]));
    mask[i] = Math.round(Math.min(1, Math.max(0, v)) * 255);
  }
  return mask;
}

/** Multiplies the alpha of RGBA pixels by the mask (same size), so already-transparent areas stay transparent. */
export function applyAlpha(rgba: Uint8ClampedArray, mask: ArrayLike<number>): void {
  if (rgba.length !== mask.length * 4) throw new Error("mask size does not match the image");
  for (let i = 0; i < mask.length; i++) rgba[i * 4 + 3] = Math.floor((rgba[i * 4 + 3] * mask[i] + 127) / 255);
}

/** Blends RGBA over an opaque colour (alpha becomes 255). In place. */
export function flattenOnto(rgba: Uint8ClampedArray, r: number, g: number, b: number): void {
  for (let i = 0; i < rgba.length; i += 4) {
    const a = rgba[i + 3];
    rgba[i] = Math.floor((rgba[i] * a + r * (255 - a) + 127) / 255);
    rgba[i + 1] = Math.floor((rgba[i + 1] * a + g * (255 - a) + 127) / 255);
    rgba[i + 2] = Math.floor((rgba[i + 2] * a + b * (255 - a) + 127) / 255);
    rgba[i + 3] = 255;
  }
}
