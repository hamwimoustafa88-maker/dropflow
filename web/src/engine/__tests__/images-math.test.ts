import { describe, expect, it } from "vitest";
import { DEFAULT_IMAGE_CAPS, type ImageCaps } from "../caps";
import {
  canDecode,
  canEncode,
  extForMime,
  imageDimensions,
  jpegDimensions,
  mimeForExt,
  sameFormatForCompress,
  sameFormatForEdit,
  sniffKind,
  svgSize,
} from "../lib/image-formats";
import {
  A4,
  applyMatrix,
  containRect,
  cropRect,
  needsManualOrientation,
  orientationMatrix,
  parseRatio,
  pdfPlacement,
  resizeDims,
  resizeSteps,
  searchQuality,
} from "../lib/image-math";
import { buildJpeg, buildPng, buildWebp, ascii, concat, u32le } from "./images-fixtures";

const caps = (over: Partial<ImageCaps["encode"]> = {}, decodeAvif = false): ImageCaps => ({
  encode: { ...DEFAULT_IMAGE_CAPS.encode, ...over },
  decodeAvif,
});

describe("cropRect", () => {
  it("takes the centred maximum area for each ratio", () => {
    expect(cropRect(1000, 500, 1, 1)).toEqual({ x: 250, y: 0, width: 500, height: 500 });
    expect(cropRect(500, 1000, 1, 1)).toEqual({ x: 0, y: 250, width: 500, height: 500 });
    expect(cropRect(1920, 1080, 4, 3)).toEqual({ x: 240, y: 0, width: 1440, height: 1080 });
    expect(cropRect(1000, 1000, 16, 9)).toEqual({ x: 0, y: 218, width: 1000, height: 563 });
    expect(cropRect(1000, 1000, 9, 16)).toEqual({ x: 218, y: 0, width: 563, height: 1000 });
  });

  it("never exceeds the source and is at least 1px, for tiny and extreme inputs", () => {
    for (const [w, h] of [[1, 1], [2, 1], [1, 5000], [5000, 1], [3, 3]]) {
      for (const [rw, rh] of [[1, 1], [16, 9], [9, 16], [3, 2]]) {
        const r = cropRect(w, h, rw, rh);
        expect(r.width).toBeGreaterThanOrEqual(1);
        expect(r.height).toBeGreaterThanOrEqual(1);
        expect(r.x).toBeGreaterThanOrEqual(0);
        expect(r.y).toBeGreaterThanOrEqual(0);
        expect(r.x + r.width).toBeLessThanOrEqual(w);
        expect(r.y + r.height).toBeLessThanOrEqual(h);
      }
    }
  });

  it("parses ratios", () => {
    expect(parseRatio("16:9")).toEqual([16, 9]);
    expect(parseRatio(" 3 : 2 ")).toEqual([3, 2]);
    expect(parseRatio("0:1")).toBeNull();
    expect(parseRatio("abc")).toBeNull();
  });
});

describe("resizeDims", () => {
  it("derives the missing side from the ratio", () => {
    expect(resizeDims(2000, 1000, 500, 0, true)).toEqual({ width: 500, height: 250 });
    expect(resizeDims(2000, 1000, 0, 100, false)).toEqual({ width: 200, height: 100 });
  });
  it("fits inside the box with keepRatio, stretches without", () => {
    expect(resizeDims(2000, 1000, 400, 400, true)).toEqual({ width: 400, height: 200 });
    expect(resizeDims(1000, 2000, 400, 400, true)).toEqual({ width: 200, height: 400 });
    expect(resizeDims(2000, 1000, 400, 400, false)).toEqual({ width: 400, height: 400 });
  });
  it("rejects both zero and sizes beyond the canvas limits", () => {
    expect(resizeDims(100, 100, 0, 0, true)).toBeNull();
    expect(resizeDims(100, 100, 20000, 0, true)).toBeNull();
    expect(resizeDims(100, 100, 16385, 10, false)).toBeNull();
    expect(resizeDims(100, 100, 16384, 16384, false)).toEqual({ width: 16384, height: 16384 }); // exactly the limit
    expect(resizeDims(100, 100, -5, 10, true)).toBeNull();
  });
  it("allows upscaling and never produces 0", () => {
    expect(resizeDims(100, 50, 400, 0, true)).toEqual({ width: 400, height: 200 });
    expect(resizeDims(10000, 10, 5, 0, true)).toEqual({ width: 5, height: 1 });
  });
});

