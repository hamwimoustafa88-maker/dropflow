import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { encodeGif, hasTransparency } from "../lib/gif";
import { decodeHeic } from "../lib/heic";
import { decodeTiff, encodeTiff } from "../lib/tiff";

// Real files through the real decoders/encoders (libheif wasm, UTIF, gifenc) - no mocks.
const fixture = (name: string) => new Uint8Array(readFileSync(fileURLToPath(new URL(`./fixtures/${name}`, import.meta.url))));
// A licence-clean HEIC shared with the desktop tests (BSD-3-Clause, see tests/samples/README.md).
const HEIC_SAMPLE = fileURLToPath(new URL("../../../../tests/samples/heic/RGB_8__128x128.heic", import.meta.url));

const px = (rgba: ArrayLike<number>, width: number, x: number, y: number) => {
  const i = (y * width + x) * 4;
  return [rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]];
};

describe("HEIC input (libheif wasm)", () => {
  it("decodes a real HEVC-coded HEIC into RGBA of the right size", async () => {
    const bytes = new Uint8Array(readFileSync(HEIC_SAMPLE));
    const img = await decodeHeic(bytes, { importer: () => import("libheif-js/libheif-wasm/libheif-bundle.mjs") });
    expect([img.width, img.height]).toEqual([128, 128]);
    expect(img.data.length).toBe(128 * 128 * 4);
    // The sample is a colour test card: opaque, and not one flat colour.
    const colours = new Set<string>();
    for (let y = 0; y < 128; y += 16) for (let x = 0; x < 128; x += 16) colours.add(px(img.data, 128, x, y).join(","));
    expect(colours.size).toBeGreaterThan(4);
    for (const c of colours) expect(c.split(",")[3]).toBe("255");
  });

  it("refuses a file that is not HEIF with the decodeFailed error", async () => {
    await expect(
      decodeHeic(new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8]), { importer: () => import("libheif-js/libheif-wasm/libheif-bundle.mjs") }),
    ).rejects.toMatchObject({ code: "decodeFailed" });
  });
});

describe("TIFF in and out (UTIF)", () => {
  it("decodes an LZW TIFF written by another program (Windows GDI+) with exact colours", () => {
    const img = decodeTiff(fixture("quadrants-lzw.tiff"));
    expect([img.width, img.height]).toEqual([64, 48]);
    expect(px(img.data, 64, 5, 5)).toEqual([255, 0, 0, 255]);
    expect(px(img.data, 64, 50, 5)).toEqual([0, 255, 0, 255]);
    expect(px(img.data, 64, 5, 40)).toEqual([0, 0, 255, 255]);
    expect(px(img.data, 64, 50, 40)).toEqual([255, 255, 255, 255]);
  });

  it("round-trips RGBA (with transparency) losslessly", () => {
    const w = 7;
    const h = 5;
    const rgba = new Uint8ClampedArray(w * h * 4);
    for (let i = 0; i < rgba.length; i++) rgba[i] = (i * 37) % 256;
    const back = decodeTiff(encodeTiff(rgba, w, h));
    expect([back.width, back.height]).toEqual([w, h]);
    expect([...back.data]).toEqual([...rgba]);
  });

  it("refuses garbage with decodeFailed", () => {
    expect(() => decodeTiff(new Uint8Array([0x49, 0x49, 0x2a, 0, 0xff, 0xff, 0xff, 0x7f]))).toThrow(expect.objectContaining({ code: "decodeFailed" }));
  });
});

describe("GIF out (gifenc)", () => {
  const solid = (w: number, h: number, rgba: [number, number, number, number]) => {
    const d = new Uint8ClampedArray(w * h * 4);
    for (let i = 0; i < d.length; i += 4) d.set(rgba, i);
    return d;
  };

  it("writes a GIF89a with the right logical screen size", () => {
    const gif = encodeGif(solid(40, 30, [200, 30, 30, 255]), 40, 30);
    expect(String.fromCharCode(...gif.slice(0, 6))).toBe("GIF89a");
    expect(gif[6] | (gif[7] << 8)).toBe(40);
    expect(gif[8] | (gif[9] << 8)).toBe(30);
    expect(gif[gif.length - 1]).toBe(0x3b); // trailer
  });

  it("keeps transparency: a graphic control extension with the transparent flag", () => {
    const d = solid(8, 8, [10, 20, 30, 255]);
    for (let i = 0; i < 16 * 4; i += 4) d[i + 3] = 0; // first two rows transparent
    expect(hasTransparency(d)).toBe(true);
    const gif = encodeGif(d, 8, 8);
    let found = false;
    for (let i = 0; i + 3 < gif.length; i++) if (gif[i] === 0x21 && gif[i + 1] === 0xf9 && gif[i + 2] === 4 && (gif[i + 3] & 1) === 1) found = true;
    expect(found).toBe(true);
  });

  it("an opaque picture has no transparent flag", () => {
    const gif = encodeGif(solid(4, 4, [1, 2, 3, 255]), 4, 4);
    for (let i = 0; i + 3 < gif.length; i++) if (gif[i] === 0x21 && gif[i + 1] === 0xf9 && gif[i + 2] === 4) expect(gif[i + 3] & 1).toBe(0);
  });
});
