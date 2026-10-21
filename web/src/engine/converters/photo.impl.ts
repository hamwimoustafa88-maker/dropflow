// Heavy half of the photo tools (see photo.ts). Canvas work runs in the DropFlow Web Worker (OffscreenCanvas) or on the
// main thread (SVG input, old engines); the maths is in lib/photo-math.ts. Every output is a NEW file; re-encoding through a
// canvas also drops all metadata, which matters for "Redact" (an EXIF thumbnail would still show the hidden area).

import { getImageCaps } from "../caps";
import { canvasToBlob, decodeImage, drawScaled, get2d, makeCanvas, rgbaOf, type AnyCanvas, type Ctx2D, type Decoded } from "../lib/canvas";
import { encodeIco } from "../lib/ico";
import { extForMime, mimeForExt, sameFormatForEdit } from "../lib/image-formats";
import {
  FAVICON_PNGS,
  ICON_SIZES,
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
  type Adjustments,
} from "../lib/photo-math";
import { extensionOf, normalizeExt, stemOf } from "../names";
import { ConvertError, throwIfAborted, type Job, type OptionValue, type ProgressFn } from "../types";

const QUALITY = 0.92;

const str = (v: OptionValue | undefined, d: string): string => (typeof v === "string" ? v : d);
const num = (v: OptionValue | undefined, d: number): number => (typeof v === "number" && Number.isFinite(v) ? v : d);
const bool = (v: OptionValue | undefined, d: boolean): boolean => (typeof v === "boolean" ? v : d);

function extOfFile(file: File): string {
  return extensionOf(file.name) || normalizeExt(extForMime(file.type) ?? "");
}

async function encodeSame(canvas: AnyCanvas, ext: string, stem: string): Promise<File> {
  const fmt = sameFormatForEdit(ext, getImageCaps());
  const mime = mimeForExt(fmt)!;
  const blob = await canvasToBlob(canvas, mime, fmt === "png" ? undefined : QUALITY);
  return new File([blob], `${stem}.${fmt}`, { type: mime });
}

async function pngFile(canvas: AnyCanvas, name: string): Promise<File> {
  return new File([await canvasToBlob(canvas, "image/png")], name, { type: "image/png" });
}

export async function runPhoto(job: Job, onProgress: ProgressFn, signal: AbortSignal): Promise<File[]> {
  if (job.files.length === 0) throw new ConvertError("noFiles");
  if (job.operation === "img.collage") return [await collage(job, onProgress, signal)];
  const out: File[] = [];
  const n = job.files.length;
  for (let i = 0; i < n; i++) {
    throwIfAborted(signal);
    const file = job.files[i];
    const ext = extOfFile(file);
    const dec = await decodeImage(file, ext);
    try {
      switch (job.operation) {
        case "img.edit":
          out.push(await edit(dec, ext, file, job));
          break;
        case "img.background":
          out.push(await background(dec, file, job));
          break;
        case "img.redact":
          out.push(await redact(dec, ext, file, job));
          break;
        case "img.icons":
          out.push(...(await icons(dec, file, job, signal)));
          break;
        case "img.qr":
          out.push(await readQr(dec, file));
          break;
        default:
          throw new ConvertError("unsupported");
      }
    } finally {
      dec.close();
    }
    onProgress((i + 1) / n);
  }
  return out;
}

// ---- edit ---------------------------------------------------------------------------------------------------------------

