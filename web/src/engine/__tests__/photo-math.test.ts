import { describe, expect, it } from "vitest";
import {
  adjustPixels,
  backgroundCanvasSize,
  boxBlur,
  clipRect,
  collageLayout,
  coverRect,
  faviconHtml,
  hexToRgb,
  isIdentity,
  parseBackground,
  parseHexColor,
  parseRegions,
  pixelate,
  rotatedSize,
  sharpen,
  webManifest,
} from "../lib/photo-math";
import { photoConverter, PHOTO_TOOLS } from "../converters/photo";
import catalog from "@/data/catalog.json";

const pixel = (...rgba: number[]) => new Uint8ClampedArray(rgba);

describe("photo maths (pure)", () => {
  it("colours: hex parsing, gradients, errors", () => {
    expect(parseHexColor("#ABC")).toBe("#aabbcc");
    expect(parseHexColor("4338ca")).toBe("#4338ca");
    expect(parseHexColor("red")).toBeNull();
    expect(hexToRgb("#0e7490")).toEqual([14, 116, 144]);
    expect(parseBackground("")).toEqual({ kind: "color", color: "#ffffff" });
    expect(parseBackground("gradient:#4338ca:#0e7490")).toEqual({ kind: "gradient", from: "#4338ca", to: "#0e7490" });
    expect(() => parseBackground("blue")).toThrow(expect.objectContaining({ code: "invalidColor" }));
    expect(() => parseBackground("gradient:#fff")).toThrow(expect.objectContaining({ code: "invalidColor" }));
  });

  it("adjustments: identity, brightness, contrast, saturation, filters; alpha untouched", () => {
    const base = { brightness: 0, contrast: 0, saturation: 100, filter: "none" as const };
    expect(isIdentity(base)).toBe(true);
    let p = pixel(100, 150, 200, 77);
    adjustPixels(p, base);
    expect([...p]).toEqual([100, 150, 200, 77]);
    p = pixel(100, 150, 200, 77);
    adjustPixels(p, { ...base, filter: "invert" });
    expect([...p]).toEqual([155, 105, 55, 77]);
    p = pixel(100, 150, 200, 255);
    adjustPixels(p, { ...base, filter: "grayscale" });
    expect(p[0]).toBe(p[1]);
    expect(p[1]).toBe(p[2]);
    p = pixel(100, 100, 100, 255);
    adjustPixels(p, { ...base, brightness: -100 });
    expect([...p.slice(0, 3)]).toEqual([0, 0, 0]);
    p = pixel(100, 160, 128, 255);
    adjustPixels(p, { ...base, contrast: -100 });
    expect([...p.slice(0, 3)]).toEqual([128, 128, 128]);
    p = pixel(200, 50, 50, 255);
    adjustPixels(p, { ...base, saturation: 0 });
    expect(p[0]).toBe(p[1]);
    p = pixel(100, 100, 100, 255);
    adjustPixels(p, { ...base, filter: "sepia" });
    expect(p[0]).toBeGreaterThan(p[1]);
    expect(p[1]).toBeGreaterThan(p[2]);
  });

  it("box blur keeps a flat picture flat and softens an edge; sharpen raises edge contrast", () => {
    const w = 10;
    const h = 4;
    const flat = new Uint8ClampedArray(w * h * 4).fill(90);
    expect([...boxBlur(flat, w, h, 2)]).toEqual([...flat]);
    const edge = new Uint8ClampedArray(w * h * 4);
    for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) edge.fill(x < 5 ? 0 : 255, (y * w + x) * 4, (y * w + x) * 4 + 4);
    const soft = boxBlur(edge, w, h, 2);
    const at = (a: Uint8ClampedArray, x: number) => a[(1 * w + x) * 4];
    expect(at(soft, 4)).toBeGreaterThan(0);
    expect(at(soft, 5)).toBeLessThan(255);
    expect(at(soft, 0)).toBeLessThan(at(soft, 4));
    expect(at(soft, 9)).toBeGreaterThan(at(soft, 5));
    const ramp = new Uint8ClampedArray(w * h * 4);
    for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) ramp.fill(x < 5 ? 100 : 150, (y * w + x) * 4, (y * w + x) * 4 + 4);
    sharpen(ramp, w, h, 1);
    expect(at(ramp, 4)).toBeLessThan(100);
    expect(at(ramp, 5)).toBeGreaterThan(150);
  });

  it("rotation swaps the size on quarter turns", () => {
    expect(rotatedSize(40, 30, 90)).toEqual({ width: 30, height: 40 });
    expect(rotatedSize(40, 30, 180)).toEqual({ width: 40, height: 30 });
  });

  it("background canvas: same numbers as the desktop BackgroundTool.CanvasSize", () => {
    expect(backgroundCanvasSize(1000, 500, 8, "original")).toEqual({ width: 1160, height: 580 });
    expect(backgroundCanvasSize(1000, 500, 0, "1:1")).toEqual({ width: 1000, height: 1000 });
    expect(backgroundCanvasSize(400, 800, 10, "16:9")).toEqual({ width: 1707, height: 960 });
    expect(backgroundCanvasSize(100, 100, 99, "original")).toEqual({ width: 180, height: 180 }); // padding capped at 40%
  });

  it("redact regions: parsing is strict (a privacy tool never guesses) and clipping keeps them inside", () => {
    expect(parseRegions("10,20,30,40; 0,0,5,5")).toEqual([
      { x: 10, y: 20, width: 30, height: 40 },
      { x: 0, y: 0, width: 5, height: 5 },
    ]);
    expect(parseRegions("  ")).toEqual([]);
    expect(() => parseRegions("10,20,30")).toThrow(expect.objectContaining({ code: "invalidRegions" }));
    expect(() => parseRegions("10,20,0,5")).toThrow(expect.objectContaining({ code: "invalidRegions" }));
    expect(() => parseRegions("a,b,c,d")).toThrow(expect.objectContaining({ code: "invalidRegions" }));
    expect(clipRect({ x: -10, y: 5, width: 30, height: 100 }, 50, 50)).toEqual({ x: 0, y: 5, width: 20, height: 45 });
    expect(clipRect({ x: 60, y: 5, width: 30, height: 10 }, 50, 50)).toBeNull();
    expect(clipRect({ x: -40, y: 0, width: 30, height: 10 }, 50, 50)).toBeNull();
  });

  it("pixelate makes every block one colour", () => {
    const w = 4;
    const d = new Uint8ClampedArray(w * w * 4);
    for (let i = 0; i < d.length; i++) d[i] = (i * 29) % 256;
    pixelate(d, w, { x: 0, y: 0, width: 4, height: 4 }, 2);
    const block = (x: number, y: number) => [...d.slice((y * w + x) * 4, (y * w + x) * 4 + 4)];
    expect(block(0, 0)).toEqual(block(1, 1));
    expect(block(2, 0)).toEqual(block(3, 1));
  });

  it("collage layout: same maths as the desktop CollageTool.Layout", () => {
    const grid = collageLayout("grid", 5, 0, 100, 10);
    expect([grid.width, grid.height]).toEqual([340, 230]); // 3 columns x 2 rows
    expect(grid.cells[4]).toEqual({ x: 120, y: 120, width: 100, height: 100 });
    const row = collageLayout("row", 3, 0, 100, 10);
    expect([row.width, row.height]).toEqual([340, 120]);
    const featured = collageLayout("featured", 3, 0, 100, 10);
    expect(featured.cells[0]).toEqual({ x: 10, y: 10, width: 200, height: 200 });
    expect(featured.cells.length).toBe(3);
    expect(collageLayout("featured", 2, 0, 100, 10).cells.length).toBe(2); // falls back to a grid
  });

  it("cover crop fills the cell from the centre", () => {
    expect(coverRect(200, 100, 100, 100)).toEqual({ x: 50, y: 0, width: 100, height: 100 });
    expect(coverRect(100, 200, 50, 50)).toEqual({ x: 0, y: 50, width: 100, height: 100 });
  });

  it("favicon set texts", () => {
    const m = JSON.parse(webManifest("My site"));
    expect(m.icons.map((i: { sizes: string }) => i.sizes)).toEqual(["192x192", "512x512"]);
    expect(faviconHtml()).toContain('rel="apple-touch-icon"');
  });
});

