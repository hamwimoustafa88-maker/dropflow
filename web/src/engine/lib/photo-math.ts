// Pure maths for the photo tools (edit, background, redact, collage, icons). No DOM, no canvas: tested in Node. Mirrors the
// desktop tools in src/DropFlow.Engines.Images (EditTool, BackgroundTool, RedactTool, CollageTool) where it makes sense.

import { ConvertError } from "../types";

// ---- colours ------------------------------------------------------------------------------------------------------------

/** "#abc" / "#aabbcc" / "aabbcc" -> "#aabbcc"; null when it is not a hex colour. */
export function parseHexColor(text: string | undefined | null): string | null {
  const m = /^\s*#?([0-9a-f]{3}|[0-9a-f]{6})\s*$/i.exec(text ?? "");
  if (!m) return null;
  const h = m[1].length === 3 ? [...m[1]].map((c) => c + c).join("") : m[1];
  return `#${h.toLowerCase()}`;
}

export function hexToRgb(hex: string): [number, number, number] {
  const h = (parseHexColor(hex) ?? "#000000").slice(1);
  return [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16)];
}

export type BackgroundSpec = { kind: "color"; color: string } | { kind: "gradient"; from: string; to: string };

/** "#ffffff" | "gradient:#4338ca:#0e7490" (same syntax as the desktop tool). Unknown text is an error, never a guess. */
export function parseBackground(text: string | undefined): BackgroundSpec {
  const t = (text ?? "").trim();
  if (t === "") return { kind: "color", color: "#ffffff" };
  if (/^gradient:/i.test(t)) {
    const [, a, b] = t.split(":");
    const from = parseHexColor(a);
    const to = parseHexColor(b);
    if (!from || !to) throw new ConvertError("invalidColor", { value: t });
    return { kind: "gradient", from, to };
  }
  const color = parseHexColor(t);
  if (!color) throw new ConvertError("invalidColor", { value: t });
  return { kind: "color", color };
}

// ---- edit ---------------------------------------------------------------------------------------------------------------

export interface Adjustments {
  /** -100..100: multiplies the colour by (1 + b/100), like CSS brightness(). */
  brightness: number;
  /** -100..100: stretches around mid grey by (1 + c/100). */
  contrast: number;
  /** 0..200, 100 = unchanged. */
  saturation: number;
  filter: "none" | "grayscale" | "sepia" | "invert";
}

export function isIdentity(a: Adjustments): boolean {
  return a.brightness === 0 && a.contrast === 0 && a.saturation === 100 && a.filter === "none";
}

const clamp255 = (v: number) => (v < 0 ? 0 : v > 255 ? 255 : Math.round(v));

/** Applies brightness, contrast, saturation and a filter to straight RGBA pixels, in place. Alpha is untouched. */
export function adjustPixels(rgba: Uint8ClampedArray, a: Adjustments): void {
  const b = 1 + Math.max(-100, Math.min(100, a.brightness)) / 100;
  const c = 1 + Math.max(-100, Math.min(100, a.contrast)) / 100;
  const s = Math.max(0, Math.min(200, a.saturation)) / 100;
  for (let i = 0; i < rgba.length; i += 4) {
    let r = rgba[i] * b;
    let g = rgba[i + 1] * b;
    let bl = rgba[i + 2] * b;
    r = (r - 128) * c + 128;
    g = (g - 128) * c + 128;
    bl = (bl - 128) * c + 128;
    if (s !== 1) {
      const l = 0.2126 * r + 0.7152 * g + 0.0722 * bl;
      r = l + (r - l) * s;
      g = l + (g - l) * s;
      bl = l + (bl - l) * s;
    }
    switch (a.filter) {
      case "grayscale": {
        const l = 0.2126 * r + 0.7152 * g + 0.0722 * bl;
        r = g = bl = l;
        break;
      }
      case "sepia": {
        const r0 = r, g0 = g, b0 = bl;
        r = 0.393 * r0 + 0.769 * g0 + 0.189 * b0;
        g = 0.349 * r0 + 0.686 * g0 + 0.168 * b0;
        bl = 0.272 * r0 + 0.534 * g0 + 0.131 * b0;
        break;
      }
      case "invert":
        r = 255 - r;
        g = 255 - g;
        bl = 255 - bl;
        break;
    }
    rgba[i] = clamp255(r);
    rgba[i + 1] = clamp255(g);
    rgba[i + 2] = clamp255(bl);
  }
}