describe("resizeSteps", () => {
  it("halves big reductions and ends exactly at the target", () => {
    const s = resizeSteps(4000, 3000, 250, 188);
    expect(s[s.length - 1]).toEqual({ width: 250, height: 188 });
    expect(s.length).toBeGreaterThan(2);
    for (let i = 1; i < s.length; i++) {
      expect(s[i].width).toBeLessThanOrEqual(s[i - 1].width);
      expect(s[i].width).toBeGreaterThanOrEqual(s[i - 1].width / 2 - 1);
    }
  });
  it("is a single step for small reductions, upscales and no-ops", () => {
    expect(resizeSteps(1000, 800, 600, 480)).toEqual([{ width: 600, height: 480 }]);
    expect(resizeSteps(100, 100, 400, 400)).toEqual([{ width: 400, height: 400 }]);
    expect(resizeSteps(100, 100, 100, 100)).toEqual([{ width: 100, height: 100 }]);
  });
  it("copes with one very reduced axis", () => {
    const s = resizeSteps(10000, 100, 10, 100);
    expect(s[s.length - 1]).toEqual({ width: 10, height: 100 });
    expect(s.every((x) => x.height === 100)).toBe(true);
  });
});

describe("containRect", () => {
  it("centres a non-square image inside a square", () => {
    expect(containRect(200, 100, 32)).toEqual({ x: 0, y: 8, width: 32, height: 16 });
    expect(containRect(100, 200, 32)).toEqual({ x: 8, y: 0, width: 16, height: 32 });
    expect(containRect(64, 64, 16)).toEqual({ x: 0, y: 0, width: 16, height: 16 });
  });
});

describe("searchQuality", () => {
  /** Fake monotonic encoder: size grows with quality. */
  const encoder = (sizeAt: (q: number) => number) => {
    const calls: number[] = [];
    return { calls, encode: async (q: number) => (calls.push(q), { size: sizeAt(q) }) };
  };

  it("returns the highest quality that fits", async () => {
    const e = encoder((q) => q * 1000); // 10 KB .. 100 KB
    const r = await searchQuality(e.encode, 57_500);
    expect(r?.quality).toBe(57);
    expect(r?.result.size).toBe(57_000);
    expect(e.calls.length).toBeLessThanOrEqual(10); // O(log n), not a linear scan
  });

  it("returns max when everything fits and null when even min is too big", async () => {
    expect((await searchQuality(encoder((q) => q).encode, 1_000_000))?.quality).toBe(100);
    expect(await searchQuality(encoder((q) => q * 1000).encode, 5_000)).toBeNull();
  });

  it("respects a custom range and exact boundaries", async () => {
    const r = await searchQuality(encoder((q) => q * 10).encode, 600, { min: 20, max: 80 });
    expect(r?.quality).toBe(60);
    const top = await searchQuality(encoder((q) => q * 10).encode, 10_000, { min: 20, max: 80 });
    expect(top?.quality).toBe(80);
    const low = await searchQuality(encoder((q) => q * 10).encode, 200, { min: 20, max: 80 });
    expect(low?.quality).toBe(20);
  });

  it("copes with a plateau (size independent of quality, like PNG)", async () => {
    expect((await searchQuality(encoder(() => 500).encode, 600))?.quality).toBe(100);
    expect(await searchQuality(encoder(() => 500).encode, 400)).toBeNull();
  });
});

