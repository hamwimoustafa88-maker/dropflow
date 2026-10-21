import { describe, expect, it } from "vitest";
import { fitRenderScale, MAX_CANVAS_PIXELS, MAX_CANVAS_SIDE } from "../lib/pdf-canvas";
import {
  normalizeRotation,
  pageNumberPlacement,
  userToVisual,
  visualToUser,
  watermarkPlacement,
  PAGE_NUMBER_POSITIONS,
  type PageNumberPosition,
} from "../lib/pdf-layout";

describe("fitRenderScale (canvas budget)", () => {
  it("keeps the requested scale when it fits", () => {
    const r = fitRenderScale({ widthPt: 595, heightPt: 842, scale: 2 });
    expect(r.scale).toBe(2);
    expect(r.reduced).toBe(false);
    expect(r.width).toBe(1190);
    expect(r.height).toBe(1684);
  });

  it("reduces the scale so the long side stays within 8192 px", () => {
    const r = fitRenderScale({ widthPt: 595, heightPt: 842, scale: 20 });
    expect(r.reduced).toBe(true);
    expect(Math.max(r.width, r.height)).toBeLessThanOrEqual(MAX_CANVAS_SIDE);
    expect(r.scale).toBeLessThan(20);
    expect(r.scale).toBeGreaterThan(9); // 8192 / 842 ~ 9.7
  });

  it("reduces the scale to respect the total pixel budget (square pages hit it before the side cap)", () => {
    const r = fitRenderScale({ widthPt: 3000, heightPt: 3000, scale: 3 });
    expect(r.reduced).toBe(true);
    expect(r.width * r.height).toBeLessThanOrEqual(MAX_CANVAS_PIXELS);
    expect(Math.max(r.width, r.height)).toBeLessThanOrEqual(MAX_CANVAS_SIDE);
  });

  it("handles a huge poster page and a custom budget", () => {
    const poster = fitRenderScale({ widthPt: 14400, heightPt: 14400, scale: 1 });
    expect(Math.max(poster.width, poster.height)).toBeLessThanOrEqual(MAX_CANVAS_SIDE);
    expect(poster.width * poster.height).toBeLessThanOrEqual(MAX_CANVAS_PIXELS);
    const small = fitRenderScale({ widthPt: 100, heightPt: 100, scale: 4, maxSide: 200, maxPixels: 10_000 });
    expect(small.width * small.height).toBeLessThanOrEqual(10_000);
    expect(small.width).toBeGreaterThanOrEqual(1);
  });

  it("never returns a scale above the request and never a zero-size canvas", () => {
    const r = fitRenderScale({ widthPt: 0, heightPt: 0, scale: 1 });
    expect(r.width).toBeGreaterThanOrEqual(1);
    expect(r.height).toBeGreaterThanOrEqual(1);
    expect(r.scale).toBeLessThanOrEqual(1);
  });
});

describe("rotation maps", () => {
  it("normalises rotations", () => {
    expect(normalizeRotation(-90)).toBe(270);
    expect(normalizeRotation(450)).toBe(90);
    expect(normalizeRotation(0)).toBe(0);
    expect(normalizeRotation(180)).toBe(180);
  });

  it("visualToUser and userToVisual are inverses for every rotation", () => {
    const W = 300;
    const H = 400;
    for (const rot of [0, 90, 180, 270]) {
      for (const [x, y] of [
        [0, 0],
        [W, 0],
        [0, H],
        [37, 211],
      ]) {
        const v = userToVisual(x, y, W, H, rot);
        const u = visualToUser(v.x, v.y, W, H, rot);
        expect(u.x).toBeCloseTo(x, 9);
        expect(u.y).toBeCloseTo(y, 9);
      }
    }
  });

  it("clockwise page rotation moves the user bottom-left corner as expected", () => {
    const W = 300;
    const H = 400;
    expect(userToVisual(0, 0, W, H, 0)).toEqual({ x: 0, y: 0 }); // stays bottom-left
    expect(userToVisual(0, 0, W, H, 90)).toEqual({ x: 0, y: W }); // top-left
    expect(userToVisual(0, 0, W, H, 180)).toEqual({ x: W, y: H }); // top-right
    expect(userToVisual(0, 0, W, H, 270)).toEqual({ x: H, y: 0 }); // bottom-right
  });
});