/** Box blur (three passes approximate a Gaussian), separable, O(pixels) per pass. Returns a NEW buffer. */
export function boxBlur(rgba: Uint8ClampedArray, width: number, height: number, radius: number, passes = 3): Uint8ClampedArray<ArrayBuffer> {
  const r = Math.max(0, Math.round(radius));
  const src = new Uint8ClampedArray(rgba);
  if (r === 0) return src;
  const tmp = new Uint8ClampedArray(rgba.length);
  for (let p = 0; p < passes; p++) {
    blurLine(src, tmp, width, height, r, true);
    blurLine(tmp, src, width, height, r, false);
  }
  return src;
}

function blurLine(src: Uint8ClampedArray, dst: Uint8ClampedArray, width: number, height: number, r: number, horizontal: boolean): void {
  const outer = horizontal ? height : width;
  const inner = horizontal ? width : height;
  const step = horizontal ? 4 : width * 4;
  const span = 2 * r + 1;
  for (let o = 0; o < outer; o++) {
    const base = horizontal ? o * width * 4 : o * 4;
    for (let ch = 0; ch < 4; ch++) {
      let sum = 0;
      for (let k = -r; k <= r; k++) sum += src[base + Math.min(inner - 1, Math.max(0, k)) * step + ch];
      for (let i = 0; i < inner; i++) {
        dst[base + i * step + ch] = sum / span;
        const add = Math.min(inner - 1, i + r + 1);
        const sub = Math.max(0, i - r);
        sum += src[base + add * step + ch] - src[base + sub * step + ch];
      }
    }
  }
}

/** Unsharp mask: original + amount * (original - blurred). amount 0..2. In place (colour only). */
export function sharpen(rgba: Uint8ClampedArray, width: number, height: number, amount: number): void {
  if (amount <= 0) return;
  const blurred = boxBlur(rgba, width, height, 1, 1);
  for (let i = 0; i < rgba.length; i += 4) {
    for (let c = 0; c < 3; c++) rgba[i + c] = clamp255(rgba[i + c] + amount * (rgba[i + c] - blurred[i + c]));
  }
}

/** Size of the picture after a quarter-turn rotation. */
export function rotatedSize(width: number, height: number, degrees: number): { width: number; height: number } {
  return degrees === 90 || degrees === 270 ? { width: height, height: width } : { width, height };
}

// ---- background ---------------------------------------------------------------------------------------------------------

/** Canvas that holds the picture with `padding`% around it, widened to `aspect` ("original" or "w:h"). Desktop CanvasSize. */
export function backgroundCanvasSize(width: number, height: number, paddingPercent: number, aspect: string): { width: number; height: number } {
  const pad = 1 + (2 * Math.max(0, Math.min(40, paddingPercent))) / 100;
  let w = width * pad;
  let h = height * pad;
  const m = /^(\d+(?:\.\d+)?):(\d+(?:\.\d+)?)$/.exec(aspect);
  if (aspect !== "original" && m) {
    const ratio = Number(m[1]) / Number(m[2]);
    const cw = Math.max(w, h * ratio);
    w = cw;
    h = cw / ratio;
  }
  return { width: Math.round(w), height: Math.round(h) };
}

// ---- redact -------------------------------------------------------------------------------------------------------------

