// GIF output (still image) with `gifenc` (MIT): a 256-colour palette from the picture's own colours, 1-bit transparency
// when the picture has transparent pixels. Canvas cannot write GIF, so this runs in JavaScript. Node-compatible.

import { GIFEncoder, applyPalette, quantize } from "gifenc";

export function hasTransparency(rgba: ArrayLike<number>): boolean {
  for (let i = 3; i < rgba.length; i += 4) if (rgba[i] < 128) return true;
  return false;
}

export function encodeGif(rgba: Uint8ClampedArray | Uint8Array, width: number, height: number): Uint8Array {
  const data = rgba instanceof Uint8Array ? rgba : new Uint8Array(rgba.buffer, rgba.byteOffset, rgba.byteLength);
  const alpha = hasTransparency(data);
  const format = alpha ? "rgba4444" : "rgb565";
  const palette = quantize(data, 256, alpha ? { format, oneBitAlpha: true } : { format });
  const index = applyPalette(data, palette, format);
  const gif = GIFEncoder();
  const transparentIndex = alpha ? palette.findIndex((c) => c.length > 3 && c[3] === 0) : -1;
  gif.writeFrame(index, width, height, transparentIndex >= 0 ? { palette, transparent: true, transparentIndex } : { palette });
  gif.finish();
  return gif.bytes();
}