describe("pageNumberPlacement", () => {
  const box = { x: 0, y: 0, width: 300, height: 400 };

  it("places bottom-center on an unrotated page", () => {
    const p = pageNumberPlacement({ box, rotation: 0, position: "bottom-center", textWidth: 20, fontSize: 10 });
    expect(p).toEqual({ x: 140, y: 24, angle: 0 });
  });

  it("places the six positions in the right visual corners for every rotation", () => {
    const tw = 20;
    for (const rot of [0, 90, 180, 270] as const) {
      const vw = rot % 180 === 0 ? 300 : 400;
      const vh = rot % 180 === 0 ? 400 : 300;
      for (const pos of PAGE_NUMBER_POSITIONS as readonly PageNumberPosition[]) {
        const p = pageNumberPlacement({ box, rotation: rot, position: pos, textWidth: tw, fontSize: 10 });
        const v = userToVisual(p.x, p.y, 300, 400, rot); // visual position of the baseline start (y up)
        const [vert, horiz] = pos.split("-");
        if (horiz === "left") expect(v.x).toBeCloseTo(24, 6);
        if (horiz === "right") expect(v.x).toBeCloseTo(vw - 24 - tw, 6);
        if (horiz === "center") expect(v.x).toBeCloseTo((vw - tw) / 2, 6);
        if (vert === "bottom") expect(v.y).toBeCloseTo(24, 6);
        if (vert === "top") expect(v.y).toBeCloseTo(vh - 24 - 8, 6);
        expect(p.angle).toBe(rot); // text turns with the page so it reads upright
      }
    }
  });

  it("adds the CropBox offset", () => {
    const p = pageNumberPlacement({ box: { x: 50, y: 70, width: 300, height: 400 }, rotation: 0, position: "bottom-left", textWidth: 10, fontSize: 10 });
    expect(p.x).toBe(74);
    expect(p.y).toBe(94);
  });
});

describe("watermarkPlacement", () => {
  const box = { x: 0, y: 0, width: 600, height: 800 };

  it("is ~60% of the shorter side wide and keeps the image aspect ratio", () => {
    const p = watermarkPlacement({ box, rotation: 0, imageWidth: 800, imageHeight: 200 });
    expect(p.width).toBeCloseTo(360, 6);
    expect(p.height).toBeCloseTo(90, 6);
    expect(p.angle).toBe(45);
  });

  it("puts the centre of the rotated image on the page centre", () => {
    for (const rot of [0, 90, 180, 270]) {
      const p = watermarkPlacement({ box, rotation: rot, imageWidth: 500, imageHeight: 100 });
      const rad = (p.angle * Math.PI) / 180;
      const cx = p.x + (p.width / 2) * Math.cos(rad) - (p.height / 2) * Math.sin(rad);
      const cy = p.y + (p.width / 2) * Math.sin(rad) + (p.height / 2) * Math.cos(rad);
      expect(cx).toBeCloseTo(300, 6);
      expect(cy).toBeCloseTo(400, 6);
      expect(p.angle).toBe((45 + rot) % 360);
    }
  });

  it("respects a CropBox offset", () => {
    const p = watermarkPlacement({ box: { x: 100, y: 200, width: 600, height: 800 }, rotation: 0, imageWidth: 100, imageHeight: 100, visualAngle: 0 });
    expect(p.x + p.width / 2).toBeCloseTo(400, 6);
    expect(p.y + p.height / 2).toBeCloseTo(600, 6);
  });
});