describe("photo converter manifest", () => {
  it("claims every decodable picture (HEIC and TIFF included), nothing else", () => {
    for (const op of PHOTO_TOOLS) {
      for (const ext of ["png", "jpg", "heic", "tiff", "svg", "gif"]) expect(photoConverter.supports(op, ext), `${op} ${ext}`).toBe(true);
      for (const ext of ["pdf", "txt", "mp4", ""]) expect(photoConverter.supports(op, ext), `${op} ${ext}`).toBe(false);
      expect(photoConverter.suffix(op)).toMatch(/^[a-z]+$/);
    }
  });

  it("every tool is web-enabled in the catalog, collage is the only multi one", () => {
    for (const id of PHOTO_TOOLS) {
      const tool = catalog.tools.find((t) => t.id === id) as { platforms: string[]; multi?: boolean } | undefined;
      expect(tool, id).toBeDefined();
      expect(tool!.platforms).toContain("web");
      expect(!!tool!.multi).toBe(id === "img.collage");
    }
  });

  it("options mirror the desktop tools (keys and defaults)", () => {
    const keys = (op: string) => photoConverter.options(op).map((o) => `${o.key}=${String(o.default)}`);
    expect(keys("img.edit")).toEqual(["brightness=0", "contrast=0", "saturation=100", "sharpen=0", "blur=0", "filter=none", "rotate=0", "flipH=false", "flipV=false"]);
    expect(keys("img.background")).toEqual(["background=#ffffff", "aspect=original", "padding=8", "radius=0", "shadow=false"]);
    expect(keys("img.redact")).toEqual(["regions=", "style=solid", "color=#000000"]);
    expect(keys("img.collage")).toEqual(["layout=grid", "columns=0", "cell=600", "spacing=16", "radius=0", "background=#ffffff", "format=jpg"]);
    expect(photoConverter.options("img.qr")).toEqual([]);
  });
});