async function edit(dec: Decoded, ext: string, file: File, job: Job): Promise<File> {
  const o = job.options;
  const adj: Adjustments = {
    brightness: num(o.brightness, 0),
    contrast: num(o.contrast, 0),
    saturation: num(o.saturation, 100),
    filter: (["grayscale", "sepia", "invert"].includes(str(o.filter, "none")) ? str(o.filter, "none") : "none") as Adjustments["filter"],
  };
  const sharpenBy = Math.max(0, Math.min(100, num(o.sharpen, 0)));
  const blurBy = Math.max(0, Math.min(20, num(o.blur, 0)));
  const rotate = [90, 180, 270].includes(Number(str(o.rotate, "0"))) ? Number(str(o.rotate, "0")) : 0;
  const flipH = bool(o.flipH, false);
  const flipV = bool(o.flipV, false);
  if (isIdentity(adj) && sharpenBy === 0 && blurBy === 0 && rotate === 0 && !flipH && !flipV) throw new ConvertError("nothingToDo");

  const size = rotatedSize(dec.width, dec.height, rotate);
  const canvas = makeCanvas(size.width, size.height);
  const ctx = get2d(canvas);
  ctx.translate(size.width / 2, size.height / 2);
  ctx.rotate((rotate * Math.PI) / 180);
  ctx.scale(flipH ? -1 : 1, flipV ? -1 : 1);
  ctx.drawImage(dec.source, -dec.width / 2, -dec.height / 2, dec.width, dec.height);
  ctx.setTransform(1, 0, 0, 1, 0, 0);

  if (!isIdentity(adj) || sharpenBy > 0 || blurBy > 0) {
    const img = ctx.getImageData(0, 0, size.width, size.height);
    let px = img.data;
    if (!isIdentity(adj)) adjustPixels(px, adj);
    if (sharpenBy > 0) sharpen(px, size.width, size.height, sharpenBy / 50);
    // Blur radius scales with the picture so "5" looks the same on a phone photo and on a small icon.
    if (blurBy > 0) px = boxBlur(px, size.width, size.height, (blurBy * Math.max(size.width, size.height)) / 1000 + blurBy / 4);
    ctx.putImageData(new ImageData(new Uint8ClampedArray(px), size.width, size.height), 0, 0);
  }
  return encodeSame(canvas, ext, stemOf(file.name));
}

// ---- background ---------------------------------------------------------------------------------------------------------

function roundedPath(ctx: Ctx2D, x: number, y: number, w: number, h: number, r: number): void {
  const rr = Math.max(0, Math.min(r, w / 2, h / 2));
  ctx.beginPath();
  ctx.moveTo(x + rr, y);
  ctx.lineTo(x + w - rr, y);
  ctx.arcTo(x + w, y, x + w, y + rr, rr);
  ctx.lineTo(x + w, y + h - rr);
  ctx.arcTo(x + w, y + h, x + w - rr, y + h, rr);
  ctx.lineTo(x + rr, y + h);
  ctx.arcTo(x, y + h, x, y + h - rr, rr);
  ctx.lineTo(x, y + rr);
  ctx.arcTo(x, y, x + rr, y, rr);
  ctx.closePath();
}

/** The picture on its own canvas, corners rounded when asked. */
function rounded(dec: Decoded, radiusPx: number): AnyCanvas {
  const c = makeCanvas(dec.width, dec.height);
  const ctx = get2d(c);
  if (radiusPx > 0) {
    roundedPath(ctx, 0, 0, dec.width, dec.height, radiusPx);
    ctx.clip();
  }
  ctx.drawImage(dec.source, 0, 0, dec.width, dec.height);
  return c;
}

async function background(dec: Decoded, file: File, job: Job): Promise<File> {
  const spec = parseBackground(str(job.options.background, "#ffffff"));
  const aspect = str(job.options.aspect, "original");
  const size = backgroundCanvasSize(dec.width, dec.height, num(job.options.padding, 8), aspect);
  const canvas = makeCanvas(size.width, size.height);
  const ctx = get2d(canvas);
  if (spec.kind === "gradient") {
    const g = ctx.createLinearGradient(0, 0, 0, size.height);
    g.addColorStop(0, spec.from);
    g.addColorStop(1, spec.to);
    ctx.fillStyle = g;
  } else {
    ctx.fillStyle = spec.color;
  }
  ctx.fillRect(0, 0, size.width, size.height);
  const radiusPx = (Math.max(0, Math.min(50, num(job.options.radius, 0))) / 100) * Math.min(dec.width, dec.height);
  const pic = rounded(dec, radiusPx);
  const x = Math.round((size.width - dec.width) / 2);
  const y = Math.round((size.height - dec.height) / 2);
  if (bool(job.options.shadow, false)) {
    ctx.save();
    ctx.shadowColor = "rgba(0,0,0,0.45)";
    ctx.shadowBlur = Math.max(4, Math.min(size.width, size.height) / 60);
    ctx.shadowOffsetX = size.width / 120;
    ctx.shadowOffsetY = size.height / 70;
    ctx.drawImage(pic, x, y);
    ctx.restore();
  } else {
    ctx.drawImage(pic, x, y);
  }
  return pngFile(canvas, `${stemOf(file.name)}.png`);
}

