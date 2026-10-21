import { describe, expect, it } from "vitest";
import { encodeBmp24, flattenOnWhite } from "../lib/bmp";
import { encodeIco, icoSizesFor } from "../lib/ico";
import { buildPng } from "./images-fixtures";

/** Independent BMP reader: returns top-down RGB rows, validating the structure on the way. */
function readBmp(b: Uint8Array) {
  const dv = new DataView(b.buffer, b.byteOffset, b.byteLength);
  expect(String.fromCharCode(b[0], b[1])).toBe("BM");
  expect(dv.getUint32(2, true)).toBe(b.length); // file size field
  const offset = dv.getUint32(10, true);
  expect(dv.getUint32(14, true)).toBe(40); // BITMAPINFOHEADER
  const width = dv.getInt32(18, true);
  const heightRaw = dv.getInt32(22, true);
  expect(dv.getUint16(26, true)).toBe(1);
  expect(dv.getUint16(28, true)).toBe(24);
  expect(dv.getUint32(30, true)).toBe(0); // BI_RGB
  const height = Math.abs(heightRaw);
  const stride = Math.ceil((width * 24) / 32) * 4;
  expect(dv.getUint32(34, true)).toBe(stride * height);
  expect(b.length).toBe(offset + stride * height);
  const bottomUp = heightRaw > 0;
  const pixels: [number, number, number][][] = [];
  for (let row = 0; row < height; row++) {
    const y = bottomUp ? height - 1 - row : row; // file row -> picture row
    const rowPixels: [number, number, number][] = [];
    for (let x = 0; x < width; x++) {
      const p = offset + row * stride + x * 3;
      rowPixels.push([b[p + 2], b[p + 1], b[p]]); // BGR -> RGB
    }
    // padding bytes are zero
    for (let p = offset + row * stride + width * 3; p < offset + (row + 1) * stride; p++) expect(b[p]).toBe(0);
    pixels[y] = rowPixels;
  }
  return { width, height, pixels, bottomUp };
}

function gradient(w: number, h: number, alpha = 255): Uint8ClampedArray {
  const px = new Uint8ClampedArray(w * h * 4);
  for (let y = 0; y < h; y++)
    for (let x = 0; x < w; x++) {
      const i = (y * w + x) * 4;
      px[i] = (x * 53 + y * 7) & 255;
      px[i + 1] = (x * 11 + y * 91) & 255;
      px[i + 2] = (x * 29 + y * 3 + 40) & 255;
      px[i + 3] = alpha;
    }
  return px;
}

describe("encodeBmp24", () => {
  it.each([1, 2, 3, 4, 5, 7])("round-trips a %ipx wide image with correct row padding", (w) => {
    const h = 3;
    const src = gradient(w, h);
    const bmp = readBmp(encodeBmp24(src, w, h));
    expect(bmp.width).toBe(w);
    expect(bmp.height).toBe(h);
    expect(bmp.bottomUp).toBe(true);
    for (let y = 0; y < h; y++)
      for (let x = 0; x < w; x++) {
        const i = (y * w + x) * 4;
        expect(bmp.pixels[y][x]).toEqual([src[i], src[i + 1], src[i + 2]]);
      }
  });

  it("flattens transparency onto white", () => {
    expect(flattenOnWhite(0, 0)).toBe(255);
    expect(flattenOnWhite(10, 255)).toBe(10);
    expect(flattenOnWhite(0, 128)).toBe(127); // black at 50% over white
    const px = new Uint8ClampedArray([0, 0, 0, 0, 255, 0, 0, 255]); // transparent black, opaque red
    const bmp = readBmp(encodeBmp24(px, 2, 1));
    expect(bmp.pixels[0][0]).toEqual([255, 255, 255]);
    expect(bmp.pixels[0][1]).toEqual([255, 0, 0]);
  });

  it("stores the first source row last (bottom-up)", () => {
    const px = new Uint8ClampedArray([1, 2, 3, 255, 9, 8, 7, 255]); // 1x2: top = (1,2,3), bottom = (9,8,7)
    const out = encodeBmp24(px, 1, 2);
    expect([out[54 + 2], out[54 + 1], out[54]]).toEqual([9, 8, 7]); // first stored row is the bottom picture row
  });

  it("rejects bad sizes and short buffers", () => {
    expect(() => encodeBmp24(new Uint8ClampedArray(4), 0, 1)).toThrow(RangeError);
    expect(() => encodeBmp24(new Uint8ClampedArray(4), 2, 1)).toThrow(RangeError);
  });
});

describe("ICO", () => {
  it("chooses standard sizes up to the image's longest side (min 16)", () => {
    expect(icoSizesFor(1000, 800)).toEqual([16, 32, 48, 64, 128, 256]);
    expect(icoSizesFor(64, 64)).toEqual([16, 32, 48, 64]);
    expect(icoSizesFor(50, 20)).toEqual([16, 32, 48]);
    expect(icoSizesFor(10, 10)).toEqual([16]);
    expect(icoSizesFor(1, 1)).toEqual([16]);
  });

  it("writes a valid header, directory, PNG payloads and stores 256 as 0", () => {
    const e16 = { size: 16, png: buildPng(16, 16) };
    const e256 = { size: 256, png: buildPng(256, 256) };
    const ico = encodeIco([e16, e256]);
    const dv = new DataView(ico.buffer, ico.byteOffset, ico.byteLength);
    expect(dv.getUint16(0, true)).toBe(0);
    expect(dv.getUint16(2, true)).toBe(1); // icon
    expect(dv.getUint16(4, true)).toBe(2); // count
    // entry 0
    expect(ico[6]).toBe(16);
    expect(ico[7]).toBe(16);
    expect(dv.getUint16(6 + 4, true)).toBe(1); // planes
    expect(dv.getUint16(6 + 6, true)).toBe(32); // bpp
    expect(dv.getUint32(6 + 8, true)).toBe(e16.png.length);
    expect(dv.getUint32(6 + 12, true)).toBe(6 + 32);
    // entry 1: 256 is stored as 0
    expect(ico[22]).toBe(0);
    expect(ico[23]).toBe(0);
    expect(dv.getUint32(22 + 8, true)).toBe(e256.png.length);
    expect(dv.getUint32(22 + 12, true)).toBe(6 + 32 + e16.png.length);
    // payloads are intact PNGs at their offsets
    const sig = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    for (const [entry, e] of [[6, e16], [22, e256]] as const) {
      const off = dv.getUint32(entry + 12, true);
      const len = dv.getUint32(entry + 8, true);
      expect(Array.from(ico.subarray(off, off + 8))).toEqual(sig);
      expect(ico.subarray(off, off + len)).toEqual(e.png);
    }
    expect(ico.length).toBe(6 + 32 + e16.png.length + e256.png.length);
  });

  it("rejects empty lists and out-of-range sizes", () => {
    expect(() => encodeIco([])).toThrow(RangeError);
    expect(() => encodeIco([{ size: 512, png: buildPng(1, 1) }])).toThrow(RangeError);
    expect(() => encodeIco([{ size: 0, png: buildPng(1, 1) }])).toThrow(RangeError);
  });
});