export interface Rect {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** "x,y,w,h; x,y,w,h" (pixels) -> rectangles. Invalid text is an error (never silently ignored on a privacy tool). */
export function parseRegions(text: string | undefined): Rect[] {
  const out: Rect[] = [];
  for (const part of (text ?? "").split(";").map((p) => p.trim()).filter(Boolean)) {
    const n = part.split(",").map((v) => v.trim());
    if (n.length !== 4 || !n.every((v) => /^-?\d+$/.test(v))) throw new ConvertError("invalidRegions", { value: part });
    const [x, y, width, height] = n.map(Number);
    if (width <= 0 || height <= 0) throw new ConvertError("invalidRegions", { value: part });
    out.push({ x, y, width, height });
  }
  return out;
}

/** Clips a rectangle to the picture; null when nothing is left. */
export function clipRect(r: Rect, width: number, height: number): Rect | null {
  const x = Math.max(0, r.x);
  const y = Math.max(0, r.y);
  const w = Math.min(r.x + r.width, width) - x;
  const h = Math.min(r.y + r.height, height) - y;
  return w > 0 && h > 0 ? { x, y, width: w, height: h } : null;
}

/** Pixelates a region in place: each block becomes its average colour (alpha too). */
export function pixelate(rgba: Uint8ClampedArray, width: number, region: Rect, block: number): void {
  const b = Math.max(2, Math.round(block));
  for (let by = region.y; by < region.y + region.height; by += b) {
    for (let bx = region.x; bx < region.x + region.width; bx += b) {
      const ex = Math.min(bx + b, region.x + region.width);
      const ey = Math.min(by + b, region.y + region.height);
      const sum = [0, 0, 0, 0];
      let n = 0;
      for (let y = by; y < ey; y++) for (let x = bx; x < ex; x++) {
        const i = (y * width + x) * 4;
        for (let c = 0; c < 4; c++) sum[c] += rgba[i + c];
        n++;
      }
      for (let y = by; y < ey; y++) for (let x = bx; x < ex; x++) {
        const i = (y * width + x) * 4;
        for (let c = 0; c < 4; c++) rgba[i + c] = Math.round(sum[c] / n);
      }
    }
  }
}

// ---- collage ------------------------------------------------------------------------------------------------------------

export type CollageLayout = "grid" | "row" | "column" | "featured";

/** Cell rectangles and canvas size for a layout. Same maths as the desktop CollageTool.Layout. */
export function collageLayout(layout: string, count: number, columns: number, cell: number, spacing: number): { cells: Rect[]; width: number; height: number } {
  const cells: Rect[] = [];
  if (layout === "row") {
    for (let i = 0; i < count; i++) cells.push({ x: spacing + i * (cell + spacing), y: spacing, width: cell, height: cell });
    return { cells, width: spacing + count * (cell + spacing), height: cell + 2 * spacing };
  }
  if (layout === "column") {
    for (let i = 0; i < count; i++) cells.push({ x: spacing, y: spacing + i * (cell + spacing), width: cell, height: cell });
    return { cells, width: cell + 2 * spacing, height: spacing + count * (cell + spacing) };
  }
  if (layout === "featured" && count >= 3) {
    const small = Math.trunc((cell * 2 - spacing * (count - 2)) / (count - 1));
    cells.push({ x: spacing, y: spacing, width: cell * 2, height: cell * 2 });
    for (let i = 1; i < count; i++) cells.push({ x: spacing * 2 + cell * 2, y: spacing + (i - 1) * (small + spacing), width: small, height: small });
    return { cells, width: spacing * 3 + cell * 2 + small, height: cell * 2 + 2 * spacing };
  }
  const cols = columns > 0 ? Math.min(columns, count) : Math.ceil(Math.sqrt(count));
  const rows = Math.ceil(count / cols);
  for (let i = 0; i < count; i++) cells.push({ x: spacing + (i % cols) * (cell + spacing), y: spacing + Math.floor(i / cols) * (cell + spacing), width: cell, height: cell });
  return { cells, width: spacing + cols * (cell + spacing), height: spacing + rows * (cell + spacing) };
}

/** Source rectangle that fills a `cw` x `ch` cell from a `w` x `h` picture (centre crop, "cover"). */
export function coverRect(w: number, h: number, cw: number, ch: number): Rect {
  const scale = Math.max(cw / w, ch / h);
  const sw = cw / scale;
  const sh = ch / scale;
  return { x: (w - sw) / 2, y: (h - sh) / 2, width: sw, height: sh };
}

// ---- icons --------------------------------------------------------------------------------------------------------------

export const ICON_SIZES = [16, 24, 32, 48, 64, 128, 256] as const;
export const FAVICON_PNGS: readonly (readonly [string, number])[] = [
  ["favicon-16x16.png", 16],
  ["favicon-32x32.png", 32],
  ["favicon-48x48.png", 48],
  ["apple-touch-icon.png", 180],
  ["android-chrome-192x192.png", 192],
  ["android-chrome-512x512.png", 512],
];

/** site.webmanifest for the favicon set (JSON, pretty-printed). */
export function webManifest(name: string, themeColor = "#ffffff", backgroundColor = "#ffffff"): string {
  return (
    JSON.stringify(
      {
        name,
        short_name: name.slice(0, 12),
        icons: [
          { src: "/android-chrome-192x192.png", sizes: "192x192", type: "image/png" },
          { src: "/android-chrome-512x512.png", sizes: "512x512", type: "image/png" },
        ],
        theme_color: themeColor,
        background_color: backgroundColor,
        display: "standalone",
      },
      null,
      2,
    ) + "\n"
  );
}

/** The <link> tags to paste into <head>. */
export function faviconHtml(): string {
  return [
    '<link rel="icon" href="/favicon.ico" sizes="any">',
    '<link rel="icon" type="image/png" sizes="32x32" href="/favicon-32x32.png">',
    '<link rel="icon" type="image/png" sizes="16x16" href="/favicon-16x16.png">',
    '<link rel="apple-touch-icon" sizes="180x180" href="/apple-touch-icon.png">',
    '<link rel="manifest" href="/site.webmanifest">',
    "",
  ].join("\n");
}