// ---- redact -------------------------------------------------------------------------------------------------------------

async function redact(dec: Decoded, ext: string, file: File, job: Job): Promise<File> {
  const regions = parseRegions(str(job.options.regions, ""));
  if (regions.length === 0) throw new ConvertError("invalidRegions", { value: "" });
  const style = str(job.options.style, "solid");
  const color = parseHexColor(str(job.options.color, "#000000"));
  if (style === "solid" && !color) throw new ConvertError("invalidColor", { value: str(job.options.color, "") });
  const canvas = drawScaled(dec, dec.width, dec.height);
  const ctx = get2d(canvas);
  let covered = 0;
  for (const r of regions) {
    const c = clipRect(r, canvas.width, canvas.height);
    if (!c) continue;
    covered++;
    if (style === "blur" || style === "pixelate") {
      const img = ctx.getImageData(c.x, c.y, c.width, c.height);
      let px = img.data;
      if (style === "blur") {
        // Two strong passes: the result must be unreadable, not just soft.
        const radius = Math.max(4, Math.max(c.width, c.height) / 6);
        px = boxBlur(boxBlur(px, c.width, c.height, radius), c.width, c.height, radius);
      } else {
        pixelate(px, c.width, { x: 0, y: 0, width: c.width, height: c.height }, Math.max(c.width, c.height) / 12);
      }
      ctx.putImageData(new ImageData(new Uint8ClampedArray(px), c.width, c.height), c.x, c.y);
    } else {
      const [r0, g0, b0] = hexToRgb(color!);
      ctx.fillStyle = `rgb(${r0},${g0},${b0})`;
      ctx.fillRect(c.x, c.y, c.width, c.height);
    }
  }
  if (covered === 0) throw new ConvertError("invalidRegions", { value: str(job.options.regions, "") });
  return encodeSame(canvas, ext, stemOf(file.name));
}

// ---- collage ------------------------------------------------------------------------------------------------------------

async function collage(job: Job, onProgress: ProgressFn, signal: AbortSignal): Promise<File> {
  if (job.files.length < 2) throw new ConvertError("needTwoImages");
  const o = job.options;
  const cell = Math.round(Math.max(100, Math.min(2000, num(o.cell, 600))));
  const spacing = Math.round(Math.max(0, Math.min(200, num(o.spacing, 16))));
  const radius = Math.max(0, Math.min(300, num(o.radius, 0)));
  const format = str(o.format, "jpg") === "png" ? "png" : "jpg";
  const bg = parseHexColor(str(o.background, "#ffffff"));
  if (!bg) throw new ConvertError("invalidColor", { value: str(o.background, "") });
  const layout = collageLayout(str(o.layout, "grid"), job.files.length, Math.round(num(o.columns, 0)), cell, spacing);
  const canvas = makeCanvas(layout.width, layout.height); // throws invalidSize when the result would be too big
  const ctx = get2d(canvas);
  ctx.fillStyle = bg;
  ctx.fillRect(0, 0, layout.width, layout.height);
  ctx.imageSmoothingEnabled = true;
  ctx.imageSmoothingQuality = "high";
  for (let i = 0; i < job.files.length; i++) {
    throwIfAborted(signal);
    const file = job.files[i];
    const dec = await decodeImage(file, extOfFile(file));
    try {
      const c = layout.cells[i];
      const src = coverRect(dec.width, dec.height, c.width, c.height);
      // Downscale by halves first for a clean result, then crop into the cell.
      const k = Math.max(c.width / src.width, c.height / src.height);
      const scaled = drawScaled(dec, Math.max(1, Math.round(dec.width * k)), Math.max(1, Math.round(dec.height * k)));
      ctx.save();
      if (radius > 0) {
        roundedPath(ctx, c.x, c.y, c.width, c.height, radius);
        ctx.clip();
      }
      ctx.drawImage(scaled, src.x * k, src.y * k, c.width, c.height, c.x, c.y, c.width, c.height);
      ctx.restore();
    } finally {
      dec.close();
    }
    onProgress((i + 1) / job.files.length);
  }
  const mime = format === "png" ? "image/png" : "image/jpeg";
  const blob = await canvasToBlob(canvas, mime, format === "png" ? undefined : QUALITY);
  return new File([blob], `${stemOf(job.files[0].name)}.${format}`, { type: mime });
}

