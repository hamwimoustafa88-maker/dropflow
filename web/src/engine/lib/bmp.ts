// BMP encoder: 24-bit BI_RGB, bottom-up rows padded to 4 bytes, alpha flattened onto white. Pure, Node-compatible.

const FILE_HEADER = 14;
const INFO_HEADER = 40;

/** Blends one 8-bit channel over white with 8-bit alpha. */
export function flattenOnWhite(c: number, a: number): number {
  if (a >= 255) return c;
  return Math.round((c * a + 255 * (255 - a)) / 255);
}

/** @param rgba width*height*4 bytes, row-major, top row first (what ImageData.data gives). */
export function encodeBmp24(rgba: Uint8ClampedArray | Uint8Array, width: number, height: number): Uint8Array {
  if (!Number.isInteger(width) || !Number.isInteger(height) || width < 1 || height < 1) throw new RangeError("bad size");
  if (rgba.length < width * height * 4) throw new RangeError("pixel buffer too small");
  const rowSize = (width * 3 + 3) & ~3;
  const imageSize = rowSize * height;
  const total = FILE_HEADER + INFO_HEADER + imageSize;
  if (total > 0xffffffff) throw new RangeError("BMP too large");
  const out = new Uint8Array(total);
  const dv = new DataView(out.buffer);
  out[0] = 0x42; // B
  out[1] = 0x4d; // M
  dv.setUint32(2, total, true);
  dv.setUint32(10, FILE_HEADER + INFO_HEADER, true);
  dv.setUint32(14, INFO_HEADER, true);
  dv.setInt32(18, width, true);
  dv.setInt32(22, height, true); // positive height = bottom-up
  dv.setUint16(26, 1, true); // planes
  dv.setUint16(28, 24, true); // bits per pixel
  dv.setUint32(30, 0, true); // BI_RGB
  dv.setUint32(34, imageSize, true);
  dv.setInt32(38, 2835, true); // 72 dpi
  dv.setInt32(42, 2835, true);
  let o = FILE_HEADER + INFO_HEADER;
  for (let y = height - 1; y >= 0; y--) {
    let p = y * width * 4;
    const rowStart = o;
    for (let x = 0; x < width; x++, p += 4) {
      const a = rgba[p + 3];
      out[o++] = flattenOnWhite(rgba[p + 2], a); // B
      out[o++] = flattenOnWhite(rgba[p + 1], a); // G
      out[o++] = flattenOnWhite(rgba[p], a); // R
    }
    o = rowStart + rowSize; // padding bytes stay 0
  }
  return out;
}