describe("EXIF orientation geometry", () => {
  // Source pixel corners of a w x h picture; the matrix must put them where the rotated/flipped picture has them.
  const w = 4;
  const h = 2;
  const cases: Record<number, { size: [number, number]; topLeft: [number, number]; topRight: [number, number] }> = {
    1: { size: [4, 2], topLeft: [0, 0], topRight: [4, 0] },
    2: { size: [4, 2], topLeft: [4, 0], topRight: [0, 0] }, // mirrored horizontally
    3: { size: [4, 2], topLeft: [4, 2], topRight: [0, 2] }, // rotated 180
    4: { size: [4, 2], topLeft: [0, 2], topRight: [4, 2] }, // mirrored vertically
    5: { size: [2, 4], topLeft: [0, 0], topRight: [0, 4] }, // transpose
    6: { size: [2, 4], topLeft: [2, 0], topRight: [2, 4] }, // rotate 90 CW
    7: { size: [2, 4], topLeft: [2, 4], topRight: [2, 0] }, // transverse
    8: { size: [2, 4], topLeft: [0, 4], topRight: [0, 0] }, // rotate 270 CW
  };
  it.each(Object.keys(cases).map(Number))("orientation %i maps the source corners correctly", (o) => {
    const m = orientationMatrix(o, w, h);
    expect([m.width, m.height]).toEqual(cases[o].size);
    expect(applyMatrix(m.matrix, 0, 0)).toEqual(cases[o].topLeft);
    expect(applyMatrix(m.matrix, w, 0)).toEqual(cases[o].topRight);
  });
  it("treats unknown values as identity", () => {
    expect(orientationMatrix(0, 5, 3)).toEqual({ matrix: [1, 0, 0, 1, 0, 0], width: 5, height: 3 });
    expect(orientationMatrix(99, 5, 3).width).toBe(5);
  });
  it("flags only transposed orientations on a bitmap that kept the raw dimensions", () => {
    expect(needsManualOrientation(6, 400, 300, 400, 300)).toBe(true);
    expect(needsManualOrientation(6, 400, 300, 300, 400)).toBe(false); // browser already rotated
    expect(needsManualOrientation(3, 400, 300, 400, 300)).toBe(false);
    expect(needsManualOrientation(1, 400, 300, 400, 300)).toBe(false);
    expect(needsManualOrientation(8, 300, 300, 300, 300)).toBe(false); // square: cannot tell
  });
});

describe("pdfPlacement", () => {
  it("fit: page equals the image", () => {
    expect(pdfPlacement(640, 480, "fit")).toEqual({ pageWidth: 640, pageHeight: 480, x: 0, y: 0, width: 640, height: 480 });
  });
  it("a4: portrait vs landscape, 20pt margin, centred, aspect kept", () => {
    const p = pdfPlacement(1000, 2000, "a4");
    expect(p.pageWidth).toBe(A4.width);
    expect(p.pageHeight).toBe(A4.height);
    expect(p.width / p.height).toBeCloseTo(0.5, 6);
    expect(p.x).toBeGreaterThanOrEqual(20 - 1e-9);
    expect(p.y).toBeGreaterThanOrEqual(20 - 1e-9);
    expect(p.x + p.width).toBeLessThanOrEqual(A4.width - 20 + 1e-9);
    expect(p.y + p.height).toBeLessThanOrEqual(A4.height - 20 + 1e-9);
    expect(p.x + p.width / 2).toBeCloseTo(A4.width / 2, 6);
    const l = pdfPlacement(2000, 1000, "a4");
    expect(l.pageWidth).toBe(A4.height);
    expect(l.pageHeight).toBe(A4.width);
    // small images are scaled up to the page, not left tiny
    expect(pdfPlacement(50, 50, "a4").width).toBeGreaterThan(400);
  });
});