// ---- icons --------------------------------------------------------------------------------------------------------------

async function squarePng(dec: Decoded, size: number, fit: "contain" | "cover"): Promise<Uint8Array> {
  const c = makeCanvas(size, size); // transparent
  const ctx = get2d(c);
  if (fit === "cover") {
    const src = coverRect(dec.width, dec.height, size, size);
    const k = size / src.width;
    const scaled = drawScaled(dec, Math.max(1, Math.round(dec.width * k)), Math.max(1, Math.round(dec.height * k)));
    ctx.drawImage(scaled, src.x * k, src.y * k, size, size, 0, 0, size, size);
  } else {
    const k = Math.min(size / dec.width, size / dec.height);
    const w = Math.max(1, Math.round(dec.width * k));
    const h = Math.max(1, Math.round(dec.height * k));
    ctx.drawImage(drawScaled(dec, w, h), Math.round((size - w) / 2), Math.round((size - h) / 2));
  }
  return new Uint8Array(await (await canvasToBlob(c, "image/png")).arrayBuffer());
}

async function icons(dec: Decoded, file: File, job: Job, signal: AbortSignal): Promise<File[]> {
  const fit = str(job.options.fit, "contain") === "cover" ? "cover" : "contain";
  const stem = stemOf(file.name);
  const pngs = new Map<number, Uint8Array>();
  const png = async (size: number) => {
    throwIfAborted(signal);
    let p = pngs.get(size);
    if (!p) {
      p = await squarePng(dec, size, fit);
      pngs.set(size, p);
    }
    return p;
  };
  const ico: { size: number; png: Uint8Array }[] = [];
  for (const s of ICON_SIZES) ico.push({ size: s, png: await png(s) });
  const favicon: { size: number; png: Uint8Array }[] = [];
  for (const s of [16, 32, 48]) favicon.push({ size: s, png: await png(s) });

  const { zipSync, strToU8 } = await import("fflate");
  const entries: Record<string, Uint8Array> = { "favicon.ico": encodeIco(favicon) };
  for (const [name, s] of FAVICON_PNGS) entries[name] = await png(s);
  entries["site.webmanifest"] = strToU8(webManifest(stem));
  entries["favicons.html"] = strToU8(faviconHtml());
  const zip = zipSync(entries, { level: 6 });
  return [
    new File([encodeIco(ico) as BlobPart], `${stem}.ico`, { type: "image/x-icon" }),
    new File([zip as BlobPart], `${stem}-favicons.zip`, { type: "application/zip" }),
  ];
}

// ---- QR -----------------------------------------------------------------------------------------------------------------

async function readQr(dec: Decoded, file: File): Promise<File> {
  const { default: jsQR } = await import("jsqr");
  const long = Math.max(dec.width, dec.height);
  // Try a comfortable size first (fast, and big photos often decode better reduced), then the full picture.
  const sizes = [...new Set([Math.min(long, 1200), Math.min(long, 2400), Math.min(long, 600)])];
  for (const target of sizes) {
    const k = target / long;
    const w = Math.max(1, Math.round(dec.width * k));
    const h = Math.max(1, Math.round(dec.height * k));
    const canvas = drawScaled(dec, w, h, { flatten: true });
    const code = jsQR(rgbaOf(canvas), w, h, { inversionAttempts: "attemptBoth" });
    if (code && code.data) {
      return new File([code.data.endsWith("\n") ? code.data : `${code.data}\n`], `${stemOf(file.name)}.txt`, { type: "text/plain;charset=utf-8" });
    }
  }
  throw new ConvertError("noQrFound");
}
