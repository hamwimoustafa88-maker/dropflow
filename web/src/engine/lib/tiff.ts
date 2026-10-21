// TIFF in and out, in plain JavaScript (UTIF.js via the `utif2` package, MIT). No browser decodes TIFF itself (only
// Safari), so DropFlow Web reads it here. Loaded lazily (dynamic import) only when a TIFF is involved.
// Node-compatible: no DOM.

import UTIF from "utif2";
import { ConvertError } from "../types";

export interface Rgba {
  width: number;
  height: number;
  /** Interleaved 8-bit RGBA, width * height * 4 bytes. */
  data: Uint8ClampedArray;
}

/** Decodes the first (largest, when several) page of a TIFF into RGBA. */
export function decodeTiff(bytes: Uint8Array): Rgba {
  let ifds: UTIF.IFD[];
  try {
    const buf = bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) as ArrayBuffer;
    ifds = UTIF.decode(buf);
    // Thumbnails (sub-files) come as extra IFDs: pick the biggest real image.
    let best: UTIF.IFD | null = null;
    for (const ifd of ifds) {
      const w = Number((ifd.t256 as number[] | undefined)?.[0] ?? 0);
      const h = Number((ifd.t257 as number[] | undefined)?.[0] ?? 0);
      if (!best || w * h > Number((best.t256 as number[])[0]) * Number((best.t257 as number[])[0])) {
        if (w > 0 && h > 0) best = ifd;
      }
    }
    if (!best) throw new Error("no image in TIFF");
    // The third argument (all IFDs) lets UTIF resolve JPEG tables shared between pages; the typings omit it.
    (UTIF.decodeImage as (b: ArrayBuffer, ifd: UTIF.IFD, all: UTIF.IFD[]) => void)(buf, best, ifds);
    const width = best.width;
    const height = best.height;
    if (!(width > 0 && height > 0)) throw new Error("empty TIFF");
    const rgba = UTIF.toRGBA8(best);
    if (rgba.length < width * height * 4) throw new Error("short TIFF data");
    return { width, height, data: new Uint8ClampedArray(rgba.buffer, rgba.byteOffset, width * height * 4) };
  } catch (e) {
    if (e instanceof ConvertError) throw e;
    throw new ConvertError("decodeFailed", {}, e instanceof Error ? e.message : String(e));
  }
}

/** Encodes RGBA as an uncompressed RGBA TIFF (what UTIF writes; every TIFF reader opens it). */
export function encodeTiff(rgba: Uint8ClampedArray | Uint8Array, width: number, height: number): Uint8Array {
  const data = rgba instanceof Uint8Array ? rgba : new Uint8Array(rgba.buffer, rgba.byteOffset, rgba.byteLength);
  return new Uint8Array(UTIF.encodeImage(data, width, height));
}