describe("formats", () => {
  it("maps extensions and mime types both ways (jpeg alias included)", () => {
    expect(mimeForExt("jpg")).toBe("image/jpeg");
    expect(mimeForExt("jpeg")).toBe("image/jpeg");
    expect(mimeForExt("ico")).toBe("image/x-icon");
    expect(mimeForExt("pdf")).toBe("application/pdf");
    expect(mimeForExt("heic")).toBe("image/heic");
    expect(mimeForExt("tif")).toBe("image/tiff");
    expect(extForMime("image/heif")).toBe("heic");
    expect(mimeForExt("docx")).toBeUndefined();
    expect(extForMime("image/jpeg")).toBe("jpg");
    expect(extForMime("image/jpg")).toBe("jpg");
    expect(extForMime("IMAGE/PNG; charset=x")).toBe("png");
    expect(extForMime("image/vnd.microsoft.icon")).toBe("ico");
    expect(extForMime("text/plain")).toBeUndefined();
  });

  it("decode/encode gating follows the detected caps; HEIC and TIFF are always decodable (wasm/JS)", () => {
    expect(canDecode("png", caps())).toBe(true);
    expect(canDecode("jpeg", caps())).toBe(true);
    expect(canDecode("avif", caps())).toBe(false);
    expect(canDecode("avif", caps({}, true))).toBe(true);
    expect(canDecode("heic", caps())).toBe(true);
    expect(canDecode("heif", caps())).toBe(true);
    expect(canDecode("tiff", caps())).toBe(true);
    expect(canDecode("tif", caps())).toBe(true);
    expect(canDecode("docx", caps())).toBe(false);
    expect(canEncode("webp", caps())).toBe(false);
    expect(canEncode("webp", caps({ webp: true }))).toBe(true);
    expect(canEncode("avif", caps({ avif: true }))).toBe(true);
    expect(canEncode("gif", caps({ avif: true }))).toBe(false);
  });

  it("'same format' decisions", () => {
    expect(sameFormatForCompress("png", caps())).toBe("png");
    expect(sameFormatForCompress("jpeg", caps())).toBe("jpg");
    expect(sameFormatForCompress("webp", caps())).toBe("jpg"); // webp not encodable here
    expect(sameFormatForCompress("webp", caps({ webp: true }))).toBe("webp");
    expect(sameFormatForCompress("bmp", caps())).toBe("jpg");
    expect(sameFormatForEdit("gif", caps())).toBe("png");
    expect(sameFormatForEdit("jpg", caps())).toBe("jpg");
    expect(sameFormatForEdit("webp", caps())).toBe("png");
    expect(sameFormatForEdit("webp", caps({ webp: true }))).toBe("webp");
  });

  it("sniffs magic bytes", () => {
    expect(sniffKind(buildJpeg().bytes)).toBe("jpeg");
    expect(sniffKind(buildPng(1, 1))).toBe("png");
    expect(sniffKind(buildWebp())).toBe("webp");
    expect(sniffKind(ascii("GIF89a"))).toBeNull();
    expect(sniffKind(new Uint8Array())).toBeNull();
  });

  it("reads dimensions from headers", () => {
    expect(jpegDimensions(buildJpeg({ width: 321, height: 123 }).bytes)).toEqual({ width: 321, height: 123 });
    expect(imageDimensions(buildPng(7, 5))).toEqual({ width: 7, height: 5 });
    expect(imageDimensions(buildWebp({ width: 300, height: 200 }))).toEqual({ width: 300, height: 200 });
    expect(imageDimensions(concat(ascii("GIF89a"), new Uint8Array([10, 0, 20, 0])))).toEqual({ width: 10, height: 20 });
    const bmp = new Uint8Array(30);
    bmp[0] = 0x42;
    bmp[1] = 0x4d;
    bmp.set(u32le(12), 18);
    bmp.set(u32le(0xfffffffd), 22); // -3 => top-down 3 rows
    expect(imageDimensions(bmp)).toEqual({ width: 12, height: 3 });
    expect(imageDimensions(new Uint8Array([1, 2, 3]))).toBeNull();
    expect(jpegDimensions(buildJpeg().bytes.subarray(0, 10))).toBeNull();
  });
});

describe("svgSize", () => {
  it("uses numeric width/height (px or unitless)", () => {
    expect(svgSize('<svg xmlns="http://www.w3.org/2000/svg" width="120" height="60"></svg>')).toEqual({ width: 120, height: 60 });
    expect(svgSize("<svg width='80px' height='40.5px'>")).toEqual({ width: 80, height: 41 });
  });
  it("falls back to the viewBox scaled to 1024 on the longest side", () => {
    expect(svgSize('<svg viewBox="0 0 24 24">')).toEqual({ width: 1024, height: 1024 });
    expect(svgSize('<svg viewBox="0 0 200 100" width="100%" height="100%">')).toEqual({ width: 1024, height: 512 });
  });
  it("honours one explicit side plus a viewBox", () => {
    expect(svgSize('<svg viewBox="0 0 200 100" width="400">')).toEqual({ width: 400, height: 200 });
  });
  it("returns null without usable size information and clamps absurd sizes", () => {
    expect(svgSize("<svg>")).toBeNull();
    expect(svgSize('<svg width="50%" height="50%">')).toBeNull();
    expect(svgSize("not svg")).toBeNull();
    const big = svgSize('<svg width="100000" height="50000">')!;
    expect(Math.max(big.width, big.height)).toBe(4096);
  });
});
