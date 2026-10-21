// ICO encoder: a directory of PNG-compressed images (supported since Windows Vista and by every browser).
// Pure, Node-compatible.

export interface IcoEntry {
  /** Edge length in pixels (1..256); the image is square. */
  size: number;
  /** A complete PNG file of size x size pixels. */
  png: Uint8Array;
}

/** Standard icon sizes <= max(w, h), but never fewer than the 16 px entry. */
export function icoSizesFor(width: number, height: number): number[] {
  const longest = Math.max(width, height);
  const sizes = [16, 32, 48, 64, 128, 256].filter((s) => s <= longest);
  return sizes.length ? sizes : [16];
}

export function encodeIco(entries: IcoEntry[]): Uint8Array {
  if (entries.length < 1 || entries.length > 0xffff) throw new RangeError("bad entry count");
  const headerSize = 6 + 16 * entries.length;
  const total = entries.reduce((sum, e) => sum + e.png.length, headerSize);
  const out = new Uint8Array(total);
  const dv = new DataView(out.buffer);
  dv.setUint16(0, 0, true); // reserved
  dv.setUint16(2, 1, true); // type: icon
  dv.setUint16(4, entries.length, true);
  let offset = headerSize;
  entries.forEach((e, i) => {
    if (!Number.isInteger(e.size) || e.size < 1 || e.size > 256) throw new RangeError("icon size must be 1..256");
    const p = 6 + 16 * i;
    const dim = e.size === 256 ? 0 : e.size; // 0 means 256
    out[p] = dim;
    out[p + 1] = dim;
    out[p + 2] = 0; // palette colours
    out[p + 3] = 0; // reserved
    dv.setUint16(p + 4, 1, true); // colour planes
    dv.setUint16(p + 6, 32, true); // bits per pixel
    dv.setUint32(p + 8, e.png.length, true);
    dv.setUint32(p + 12, offset, true);
    out.set(e.png, offset);
    offset += e.png.length;
  });
  return out;
}
